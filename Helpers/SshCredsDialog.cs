using System;
using System.Drawing;
using System.Windows.Forms;
using ShadowRDP.Controls;

namespace ShadowRDP.Helpers
{
    // v2.1.92 — One-shot prompt for SSH username + password. Returns creds
    // on OK, null on cancel. Credentials stay in memory ONLY — never
    // written to disk or Credential Manager. Caller stores them on the
    // SshTerminalControl for the lifetime of the connection.
    public static class SshCredsDialog
    {
        public sealed class Creds
        {
            public string User { get; set; } = "";
            public string Password { get; set; } = "";
        }

        public static Creds? Prompt(IWin32Window owner, string host)
        {
            using var dlg = new Form
            {
                Text = $"SSH: {host}",
                // v2.2.6: 420 → 460 — was too narrow, Cancel button was
                // 4px from right edge. Now 28px margin.
                ClientSize = new Size(460, 200),
                StartPosition = owner == null
                    ? FormStartPosition.CenterScreen
                    : FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                Font = new Font("Segoe UI", 9F),
                BackColor = UiTheme.DialogBackground
            };

            var lblUser = new Label
            {
                Text = "Пользователь:",
                Location = new Point(16, 16),
                Size = new Size(120, 22),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = UiTheme.Foreground
            };
            dlg.Controls.Add(lblUser);

            var tbUser = new TextBox
            {
                // v2.2.6: widened 264 → 304 to fit 460-tall dialog
                Location = new Point(140, 14),
                Size = new Size(304, 26),
                Font = new Font("Segoe UI", 10F)
            };
            dlg.Controls.Add(tbUser);

            var lblPass = new Label
            {
                Text = "Пароль:",
                Location = new Point(16, 50),
                Size = new Size(120, 22),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = UiTheme.Foreground
            };
            dlg.Controls.Add(lblPass);

            var tbPass = new TextBox
            {
                Location = new Point(140, 48),
                Size = new Size(304, 26),
                UseSystemPasswordChar = true,
                Font = new Font("Segoe UI", 10F)
            };
            dlg.Controls.Add(tbPass);

            var hint = new Label
            {
                Text = "Креды не сохраняются — только на время сессии.",
                Location = new Point(16, 84),
                Size = new Size(388, 36),
                ForeColor = UiTheme.MutedForeground,
                Font = new Font("Segoe UI", 8.5F)
            };
            dlg.Controls.Add(hint);

            // v2.2.3: both buttons MakeFilled with SAME width=96 height=30.
            // Different bg colours (#296AA0 for OK = primary, MutedForeground
            // gray for Cancel = secondary). User reported Cancel was visually
            // cut in half when MakeFlat was force-sized — MakeFilled draws
            // a proper filled rectangle so no AutoSize weirdness.
            var okBtn = UiButtons.MakeCompactFilled("OK", 100);
            okBtn.Location = new Point(227, 153);
            dlg.Controls.Add(okBtn);

            var cancelBtn = UiButtons.MakeCompactFilled("Отмена", 100,
                bgOverride: UiTheme.MutedForeground);
            cancelBtn.Location = new Point(335, 153);
            dlg.Controls.Add(cancelBtn);

            // Hidden buttons for Enter/Esc routing.
            var hiddenOk = new Button { Visible = true, TabStop = false, Location = new Point(-100, -100), Size = new Size(1, 1) };
            var hiddenCancel = new Button { Visible = true, TabStop = false, Location = new Point(-200, -200), Size = new Size(1, 1), DialogResult = DialogResult.Cancel };
            dlg.Controls.Add(hiddenOk);
            dlg.Controls.Add(hiddenCancel);
            dlg.AcceptButton = hiddenOk;
            dlg.CancelButton = hiddenCancel;

            Creds? result = null;
            void Submit()
            {
                var u = tbUser.Text.Trim();
                var p = tbPass.Text;
                if (string.IsNullOrEmpty(u))
                {
                    MessageBox.Show(dlg, "Введите имя пользователя.", "SSH", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    tbUser.Focus();
                    return;
                }
                result = new Creds { User = u, Password = p };
                dlg.DialogResult = DialogResult.OK;
            }
            okBtn.Click += (_, _) => Submit();
            hiddenOk.Click += (_, _) => Submit();
            cancelBtn.Click += (_, _) => dlg.DialogResult = DialogResult.Cancel;

            dlg.Shown += (_, _) => tbUser.Focus();
            return UiModal.ShowDimmed(owner, dlg) == DialogResult.OK ? result : null;
        }
    }
}
