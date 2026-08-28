using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace ShadowRDP.Controls
{
    // v3.6.8 — Embeds a launched application's top-level window inside this
    // Panel via Win32 SetParent, same trick the legacy mstsc-based RdpClient
    // used. Powered by the RDP Manager "Встроить ММС" button (MMC consoles
    // only): the process runs as an embedded session (no sidebar tab), with a
    // slim title bar on top showing its name and a ✕ to close it.
    //
    // Flow: Start() launches the process and polls (~400ms) for its main
    // window (Process.MainWindowHandle first, otherwise the largest visible
    // top-level window of the PID). Once found, EmbedWindow() strips the
    // chrome, reparents it into this control and clamps it to ClientSize
    // (minus the title bar). A 200ms resize guard handles apps that fight
    // SetWindowPos. Process exit is detected by the same poll timer and
    // surfaced via ProcessExited.
    public class EmbeddedProgramControl : Panel
    {
        private readonly string _exePath;
        private readonly string _arguments;
        private string _displayName;
        private readonly Panel _titleBar;
        private readonly Label _nameLbl;
        private readonly Label _closeBtn;
        private readonly Label _statusLbl;

        private Process? _process;
        private IntPtr _window = IntPtr.Zero;
        private bool _embedded;
        private bool _closing;
        private System.Windows.Forms.Timer? _pollTimer;
        private System.Windows.Forms.Timer? _resizeTimer;
        private int _pollAttempts;
        private bool _exitedRaised;
        // Last applied size — RefreshSize() skips SetWindowPos when nothing
        // changed, so the steady state doesn't hammer the embedded window
        // with forced repaints (that caused the "old TV" flicker).
        private int _lastW = -1;
        private int _lastH = -1;
        private int _resizeTicks;
        private int _reembedMisses;
        private System.Windows.Forms.Timer? _closeTimer;
        private int _closeTicks;
        private string _lastTitle = "";

        // ~20s of 400ms ticks before giving up on finding a window.
        private const int EmbedTimeoutTicks = 50;
        // ~3.2s (8 × 400ms) before a dead window with a live process is
        // treated as a crash/minimize — long enough for apps that recreate
        // their main window on theme/DPI/fullscreen switches.
        private const int ReembedGraceTicks = 8;
        // ~3s (10 × 300ms) for the app to exit after WM_CLOSE, then kill.
        private const int CloseTimeoutTicks = 10;

        public event EventHandler? ProcessExited;
        public event EventHandler? EmbedFailed;
        // v3.6.8 — raised when the embedded window's title changes (polled on
        // the same 400ms timer). Lets the host rename the tab to match the
        // program, e.g. an MMC console whose title reflects its snap-in.
        public event EventHandler<string>? WindowTitleChanged;

        public bool IsEmbedded => _embedded;
        public string DisplayName => _displayName;
        public bool HasExited => _process == null || _closing || _process.HasExited;

        // === Win32 ===
        [DllImport("user32.dll")] private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int n);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int n, int v);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool r);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr h, IntPtr clip, IntPtr upd, uint flags);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lparam);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] private static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, ref RECT r);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint msg, IntPtr wParam, IntPtr lParam);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;
        private const int WS_CAPTION = 0x00C00000;
        private const int WS_THICKFRAME = 0x00040000;
        private const int WS_MINIMIZEBOX = 0x00020000;
        private const int WS_MAXIMIZEBOX = 0x00010000;
        private const int WS_SYSMENU = 0x00080000;
        private const int WS_CHILD = 0x40000000;
        private const int WS_VISIBLE = 0x10000000;
        private const int WS_CLIPSIBLINGS = 0x04000000;
        private const int WS_POPUP = unchecked((int)0x80000000);
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int SW_SHOW = 5;
        private const int SW_RESTORE = 9;
        private const int SWP_NOZORDER = 0x0004;
        private const int SWP_NOACTIVATE = 0x0010;
        private const int SWP_FRAMECHANGED = 0x0020;
        private const int SWP_NOCOPYBITS = 0x0100;
        private const uint RDW_INVALIDATE = 0x0001;
        private const uint RDW_ALLCHILDREN = 0x0080;
        private const uint RDW_UPDATENOW = 0x0100;
        private const uint WM_SIZE = 0x0005;
        private const int WM_CLOSE = 0x0010;
        private const int SIZE_RESTORED = 0;

        public EmbeddedProgramControl(string exePath, string arguments, string displayName)
        {
            _exePath = exePath;
            _arguments = arguments ?? "";
            _displayName = displayName;

            BackColor = Color.Black;
            Dock = DockStyle.Fill;
            Padding = Padding.Empty;

            _titleBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 24,
                BackColor = ShadowRDP.Helpers.UiTheme.Sidebar
            };
            _nameLbl = new Label
            {
                Text = displayName,
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI", 9F),
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                Padding = new Padding(8, 0, 0, 0)
            };
            _closeBtn = new Label
            {
                Text = "✕",
                Dock = DockStyle.Right,
                Width = 28,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 9F),
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand
            };
            _closeBtn.MouseEnter += (s, e) => _closeBtn.BackColor = Color.FromArgb(0xC0, 0x39, 0x2B);
            _closeBtn.MouseLeave += (s, e) => _closeBtn.BackColor = Color.Transparent;
            _closeBtn.Click += (s, e) => Close();

            _titleBar.Controls.Add(_nameLbl);   // Fill — add first
            _titleBar.Controls.Add(_closeBtn);  // Right — add second
            Controls.Add(_titleBar);            // Top — docks above the fill

            _statusLbl = new Label
            {
                Text = $"Запуск {displayName}…",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 11F),
                ForeColor = Color.FromArgb(0xCC, 0xCC, 0xCC),
                BackColor = Color.FromArgb(0x22, 0x27, 0x2E),
                Visible = false
            };
            Controls.Add(_statusLbl);

            Resize += (s, e) => RefreshSize();
        }

        // Launches the process and starts looking for its main window.
        public void Start()
        {
            if (_process != null) return;
            try
            {
                var psi = new ProcessStartInfo(_exePath, _arguments)
                {
                    UseShellExecute = false,
                    WorkingDirectory = System.IO.Path.GetDirectoryName(_exePath) ?? ""
                };
                _process = Process.Start(psi);
                if (_process == null)
                {
                    Logger.Warn($"Embed: failed to start '{_exePath}' (null process)");
                    RaiseEmbedFailed();
                    return;
                }
                _process.EnableRaisingEvents = true;
                Logger.Info($"Embed: started '{_displayName}' PID={_process.Id}");

                _statusLbl.Visible = true;
                _pollAttempts = 0;
                _exitedRaised = false;
                _pollTimer = new System.Windows.Forms.Timer { Interval = 400 };
                _pollTimer.Tick += OnPollTick;
                _pollTimer.Start();
            }
            catch (Exception ex)
            {
                Logger.Warn($"Embed: failed to start '{_exePath}': {ex.Message}");
                RaiseEmbedFailed();
            }
        }

        private void OnPollTick(object? sender, EventArgs e)
        {
            if (_process == null) return;

            // 1) Exit detection — always active (also ends the graceful-close
            // wait after WM_CLOSE: RaiseProcessExited is idempotent).
            if (_process.HasExited)
            {
                StopTimers();
                Logger.Info($"Embed: '{_displayName}' exited, code={ExitCodeSafe()}");
                RaiseProcessExited();
                return;
            }

            // 2) Closing — the _closeTimer owns the fallback force-kill.
            if (_closing) return;

            // 3) Embedded already — the window may have been recreated by the
            // app (theme/DPI/fullscreen switch) or hidden to the tray.
            if (_embedded)
            {
                CheckTitle();
                if (_window == IntPtr.Zero || !IsWindow(_window))
                {
                    // Try to re-embed a fresh window before declaring it dead.
                    var hwnd = FindMainWindow(_process.Id);
                    if (hwnd != IntPtr.Zero)
                    {
                        _reembedMisses = 0;
                        EmbedWindow(hwnd);
                        return;
                    }
                    if (++_reembedMisses >= ReembedGraceTicks)
                    {
                        StopTimers();
                        Logger.Warn($"Embed: '{_displayName}' window gone, no replacement — closing");
                        RaiseProcessExited();
                    }
                    return;
                }
                return;
            }

            // 4) Still looking for the main window.
            var mainHwnd = FindMainWindow(_process.Id);
            if (mainHwnd != IntPtr.Zero)
            {
                _pollTimer?.Stop();
                EmbedWindow(mainHwnd);
                return;
            }

            if (++_pollAttempts >= EmbedTimeoutTicks)
            {
                StopTimers();
                Logger.Warn($"Embed: no window found for '{_displayName}' after ~20s — killing process");
                KillProcess();
                RaiseEmbedFailed();
            }
        }

        // v3.6.8 — polls the embedded window's title and raises
        // WindowTitleChanged when it differs from the last seen value.
        // No-op until a window is embedded (title before embedding is empty).
        private void CheckTitle()
        {
            if (_window == IntPtr.Zero) return;
            var sb = new StringBuilder(512);
            GetWindowText(_window, sb, 512);
            string title = sb.ToString();
            if (title != _lastTitle)
            {
                _lastTitle = title;
                WindowTitleChanged?.Invoke(this, title);
            }
        }

        // v3.6.8 — updates the name shown on this control's title bar (and
        // used in log lines), e.g. after the host renames an MMC console to
        // match its snap-in.
        public void SetDisplayName(string name)
        {
            _displayName = name;
            _nameLbl.Text = name;
        }

        private void EmbedWindow(IntPtr hwnd)
        {
            _window = hwnd;
            CheckTitle();
            try
            {
                if (!IsHandleCreated) CreateControl();
                if (!Visible) Visible = true;

                int style = GetWindowLong(hwnd, GWL_STYLE);
                style &= ~(WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU);
                style &= ~WS_POPUP;
                style |= WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS;
                SetWindowLong(hwnd, GWL_STYLE, style);

                SetParent(hwnd, Handle);
                _lastW = -1;
                _lastH = -1;
                RefreshSize();

                ShowWindow(hwnd, SW_SHOW);
                RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ALLCHILDREN | RDW_UPDATENOW);
                RedrawWindow(Handle, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ALLCHILDREN | RDW_UPDATENOW);

                // Keep the title bar above the reparented window.
                _titleBar.BringToFront();

                _embedded = true;
                _statusLbl.Visible = false;

                // Short-lived resize guard (~3s) for apps that fight
                // SetWindowPos right after reparenting. Afterwards the
                // Resize event alone keeps the window in sync.
                _resizeTimer = new System.Windows.Forms.Timer { Interval = 200 };
                _resizeTimer.Tick += (s2, e2) =>
                {
                    if (++_resizeTicks > 15)
                    {
                        _resizeTimer.Stop();
                        _resizeTimer.Dispose();
                        _resizeTimer = null;
                        return;
                    }
                    RefreshSize();
                };
                _resizeTimer.Start();

                Logger.Info($"Embed: '{_displayName}' embedded (hwnd=0x{hwnd.ToInt64():X}, parent=0x{Handle.ToInt64():X})");
            }
            catch (Exception ex)
            {
                Logger.Warn($"Embed: EmbedWindow failed for '{_displayName}': {ex.Message}");
                KillProcess();
                RaiseEmbedFailed();
            }
        }

        // Keeps the embedded window clamped to the area below the title bar.
        // No-op when the size didn't change — prevents the flicker that
        // came from force-resizing (and repainting) every 200ms.
        public void RefreshSize()
        {
            if (_window == IntPtr.Zero || !_embedded) return;
            int w = ClientSize.Width;
            int h = ClientSize.Height - _titleBar.Height;
            if (w <= 0 || h <= 0) return;
            if (w == _lastW && h == _lastH) return;
            _lastW = w;
            _lastH = h;
            SetWindowPos(_window, IntPtr.Zero, 0, _titleBar.Height, w, h,
                SWP_NOZORDER | SWP_NOACTIVATE);
            PostMessage(_window, WM_SIZE, (IntPtr)SIZE_RESTORED, (IntPtr)((h << 16) | (w & 0xFFFF)));
        }

        // Brings a minimized/hidden embedded window back, e.g. when the user
        // clicks its pill in the header or restores it from the tray.
        // SW_RESTORE also reactivates it.
        public void RestoreWindow()
        {
            if (_window == IntPtr.Zero) return;
            ShowWindow(_window, SW_RESTORE);
            // v3.6.8 — SW_RESTORE brings the window back at ITS OWN saved
            // (pre-minimize) size, but RefreshSize skips SetWindowPos when the
            // panel target size didn't change (anti-flicker guard). Resetting
            // the cached size forces the re-clamp to the panel area, so the
            // window fills the host again and gets a correct WM_SIZE to lay
            // itself out.
            _lastW = -1;
            _lastH = -1;
            RefreshSize();
            RedrawWindow(_window, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ALLCHILDREN | RDW_UPDATENOW);
        }

        // Finds the best candidate main window for the given PID.
        // Prefers Process.MainWindowHandle, else the largest visible
        // top-level window (skipping dialogs/tool windows/consoles).
        private static IntPtr FindMainWindow(int processId)
        {
            IntPtr best = IntPtr.Zero;
            long bestArea = 0;
            EnumWindows((h, _) =>
            {
                GetWindowThreadProcessId(h, out uint pid);
                if (pid != processId) return true;
                if (!IsWindowVisible(h)) return true;

                int exStyle = GetWindowLong(h, GWL_EXSTYLE);
                if ((exStyle & WS_EX_TOOLWINDOW) != 0) return true;

                var cls = new StringBuilder(256);
                GetClassName(h, cls, 256);
                string c = cls.ToString();
                if (c == "#32770") return true;              // dialog
                if (c == "ConsoleWindowClass") return true;  // console

                var r = new RECT();
                GetWindowRect(h, ref r);
                long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
                if (area > bestArea) { bestArea = area; best = h; }
                return true;
            }, IntPtr.Zero);
            return best;
        }

        private static IntPtr FindMainWindow(Process p)
        {
            try
            {
                var mh = p.MainWindowHandle;
                if (mh != IntPtr.Zero && mh != new IntPtr(-1) && IsWindowVisible(mh)) return mh;
            }
            catch { }
            return FindMainWindow((int)p.Id);
        }

        // Closes the embedded process gracefully: posts WM_CLOSE so the app
        // can save its state, then force-kills only if it ignores the message
        // for ~3s. The poll timer detects the actual exit and raises
        // ProcessExited.
        public void Close()
        {
            if (_closing) return;
            _closing = true;
            if (_window != IntPtr.Zero)
            {
                PostMessage(_window, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                Logger.Info($"Embed: '{_displayName}' — WM_CLOSE sent, waiting up to ~3s");
            }
            _closeTicks = 0;
            _closeTimer = new System.Windows.Forms.Timer { Interval = 300 };
            _closeTimer.Tick += (s2, e2) =>
            {
                if (_process == null || _process.HasExited)
                {
                    StopCloseTimer();
                    RaiseProcessExited();
                    return;
                }
                if (++_closeTicks >= CloseTimeoutTicks)
                {
                    StopCloseTimer();
                    Logger.Warn($"Embed: '{_displayName}' ignored WM_CLOSE — force kill");
                    KillProcess();
                    RaiseProcessExited();
                }
            };
            _closeTimer.Start();
        }

        // Hard-kill without events. Used by FormClosing and by the form's
        // RemoveEmbeddedProgram (which prefers to do its own bookkeeping).
        public void KillProcess()
        {
            StopTimers();
            if (_process != null)
            {
                try
                {
                    if (!_process.HasExited) _process.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Embed: kill failed for '{_displayName}': {ex.Message}");
                }
            }
            _window = IntPtr.Zero;
        }

        private void StopTimers()
        {
            if (_pollTimer != null) { _pollTimer.Stop(); _pollTimer.Dispose(); _pollTimer = null; }
            if (_resizeTimer != null) { _resizeTimer.Stop(); _resizeTimer.Dispose(); _resizeTimer = null; }
            StopCloseTimer();
        }

        private void StopCloseTimer()
        {
            if (_closeTimer != null) { _closeTimer.Stop(); _closeTimer.Dispose(); _closeTimer = null; }
        }

        private int ExitCodeSafe()
        {
            try { return _process?.ExitCode ?? 0; } catch { return 0; }
        }

        private void RaiseProcessExited()
        {
            if (_exitedRaised) return;
            _exitedRaised = true;
            ProcessExited?.Invoke(this, EventArgs.Empty);
        }

        private void RaiseEmbedFailed()
        {
            if (_exitedRaised) return;
            _exitedRaised = true;
            EmbedFailed?.Invoke(this, EventArgs.Empty);
        }
    }
}
