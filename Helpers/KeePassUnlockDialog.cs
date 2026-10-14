using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using ShadowRDP.Controls;
using ShadowRDP.Services;

namespace ShadowRDP.Helpers
{
    // v4.3.3 — окно в стиле FTP-эталона (hero + карточки + футер).
    // Unlock dialog for the KeePass vault. Asks for the .kdbx
    // path (Browse…), an optional key-file, and the master password.
    // Returns the choice on OK, null on cancel.
    //
    // The caller (KeePassVault) is responsible for opening the database and
    // persisting keepass.json — this dialog never touches the password.
    //
    // Layout (600×580):
    //   hero:    Хранилище паролей
    //   card 1:  База данных ... [путь] [Обзор…] / [ключ] [Обзор…]
    //   card 2:  Мастер-пароль .. [пароль] + подсказка + свитч
    //   footer:                          [Открыть] [Отмена]
    public static class KeePassUnlockDialog
    {
        // v3.9.5 — путь новой базы по умолчанию (первый запуск): рядом
        // с остальными данными программы.
        public static string DefaultDbPath()
        {
            try { return Path.Combine(ShadowRDP.AppPaths.DataDir, "passwords.kdbx"); }
            catch { return "passwords.kdbx"; }
        }

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
                    Size = new Size(600, 556),
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
                BackColor = UiTheme.DialogBackground,
                AutoScroll = false
            };
            dlg.KeyPreview = true;

            // ——— 1. Hero (без крестика: у окна есть штатный) ———
            var hero = UiModal.Hero("Хранилище паролей", "KeePass (.kdbx) — зашифрованная база", "shield");

            // ——— 2. Section cards ———
            const int ClientW = 584; // ширина клиента при outer 600 (рамки ~16px)

            // Card #1: База данных
            var cardDb = UiModal.Card(14, 14, 556, 160, "База данных", "book");
            var lDb = UiModal.FieldLabel("База (.kdbx)", 16, 46);
            var wDb = UiModal.Field(new TextBox(), 380);
            wDb.Location = new Point(16, 62);
            var btnBrowseDb = UiButtons.MakeCompactFilled("Обзор…", 76);
            btnBrowseDb.Location = new Point(402, 66);
            var lKey = UiModal.FieldLabel("Файл-ключ", 16, 100);
            var wKey = UiModal.Field(new TextBox(), 380);
            wKey.Location = new Point(16, 116);
            var btnBrowseKey = UiButtons.MakeCompactFilled("Обзор…", 76);
            btnBrowseKey.Location = new Point(402, 120);
            var txtDb = (TextBox)wDb.Controls[0];
            var txtKey = (TextBox)wKey.Controls[0];
            if (!string.IsNullOrEmpty(current.DbPath)) txtDb.Text = current.DbPath;
            // v3.9.5 — первого запуска: предлагаем путь новой базы сразу,
            // остаётся вбить мастер-пароль и нажать «Открыть» — файл создастся.
            else txtDb.Text = DefaultDbPath();
            if (!string.IsNullOrEmpty(current.KeyFilePath)) txtKey.Text = current.KeyFilePath;

            // Card #2: Мастер-пароль
            var cardPass = UiModal.Card(14, 14 + 160 + 12, 556, 176, "Мастер-пароль", "key");
            var lPass = UiModal.FieldLabel("Мастер-пароль", 16, 46);
            var txtPass = new TextBox { UseSystemPasswordChar = true };
            var wPass = UiModal.Field(txtPass, 524);
            wPass.Location = new Point(16, 62);
            var hint = new Label
            {
                Text = "Мастер-пароль не сохраняется на диск.\nНет файла по пути — создастся новая база.",
                Location = new Point(16, 100),
                AutoSize = false,
                Size = new Size(524, 30),
                ForeColor = UiTheme.MutedForeground,
                Font = new Font("Segoe UI", 8.5F),
                BackColor = Color.Transparent
            };
            var chkAuto = new UiToggle
            {
                Text = "Разблокировать при старте приложения",
                Checked = current.AutoUnlockAtStartup,
                Location = new Point(16, 140),
                Width = 300
            };

            // ——— 3. Footer (Открыть / Отмена) ———
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = UiTheme.DialogCard };
            footer.Paint += (s, e) => { using var pen = new Pen(UiTheme.Border, 1); e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };
            var okBtn = UiButtons.MakeCompactFilled("Открыть", 100);
            okBtn.Location = new Point(ClientW - 233, 15);
            okBtn.Click += (_, _) => Submit();
            var cancelBtn = UiButtons.MakeCompactFilled("Отмена", 100,
                bgOverride: UiTheme.MutedForeground);
            cancelBtn.Location = new Point(ClientW - 125, 15);
            cancelBtn.Click += (_, _) => dlg.DialogResult = DialogResult.Cancel;
            var hiddenOk = new Button { Visible = true, TabStop = false, Location = new Point(-100, -100), Size = new Size(1, 1) };
            var hiddenCancel = new Button { DialogResult = DialogResult.Cancel, Visible = true, TabStop = false, Location = new Point(-100, -100), Size = new Size(1, 1) };
            footer.Controls.Add(okBtn);
            footer.Controls.Add(cancelBtn);
            footer.Controls.Add(hiddenOk);
            footer.Controls.Add(hiddenCancel);
            dlg.AcceptButton = hiddenOk;
            dlg.CancelButton = hiddenCancel;

            Result? result = null;
            void Submit()
            {
                var db = txtDb.Text.Trim();
                if (string.IsNullOrEmpty(db))
                {
                    MessageBox.Show(dlg, "Укажите путь к базе .kdbx.",
                        "Хранилище паролей", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    txtDb.Focus();
                    return;
                }
                var key = txtKey.Text.Trim();
                if (!string.IsNullOrEmpty(key) && !File.Exists(key))
                {
                    MessageBox.Show(dlg, "Файл-ключ не найден.",
                        "Хранилище паролей", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    txtKey.Focus();
                    return;
                }
                if (string.IsNullOrEmpty(txtPass.Text) && string.IsNullOrEmpty(key))
                {
                    MessageBox.Show(dlg, "Укажите мастер-пароль или файл-ключ.",
                        "Хранилище паролей", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    txtPass.Focus();
                    return;
                }
                result = new Result { DbPath = db, KeyFilePath = key, Password = txtPass.Text, AutoUnlockAtStartup = chkAuto.Checked };
                dlg.DialogResult = DialogResult.OK;
            }

            void BrowseDb()
            {
                // v3.9.5 — SaveFileDialog вместо OpenFileDialog: можно и
                // выбрать существующую базу, и вбить имя новой (создастся
                // при открытии). OverwritePrompt выключен: существующую
                // только открываем, не перезаписываем.
                using var sfd = new SaveFileDialog
                {
                    Title = "Выберите или создайте базу KeePass (.kdbx)",
                    Filter = "База KeePass (*.kdbx)|*.kdbx|Все файлы (*.*)|*.*",
                    DefaultExt = "kdbx",
                    CheckFileExists = false,
                    OverwritePrompt = false
                };
                try
                {
                    if (!string.IsNullOrEmpty(txtDb.Text))
                    {
                        var dir = Path.GetDirectoryName(Path.GetFullPath(txtDb.Text));
                        if (Directory.Exists(dir)) sfd.InitialDirectory = dir;
                        sfd.FileName = Path.GetFileName(txtDb.Text);
                    }
                    else
                    {
                        if (Directory.Exists(ShadowRDP.AppPaths.DataDir))
                            sfd.InitialDirectory = ShadowRDP.AppPaths.DataDir;
                        sfd.FileName = "passwords.kdbx";
                    }
                }
                catch { }
                if (sfd.ShowDialog(dlg) == DialogResult.OK)
                    txtDb.Text = sfd.FileName;
            }

            void BrowseKey()
            {
                using var ofd = new OpenFileDialog
                {
                    Title = "Выберите файл-ключ",
                    Filter = "Файл-ключ (*.key)|*.key|Все файлы (*.*)|*.*"
                };
                if (ofd.ShowDialog(dlg) == DialogResult.OK)
                    txtKey.Text = ofd.FileName;
            }

            btnBrowseDb.Click += (_, _) => BrowseDb();
            btnBrowseKey.Click += (_, _) => BrowseKey();
            okBtn.Click += (_, _) => Submit();
            cancelBtn.Click += (_, _) => dlg.DialogResult = DialogResult.Cancel;
            hiddenOk.Click += (_, _) => Submit();
            hiddenCancel.Click += (_, _) => dlg.DialogResult = DialogResult.Cancel;

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

            // ——— assemble: hero (top) + footer (bottom) + scrollable content ———
            // Widgets are CHILDREN of their card (relative coordinates), so the
            // card paints its rounded fill first and the widgets render on top.
            var content = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = UiTheme.DialogBackground };
            cardDb.Controls.AddRange(new Control[] { lDb, wDb, btnBrowseDb, lKey, wKey, btnBrowseKey });
            cardPass.Controls.AddRange(new Control[] { lPass, wPass, hint, chkAuto });
            content.Controls.AddRange(new Control[] { cardDb, cardPass });
            // v4.4.0 — своя тонкая полоса вместо системной.
            ShadowScroll.AttachPanel(content);
            dlg.Controls.Add(content);
            dlg.Controls.Add(hero);
            dlg.Controls.Add(footer);

            CenterEditor(wDb);
            CenterEditor(wKey);
            CenterEditor(wPass);
            // v3.9.3Y — Field/Card гладкие сами; Region давал лесенку.
            foreach (var c in new Control[] { wDb, wKey, wPass, cardDb, cardPass })
            { try { c.Region?.Dispose(); c.Region = null; } catch { } }

            var dpiScale = UiModal.ApplyDpiScale(dlg);
            if (dpiScale > 1.001f)
                dlg.ClientSize = new Size((int)Math.Round(dlg.ClientSize.Width * dpiScale), (int)Math.Round(dlg.ClientSize.Height * dpiScale));

            // Финальные координаты — по факту, когда у панелей реальный размер.
            // Форма FixedDialog — дальше ресайзов нет, позиции стоят мёртво.
            dlg.Shown += (_, _) =>
            {
                okBtn.Location = new Point(footer.Width - 233, 15);
                cancelBtn.Location = new Point(footer.Width - 125, 15);
                txtPass.Focus();
                try { dlg.BeginInvoke(new Action(() => { try { dlg.Refresh(); } catch { } })); } catch { }
            };

            return UiModal.ShowDimmed(owner, dlg) == DialogResult.OK ? result : null;
        }

        // ComboBox/TextBox игнорируют заданный Height (высота от шрифта) —
        // центрируем редактор в 30px-обёртке.
        private static void CenterEditor(Panel wrap)
        {
            if (wrap.Controls.Count == 0) return;
            var ed = wrap.Controls[0];
            int pref = 0;
            try
            {
                if (ed is ComboBox cb) pref = cb.PreferredHeight;
                else if (ed is DateTimePicker dt) pref = dt.PreferredHeight;
                else if (ed is NumericUpDown nud) pref = nud.PreferredHeight;
                else if (ed is TextBox tb) pref = tb.PreferredHeight;
                else return;
            }
            catch { return; }
            if (pref < 1 || pref > wrap.Height - 4) return;
            ed.Height = pref;
            ed.Location = new Point(wrap.Padding.Left, (wrap.Height - pref) / 2);
        }

    }
}
