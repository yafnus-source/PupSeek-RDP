using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using ShadowRDP.Controls;
using ShadowRDP.Services;

namespace ShadowRDP.Helpers
{
    // v3.7.0 — Unlock dialog for the KeePass vault. Asks for the .kdbx
    // path (Browse…, or create-new if missing), an optional key-file, and
    // the master password. Returns the choice on OK, null on cancel.
    //
    // The caller (KeePassVault) is responsible for opening the database and
    // persisting keepass.json — this dialog never touches the password.
    //
    // v3.7.1 — redesign: blue UiHeader bar (matches the vault manager),
    // consistent indented field rows (label 120px / input fills to browse),
    // master-password field stretches to match path fields, action buttons
    // right-aligned (Cancel + Open) — was a centered pair that read as
    // uncommitted. Dialog widened 520→580, deepened 264→350 to fit the
    // header and give the form room to breathe.
    public static class KeePassUnlockDialog
    {
        public sealed class Result
        {
            public string DbPath { get; set; } = "";
            public string KeyFilePath { get; set; } = "";
            public string Password { get; set; } = "";
            public bool AutoUnlockAtStartup { get; set; }
        }

        public static Result? Show(IWin32Window? owner, KeePassVault.Config current)
        {
            using var dlg = new Form
            {
                Text = "Хранилище паролей (KeePass)",
                ClientSize = new Size(580, 350),
                StartPosition = owner == null
                    ? FormStartPosition.CenterScreen
                    : FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                // v3.7.2 — TopMost so the dialog appears above whatever was
                // foreground when the elevated process re-launched (after
                // UAC the OS often drops our window behind Explorer / the
                // previous non-elevated instance's splash). Matches the
                // shared RdpPasswordPrompt behaviour.
                TopMost = true,
                Font = new Font("Segoe UI", 9F),
                BackColor = Color.FromArgb(0xFA, 0xF7, 0xF4)
            };

            // === Blue header (matches KeePassVaultForm / report dialogs) ===
            var header = UiHeader.Create("Хранилище паролей", "KeePass (.kdbx) — зашифрованная база", "shield",
                Color.FromArgb(0x62, 0x6F, 0x5E), Color.White);
            dlg.Controls.Add(header);

            // Field rows: label (120) + input (340) + browse button (76).
            // All three rows share the same Y-offset pattern, just shifted
            // by 34px each. Header occupies 76px so rows start at Y=92.
            const int LabelX = 20;
            const int LabelW = 120;
            const int InputX = 144;
            const int InputW = 332;
            const int BrowseX = 484;
            const int BrowseW = 76;

            var lblDb = new Label
            {
                Text = "База (.kdbx):",
                Location = new Point(LabelX, 96),
                Size = new Size(LabelW, 22),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = UiTheme.Foreground
            };
            dlg.Controls.Add(lblDb);

            var tbDb = new TextBox
            {
                Location = new Point(InputX, 94),
                Size = new Size(InputW, 26),
                Font = new Font("Segoe UI", 10F)
            };
            if (!string.IsNullOrEmpty(current.DbPath)) tbDb.Text = current.DbPath;
            dlg.Controls.Add(tbDb);

            // v3.8.x — "Обзор" buttons: white bg, 1px #BFBFBF border,
            // gray text. Height 25 = field height minus 1px at the bottom;
            // both buttons share the same X (BrowseX) and sit top-aligned
            // with the База / Файл-ключ fields, so they line up horizontally.
            var btnBrowseDb = UiButtons.MakeCompactOutlined("Обзор…", BrowseW, height: 25);
            btnBrowseDb.Location = new Point(BrowseX, 94);
            dlg.Controls.Add(btnBrowseDb);

            var lblKey = new Label
            {
                Text = "Файл-ключ:",
                Location = new Point(LabelX, 130),
                Size = new Size(LabelW, 22),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = UiTheme.Foreground
            };
            dlg.Controls.Add(lblKey);

            var tbKey = new TextBox
            {
                Location = new Point(InputX, 128),
                Size = new Size(InputW, 26),
                Font = new Font("Segoe UI", 10F)
            };
            if (!string.IsNullOrEmpty(current.KeyFilePath)) tbKey.Text = current.KeyFilePath;
            dlg.Controls.Add(tbKey);

            var btnBrowseKey = UiButtons.MakeCompactOutlined("Обзор…", BrowseW, height: 25);
            btnBrowseKey.Location = new Point(BrowseX, 128);
            dlg.Controls.Add(btnBrowseKey);

            var lblPass = new Label
            {
                Text = "Мастер-пароль:",
                Location = new Point(LabelX, 164),
                Size = new Size(LabelW, 22),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = UiTheme.Foreground
            };
            dlg.Controls.Add(lblPass);

            // v3.7.1 — master password field stretches to match path fields
            // (was 304px, ended mid-form; looked orphaned next to the long
            // path fields above). Now aligned with tbDb/tbKey right edge.
            var tbPass = new TextBox
            {
                Location = new Point(InputX, 162),
                Size = new Size(InputW + BrowseW + 8, 26),
                UseSystemPasswordChar = true,
                Font = new Font("Segoe UI", 10F)
            };
            dlg.Controls.Add(tbPass);

            var hint = new Label
            {
                Text = "Мастер-пароль не сохраняется на диск.\nЕсли файл базы не существует — он будет создан.",
                Location = new Point(LabelX, 196),
                Size = new Size(540, 36),
                ForeColor = UiTheme.MutedForeground,
                Font = new Font("Segoe UI", 8.5F)
            };
            dlg.Controls.Add(hint);

            var chkAuto = new CheckBox
            {
                Text = "Разблокировать при старте приложения",
                Location = new Point(LabelX, 240),
                Size = new Size(400, 24),
                ForeColor = UiTheme.Foreground,
                Checked = current.AutoUnlockAtStartup
            };
            dlg.Controls.Add(chkAuto);

            // v3.7.1 — buttons switched to compact filled style (same shape
            // as the "+ Сессия" / "+ В книгу" toolbar buttons). Cancel gets
            // MutedForeground bg via the new bgOverride param so it reads as
            // a secondary action without a separate visual language.
            // v3.7.1 — Cancel shifted −5px (372→367), Open −10px (484→474),
            // then both again −10px (367→357, 474→464) per user request —
            // pair was hugging the right edge too tight.
            var okBtn = UiButtons.MakeCompactFilled("Открыть", 96);
            okBtn.Location = new Point(464, 308);
            dlg.Controls.Add(okBtn);

            var cancelBtn = UiButtons.MakeCompactFilled("Отмена", 96,
                bgOverride: UiTheme.MutedForeground);
            cancelBtn.Location = new Point(357, 308);
            dlg.Controls.Add(cancelBtn);

            void BrowseDb()
            {
                using var ofd = new OpenFileDialog
                {
                    Title = "Выберите базу KeePass (.kdbx)",
                    Filter = "База KeePass (*.kdbx)|*.kdbx|Все файлы (*.*)|*.*"
                };
                if (!string.IsNullOrEmpty(tbDb.Text))
                {
                    var dir = Path.GetDirectoryName(Path.GetFullPath(tbDb.Text));
                    if (Directory.Exists(dir)) ofd.InitialDirectory = dir;
                }
                if (ofd.ShowDialog(dlg) == DialogResult.OK)
                    tbDb.Text = ofd.FileName;
            }

            void BrowseKey()
            {
                using var ofd = new OpenFileDialog
                {
                    Title = "Выберите файл-ключ",
                    Filter = "Файл-ключ (*.key)|*.key|Все файлы (*.*)|*.*"
                };
                if (ofd.ShowDialog(dlg) == DialogResult.OK)
                    tbKey.Text = ofd.FileName;
            }

            btnBrowseDb.Click += (_, _) => BrowseDb();
            btnBrowseKey.Click += (_, _) => BrowseKey();

            Result? result = null;
            void Submit()
            {
                var db = tbDb.Text.Trim();
                if (string.IsNullOrEmpty(db))
                {
                    MessageBox.Show(dlg, "Укажите путь к базе .kdbx.",
                        "Хранилище паролей", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    tbDb.Focus();
                    return;
                }
                var key = tbKey.Text.Trim();
                if (!string.IsNullOrEmpty(key) && !File.Exists(key))
                {
                    MessageBox.Show(dlg, "Файл-ключ не найден.",
                        "Хранилище паролей", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    tbKey.Focus();
                    return;
                }
                if (string.IsNullOrEmpty(tbPass.Text) && string.IsNullOrEmpty(key))
                {
                    MessageBox.Show(dlg, "Укажите мастер-пароль или файл-ключ.",
                        "Хранилище паролей", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    tbPass.Focus();
                    return;
                }
                result = new Result { DbPath = db, KeyFilePath = key, Password = tbPass.Text, AutoUnlockAtStartup = chkAuto.Checked };
                dlg.DialogResult = DialogResult.OK;
            }

            okBtn.Click += (_, _) => Submit();
            cancelBtn.Click += (_, _) => dlg.DialogResult = DialogResult.Cancel;

            var hiddenCancel = new Button
            {
                DialogResult = DialogResult.Cancel,
                Visible = true,
                TabStop = false,
                Location = new Point(-200, -200),
                Size = new Size(1, 1)
            };
            hiddenCancel.Click += (_, _) => dlg.DialogResult = DialogResult.Cancel;
            dlg.Controls.Add(hiddenCancel);
            dlg.CancelButton = hiddenCancel;

            var hiddenOk = new Button
            {
                Visible = true,
                TabStop = false,
                Location = new Point(-300, -300),
                Size = new Size(1, 1)
            };
            hiddenOk.Click += (_, _) => Submit();
            dlg.Controls.Add(hiddenOk);
            dlg.AcceptButton = hiddenOk;

            // v3.8.x — field labels / checkbox blend with the dialog background
            // (their default grey would show as blocks on the near-white bg).
            foreach (Control c in dlg.Controls)
                if (c is Label || c is CheckBox) c.BackColor = Color.Transparent;

            dlg.KeyPreview = true;
            dlg.KeyDown += (_, ke) =>
            {
                if (ke.KeyCode == Keys.Enter)
                {
                    Submit();
                    ke.Handled = true;
                    ke.SuppressKeyPress = true;
                }
                else if (ke.KeyCode == Keys.Escape)
                {
                    dlg.DialogResult = DialogResult.Cancel;
                    ke.Handled = true;
                }
            };

            dlg.Shown += (_, _) => tbPass.Focus();

            return dlg.ShowDialog(owner) == DialogResult.OK ? result : null;
        }
    }
}
