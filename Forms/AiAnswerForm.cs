using System;
using System.Drawing;
using System.Windows.Forms;
using ShadowRDP.Controls;
using ShadowRDP.Helpers;

namespace ShadowRDP.Forms
{
    // Ответ ИИ на событие дашборда.
    //
    // v4.3.3 — редизайн в стиле FTP-эталона: градиентный hero (все метаданные
    // события в подзаголовке), текст ответа в карточке, «Закрыть» в футере.
    // Логика показа (плейсхолдеры, отступы текста, Esc, фокус) без изменений.
    public class AiAnswerForm : Form
    {
        private readonly ScrollHiddenRichTextBox _txtBox;
        private readonly Label _closeBtn;

        // Ответ ИИ на одно событие дашборда.
        public AiAnswerForm(int eventId, string source, string level, string computer, string aiAnswer)
            : this($"Ответ ИИ — EventID {eventId} — {source}",
                   $"EventID {eventId} — {source}  •  Уровень: {level}  •  Хост: {computer}",
                   aiAnswer)
        {
        }

        // v4.0.0.1 — универсальный вход (ИИ-анализ журнала и др.): произвольные
        // заголовок/подзаголовок. Старый конструктор (событие дашборда) — обёртка.
        public AiAnswerForm(string title, string subtitle, string aiAnswer)
        {
            Text = title;
            Size = new Size(756, 812);
            MinimumSize = new Size(560, 420);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            KeyPreview = true;
            BackColor = UiTheme.DialogBackground;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); } };

            // ——— 1. Hero (без крестика: у окна есть штатный) ———
            var hero = UiModal.Hero(title, subtitle, "info");

            // ——— 2. Content: answer text inside a section card ———
            var content = new Panel { Dock = DockStyle.Fill, AutoScroll = false, BackColor = UiTheme.DialogBackground, Padding = new Padding(14) };
            var cardText = UiModal.Card(0, 0, 100, 100, "", "", withHeader: false);
            cardText.Dock = DockStyle.Fill;
            content.Controls.Add(cardText);
            // v3.9.3Y — UiModal.Card гладкая сама, Region давал лесенку.
            try { cardText.Region?.Dispose(); cardText.Region = null; } catch { }

            // v4.4.0 — подкласс без системной полосы + своя тонкая.
            _txtBox = new ScrollHiddenRichTextBox
            {
                ReadOnly = true,
                WordWrap = true,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                BackColor = UiTheme.DialogCard,
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
            cardText.Controls.Add(_txtBox);
            cardText.Resize += (s, e) => FitCardChild(cardText, _txtBox);
            FitCardChild(cardText, _txtBox);
            ShadowScroll.AttachRichTextBox(_txtBox);

            // ——— 3. Footer with close button ———
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = UiTheme.DialogCard };
            footer.Paint += (s, e) => { using var pen = new Pen(UiTheme.Border, 1); e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };
            _closeBtn = UiButtons.MakeCompactFilled("Закрыть", 100);
            _closeBtn.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };
            footer.Controls.Add(_closeBtn);
            footer.Resize += (s, e) =>
            {
                _closeBtn.Left = footer.ClientSize.Width - 16 - _closeBtn.Width;
                _closeBtn.Top = 15;
            };

            // Dock order: content Fill first, hero Top, footer Bottom last —
            // the last added docks first, so the footer hugs the bottom edge.
            Controls.Add(content);
            Controls.Add(hero);
            Controls.Add(footer);

            var dpiScale = UiModal.ApplyDpiScale(this);
            if (dpiScale > 1.001f)
                ClientSize = new Size((int)Math.Round(ClientSize.Width * dpiScale), (int)Math.Round(ClientSize.Height * dpiScale));

            Shown += (s, e) =>
            {
                var wa = Screen.FromControl(this).WorkingArea;
                if (Width > wa.Width) Width = wa.Width;
                if (Height > wa.Height) Height = wa.Height;
                Left = Math.Max(wa.Left, Math.Min(Left, wa.Right - Width));
                Top = Math.Max(wa.Top, Math.Min(Top, wa.Bottom - Height));

                _closeBtn.Left = footer.ClientSize.Width - 16 - _closeBtn.Width;
                _closeBtn.Top = 15;
                ActiveControl = null;

                BeginInvoke(new Action(() =>
                {
                    _txtBox.Select(0, 0);
                    _txtBox.ScrollToCaret();
                }));
            };
        }

        // Child of a headerless card: uniform 14px padding on all sides.
        private static void FitCardChild(Panel card, Control child)
        {
            child.Bounds = new Rectangle(14, 14,
                Math.Max(50, card.ClientSize.Width - 28),
                Math.Max(50, card.ClientSize.Height - 28));
        }

        // Скругление углов: режем Region контрола по скруглённому rect.
        
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
    }
}
