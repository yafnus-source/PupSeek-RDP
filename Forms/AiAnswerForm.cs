using System;
using System.Drawing;
using System.Windows.Forms;
using ShadowRDP.Helpers;

namespace ShadowRDP.Forms
{
    public class AiAnswerForm : Form
    {
        public AiAnswerForm(int eventId, string source, string level, string computer, string aiAnswer)
        {
            Text = $"Ответ ИИ — EventID {eventId} — {source}";
            Size = new Size(520, 550);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = UiTheme.Card;

            var header = new Panel { Dock = DockStyle.Top, Height = 48, BackColor = UiTheme.HeaderBar };
            var titleLabel = new Label
            {
                Text = $"Ответ ИИ — EventID {eventId} — {source}",
                Font = new Font("Segoe UI Semibold", 11f),
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                AutoSize = true,
                Location = new Point(16, 14)
            };
            header.Controls.Add(titleLabel);
            Controls.Add(header);

            var infoPanel = new Panel { Dock = DockStyle.Top, Height = 60, BackColor = UiTheme.Muted, Padding = new Padding(12, 8, 12, 8) };
            var infoLabel = new Label
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = UiTheme.Foreground,
                Text = $"Уровень: {level}  |  Хост: {computer}"
            };
            infoPanel.Controls.Add(infoLabel);
            Controls.Add(infoPanel);

            var txtBox = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                BackColor = UiTheme.Card,
                ForeColor = UiTheme.Foreground,
                Font = new Font("Segoe UI", 10f),
                BorderStyle = BorderStyle.None,
                DetectUrls = false,
                WordWrap = true,
                Text = aiAnswer
            };
            Controls.Add(txtBox);

            var btnClose = UiButtons.MakeCompactFilled("Закрыть", 80);
            btnClose.Location = new Point(Width - 100, Height - 70);
            btnClose.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };
            Controls.Add(btnClose);

            Resize += (s, e) => { btnClose.Location = new Point(Width - 100, Height - 70); };

            Shown += (s, e) => BeginInvoke(new Action(() => { txtBox.SelectionStart = 0; txtBox.SelectionLength = 0; txtBox.ScrollToCaret(); }));
        }
    }
}
