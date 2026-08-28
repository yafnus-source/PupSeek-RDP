using System;
using System.Drawing;
using System.Windows.Forms;
using ShadowRDP.Controls;
using ShadowRDP.Services;

namespace ShadowRDP
{
    // One-shot prompt for shared RDP credentials (login + password).
    // Shows up at first RDP launch of the current app session — not
    // persisted to disk, so the user types them once per app run. If
    // they mistype, the "сменить пароль" button in RDP Manager clears
    // the cache and re-prompts.
    //
    // Login is a free-form field — accepts plain SAM name ("user")
    // or DOMAIN\user or user@domain. mstsc handles all three forms.
    //
    // Returns Creds on OK, null on cancel.
    public static class RdpPasswordPrompt
    {
        public static RdpCredentialStore.Creds? Show(IWin32Window? owner)
        {
            using var dlg = new Form
            {
                Text = "Учётные данные для RDP",
                ClientSize = new Size(440, 210),
                StartPosition = owner == null
                    ? FormStartPosition.CenterScreen
                    : FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                // v3.0.1: TopMost so the prompt is always visible on top of
                // other windows (was hidden behind VS / browser during dev).
                TopMost = true,
                // v2.1.58: ControlBox stays on — user can close via the X
                // OR press Esc (handled in KeyDown below) — both cancel
                // the dialog and abort RDP launch.
                Font = new Font("Segoe UI", 9F),
                BackColor = Helpers.UiTheme.Background
            };

            var lblTitle = new Label
            {
                Text = "Введите логин и пароль для RDP-сессий\n(один раз за запуск приложения):",
                Location = new Point(16, 14),
                Size = new Size(408, 36),
                ForeColor = Helpers.UiTheme.Foreground
            };
            dlg.Controls.Add(lblTitle);

            var lblLogin = new Label
            {
                Text = "Логин:",
                Location = new Point(16, 60),
                Size = new Size(80, 22),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Helpers.UiTheme.Foreground
            };
            dlg.Controls.Add(lblLogin);

            var tbLogin = new TextBox
            {
                Location = new Point(100, 58),
                Size = new Size(324, 26),
                Font = new Font("Segoe UI", 10F)
            };
            dlg.Controls.Add(tbLogin);

            var lblPass = new Label
            {
                Text = "Пароль:",
                Location = new Point(16, 94),
                Size = new Size(80, 22),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Helpers.UiTheme.Foreground
            };
            dlg.Controls.Add(lblPass);

            var tbPass = new TextBox
            {
                Location = new Point(100, 92),
                Size = new Size(324, 26),
                UseSystemPasswordChar = true,
                Font = new Font("Segoe UI", 10F)
            };
            dlg.Controls.Add(tbPass);

            // v2.1.66: removed the play/arrow icon from OK — was just a green
            // triangle, no semantic value. Also serves as a visual marker
            // for the user to confirm they're running this exact build
            // (was "play" icon in the prior build).
            // v2.2.8: both buttons same style — MakeFilled width=96 height=30.
            // Was MakeFlat for Cancel (AutoSize=true, wider than OK on hover).
            // Now Cancel has MutedForeground (gray) bg so it reads as a
            // secondary button without visual mismatch.
            var okBtn = Helpers.UiButtons.MakeFilled(
                "OK",
                null,
                ShadowRDP.Helpers.UiTheme.HeaderBar,
                width: 96,
                radius: 5);
            okBtn.Location = new Point(212, 158);
            dlg.Controls.Add(okBtn);

            var cancelBtn = Helpers.UiButtons.MakeFilled("Отмена", null,
                Helpers.UiTheme.MutedForeground, width: 96, radius: 1);
            cancelBtn.Location = new Point(324, 158);
            dlg.Controls.Add(cancelBtn);

            RdpCredentialStore.Creds? result = null;
            void Submit()
            {
                var l = tbLogin.Text.Trim();
                var p = tbPass.Text;
                if (string.IsNullOrEmpty(l))
                {
                    MessageBox.Show(dlg, "Логин не может быть пустым.",
                        "Учётные данные", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    tbLogin.Focus();
                    return;
                }
                if (string.IsNullOrEmpty(p))
                {
                    MessageBox.Show(dlg, "Пароль не может быть пустым.",
                        "Учётные данные", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    tbPass.Focus();
                    return;
                }
                RdpCredentialStore.SetCached(l, p);
                result = new RdpCredentialStore.Creds { Login = l, Password = p };
                dlg.DialogResult = DialogResult.OK;
            }
            okBtn.Click += (_, _) => Submit();
            cancelBtn.Click += (_, _) => dlg.DialogResult = DialogResult.Cancel;

            // v2.1.60: UiButtons.MakeFilled/MakeFlat return Label, not
            // Button (Label doesn't implement IButtonControl). Form-level
            // KeyDown for Esc was unreliable ("через раз" per user report)
            // because TextBoxes can swallow the key in some focus states.
            // Hidden Button + CancelButton/AcceptButton wiring is the
            // canonical WinForms way — Esc/Enter are then OS-routed to the
            // hidden button's click, which sets DialogResult and closes.
            // v2.1.68: Visible=false made AcceptButton silently fail to
            // fire on Enter in some focus states (user reported Enter
            // didn't submit). Visible=true + off-screen position is the
            // reliable incantation — WinForms skips AcceptButton routing
            // for non-visible buttons in certain paths.
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

            // Belt-and-suspenders: explicit KeyDown handler in case the
            // hidden-button routing still doesn't fire (WinForms edge cases
            // with TextBox focus + Enter on single-line boxes).
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

            dlg.Shown += (_, _) => tbLogin.Focus();

            return dlg.ShowDialog(owner) == DialogResult.OK ? result : null;
        }

        // Convenience helper for the common case: "give me the creds to
        // use, prompting the user if not yet in memory". Returns null
        // if the user cancels — callers should treat that as "abort".
        public static RdpCredentialStore.Creds? EnsureAvailable(IWin32Window? owner)
        {
            var cached = RdpCredentialStore.GetCached();
            if (cached != null && !string.IsNullOrEmpty(cached.Login) && !string.IsNullOrEmpty(cached.Password))
                return cached;
            return Show(owner);
        }

        // v3.7.1 — per-computer credential picker. Replaces the shared
        // start-up prompt that the user wanted removed. Shown when the user
        // clicks an RDP tab that has no KeePass entry and no override.
        //
        // UI: Login + Password fields + a checkbox "Сохранить в базе KeePass".
        // On OK with the checkbox on, if the vault isn't open yet the user is
        // sent through KeePassUnlockDialog first (cancel there returns to the
        // picker so they can re-think). On a successful unlock the entry is
        // written to PupSeekRDP/RDP named after the computer. The shared
        // RdpCredentialStore is NOT touched — no implicit cross-session
        // sharing of credentials the user picked for one computer.
        //
        // Returns Creds on OK, null on cancel.
        public static RdpCredentialStore.Creds? ShowForComputer(IWin32Window? owner, string computer)
        {
            using var dlg = new Form
            {
                Text = "Учётные данные для RDP",
                ClientSize = new Size(480, 252),
                StartPosition = owner == null
                    ? FormStartPosition.CenterScreen
                    : FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                TopMost = true,
                Font = new Font("Segoe UI", 9F),
                BackColor = Helpers.UiTheme.Background
            };

            var lblTitle = new Label
            {
                Text = $"Логин и пароль для сессии:\n{computer}",
                Location = new Point(16, 14),
                Size = new Size(448, 36),
                ForeColor = Helpers.UiTheme.Foreground
            };
            dlg.Controls.Add(lblTitle);

            var lblLogin = new Label
            {
                Text = "Логин:",
                Location = new Point(16, 60),
                Size = new Size(80, 22),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Helpers.UiTheme.Foreground
            };
            dlg.Controls.Add(lblLogin);

            var tbLogin = new TextBox
            {
                Location = new Point(100, 58),
                Size = new Size(364, 26),
                Font = new Font("Segoe UI", 10F)
            };
            dlg.Controls.Add(tbLogin);

            var lblPass = new Label
            {
                Text = "Пароль:",
                Location = new Point(16, 94),
                Size = new Size(80, 22),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Helpers.UiTheme.Foreground
            };
            dlg.Controls.Add(lblPass);

            var tbPass = new TextBox
            {
                Location = new Point(100, 92),
                Size = new Size(364, 26),
                UseSystemPasswordChar = true,
                Font = new Font("Segoe UI", 10F)
            };
            dlg.Controls.Add(tbPass);

            var chkSave = new CheckBox
            {
                Text = "Сохранить в базе KeePass (PupSeekRDP/RDP)",
                Location = new Point(100, 128),
                Size = new Size(360, 24),
                ForeColor = Helpers.UiTheme.Foreground,
                // v3.7.1 — checked by default so credentials persist for next
                // launch. User can uncheck for one-off access.
                Checked = true
            };
            dlg.Controls.Add(chkSave);

            var hint = new Label
            {
                Text = "Если база закрыта — после OK будет предложено её открыть.\nБез сохранения креды действуют только для этой сессии.",
                Location = new Point(100, 156),
                Size = new Size(360, 32),
                ForeColor = Helpers.UiTheme.MutedForeground,
                Font = new Font("Segoe UI", 8.5F)
            };
            dlg.Controls.Add(hint);

            var okBtn = Helpers.UiButtons.MakeFilled(
                "OK", null, ShadowRDP.Helpers.UiTheme.HeaderBar, width: 96, radius: 5);
            okBtn.Location = new Point(264, 204);
            dlg.Controls.Add(okBtn);

            var cancelBtn = Helpers.UiButtons.MakeFilled("Отмена", null,
                Helpers.UiTheme.MutedForeground, width: 96, radius: 1);
            cancelBtn.Location = new Point(368, 204);
            dlg.Controls.Add(cancelBtn);

            RdpCredentialStore.Creds? result = null;
            bool Submit()
            {
                var l = tbLogin.Text.Trim();
                var p = tbPass.Text;
                if (string.IsNullOrEmpty(l))
                {
                    MessageBox.Show(dlg, "Логин не может быть пустым.",
                        "Учётные данные", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    tbLogin.Focus();
                    return false;
                }
                if (string.IsNullOrEmpty(p))
                {
                    MessageBox.Show(dlg, "Пароль не может быть пустым.",
                        "Учётные данные", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    tbPass.Focus();
                    return false;
                }

                if (chkSave.Checked)
                {
                    // Ensure vault is open. If not, walk the user through unlock.
                    if (!Services.KeePassVault.IsOpen)
                    {
                        var cfg = Services.KeePassVault.LoadConfig();
                        var res = Helpers.KeePassUnlockDialog.Show(dlg, cfg);
                        if (res == null)
                        {
                            // User cancelled unlock — return to the picker so
                            // they can either uncheck "save" or retry unlock.
                            return false;
                        }
                        var err = Services.KeePassVault.Open(res.DbPath, res.KeyFilePath, res.Password);
                        if (err != null)
                        {
                            MessageBox.Show(dlg, $"Не удалось открыть базу:\n{err}",
                                "Хранилище паролей", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            return false;
                        }
                        cfg.DbPath = res.DbPath;
                        cfg.KeyFilePath = res.KeyFilePath;
                        cfg.AutoUnlockAtStartup = res.AutoUnlockAtStartup;
                        Services.KeePassVault.SaveConfig(cfg);
                    }
                    var saveErr = Services.KeePassVault.SaveLogin(
                        Services.KeePassVault.RdpGroupPath, computer, l, p);
                    if (saveErr != null)
                    {
                        MessageBox.Show(dlg, $"Не удалось сохранить запись в базу:\n{saveErr}",
                            "Хранилище паролей", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        // Continue anyway — credentials are valid, just not persisted.
                    }
                }

                // v3.7.1 — deliberately NOT calling RdpCredentialStore.SetCached:
                // these credentials are for THIS computer only. The old shared
                // cache was the source of the "shared password prompt at start"
                // UX the user explicitly asked us to remove.
                result = new RdpCredentialStore.Creds { Login = l, Password = p };
                dlg.DialogResult = DialogResult.OK;
                return true;
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

            dlg.KeyPreview = true;
            dlg.KeyDown += (_, ke) =>
            {
                if (ke.KeyCode == Keys.Enter)
                {
                    if (Submit())
                    {
                        ke.Handled = true;
                        ke.SuppressKeyPress = true;
                    }
                }
                else if (ke.KeyCode == Keys.Escape)
                {
                    dlg.DialogResult = DialogResult.Cancel;
                    ke.Handled = true;
                }
            };

            dlg.Shown += (_, _) => tbLogin.Focus();

            return dlg.ShowDialog(owner) == DialogResult.OK ? result : null;
        }

        // v3.7.2 — Web variant of ShowForComputer. Same dialog UX (Login +
        // Password + "save to KeePass" checkbox default on), but writes to
        // PupSeekRDP/Web instead of PupSeekRDP/RDP, and the title/labels say
        // "сайт"/"Web" instead of "сессия"/"RDP". Used by the WebView2 popup
        // flow when the user clicks "Ввести и сохранить" on a Web login form.
        //
        // Mirror of ShowForComputer — any bug fix here likely applies there
        // too.
        public static RdpCredentialStore.Creds? ShowForWeb(IWin32Window? owner, string host)
        {
            using var dlg = new Form
            {
                Text = "Учётные данные для Web",
                ClientSize = new Size(480, 252),
                StartPosition = owner == null
                    ? FormStartPosition.CenterScreen
                    : FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                TopMost = true,
                Font = new Font("Segoe UI", 9F),
                BackColor = Helpers.UiTheme.Background
            };

            var lblTitle = new Label
            {
                Text = $"Логин и пароль для сайта:\n{host}",
                Location = new Point(16, 14),
                Size = new Size(448, 36),
                ForeColor = Helpers.UiTheme.Foreground
            };
            dlg.Controls.Add(lblTitle);

            var lblLogin = new Label
            {
                Text = "Логин:",
                Location = new Point(16, 60),
                Size = new Size(80, 22),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Helpers.UiTheme.Foreground
            };
            dlg.Controls.Add(lblLogin);

            var tbLogin = new TextBox
            {
                Location = new Point(100, 58),
                Size = new Size(364, 26),
                Font = new Font("Segoe UI", 10F)
            };
            dlg.Controls.Add(tbLogin);

            var lblPass = new Label
            {
                Text = "Пароль:",
                Location = new Point(16, 94),
                Size = new Size(80, 22),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Helpers.UiTheme.Foreground
            };
            dlg.Controls.Add(lblPass);

            var tbPass = new TextBox
            {
                Location = new Point(100, 92),
                Size = new Size(364, 26),
                UseSystemPasswordChar = true,
                Font = new Font("Segoe UI", 10F)
            };
            dlg.Controls.Add(tbPass);

            var chkSave = new CheckBox
            {
                Text = "Сохранить в базе KeePass (PupSeekRDP/Web)",
                Location = new Point(100, 128),
                Size = new Size(360, 24),
                ForeColor = Helpers.UiTheme.Foreground,
                Checked = true
            };
            dlg.Controls.Add(chkSave);

            var hint = new Label
            {
                Text = "Если база закрыта — после OK будет предложено её открыть.\nЗапись будет храниться под именем хоста (например example.com).",
                Location = new Point(100, 156),
                Size = new Size(360, 32),
                ForeColor = Helpers.UiTheme.MutedForeground,
                Font = new Font("Segoe UI", 8.5F)
            };
            dlg.Controls.Add(hint);

            var okBtn = Helpers.UiButtons.MakeCompactFilled("OK", 96);
            okBtn.Location = new Point(264, 208);
            dlg.Controls.Add(okBtn);

            var cancelBtn = Helpers.UiButtons.MakeCompactFilled("Отмена", 96,
                bgOverride: Helpers.UiTheme.MutedForeground);
            cancelBtn.Location = new Point(368, 208);
            dlg.Controls.Add(cancelBtn);

            RdpCredentialStore.Creds? result = null;
            bool Submit()
            {
                var l = tbLogin.Text.Trim();
                var p = tbPass.Text;
                if (string.IsNullOrEmpty(l))
                {
                    MessageBox.Show(dlg, "Логин не может быть пустым.",
                        "Учётные данные", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    tbLogin.Focus();
                    return false;
                }
                if (string.IsNullOrEmpty(p))
                {
                    MessageBox.Show(dlg, "Пароль не может быть пустым.",
                        "Учётные данные", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    tbPass.Focus();
                    return false;
                }

                if (chkSave.Checked)
                {
                    if (!Services.KeePassVault.IsOpen)
                    {
                        var cfg = Services.KeePassVault.LoadConfig();
                        var res = Helpers.KeePassUnlockDialog.Show(dlg, cfg);
                        if (res == null) return false;
                        var err = Services.KeePassVault.Open(res.DbPath, res.KeyFilePath, res.Password);
                        if (err != null)
                        {
                            MessageBox.Show(dlg, $"Не удалось открыть базу:\n{err}",
                                "Хранилище паролей", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            return false;
                        }
                        cfg.DbPath = res.DbPath;
                        cfg.KeyFilePath = res.KeyFilePath;
                        cfg.AutoUnlockAtStartup = res.AutoUnlockAtStartup;
                        Services.KeePassVault.SaveConfig(cfg);
                    }
                    var saveErr = Services.KeePassVault.SaveLogin(
                        Services.KeePassVault.WebGroupPath, host, l, p);
                    if (saveErr != null)
                    {
                        MessageBox.Show(dlg, $"Не удалось сохранить запись в базу:\n{saveErr}",
                            "Хранилище паролей", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }

                result = new RdpCredentialStore.Creds { Login = l, Password = p };
                dlg.DialogResult = DialogResult.OK;
                return true;
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

            dlg.KeyPreview = true;
            dlg.KeyDown += (_, ke) =>
            {
                if (ke.KeyCode == Keys.Enter)
                {
                    if (Submit())
                    {
                        ke.Handled = true;
                        ke.SuppressKeyPress = true;
                    }
                }
                else if (ke.KeyCode == Keys.Escape)
                {
                    dlg.DialogResult = DialogResult.Cancel;
                    ke.Handled = true;
                }
            };

            dlg.Shown += (_, _) => tbLogin.Focus();

            return dlg.ShowDialog(owner) == DialogResult.OK ? result : null;
        }

        // v2.1.53 — per-session credential prompt. Same dialog UI as Show,
        // but DOES NOT write to the shared RdpCredentialStore. Used by the
        // "Войти под другой учётной записью…" context-menu action so each
        // RDP Manager session can have its own in-memory login+password
        // override (cleared on app restart).
        //
        // v2.1.55: dialog widened to 520px, OK button swapped from NeoButton
        // to UiButtons.MakeFilled (#296AA0 bg + white text + restart icon)
        // per user request "расширь, цвет как у таба Книга". Was clipping
        // "Перезапустить с этими данными" text on the old 170px NeoButton.
        public static RdpCredentialStore.Creds? ShowPerSession(IWin32Window? owner, string computer)
        {
            using var dlg = new Form
            {
                Text = "Войти под другой учётной записью",
                ClientSize = new Size(520, 220),
                StartPosition = owner == null
                    ? FormStartPosition.CenterScreen
                    : FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                Font = new Font("Segoe UI", 9F),
                BackColor = Helpers.UiTheme.Background
            };

            var lblTitle = new Label
            {
                Text = $"Новые логин и пароль для сессии:\n{computer}",
                Location = new Point(16, 14),
                Size = new Size(488, 36),
                ForeColor = Helpers.UiTheme.Foreground
            };
            dlg.Controls.Add(lblTitle);

            var lblLogin = new Label
            {
                Text = "Логин:",
                Location = new Point(16, 60),
                Size = new Size(80, 22),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Helpers.UiTheme.Foreground
            };
            dlg.Controls.Add(lblLogin);

            var tbLogin = new TextBox
            {
                Location = new Point(100, 58),
                Size = new Size(404, 26),
                Font = new Font("Segoe UI", 10F)
            };
            dlg.Controls.Add(tbLogin);

            var lblPass = new Label
            {
                Text = "Пароль:",
                Location = new Point(16, 94),
                Size = new Size(80, 22),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Helpers.UiTheme.Foreground
            };
            dlg.Controls.Add(lblPass);

            var tbPass = new TextBox
            {
                Location = new Point(100, 92),
                Size = new Size(404, 26),
                UseSystemPasswordChar = true,
                Font = new Font("Segoe UI", 10F)
            };
            dlg.Controls.Add(tbPass);

            // OK = MakeFilled → #296AA0 bg + white text + restart icon,
            // matches the Book tab style in RDP Manager sidebar.
            var okBtn = Helpers.UiButtons.MakeFilled(
                "Перезапустить с этими данными",
                "restart",
                ShadowRDP.Helpers.UiTheme.HeaderBar,
                width: 300,
                radius: 5);
            okBtn.Location = new Point(16, 162);
            dlg.Controls.Add(okBtn);

            // v2.2.8: Cancel = MakeFilled gray, fixed 96×30 (was MakeFlat —
            // wider than the 300px OK button on hover, looked mismatched).
            var cancelBtn = Helpers.UiButtons.MakeFilled("Отмена", null,
                Helpers.UiTheme.MutedForeground, width: 96, radius: 1);
            cancelBtn.Location = new Point(326, 162);
            dlg.Controls.Add(cancelBtn);

            RdpCredentialStore.Creds? result = null;
            void Submit()
            {
                var l = tbLogin.Text.Trim();
                var p = tbPass.Text;
                if (string.IsNullOrEmpty(l))
                {
                    MessageBox.Show(dlg, "Логин не может быть пустым.",
                        "Учётные данные", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    tbLogin.Focus();
                    return;
                }
                if (string.IsNullOrEmpty(p))
                {
                    MessageBox.Show(dlg, "Пароль не может быть пустым.",
                        "Учётные данные", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    tbPass.Focus();
                    return;
                }
                // NO SetCached here — per-session creds stay only on the
                // RdpSession object. The shared cache is untouched.
                result = new RdpCredentialStore.Creds { Login = l, Password = p };
                dlg.DialogResult = DialogResult.OK;
            }
            okBtn.Click += (_, _) => Submit();
            cancelBtn.Click += (_, _) => dlg.DialogResult = DialogResult.Cancel;

            // v2.1.60: hidden Button trick — see Show() for rationale.
            var hiddenCancel = new Button
            {
                DialogResult = DialogResult.Cancel,
                Visible = false,
                Location = new Point(-100, -100),
                Size = new Size(1, 1)
            };
            hiddenCancel.Click += (_, _) => dlg.DialogResult = DialogResult.Cancel;
            dlg.Controls.Add(hiddenCancel);
            dlg.CancelButton = hiddenCancel;

            var hiddenOk = new Button
            {
                Visible = false,
                Location = new Point(-100, -100),
                Size = new Size(1, 1)
            };
            hiddenOk.Click += (_, _) => Submit();
            dlg.Controls.Add(hiddenOk);
            dlg.AcceptButton = hiddenOk;

            dlg.Shown += (_, _) => tbLogin.Focus();

            return dlg.ShowDialog(owner) == DialogResult.OK ? result : null;
        }
    }
}
