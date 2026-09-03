using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using ShadowRDP.Helpers;

namespace ShadowRDP.Forms
{
    public class AiAnswerForm : Form
    {
        private readonly Panel _header;
        private readonly Panel _infoPanel;
        private readonly RichTextBox _txtBox;
        private readonly Panel _btnBar;
        private readonly Label _btnClose;
        private const int BarH = 40;

        public AiAnswerForm(int eventId, string source, string level, string computer, string aiAnswer)
        {
            Text = $"Ответ ИИ — EventID {eventId} — {source}";
            Size = new Size(756, 812);
            MinimumSize = new Size(560, 420);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            KeyPreview = true;
            BackColor = UiTheme.Card;

            _header = new Panel { BackColor = UiTheme.HeaderBar };
            var titleLabel = new Label
            {
                Text = $"Ответ ИИ — EventID {eventId} — {source}",
                Font = new Font("Segoe UI Semibold", 11f),
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                AutoSize = true,
                Location = new Point(16, 14)
            };
            _header.Controls.Add(titleLabel);

            _infoPanel = new Panel { BackColor = UiTheme.Muted, Padding = new Padding(12, 8, 12, 8) };
            var infoLabel = new Label
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = UiTheme.Foreground,
                Text = $"Уровень: {level}  |  Хост: {computer}"
            };
            _infoPanel.Controls.Add(infoLabel);

            _txtBox = new RichTextBox
            {
                ReadOnly = true,
                WordWrap = true,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                BackColor = UiTheme.Card,
                ForeColor = UiTheme.Foreground,
                Font = new Font("Segoe UI", 10f),
                BorderStyle = BorderStyle.None,
                TabStop = false,
                HideSelection = false,
                DetectUrls = false,
                Text = "\n" + NormalizeNewlines(aiAnswer ?? "")
            };
            _txtBox.SelectAll();
            _txtBox.SelectionIndent = 20;
            _txtBox.SelectionRightIndent = 20;
            _txtBox.SelectionStart = 0;
            _txtBox.SelectionLength = 0;

            _btnClose = UiButtons.MakeCompactFilled("Закрыть", 80);
            _btnClose.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };
            _btnBar = new Panel { BackColor = UiTheme.Card };
            _btnBar.Controls.Add(_btnClose);

            Controls.Add(_header);
            Controls.Add(_infoPanel);
            Controls.Add(_txtBox);
            Controls.Add(_btnBar);

            _txtBox.BringToFront();
            _btnBar.BringToFront();
            _btnClose.BringToFront();

            ApplyLayout();
            Resize += (s, e) => ApplyLayout();
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); } };

            Shown += (s, e) =>
            {
                var wa = Screen.FromControl(this).WorkingArea;
                if (Width > wa.Width) Width = wa.Width;
                if (Height > wa.Height) Height = wa.Height;
                Left = Math.Max(wa.Left, Math.Min(Left, wa.Right - Width));
                Top = Math.Max(wa.Top, Math.Min(Top, wa.Bottom - Height));

                ApplyLayout();
                ActiveControl = null;

                BeginInvoke(new Action(() =>
                {
                    _txtBox.Select(0, 0);
                    _txtBox.ScrollToCaret();
                    DumpDiag("Shown+afterBeginInvoke");
                }));
            };
        }

        private void ApplyLayout()
        {
            var cw = ClientSize.Width;
            var ch = ClientSize.Height;
            _header.SetBounds(0, 0, cw, 48);
            _infoPanel.SetBounds(0, 48, cw, 60);
            _txtBox.SetBounds(0, 108, cw, ch - 108 - BarH);
            _btnBar.SetBounds(0, ch - BarH, cw, BarH);
            _btnClose.Location = new Point(cw - 98, (BarH - _btnClose.Height) / 2);
        }

        private static string NormalizeNewlines(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            text = text.Replace("\r\n", "\n").Replace("\r", "\n");
            var sb = new System.Text.StringBuilder(text.Length + 64);
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    sb.Append("\n\n");
                    while (i + 1 < text.Length && text[i + 1] == '\n') i++;
                }
                else
                {
                    sb.Append(text[i]);
                }
            }
            return sb.ToString();
        }

        private void DumpDiag(string tag)
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PupSeekRDP");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "aidialog_diag.log");
                var wa = Screen.FromControl(this).WorkingArea;
                using var sw = new StreamWriter(path, append: true);
                sw.WriteLine($"{DateTime.Now:HH:mm:ss.fff} | {tag} | Form=({Left},{Top}) {Width}x{Height} Client={ClientSize.Width}x{ClientSize.Height} DPI={DeviceDpi} WorkArea=({wa.Left},{wa.Top},{wa.Width},{wa.Height})");
                sw.WriteLine($"  header={_header.Bounds} info={_infoPanel.Bounds} txt={_txtBox.Bounds} bar={_btnBar.Bounds} btn={_btnClose.Bounds} btnVisible={_btnClose.Visible} btnParent={_btnBar.Bounds}");
                sw.WriteLine($"  txt Wrap={_txtBox.WordWrap} Scroll={_txtBox.ScrollBars} Len={_txtBox.TextLength} Sel={_txtBox.SelectionStart}+{_txtBox.SelectionLength} ActiveControl={ActiveControl?.Name}");
            }
            catch { }
        }
    }
}
