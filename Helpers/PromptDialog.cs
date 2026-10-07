using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ShadowRDP
{
    public static class PromptDialog
    {
        public static string? Show(IWin32Window owner, string title, string label, string defaultValue)
        {
            using var dlg = new Form
            {
                Text = title,
                ClientSize = new Size(500, 200),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                Font = new Font("Segoe UI", 9F),
                BackColor = Helpers.UiTheme.Card,
                ShowInTaskbar = false
            };
            // тонкая рамка как у других диалогов
            dlg.Paint += (se, e) =>
            {
                e.Graphics.DrawRectangle(new Pen(Helpers.UiTheme.Border, 1), 0, 0, dlg.Width - 1, dlg.Height - 1);
                // верхняя акцентная полоса #626F5E как у Дашборда
                using var ac = new SolidBrush(Color.FromArgb(0x62, 0x6F, 0x5E));
                e.Graphics.FillRectangle(ac, 1, 1, dlg.Width - 2, 4);
            };

            // Заголовок — как в RdpPasswordPrompt (жирный 10)
            var lblTitle = new Label
            {
                Text = title,
                Location = new Point(20, 18),
                AutoSize = true,
                Font = new Font("Segoe UI Semibold", 10F),
                ForeColor = Helpers.UiTheme.Foreground,
                BackColor = Color.Transparent
            };
            dlg.Controls.Add(lblTitle);

            var lbl = new Label
            {
                Text = label,
                Location = new Point(20, 44),
                Size = new Size(460, 50),
                ForeColor = Helpers.UiTheme.MutedForeground,
                Font = new Font("Segoe UI", 9F),
                BackColor = Color.Transparent
            };
            var needed = TextRenderer.MeasureText(label, lbl.Font, new Size(460, 200), TextFormatFlags.WordBreak);
            lbl.Height = Math.Clamp(needed.Height, 18, 50);
            dlg.Controls.Add(lbl);

            // Поле ввода — карточка белая с бордером, как в TodoPairDialog
            var card = new Panel { Location = new Point(20, lbl.Bottom + 8), Size = new Size(460, 34), BackColor = Color.White };
            card.Paint += (se, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using var path = Helpers.UiButtons.RoundedRect(new Rectangle(0, 0, card.Width - 1, card.Height - 1), 6);
                e.Graphics.FillPath(new SolidBrush(Color.White), path);
                e.Graphics.DrawPath(new Pen(Helpers.UiTheme.BorderSubtle, 1), path);
            };
            var tb = new TextBox
            {
                Text = defaultValue ?? "",
                Location = new Point(10, 6),
                Size = new Size(440, 22),
                BorderStyle = BorderStyle.None,
                Font = new Font("Segoe UI", 10F),
                BackColor = Color.White,
                ForeColor = Helpers.UiTheme.Foreground
            };
            card.Controls.Add(tb);
            dlg.Controls.Add(card);

            var btnTop = card.Bottom + 18;
            dlg.ClientSize = new Size(500, btnTop + 47);

            // Кнопки как везде — MakeCompactFilled 100×22, по правому краю (25px)
            var okBtn = Helpers.UiButtons.MakeCompactFilled("OK", 100);
            okBtn.Location = new Point(dlg.ClientSize.Width - 233, btnTop);
            okBtn.Click += (se, ev) => dlg.DialogResult = DialogResult.OK;
            dlg.Controls.Add(okBtn);

            var cancelBtn = Helpers.UiButtons.MakeCompactFilled("Отмена", 100, bgOverride: Helpers.UiTheme.MutedForeground);
            cancelBtn.Location = new Point(dlg.ClientSize.Width - 125, btnTop);
            cancelBtn.Click += (se, ev) => dlg.DialogResult = DialogResult.Cancel;
            dlg.Controls.Add(cancelBtn);

            // скрытые для Enter/Esc (Label не IButtonControl)
            var hiddenOk = new Button { DialogResult = DialogResult.OK, Visible = false, Location = new Point(-100, -100), Size = new Size(1, 1) };
            dlg.Controls.Add(hiddenOk);
            var hiddenCancel = new Button { DialogResult = DialogResult.Cancel, Visible = false, Location = new Point(-100, -100), Size = new Size(1, 1) };
            dlg.Controls.Add(hiddenCancel);
            dlg.AcceptButton = hiddenOk;
            dlg.CancelButton = hiddenCancel;
            // явный Esc — гарантия закрытия по любой раскладке клавиатуры
            dlg.KeyPreview = true;
            dlg.KeyDown += (se, ev) => { if (ev.KeyCode == Keys.Escape) { ev.Handled = true; dlg.DialogResult = DialogResult.Cancel; } };

            tb.SelectAll();
            dlg.Shown += (se, ev) => tb.Focus();
            return Helpers.UiModal.ShowDimmed(owner, dlg) == DialogResult.OK ? tb.Text : null;
        }
    }
}
