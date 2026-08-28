using System;
using System.Drawing;
using System.Windows.Forms;

namespace ShadowRDP
{
    // Single-input modal dialog. Replacement for the missing InputBox in
    // WinForms (VisualBasic.Interaction.InputBox exists but pulling in
    // Microsoft.VisualBasic for one dialog is overkill).
    //
    // Usage: PromptDialog.Show(owner, "title", "label text", "default value")
    //   → returns the entered string, or null if the user cancelled.
    public static class PromptDialog
    {
        public static string? Show(IWin32Window owner, string title, string label, string defaultValue)
        {
            using var dlg = new Form
            {
                Text = title,
                ClientSize = new Size(440, 140),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                Font = new Font("Segoe UI", 9F),
                BackColor = ShadowRDP.Helpers.UiTheme.Background
            };

            var lbl = new Label
            {
                Text = label,
                Location = new Point(16, 16),
                Size = new Size(408, 22),
                ForeColor = ShadowRDP.Helpers.UiTheme.Foreground
            };
            dlg.Controls.Add(lbl);

            var tb = new TextBox
            {
                Text = defaultValue ?? "",
                Location = new Point(16, 44),
                Size = new Size(408, 26),
                Multiline = true,
                Height = 50
            };
            dlg.Controls.Add(tb);

            var okBtn = new Button
            {
                Text = "OK",
                DialogResult = DialogResult.OK,
                Location = new Point(248, 102),
                Size = new Size(80, 28),
                BackColor = ShadowRDP.Helpers.UiTheme.Accent,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            okBtn.FlatAppearance.BorderSize = 0;
            dlg.Controls.Add(okBtn);

            var cancelBtn = new Button
            {
                Text = "Отмена",
                DialogResult = DialogResult.Cancel,
                Location = new Point(344, 102),
                Size = new Size(80, 28),
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            cancelBtn.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
            dlg.Controls.Add(cancelBtn);

            dlg.AcceptButton = okBtn;
            dlg.CancelButton = cancelBtn;

            return dlg.ShowDialog(owner) == DialogResult.OK ? tb.Text : null;
        }
    }
}
