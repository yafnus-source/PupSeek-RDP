using System;
using System.Drawing;
using System.Security.Cryptography;
using System.Windows.Forms;
using ShadowRDP.Controls;

namespace ShadowRDP
{
    // Two-field password prompt with a "Generate" button that produces a
    // cryptographically strong random password meeting typical Windows
    // complexity requirements (upper + lower + digit + symbol, 14 chars).
    //
    // Used by the "Сменить пароль" action in LocalAdminsForm.
    //
    // Returns the entered password, or null if the user cancelled. The two
    // fields must match before OK is accepted — otherwise the dialog beeps
    // and stays open.
    public static class PasswordDialog
    {
        // Default generated length. 14 is comfortably above the Windows
        // default minimum (7) and modern recommendations (12+).
        private const int GeneratedLength = 14;

        public static string? Show(IWin32Window owner, string title, string label)
        {
            using var dlg = new Form
            {
                Text = title,
                ClientSize = new Size(440, 200),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                Font = new Font("Segoe UI", 9F),
                BackColor = ShadowRDP.Helpers.UiTheme.DialogBackground
            };

            var lbl = new Label
            {
                Text = label,
                Location = new Point(16, 14),
                Size = new Size(408, 22),
                ForeColor = ShadowRDP.Helpers.UiTheme.Foreground
            };
            dlg.Controls.Add(lbl);

            var lblPass = new Label
            {
                Text = "Новый пароль:",
                Location = new Point(16, 44),
                Size = new Size(120, 22),
                TextAlign = ContentAlignment.MiddleLeft
            };
            dlg.Controls.Add(lblPass);

            var tbPass = new TextBox
            {
                Location = new Point(140, 44),
                Size = new Size(284, 26),
                UseSystemPasswordChar = true
            };
            dlg.Controls.Add(tbPass);

            var lblConfirm = new Label
            {
                Text = "Подтверждение:",
                Location = new Point(16, 78),
                Size = new Size(120, 22),
                TextAlign = ContentAlignment.MiddleLeft
            };
            dlg.Controls.Add(lblConfirm);

            var tbConfirm = new TextBox
            {
                Location = new Point(140, 78),
                Size = new Size(284, 26),
                UseSystemPasswordChar = true
            };
            dlg.Controls.Add(tbConfirm);

            // Generate button — fills both fields and copies the result to
            // the clipboard so the user can paste it elsewhere (password
            // manager, IT ticket, etc.) without retyping.
            var genBtn = new NeoButton
            {
                Text = "Сгенерировать",
                Location = new Point(16, 116),
                Size = new Size(130, 30),
                Accent = false
            };
            dlg.Controls.Add(genBtn);

            // Live equality indicator — turns green when both fields match
            // and are non-empty, grey otherwise. Helps the user notice a
            // typo before clicking OK.
            var matchLabel = new Label
            {
                Text = "",
                Location = new Point(160, 120),
                Size = new Size(264, 22),
                ForeColor = Color.Gray,
                TextAlign = ContentAlignment.MiddleLeft
            };
            dlg.Controls.Add(matchLabel);

            EventHandler updateMatch = (_, _) =>
            {
                var a = tbPass.Text;
                var b = tbConfirm.Text;
                if (a.Length == 0 && b.Length == 0)
                {
                    matchLabel.Text = "";
                    matchLabel.ForeColor = Color.Gray;
                }
                else if (a == b)
                {
                    matchLabel.Text = "✓ совпадают";
                    matchLabel.ForeColor = Color.Green;
                }
                else
                {
                    matchLabel.Text = "✗ не совпадают";
                    matchLabel.ForeColor = Color.FromArgb(200, 60, 60);
                }
            };
            tbPass.TextChanged += updateMatch;
            tbConfirm.TextChanged += updateMatch;

            var okBtn = new NeoButton
            {
                Text = "OK",
                Location = new Point(239, 145),
                Size = new Size(80, 30),
                Accent = true
            };
            dlg.Controls.Add(okBtn);

            var cancelBtn = new NeoButton
            {
                Text = "Отмена",
                Location = new Point(335, 145),
                Size = new Size(80, 30),
                Accent = false
            };
            dlg.Controls.Add(cancelBtn);

            // Wire OK to validate-then-close instead of DialogResult.OK, so
            // we can refuse to close on mismatched/empty fields.
            string? result = null;
            okBtn.Click += (_, _) =>
            {
                var p = tbPass.Text;
                var c = tbConfirm.Text;
                if (string.IsNullOrEmpty(p))
                {
                    MessageBox.Show(dlg, "Пароль не может быть пустым.",
                        "Сменить пароль", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    tbPass.Focus();
                    return;
                }
                if (p != c)
                {
                    MessageBox.Show(dlg, "Пароль и подтверждение не совпадают.",
                        "Сменить пароль", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    tbConfirm.Focus();
                    return;
                }
                result = p;
                dlg.DialogResult = DialogResult.OK;
            };
            cancelBtn.Click += (_, _) => dlg.DialogResult = DialogResult.Cancel;

            dlg.AcceptButton = okBtn;
            dlg.CancelButton = cancelBtn;

            genBtn.Click += (_, _) =>
            {
                var generated = GenerateStrongPassword(GeneratedLength);
                tbPass.Text = generated;
                tbConfirm.Text = generated;
                try
                {
                    Clipboard.SetText(generated);
                    MessageBox.Show(dlg,
                        "Сгенерирован сильный пароль и скопирован в буфер обмена.\n\n" +
                        "Сохраните его в безопасном месте — буфер обмена будет\n" +
                        "перезаписан при следующем копировании.",
                        "Сгенерирован пароль", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch
                {
                    // Clipboard can fail in some contexts (RDP redirect, etc.)
                    // — non-fatal, the password is still in the text fields.
                }
            };

            return Helpers.UiModal.ShowDimmed(owner, dlg) == DialogResult.OK ? result : null;
        }

        // Cryptographically strong password generator. Guarantees at least
        // one upper, one lower, one digit, one symbol — meets the default
        // Windows complexity policy. Excludes look-alike chars (0/O, 1/l/I)
        // to reduce transcription errors.
        private static string GenerateStrongPassword(int length)
        {
            const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
            const string lower = "abcdefghijkmnopqrstuvwxyz";
            const string digit = "23456789";
            const string symbol = "!@#$%^&*-_=+";
            const string all = upper + lower + digit + symbol;

            Span<char> buf = stackalloc char[length];
            using var rng = RandomNumberGenerator.Create();

            // First four positions: one from each required class.
            buf[0] = Pick(upper, rng);
            buf[1] = Pick(lower, rng);
            buf[2] = Pick(digit, rng);
            buf[3] = Pick(symbol, rng);

            // Remaining positions: random from the full set.
            for (int i = 4; i < length; i++)
            {
                buf[i] = Pick(all, rng);
            }

            // Fisher-Yates shuffle so the required chars don't always land
            // in positions 0-3.
            Span<byte> swapBuf = stackalloc byte[4];
            for (int i = length - 1; i > 0; i--)
            {
                rng.GetBytes(swapBuf);
                int j = (int)(BitConverter.ToUInt32(swapBuf) % (uint)(i + 1));
                (buf[i], buf[j]) = (buf[j], buf[i]);
            }

            return new string(buf);
        }

        private static char Pick(string set, RandomNumberGenerator rng)
        {
            Span<byte> b = stackalloc byte[4];
            rng.GetBytes(b);
            return set[(int)(BitConverter.ToUInt32(b) % (uint)set.Length)];
        }
    }
}
