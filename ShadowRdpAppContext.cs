using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using ShadowRDP.Helpers;
using ShadowRDP.Services;

namespace ShadowRDP
{
    // v3.0 — ApplicationContext owns the RdpManagerForm (was: MainForm) +
    // NotifyIcon.
    //
    // Behaviour:
    //   - Closing RDP Manager via the X button HIDES it (Visible=false)
    //     instead of disposing. The tray icon keeps the process alive.
    //   - Double-click on the tray icon re-shows the form.
    //   - Right-click on the tray icon shows a context menu with Show + Exit.
    //   - Ctrl + middle-click ANYWHERE shows a popup menu with the active
    //     RDP Manager sessions — click one to bring its RDP window forward.
    //   - Calling ExitThread() (e.g. from the Exit menu item) is the only way
    //     to actually terminate the app.
    //
    // The tray icon reuses StatusIcon (same red/green circle as the form's
    // title-bar icon) so the admin/non-admin indicator is consistent.
    public class ShadowRdpAppContext : ApplicationContext
    {
        private readonly NotifyIcon _notifyIcon;
        private readonly Helpers.ThemedContextMenu _trayMenu;
        // Not readonly — installed on Shown, not in ctor.
        private GlobalMouseHook? _mouseHook;
        private static bool _realExit;
        private static ShadowRdpAppContext? _instance;
        public static void ShowBalloon(string title, string text, ToolTipIcon icon = ToolTipIcon.Info)
        {
            try
            {
                if (_instance?._notifyIcon == null) return;
                _instance._notifyIcon.BalloonTipTitle = title;
                _instance._notifyIcon.BalloonTipText = text;
                _instance._notifyIcon.BalloonTipIcon = icon;
                _instance._notifyIcon.ShowBalloonTip(4000);
            }
            catch { }
        }

        // v3.6.8 — lets RdpManagerForm skip destructive cleanup when the X
        // button only hides the window to tray (its own FormClosing handler
        // runs BEFORE this context's cancel-and-hide handler).
        internal static bool IsRealExit => _realExit;

        public ShadowRdpAppContext(bool isAdmin)
        {
            _instance = this;
            // v3.0: RDP Manager is the main window (MainForm removed). Singleton
            // via Instance — first access constructs it.
            var rdpForm = Forms.RdpManagerForm.Instance;

            // === Tray context menu (themed popup) — без header только в трее ===
            _trayMenu = new Helpers.ThemedContextMenu();
            _trayMenu.AddItem("Показать", "play", () => ShowMainForm());
            _trayMenu.AddItem("Пароли…", "shield", () => ShowVaultForm());
            _trayMenu.AddItem("Выход", "delete", () => ExitApplication());

            // === Tray icon ===
            _notifyIcon = new NotifyIcon
            {
                Text = $"Pup Seek RDP — {(isAdmin ? "Администратор" : "без прав")}",
                Visible = true
            };
            try
            {
                _notifyIcon.Icon = StatusIcon.Create(isAdmin);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Tray StatusIcon failed: {ex.Message}");
            }
            _notifyIcon.DoubleClick += (s, e) => ShowMainForm();

            // v3.9.0 — show the themed popup on right-click of the tray icon.
            _notifyIcon.MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Right)
                    _trayMenu.Show(null, Control.MousePosition);
            };
            _notifyIcon.BalloonTipClicked += (s, e) =>
            {
                try { var f = Forms.RdpManagerForm.Instance; f.Show(); if (f.WindowState == FormWindowState.Minimized) f.WindowState = FormWindowState.Normal; f.Activate(); try { f.ShowDashboard(); } catch { } } catch { }
            };

            // Override the X-button behaviour: hide to tray instead of exit.
            // Setting e.Cancel = true in FormClosing prevents disposal; we
            // then call Hide() to drop the window from screen.
            rdpForm.FormClosing += (s, e) =>
            {
                if (!_realExit && e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    rdpForm.Hide();
                    _notifyIcon.BalloonTipTitle = "Pup Seek RDP";
                    _notifyIcon.BalloonTipText = "Приложение свернуто в трей. Двойной клик — открыть.";
                    _notifyIcon.ShowBalloonTip(1500);
                }
            };

            // v2.1.53 — Ctrl + middle-click anywhere opens the quick-session
            // switcher. Hook installed AFTER the form is shown so the hook
            // callback (which runs on our UI thread) always has a valid
            // message pump.
            rdpForm.Shown += (s, e) =>
            {
                _mouseHook = new GlobalMouseHook();
                _mouseHook.CtrlMiddleButtonDown += OnCtrlMiddleClick;
                _mouseHook.Start();
            };

            // v3.7.0 — KeePass startup flow. The master password is NEVER
            // stored — the user types it once per app run and the vault stays
            // unlocked (auto-lock: 5 min inactivity).
            //
            // v3.7.1 — first run (no keepass.json yet): offer to create a
            // KeePass store ONCE. The offer marker is written on both Yes and
            // No, so the question never reappears. On Yes the unlock dialog
            // runs and, if the path doesn't exist, KeePassVault.Open creates
            // a new .kdbx on the fly.
            var kpCfg = Services.KeePassVault.LoadConfig();
            if (string.IsNullOrEmpty(kpCfg.DbPath) && !File.Exists(AppPaths.KeePassOfferDonePath))
            {
                var offer = MessageBox.Show(
                    "Создать хранилище паролей (KeePass)?\n\n" +
                    "Логины и пароли для RDP/SSH будут храниться в зашифрованной базе .kdbx — " +
                    "вводить их придётся реже, а при открытой базе общий запрос пароля при старте " +
                    "показываться не будет.\n\n" +
                    "Если отказаться, предложение больше не появится.",
                    "Хранилище паролей (KeePass)",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);
                try { File.WriteAllText(AppPaths.KeePassOfferDonePath, "1"); } catch { }
                if (offer == DialogResult.Yes)
                {
                    var kpRes = Helpers.KeePassUnlockDialog.Show(null, kpCfg);
                    if (kpRes != null)
                    {
                        var kpErr = Services.KeePassVault.Open(kpRes.DbPath, kpRes.KeyFilePath, kpRes.Password);
                        if (kpErr == null)
                        {
                            kpCfg.DbPath = kpRes.DbPath;
                            kpCfg.KeyFilePath = kpRes.KeyFilePath;
                            kpCfg.AutoUnlockAtStartup = kpRes.AutoUnlockAtStartup;
                            Services.KeePassVault.SaveConfig(kpCfg);
                            Logger.Info("KeePass vault created from first-run offer");
                        }
                        else
                        {
                            Logger.Warn($"KeePass vault create failed: {kpErr}");
                        }
                    }
                }
            }
            else if (!string.IsNullOrEmpty(kpCfg.DbPath) && kpCfg.AutoUnlockAtStartup && !Services.KeePassVault.IsOpen)
            {
                var kpRes = Helpers.KeePassUnlockDialog.Show(null, kpCfg);
                if (kpRes != null)
                {
                    var kpErr = Services.KeePassVault.Open(kpRes.DbPath, kpRes.KeyFilePath, kpRes.Password);
                    if (kpErr == null)
                        Logger.Info("KeePass vault auto-unlocked at startup");
                    else
                        Logger.Warn($"KeePass vault auto-unlock failed: {kpErr}");
                }
            }

            // v3.7.1 — shared start-up RDP password prompt removed entirely.
            // Sessions without credentials stay pending in the sidebar; the
            // user clicks a tab and gets a per-computer picker that can save
            // to KeePass or use creds one-off. The "🔑 Сменить пароль" button
            // in RDP Manager's header still clears the in-memory cache.
            //
            // v3.0.1: RestoreSessions loads rdp_sessions.json +
            // custom_sessions.json and calls Show() at the end (replaces the
            // old direct .Show() call which left the sidebar empty).
            rdpForm.RestoreSessions();
        }

        // Build a popup with one row per active RDP Manager session and
        // show it under the cursor. Click → show RDP Manager + activate
        // that session.
        private void OnCtrlMiddleClick(object? sender, Point screenLoc)
        {
            var menu = new Helpers.ThemedContextMenu();
            menu.AddHeader("Активные сессии");
            var sessions = Forms.RdpManagerForm.IsOpen
                ? Forms.RdpManagerForm.Instance.GetActiveSessions()
                : new System.Collections.Generic.List<Forms.RdpManagerForm.SessionInfo>();

            if (sessions.Count == 0)
            {
                menu.AddItem("Нет активных сессий", "info", () => { });
            }
            else
            {
                foreach (var sess in sessions)
                {
                    var label = string.IsNullOrEmpty(sess.Ip)
                        ? sess.Computer
                        : $"{sess.Computer}   ({sess.Ip})";
                    var captured = sess.Computer;  // closure capture
                    menu.AddItem(label, "computer", () => ActivateRdpSession(captured));
                }
            }

            // menu.Show(Point) is UI-thread safe because the WH_MOUSE_LL
            // callback runs on the UI thread (it dispatches via our message pump).
            menu.Show(null, screenLoc);
        }

        private void ActivateRdpSession(string computer)
        {
            var form = Forms.RdpManagerForm.Instance;
            // Make sure the RDP Manager window is visible + in front.
            form.Show();
            if (form.WindowState == FormWindowState.Minimized)
                form.WindowState = FormWindowState.Normal;
            // v2.1.45 trick: TopMost flash forces the window forward even
            // when SetForegroundWindow would normally be blocked.
            form.TopMost = true;
            form.TopMost = false;
            form.ActivateSessionByComputer(computer);
            Logger.Info($"Quick-switch menu → activated session '{computer}'");
        }

        private void ShowMainForm()
        {
            var form = Forms.RdpManagerForm.Instance;
            form.Show();
            // v3.6.8 — only un-minimize a minimized window; a maximized one
            // must restore maximized (forcing Normal here "shrunk" the window
            // after a tray restore instead of filling the screen).
            if (form.WindowState == FormWindowState.Minimized)
                form.WindowState = FormWindowState.Normal;
            form.Activate();
        }

        // v3.7.0 — tray "Пароли…" opens the KeePass vault manager. Its
        // Shown handler runs the unlock dialog if the vault isn't open yet.
        private void ShowVaultForm()
        {
            var form = Forms.KeePassVaultForm.Instance;
            form.Show();
            form.Activate();
        }

        private void ExitApplication()
        {
            _realExit = true;
            _notifyIcon.Visible = false;
            ExitThread();
        }

        // Make sure the tray icon is removed even if ExitThread isn't called
        // (e.g. unhandled exception, kill from Task Manager).
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _mouseHook?.Dispose(); } catch { }
                try { _notifyIcon.Visible = false; _notifyIcon.Dispose(); } catch { }
                try { _trayMenu.Dispose(); } catch { }
                // v3.0: don't dispose RdpManagerForm.Instance — Singleton,
                // lives for the whole process. Process exit cleans it up.
            }
            base.Dispose(disposing);
        }
    }
}
