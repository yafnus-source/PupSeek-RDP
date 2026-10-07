using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using ShadowRDP.Controls;
using ShadowRDP.Services;

namespace ShadowRDP.Forms
{
    // RDP Manager — tabbed multi-session RDP client. Figma redesign.
    // Layout: Header (title + status) | Sidebar (server list) | Main (RDP area).
    //
    // CRITICAL: WinForms Dock.Fill controls MUST be added FIRST so that
    // Dock.Top/Left controls reserve space on top. Reversed order = Fill
    // covers everything = invisible controls (was the bug in v1).
    public class RdpManagerForm : Form
    {
        private static RdpManagerForm? _instance;
        public static RdpManagerForm Instance => _instance ??= new RdpManagerForm();
        public static bool IsOpen => _instance != null && !_instance.IsDisposed;

        // v2.3.9: dark-blue sidebar theme.
        //   SidebarBg          = main panel bg (#1E3A5F)
        //   SidebarItemHover   = lighter shade for inactive-session hover
        //   SidebarGroupBg     = darker band for group headers (was HeaderBg)
        //   SidebarSubFg       = light blue-grey for secondary text (IP / target)
        // v2.3.10: SidebarGroupBg darkened further; GroupTitleFg = muted grey
        // (less harsh than pure white on the dark band).
        // v3.8.2: routed through UiTheme so the Olive theme re-themes the sidebar.
        private static Color SidebarBg => Helpers.UiTheme.Sidebar;
        private static Color SidebarItemHover => Helpers.UiTheme.SidebarItemHover;
        private static Color SidebarGroupBg => Helpers.UiTheme.SidebarGroupBg;
        private static Color SidebarSubFg => Helpers.UiTheme.SidebarSubFg;
        private static readonly Color GroupTitleFg = Color.FromArgb(0xA0, 0xA8, 0xB5);
        // v3.8.x — collapsed group header: arrow + title switch to a warm
        // highlight (#F8E5BC) so collapsed groups stand out from expanded ones.
        private static readonly Color GroupTitleCollapsedFg = Color.FromArgb(0xF8, 0xE5, 0xBC);

        // Native 16×16 icons for the CMD / PS buttons, cached on first use.
        private static Bitmap? _cmdIcon;
        private static Bitmap? _psIcon;
        private static Bitmap? ExtractExeIcon(string exePath)
        {
            try
            {
                if (!System.IO.File.Exists(exePath)) return null;
                using var icon = Icon.ExtractAssociatedIcon(exePath);
                if (icon == null) return null;
                using var small = new Icon(icon, 16, 16);
                return small.ToBitmap();
            }
            catch { return null; }
        }
        private static Bitmap? CmdIcon => _cmdIcon ??= ExtractExeIcon(System.IO.Path.Combine(Environment.SystemDirectory, "cmd.exe"));
        private static Bitmap? PsIcon => _psIcon ??= ExtractExeIcon(System.IO.Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"));

        // v3.6.8 — cached variant of ExtractExeIcon for embedded consoles.
        private Bitmap? GetEmbedIcon(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return null;
            if (_embedIconCache.TryGetValue(exePath, out var cached)) return cached;
            var icon = ExtractExeIcon(exePath);
            if (icon != null) _embedIconCache[exePath] = icon;
            return icon;
        }

        private readonly Panel _mainArea;
        private readonly FlowLayoutPanel _sidebarList;
        private readonly List<RdpSession> _sessions = new();
        // v2.1.83 — non-RDP session shortcuts (SSH/Web). Persisted separately.
        private readonly List<Models.CustomSession> _customSessions = new();
        // Lazily-created placeholder panels for each custom session. Cleared
        // when the session is deleted or the form closes.
        private readonly Dictionary<string, Panel> _customPlaceholders = new();
        // v3.7.2 — singleton Web cred popup state. Only one popup alive at a
        // time per RDP Manager window; opening a new one for a different host
        // closes the previous. Set/cleared in ShowWebCredPopupCore.
        private Form? _webCredPopupForm;
        private string? _webCredPopupHost;
        // v3.7.2 — per-WebView2 set of hosts the user has dismissed (either
        // by clicking ✕ or by accepting a fill). Subsequent loginFocus events
        // for a dismissed host are silently ignored until the WebView2 is
        // disposed. Cleared implicitly when the placeholder panel (owning the
        // WebView2) is disposed — but we don't bother wiring that, the leak
        // is one HashSet<string> per session, bounded.
        private readonly Dictionary<Microsoft.Web.WebView2.WinForms.WebView2, HashSet<string>> _webPopupDismissed = new();
        // Active custom session's Id, or null if no custom session is active
        // (e.g. an RDP session is active or nothing is). Drives sidebar
        // highlight + which placeholder panel is visible.
        private string? _activeCustomSessionId;
        // v2.1.96 — in-memory SSH creds cache. Keyed by CustomSession.Id.
        // Survives across placeholder re-creates within a single app run
        // so that "Перезапуск сессии" doesn't re-prompt. Cleared when the
        // user picks "Зайти под другой учётной записью" or when the app
        // exits (it's just a Dictionary, not persisted).
        private readonly Dictionary<string, (string User, string Password)> _sshCredsCache = new();
        // v3.7.0 — session ids where the user explicitly asked for a fresh
        // SSH credential prompt ("Зайти под другой учётной записью"). While
        // present, KeePass auto-fill is skipped so the prompt always shows;
        // the flag is consumed on the next resolution.
        private readonly HashSet<string> _sshForcePrompt = new();
        // v2.1.49 — which groups the user has collapsed (click header toggles).
        private readonly HashSet<string> _collapsedGroups = new();

        // v3.5.4 — embedded console sessions (PS / CMD). Not shown in sidebar,
        // persist across tab switches, reused when the same button is clicked again.
        private Controls.ConsoleSessionControl? _psSession;
        private Controls.ConsoleSessionControl? _cmdSession;

        // v3.6.8 — embedded MMC consoles ("Встроить ММС"). Each runs in its
        // own EmbeddedProgramControl inside _mainArea (no sidebar tab) and
        // gets a pill-button in the header (right of the button). Only MMC
        // consoles are embedded (arbitrary programs are not).
        private sealed class EmbeddedProgramSession
        {
            public Models.EmbeddedAppEntry Entry = null!;
            public Controls.EmbeddedProgramControl Control = null!;
            public Label PillButton = null!;
        }

        private readonly List<EmbeddedProgramSession> _embeddedSessions = new();
        private readonly Dictionary<Controls.ConsoleSessionControl, Label> _consolePills = new();
        private EmbeddedProgramSession? _activeEmbedded;
        private Label _toolsTab = null!;
        private Panel _headerPanel = null!;
        private ToolTip _toolTip = null!;
        private string? _lastEmbedDir;
        // v3.6.8 — cached 16×16 icons of embedded consoles (key: .msc path),
        // shared by the header pill buttons and the recent-console menu.
        private readonly Dictionary<string, Bitmap> _embedIconCache = new();

        // v2.2.25: sidebar scroll fields.
        private Panel _sidebarScrollHost = null!;
        private int _sidebarScrollOffset;
        // Routes the mouse wheel to whatever is under the cursor (sidebar
        // host or any DataGridView) instead of the focused control.
        private Helpers.WheelRouter _wheelRouter = null!;

        // v2.2.28: in-memory cache for RDP per-session creds. Keyed by
        // Computer name. Survives RdpManagerForm close/reopen. NOT persisted.
        private readonly Dictionary<string, (string Login, string Password)> _rdpPerSessionCreds = new();

        // v2.2.22: Win32 scrollbar control for sidebar wheel-scrolling.
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowScrollBar(IntPtr hWnd, int wBar, bool bShow);
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, uint wParam, int lParam);
        private const int SB_VERT = 1;
        private const uint WM_VSCROLL = 0x0115;

        // Sidebar tabs — Книга embeds AddressBookForm; Сессии is the
        // default RDP-session view.
        private Label _bookTab = null!;
        private Label _passwordsTab = null!;
        private Label _zabbixAgentTab = null!;
        // v4.7 — вкладки модулей (бывшая жёсткая TODO.шная): кнопки и хосты
        // строятся динамически из включённых модулей (ModuleManager).
        private Panel _modulesPanel = null!;
        private readonly System.Collections.Generic.Dictionary<string, Label> _moduleTabButtons = new(System.StringComparer.OrdinalIgnoreCase);
        private readonly System.Collections.Generic.Dictionary<string, Panel> _moduleHosts = new(System.StringComparer.OrdinalIgnoreCase);
        private readonly System.Collections.Generic.Dictionary<string, Control> _moduleForms = new(System.StringComparer.OrdinalIgnoreCase);
        private string? _activeModuleId;
        private Panel _bookHost = null!;
        private Panel _dashHost = null!;
        private AddressBookForm? _bookForm;
        private Forms.ZabbixAgentDashboardForm? _dashForm;
        private SidebarTab _activeTab = SidebarTab.Sessions;

        internal enum SidebarTab { Sessions, Book, Dashboard }

        // v3.9.3K — SHA256 запущенного exe (первые 8). Синхронно: 78МБ
        // читаются ~0.3-1с, зато хеш в заголовке с первой секунды.
        // Пути-кандидаты по надёжности; чтение с FileShare.ReadWrite,
        // чтобы не конфликтовать с образом в памяти. Неудача — в лог.
        internal static string ComputeOwnHash()
        {
            string asmLoc = "";
            try { asmLoc = System.Reflection.Assembly.GetExecutingAssembly().Location ?? ""; } catch { }
            string[] candidates = new string[]
            {
                System.Environment.ProcessPath ?? "",
                Application.ExecutablePath ?? "",
                asmLoc
            };
            foreach (var path in candidates)
            {
                try
                {
                    if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) continue;
                    using var sha = System.Security.Cryptography.SHA256.Create();
                    using var fs = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete);
                    var h = sha.ComputeHash(fs);
                    return BitConverter.ToString(h).Replace("-", "").Substring(0, 8);
                }
                catch (Exception ex)
                {
                    try { Logger.Warn("BuildHash: " + path + ": " + ex.GetType().Name + ": " + ex.Message); } catch { }
                }
            }
            try { Logger.Warn("BuildHash: все кандидаты недоступны"); } catch { }
            return "????????";
        }
        // v4.8 — единый дашборд: шимы открывают его сразу на нужной вьюхе.
        internal void ShowDashboard() => OpenDash(Forms.ZabbixAgentDashboardForm.DashView.Events);
        internal void ShowZabbixAgent() => OpenDash(Forms.ZabbixAgentDashboardForm.DashView.Monitor);
        // v4.7 — навигация на вкладку модуля по id ("todo").
        internal void ShowModule(string id) => SwitchToModule(id);

        private RdpManagerForm()
        {
            // v3.0: RDP Manager is now the main window. Show admin status in
            // the title bar (used to live in MainForm). Icon uses StatusIcon
            // (red = admin, green = unprivileged) for the same visual cue.
            var isAdmin = Services.AdminElevation.IsAdministrator();
            // v3.9.3K — хеш сборки СИНХРОННО до показа окна (асинхронный
            // вариант терялся молча: пользователь никогда не видел хеш).
            string buildHash = ComputeOwnHash();
            Text = $"Pup Seek RDP — Менеджер   [{(isAdmin ? "Администратор" : "без прав")}]   [{buildHash}]";
            ClientSize = new Size(1200, 800);
            MinimumSize = new Size(900, 500);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            Font = new Font("Segoe UI", 9F);
            BackColor = Helpers.UiTheme.Background;
            try { Icon = Services.StatusIcon.Create(isAdmin); }
            catch (Exception ex) { Logger.Warn($"StatusIcon failed: {ex.Message}"); }

            // v4.9.4 — любое изменение TODO (голосовой ввод, автосинк
            // календаря, ручные правки) обновляет бейдж-счётчик в сайдбаре.
            try { Services.TodoStore.DataChanged += OnTodoDataChanged; } catch { }

            // v3.0.1: persist window size. FormClosed won't fire (X-button
            // is intercepted to hide-to-tray), so we live-save on Resize/Move
            // debounced. Restore on construction via Register.
            Services.WindowStateStore.Register(this, "rdpManagerForm");
            System.Windows.Forms.Timer? sizeSaveTimer = null;
            EventHandler scheduleSizeSave = (s, e) =>
            {
                sizeSaveTimer ??= new System.Windows.Forms.Timer { Interval = 500 };
                sizeSaveTimer.Stop();
                sizeSaveTimer.Tick += (ts, te) =>
                {
                    sizeSaveTimer!.Stop();
                    Services.WindowStateStore.Save(this, "rdpManagerForm");
                };
                sizeSaveTimer.Start();
            };
            Resize += scheduleSizeSave;
            Move += scheduleSizeSave;

            // v3.6.8 — on every (re)show AFTER the first one, e.g. returning
            // from the tray, re-render the sidebar and re-run layout so the
            // session list and header are never left empty or mislaid out after
            // the window was hidden. The FIRST show is skipped: the queued
            // RenderSidebar/PerformLayout would reposition the "Встроить
            // ММС" button right after startup and instantly close its
            // just-opened menu (that was why the button only worked from the
            // second click). RestoreSessions already renders the sidebar.
            bool shownOnce = false;
            VisibleChanged += (s, e) =>
            {
                if (!Visible) return;
                if (!shownOnce) { shownOnce = true; return; }
                BeginInvoke(new Action(() =>
                {
                    RenderSidebar();
                    PerformLayout();
                    Invalidate(true);
                }));
            };

            // === Body container (Fill) — holds sidebar + main ===
            var body = new Panel { Dock = DockStyle.Fill, BackColor = Helpers.UiTheme.Background };

            // Add in REVERSE dock order: Fill first, then Left items.
            // _mainArea (Dock.Fill) goes FIRST so sidebar reserves space.
            _mainArea = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black
            };

            // Book host — sits on top of _mainArea, holds the embedded
            // AddressBookForm when the Книга tab is active. Hidden until
            // the user clicks Книга (lazy-creates the form on first show).
            _bookHost = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Helpers.UiTheme.Background,
                Visible = false  // Sessions view is the default
            };
            _mainArea.Controls.Add(_bookHost);
            _dashHost = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Helpers.UiTheme.Background,
                Visible = false
            };
            _mainArea.Controls.Add(_dashHost);

            // v4.7 — секция вкладок модулей (Dock=Top, высота по контенту):
            // кнопки строятся из включённых модулей (бывшая TODO.шная).

            // v3.0.5: sidebar separator removed — main area now directly
            // touches the sidebar. Was a 1px Dock=Left panel (UiTheme.Border).

            // Sidebar (Dock.Left, 166px).
            // v2.3.9: sidebar bg = #1E3A5F (was UiTheme.Muted). Text inverts
            // to white — see AddSessionItem / AddCustomSessionItem /
            // AddGroupHeader for per-row colours.
            var sidebarBg = SidebarBg;
            var sidebar = new Panel
            {
                Dock = DockStyle.Left,
                Width = 166,
                BackColor = sidebarBg
            };

            // --- Sidebar internal layout ---
            // Order from top: bookTab (new) | sessionsTab | _sidebarList (Fill).
            // WinForms docks in reverse z-order — last added = topmost.
            // v2.1.51: AutoScroll disabled — with AutoScroll=true the
            // v2.2.25: sidebar scroll via Panel-wrapper + manual Y-shift.
            // No AutoScroll, no ShowScrollBar, no flicker, no DoubleClick break.
            //
            // Structure:
            //   sidebar (Panel, Dock=Left 166px)
            //     └─ _sidebarScrollHost (Panel, Dock=Fill, clips overflow)
            //          └─ _sidebarList (FlowLayoutPanel, AutoSize=true,
            //              Location.Y = -scrollOffset)
            //
            // Mouse wheel → adjust _sidebarScrollOffset → shift _sidebarList.Y.
            _sidebarScrollHost = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = sidebarBg,
                AutoScroll = false
            };

            _sidebarList = new FlowLayoutPanel
            {
                // NOT Dock=Fill — Location is controlled manually for scroll.
                Location = Point.Empty,
                Width = 166,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = false,
                BackColor = sidebarBg,
                Padding = new Padding(0, 8, 0, 8)
            };
            _sidebarScrollHost.Controls.Add(_sidebarList);
            // v3.8.x — insertion-slot line drawn during drag&drop reorder.
            _sidebarList.Paint += (s, e) => DrawDropIndicator(e);
            sidebar.Controls.Add(_sidebarScrollHost);

            // Mouse wheel → shift _sidebarList up/down. Hook on the scroll
            // host so wheel events from children (session items) bubble up.
            _sidebarScrollHost.MouseWheel += OnSidebarWheel;
            // v3.7.x — global wheel router: the direct hook above only fires
            // when focus is inside the sidebar (it never is — session items
            // are non-focusable Panels). The router intercepts WM_MOUSEWHEEL
            // by cursor position instead, so hover-scrolling works for the
            // sidebar AND every DataGridView in the app.
            _wheelRouter = new Helpers.WheelRouter(_sidebarScrollHost, ScrollSidebar);
            Application.AddMessageFilter(_wheelRouter);
            FormClosed += (s, e) =>
            {
                if (_wheelRouter != null)
                    Application.RemoveMessageFilter(_wheelRouter);
            };
            // v4.9.34 — меню на пустом месте сайдбара убрано: "+ Сессия…"
// живёт пунктами в контекстных меню самих сессий.
            // v2.2.25: _sidebarList is now inside _sidebarScrollHost (added
            // in the constructor above). No direct add to sidebar.

            // v3.7.1 — Sidebar button replaced: was "Сессии" (passive —
            // sessions list is always visible by default), now "Пароли" —
            // opens the KeePass vault manager form (same UI as from tray).
            // Sessions are still visible in the sidebar as tabs; this button
            // is a shortcut, not a tab toggle.
            // v4.8 — единый Дашборд (внутри пил Events/Monitor/Web) — на месте Z.
            // v3.9.3CC — синяя тема: тёмно-синий вместо бордо.
            bool isBlue = ShadowRDP.Helpers.UiTheme.CurrentName != "Olive";
            _zabbixAgentTab = Helpers.UiButtons.MakeFilled("Дашборд", "dashboard",
                isBlue ? Color.FromArgb(0x24, 0x46, 0x6B) : Color.FromArgb(0x48, 0x30, 0x2F), width: 166, radius: 0);
            _zabbixAgentTab.Dock = DockStyle.Top;
            _zabbixAgentTab.Height = 36;
            _zabbixAgentTab.Margin = Padding.Empty;
            sidebar.Controls.Add(_zabbixAgentTab);

            // v3.9 — "Инструменты" dropdown, placed in the sidebar BELOW
            // "Пароли" with the same filled nav-button look as Книга/Пароли.
            // Added BEFORE Пароли/Книга so with DockStyle.Top it ends up at
            // the bottom of the three (under Пароли).
            _toolsTab = Helpers.UiButtons.MakeFilled("Инструменты", "gear",
                isBlue ? Color.FromArgb(0x2E, 0x6D, 0xA4) : Color.FromArgb(0x56, 0x5C, 0x30), width: 166, radius: 0);
            _toolsTab.Dock = DockStyle.Top;
            _toolsTab.Height = 36;
            _toolsTab.Margin = Padding.Empty;
            sidebar.Controls.Add(_toolsTab);

            _passwordsTab = Helpers.UiButtons.MakeFilled("Пароли", "shield",
                ShadowRDP.Helpers.UiTheme.SidebarTab, width: 166, radius: 0);
            _passwordsTab.Dock = DockStyle.Top;
            _passwordsTab.Height = 36;
            _passwordsTab.Margin = Padding.Empty;
            sidebar.Controls.Add(_passwordsTab);  // Top second

            // v4.7 — контейнер кнопок модулей на месте бывшей TODO.шной
            // (в тон Книге, между Паролями и Книгой). Высота = 36px × N.
            _modulesPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 0,
                Margin = Padding.Empty,
                BackColor = sidebarBg,
                Visible = false
            };
            sidebar.Controls.Add(_modulesPanel);
            RebuildModuleTabs();
            try { Services.ModuleManager.StateChanged += OnModuleStateChanged; } catch { }

            // Book tab — sits ABOVE СЕССИИ. Styled like the RDP Manager
            // button: filled #296AA0, white text + white icon. Click →
            // embed AddressBookForm into _bookHost and switch main area.
            _bookTab = Helpers.UiButtons.MakeFilled("Книга", "list", ShadowRDP.Helpers.UiTheme.SidebarTab, width: 166, radius: 0);
            _bookTab.Dock = DockStyle.Top;
            _bookTab.Height = 36;
            _bookTab.Margin = Padding.Empty;
            // MakeFilled builds a Label with Paint handler — keep its
            // internals, just wrap click + adjust appearance for active
            // vs inactive state.
            sidebar.Controls.Add(_bookTab);  // Top last → becomes topmost

            // Logo at the bottom of the sidebar — branding. v2.1.41:
            // program name label below the logo (within the same
            // container Panel).
            var logo = Helpers.AppLogo.Load();
            if (logo != null)
            {
                const int logoSize = 40;
                var container = new Panel
                {
                    Dock = DockStyle.Bottom,
                    Height = 80,
                    BackColor = sidebarBg,
                    Margin = Padding.Empty
                };
                var logoBox = new PictureBox
                {
                    Image = logo,
                    SizeMode = PictureBoxSizeMode.Zoom,
                    Bounds = new Rectangle((sidebar.Width - logoSize) / 2, 10, logoSize, logoSize),
                    BackColor = sidebarBg,
                    Margin = Padding.Empty
                };
                var nameLabel = new Label
                {
                    Text = "Pup Seek RDP",
                    Bounds = new Rectangle(0, 52, sidebar.Width, 18),
                    Font = new Font("Segoe UI Semibold", 9F),
                    ForeColor = Color.White,
                    BackColor = sidebarBg,
                    TextAlign = ContentAlignment.MiddleCenter
                };
                container.Controls.Add(logoBox);
                container.Controls.Add(nameLabel);
                sidebar.Controls.Add(container);
            }

            // Click handlers — Book/Dashboard/ZabbixAgent switch the main area; Пароли opens
            _bookTab.Click += (s, e) => SwitchTab(SidebarTab.Book);
            _zabbixAgentTab.Click += (s, e) => SwitchTab(SidebarTab.Dashboard);
            _passwordsTab.Click += (s, e) => OpenPasswordVault();

            // Active-tab arrow — small left-pointing triangle at the
            // right edge of the Book tab when Book is active. Per-session
            // items paint their own arrow in their own Paint handler
            // (AddSidebarItem wires that up). The "СЕССИИ" header is just
            // a label, not a tab, so no arrow there.
            // v2.3.12: active-tab arrow removed per user request.
            _bookTab.Paint += (s, e) => { };

            // Now add body children in order: Fill, then Left.
            body.Controls.Add(_mainArea);     // Fill — docks LAST, fills remaining
            body.Controls.Add(sidebar);       // Left — docks before Fill

            // Header separator (Dock.Top, 1px).
            // v3.9.3CO — убран: светлая граница под областью шапки.
            // var headerSep = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Helpers.UiTheme.Border };

            // === Header (Dock.Top) ===
            // v2.3: 48 → 36 (−6 top + −6 bottom per user request). Button
            // heights scaled proportionally (30 → 22).
            var header = new Panel
            {
                Dock = DockStyle.Top,
                Height = 36,
                // v3.9.3CM — эксперимент: цвет области выбирается (как hero в Даше).
                BackColor = Helpers.UiTheme.RdpHeaderBg
            };

            // v2.1.48 — replaced the old programmatic monitor icon with
            // our actual logo (PictureBox 24×24) loaded from the embedded
            // resource. Falls back to a transparent panel if logo fails.
            var icon = new PictureBox
            {
                Location = new Point(20, 6),
                Size = new Size(24, 24),
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Transparent
            };
            var logoForIcon = Helpers.AppLogo.Load();
            if (logoForIcon != null) icon.Image = logoForIcon;
            header.Controls.Add(icon);

            var title = new Label
            {
                Text = "RDP Менеджер",
                Location = new Point(52, 10),
                AutoSize = true,
                Font = new Font("Segoe UI Semibold", 10F),
                ForeColor = Helpers.UiTheme.RdpHeaderFg,
                BackColor = Color.Transparent
            };
            header.Controls.Add(title);
            _hdrTitle = title;

            // Status badge.
            _statusBadge = new Label
            {
                Text = "",
                Tag = "Подключено",
                Location = new Point(170, 10),
                AutoSize = false,
                Size = new Size(110, 20),
                Font = new Font("Segoe UI", 8.5F),
                ForeColor = Helpers.UiTheme.RdpHeaderFg,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft
            };
            _statusBadge.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var dot = new SolidBrush(Color.FromArgb(5, 150, 105));
                e.Graphics.FillEllipse(dot, 0, 6, 8, 8);
                TextRenderer.DrawText(e.Graphics, _statusBadge.Tag as string ?? "", _statusBadge.Font,
                    new Rectangle(13, 0, _statusBadge.Width - 13, _statusBadge.Height),
                    Helpers.UiTheme.RdpHeaderFg,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            };
            header.Controls.Add(_statusBadge);

            // v3.0.19 — pills to the right of "Подключено": active session
            // name + IP. Different colour per pill so they read as separate
            // facts. Hidden until a session is activated (UpdateSessionInfoPills).
            _sessionNameLbl = new Label
            {
                Text = "",
                Visible = false,
                AutoSize = false,
                Size = new Size(10, 22),
                Location = new Point(0, 8),
                Font = new Font("Segoe UI", 8.5F),
                ForeColor = Helpers.UiTheme.RdpHeaderFg,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft
            };
            _sessionNameLbl.Paint += (s, e) => PaintHeaderFact(_sessionNameLbl, e.Graphics, "computer");
            _sessionNameLbl.Cursor = Cursors.Hand;
            _sessionNameLbl.MouseUp += (s, e) => { if (e.Button == MouseButtons.Left) JumpToPillSession(); };
            header.Controls.Add(_sessionNameLbl);

            _sessionIpLbl = new Label
            {
                Text = "",
                Visible = false,
                AutoSize = false,
                Size = new Size(10, 22),
                Location = new Point(0, 8),
                Font = new Font("Segoe UI", 8.5F),
                ForeColor = Helpers.UiTheme.RdpHeaderFg,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft
            };
            _sessionIpLbl.Paint += (s, e) => PaintHeaderFact(_sessionIpLbl, e.Graphics, "globe");
            _sessionIpLbl.Cursor = Cursors.Hand;
            _sessionIpLbl.MouseUp += (s, e) => { if (e.Button == MouseButtons.Left) JumpToPillSession(); };
            header.Controls.Add(_sessionIpLbl);

            // v3.5.4 — terminal indicator pill (shows "🖥️ PowerShell" or "🖥️ CMD").
            _terminalLbl = new Label
            {
                Text = "",
                Visible = false,
                AutoSize = false,
                Size = new Size(10, 22),
                Location = new Point(0, 8),
                Font = new Font("Segoe UI", 8.5F),
                ForeColor = Helpers.UiTheme.RdpHeaderFg,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft
            };
            _terminalLbl.Paint += (s, e) =>
            {
                string key = _terminalLbl.Text.Contains("Power", StringComparison.OrdinalIgnoreCase) ? "powershell" : "terminal";
                PaintHeaderFact(_terminalLbl, e.Graphics, key);
            };
            header.Controls.Add(_terminalLbl);

            // "Сменить пароль" — clears the in-memory credential cache and
            // re-prompts. Anchored to the top-right so it survives resize.
            // Use case: user mistyped the password in the initial prompt
            // and mstsc now rejects all sessions with "Недопустимые
            // учётные данные" — click this, type the right one, restart
            // the affected sessions.
            // v2.3.3: hover no longer draws a background — just bumps the
            // font to Semibold. AutoSize is off so the label keeps a fixed
            // v3.6.8 — text button matching "Встроить ММС": always
            // Semibold, hover just brightens the text color. Size is computed
            // with the Semibold variant so nothing shifts on hover.
            var changePwdFont = new Font("Segoe UI", 9F);
            var changePwdColor = ShadowRDP.Helpers.UiTheme.Sidebar;
            var changePwdHoverColor = Color.FromArgb(0x2A, 0x5A, 0x8E);
            var changePwdText = "🔑 Сменить пароль";
            var changePwdSize = TextRenderer.MeasureText(changePwdText, changePwdFont,
                new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
            var changePwdBtn = new Label
            {
                Text = changePwdText,
                AutoSize = false,
                Size = new Size(changePwdSize.Width + 16, changePwdSize.Height + 12),
                TextAlign = ContentAlignment.MiddleCenter,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Font = changePwdFont,
                ForeColor = changePwdColor,
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand,
                Margin = Padding.Empty
            };
            changePwdBtn.Location = new Point(header.Width - changePwdBtn.Width - 12, 7);
            changePwdBtn.MouseEnter += (s, e) => changePwdBtn.ForeColor = changePwdHoverColor;
            changePwdBtn.MouseLeave += (s, e) => changePwdBtn.ForeColor = changePwdColor;
            Action changePwd = () =>
            {
                RdpCredentialStore.Clear();
                Logger.Info("RDP Manager: user clicked 'Сменить пароль' — cache cleared");
                // If at least one session exists, re-prompt immediately so
                // the new creds are ready for the next restart/connect.
                if (_sessions.Count > 0)
                {
                    RdpPasswordPrompt.Show(this);
                }
            };
            changePwdBtn.Click += (s, e) => changePwd();
            header.Controls.Add(changePwdBtn);

            // v3.8.2 — theme switcher ("🎨 Тема"): toggles Light ⇄ Olive and
            // restarts so the new palette is applied cleanly (no half-themed UI).
            var themeBtnFont = new Font("Segoe UI", 9F);
            var themeBtnText = "Тема";
            var themeBtnSize = TextRenderer.MeasureText(themeBtnText, themeBtnFont,
                new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
            var themeBtn = new Label
            {
                Text = "",
                AutoSize = false,
                Size = new Size(14 + 6 + themeBtnSize.Width + 10, 22),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Font = themeBtnFont,
                Cursor = Cursors.Hand,
                Margin = Padding.Empty
            };
            const int themeBtnRadius = 1;
            Color themeBtnBg = ShadowRDP.Helpers.UiTheme.Sidebar;
            Color themeBtnHover = ControlPaint.Light(themeBtnBg, 0.25f);
            var themeBtnColor = themeBtnBg;
            themeBtn.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                const int icon = 14, gap = 6, ix = 6;
                int iy = (themeBtn.Height - icon) / 2;
                int cx = ix + icon / 2, cy = iy + icon / 2;
                using var pen = new Pen(themeBtnColor, 1.2f);
                // outline sun (web-style): 8 rays + central circle
                for (int a = 0; a < 8; a++)
                {
                    double ang = a * Math.PI / 4;
                    int x1 = cx + (int)(Math.Cos(ang) * 4);
                    int y1 = cy + (int)(Math.Sin(ang) * 4);
                    int x2 = cx + (int)(Math.Cos(ang) * 6.5);
                    int y2 = cy + (int)(Math.Sin(ang) * 6.5);
                    g.DrawLine(pen, x1, y1, x2, y2);
                }
                g.DrawEllipse(pen, cx - 3, cy - 3, 6, 6);
                TextRenderer.DrawText(g, themeBtnText, themeBtn.Font,
                    new Rectangle(ix + icon + gap, 0, themeBtn.Width - (ix + icon + gap), themeBtn.Height),
                    themeBtnColor, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            };
            themeBtn.MouseEnter += (s, e) => { themeBtnColor = themeBtnHover; themeBtn.Invalidate(); };
            themeBtn.MouseLeave += (s, e) => { themeBtnColor = themeBtnBg; themeBtn.Invalidate(); };
            Action switchTheme = () =>
            {
                var next = ShadowRDP.Helpers.UiTheme.CurrentName == "Olive" ? "Light" : "Olive";
                ShadowRDP.Helpers.UiTheme.SetTheme(next);
                Logger.Info($"RDP Manager: theme switched to {next} — restarting");
                Application.Restart();
            };
            themeBtn.Click += (s, e) => switchTheme();
            header.Controls.Add(themeBtn);

            // v2.1.83: "+" button — opens CustomSessionEditForm to add an
            // SSH or Web shortcut to the sidebar. Anchored to the right,
            // positioned just LEFT of changePwdBtn via header.Layout below.
            // v2.2.1: custom Paint for rounded corners (was rectangle bg
            // — ApplyRoundedRegion would clip but mouse events still
            // register in the rectangular bounds, which felt weird. Now
            // Paint draws a rounded rect inside the label bounds + Region
            // clips to the rounded shape so the visual + click region match).
            var addSessionBtn = new Label
            {
                Text = "",
                AutoSize = false,
                Size = new Size(96, 22),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Font = new Font("Segoe UI Semibold", 9F),
                Cursor = Cursors.Hand,
                Margin = Padding.Empty
            };
            // v2.3.6: 5 → 1 (user: "скругление 1px во всех кнопках кроме сайдбара").
            const int addBtnRadius = 1;
            Color addBtnColor = ShadowRDP.Helpers.UiTheme.OutlineFg;
            Color addBtnHover = ControlPaint.Light(addBtnColor, 0.25f);
            var addBtnBorder = ShadowRDP.Helpers.UiTheme.OutlineBorder;
            var addBtnBg = Color.White;
            addSessionBtn.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                var rb = new Rectangle(0, 0, addSessionBtn.Width - 1, addSessionBtn.Height - 1);
                using var path = Helpers.UiButtons.RoundedRect(rb, 4);
                if (addBtnBg != Color.Transparent)
                {
                    g.SetClip(path);
                    g.FillRectangle(new SolidBrush(addBtnBg), 0, 0, addSessionBtn.Width, addSessionBtn.Height);
                    g.ResetClip();
                }
                if (addBtnBorder != Color.Transparent)
                {
                    using var bpen = new Pen(addBtnBorder, 1);
                    g.DrawPath(bpen, path);
                }
                TextRenderer.DrawText(g, "+  Сессия", addSessionBtn.Font,
                    new Rectangle(0, 0, addSessionBtn.Width, addSessionBtn.Height),
                    addBtnColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            };
            addSessionBtn.MouseEnter += (s, e) => { addBtnColor = addBtnHover; addSessionBtn.Invalidate(); };
            addSessionBtn.MouseLeave += (s, e) => { addBtnColor = ShadowRDP.Helpers.UiTheme.OutlineFg; addSessionBtn.Invalidate(); };
            Action addSession = () =>
            {
                var knownGroups = _sessions
                    .Select(x => x.Group ?? "")
                    .Concat(_customSessions.Select(x => x.Group ?? ""))
                    .Where(g => !string.IsNullOrEmpty(g))
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                var edited = Forms.CustomSessionEditForm.Edit(this, null, knownGroups);
                if (edited == null) return;
                edited.SortOrder = NextSortOrder(edited.Group ?? "");
                _customSessions.Add(edited);
                Services.CustomSessionStore.Save(_customSessions);
                RenderSidebar();
                Logger.Info($"RDP Manager: added custom session '{edited.Name}' ({edited.Kind}, {edited.Target})");
            };
            addSessionBtn.Click += (s, e) => addSession();
            header.Controls.Add(addSessionBtn);

            // v2.2.10: "+ В книгу" button — opens EditEntryForm in add-mode
            // to add a new entry to the Address Book without leaving the
            // RDP Manager window. Same Figma-styled dialog as editing.
            var addToBookBtn = new Label
            {
                Text = "",
                AutoSize = false,
                Size = new Size(96, 22),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Font = new Font("Segoe UI Semibold", 9F),
                Cursor = Cursors.Hand,
                Margin = Padding.Empty
            };
            Color bookColor = ShadowRDP.Helpers.UiTheme.OutlineFg;
            Color bookHover = ControlPaint.Light(bookColor, 0.25f);
            var bookBorder = ShadowRDP.Helpers.UiTheme.OutlineBorder;
            var bookBg = Color.White;
            addToBookBtn.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                var rb = new Rectangle(0, 0, addToBookBtn.Width - 1, addToBookBtn.Height - 1);
                using var path = Helpers.UiButtons.RoundedRect(rb, 4);
                if (bookBg != Color.Transparent)
                {
                    g.SetClip(path);
                    g.FillRectangle(new SolidBrush(bookBg), 0, 0, addToBookBtn.Width, addToBookBtn.Height);
                    g.ResetClip();
                }
                if (bookBorder != Color.Transparent)
                {
                    using var bpen = new Pen(bookBorder, 1);
                    g.DrawPath(bpen, path);
                }
                TextRenderer.DrawText(g, "+  В книгу", addToBookBtn.Font,
                    new Rectangle(0, 0, addToBookBtn.Width, addToBookBtn.Height),
                    bookColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            };
            addToBookBtn.MouseEnter += (s, e) => { bookColor = bookHover; addToBookBtn.Invalidate(); };
            addToBookBtn.MouseLeave += (s, e) => { bookColor = ShadowRDP.Helpers.UiTheme.OutlineFg; addToBookBtn.Invalidate(); };
            Action addToBook = () =>
            {
                var added = Forms.EditEntryForm.ShowAddDialog(this);
                if (added == null) return;
                // Refresh AddressBookForm if it's already open.
                if (Forms.AddressBookForm.IsOpen) Forms.AddressBookForm.Instance.ReloadFromStore();
                Logger.Info($"RDP Manager: added new address book entry '{added.Computer}' / '{added.User}'");
            };
            addToBookBtn.Click += (s, e) => addToBook();
            header.Controls.Add(addToBookBtn);

            // v3.8.x: "Экспорт/Импорт" button — dropdown menu:
            //   Экспорт адресной книги (CSV, as v2.3.13)
            //   Экспорт сессий (JSON: RDP + SSH/Web sidebar)
            //   ---- separator ----
            //   Импорт книги (CSV)
            //   Импорт сессий (JSON)
            var exportBtn = new Label
            {
                Text = "",
                AutoSize = false,
                Size = new Size(120, 22),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Font = new Font("Segoe UI Semibold", 9F),
                Cursor = Cursors.Hand,
                Margin = Padding.Empty
            };
            Color exportColor = ShadowRDP.Helpers.UiTheme.OutlineFg;
            Color exportHover = ControlPaint.Light(exportColor, 0.25f);
            var exportBorder = ShadowRDP.Helpers.UiTheme.OutlineBorder;
            var exportBg = Color.White;
            exportBtn.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                var rb = new Rectangle(0, 0, exportBtn.Width - 1, exportBtn.Height - 1);
                using var path = Helpers.UiButtons.RoundedRect(rb, 4);
                if (exportBg != Color.Transparent)
                {
                    g.SetClip(path);
                    g.FillRectangle(new SolidBrush(exportBg), 0, 0, exportBtn.Width, exportBtn.Height);
                    g.ResetClip();
                }
                if (exportBorder != Color.Transparent)
                {
                    using var bpen = new Pen(exportBorder, 1);
                    g.DrawPath(bpen, path);
                }
                TextRenderer.DrawText(g, "Экспорт/Импорт", exportBtn.Font,
                    new Rectangle(0, 0, exportBtn.Width, exportBtn.Height),
                    exportColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            };
            exportBtn.MouseEnter += (s, e) => { exportColor = exportHover; exportBtn.Invalidate(); };
            exportBtn.MouseLeave += (s, e) => { exportColor = ShadowRDP.Helpers.UiTheme.OutlineFg; exportBtn.Invalidate(); };

            var exportMenu = new Helpers.ThemedContextMenu();
            exportMenu.AddHeader("Экспорт / импорт");
            exportMenu.AddItem("Экспорт адресной книги", null, () => ExportAddressBook());
            exportMenu.AddItem("Экспорт сессий", null, () => ExportSessions());
            exportMenu.AddSeparator();
            exportMenu.AddItem("Импорт книги", null, () => ImportAddressBook());
            exportMenu.AddItem("Импорт сессий", null, () => ImportSessions());
            // Both left and right click show the menu below the button.
            exportBtn.MouseDown += (s, e) => exportMenu.Show(this, exportBtn.PointToScreen(new Point(0, exportBtn.Height)));
            header.Controls.Add(exportBtn);

            // PowerShell button — launches PowerShell as admin.
            var psBtn = new Label
            {
                Text = "",
                AutoSize = false,
                Size = new Size(58, 22),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Font = new Font("Segoe UI Semibold", 8F),
                Cursor = Cursors.Hand,
                Margin = Padding.Empty
            };
            Color psBg = Color.FromArgb(0x01, 0x24, 0x56);
            Color psHover = ControlPaint.Dark(psBg, 0.08f);
            var psColor = psBg;
            psBtn.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                var r = new Rectangle(0, 0, psBtn.Width, psBtn.Height);
                using var path = Helpers.UiButtons.RoundedRect(r, addBtnRadius);
                using var brush = new SolidBrush(psColor);
                e.Graphics.FillPath(brush, path);
                var icon = PsIcon;
                var iconW = 0;
                if (icon != null)
                {
                    var iconH = psBtn.Font.Height;
                    iconW = (int)Math.Round(icon.Width * (double)iconH / icon.Height);
                    // v3.9.3 — bicubic-масштаб иконки (был Default → пиксели).
                    e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    e.Graphics.DrawImage(icon, new Rectangle(6, (psBtn.Height - iconH) / 2, iconW, iconH));
                }
                var textRect = new Rectangle(icon != null ? 6 + iconW + 4 : 0, 0,
                    psBtn.Width - (icon != null ? 6 + iconW + 4 : 0), psBtn.Height);
                TextRenderer.DrawText(e.Graphics, "PS", psBtn.Font, textRect,
                    Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            };
            psBtn.MouseEnter += (s, e) => { psColor = psHover; psBtn.Invalidate(); };
            psBtn.MouseLeave += (s, e) => { psColor = psBg; psBtn.Invalidate(); };
            Action launchPs = () =>
            {
                string psVer = "5.1";
                try
                {
                    var vProc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe", "-NoLogo -Command \"$PSVersionTable.PSVersion.ToString()\"")
                    {
                        UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true
                    });
                    if (vProc != null) { psVer = vProc.StandardOutput.ReadToEnd().Trim(); vProc.WaitForExit(); vProc.Dispose(); }
                } catch { }
                var psBanner = $"Windows PowerShell\nCopyright (C) Microsoft Corporation. All rights reserved.\n\nПопробуйте новую кроссплатформенную PowerShell https://aka.ms/pscore6\n";
                _psSession = ShowOrCreateConsoleSession(_psSession, "powershell.exe", "-NoLogo", "PowerShell", Encoding.GetEncoding(866), psBanner, ShadowRDP.Helpers.UiTheme.HeaderBar);
            };
            psBtn.Click += (s, e) => launchPs();
            header.Controls.Add(psBtn);

            // CMD button — launches CMD as admin.
            var cmdBtn = new Label
            {
                Text = "",
                AutoSize = false,
                Size = new Size(58, 22),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Font = new Font("Segoe UI Semibold", 8F),
                Cursor = Cursors.Hand,
                Margin = Padding.Empty
            };
            Color cmdBg = Color.FromArgb(0x3C, 0x3C, 0x3C);
            Color cmdHover = ControlPaint.Dark(cmdBg, 0.08f);
            var cmdColor = cmdBg;
            cmdBtn.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                var r = new Rectangle(0, 0, cmdBtn.Width, cmdBtn.Height);
                using var path = Helpers.UiButtons.RoundedRect(r, addBtnRadius);
                using var brush = new SolidBrush(cmdColor);
                e.Graphics.FillPath(brush, path);
                var icon = CmdIcon;
                var iconW = 0;
                if (icon != null)
                {
                    var iconH = (int)Math.Round(cmdBtn.Font.Height * 0.8);
                    iconW = (int)Math.Round(icon.Width * (double)iconH / icon.Height);
                    // v3.9.3 — bicubic-масштаб иконки (был Default → пиксели).
                    e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    e.Graphics.DrawImage(icon, new Rectangle(6, (cmdBtn.Height - iconH) / 2, iconW, iconH));
                }
                var textRect = new Rectangle(icon != null ? 6 + iconW + 4 : 0, 0,
                    cmdBtn.Width - (icon != null ? 6 + iconW + 4 : 0), cmdBtn.Height);
                TextRenderer.DrawText(e.Graphics, "CMD", cmdBtn.Font, textRect,
                    Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            };
            cmdBtn.MouseEnter += (s, e) => { cmdColor = cmdHover; cmdBtn.Invalidate(); };
            cmdBtn.MouseLeave += (s, e) => { cmdColor = cmdBg; cmdBtn.Invalidate(); };
            Action launchCmd = () => _cmdSession = ShowOrCreateConsoleSession(_cmdSession, "cmd.exe", "/Q", "CMD", Encoding.GetEncoding(866), accentColor: Color.FromArgb(0x6E, 0x6E, 0x6E));
            cmdBtn.Click += (s, e) => launchCmd();
            header.Controls.Add(cmdBtn);

            // v3.6.8 — "Встроить ММС". Opens an MMC console (app-owned via
            // "Добавить MMC" or a user .msc file) and embeds its window into
            // the main area (same SetParent trick the mstsc sessions use).
            // The button sits at the LEFT edge of the header; left or right
            // click opens the recent-console menu (history from
            // embedded_apps.json) with "Открыть консоль MMC…" and "Добавить
            // MMC" at the bottom. Each running console gets a pill-button
            // placed directly on the header (right of the button) to switch
            // back. Arbitrary programs are NOT embedded (MMC only).
            _toolTip = new ToolTip { ShowAlways = true, InitialDelay = 300, AutoPopDelay = 10000 };
            _headerPanel = header;
            // v3.9.3CY — выбор цвета области перенесён в «Инструменты → Настройки цвета» (ПКМ убран).
            Helpers.UiTheme.RdpHeaderColorChanged += ApplyRdpHeaderColors;
            header.Disposed += (s, e) => { try { Helpers.UiTheme.RdpHeaderColorChanged -= ApplyRdpHeaderColors; } catch { } };

            // v3.9 D — header "Встроить ММС" removed: legacy embedBtn/embedMenu (ContextMenuStrip popup)
            // caused first-click miss due to toolsMenu.Close()+BeginInvoke race and handle warming.
            // Now handled as dynamic submenu inside toolsMenu — no second top-level popup.

            // v3.9 — collapse the 7 header action buttons into a single "Инструменты" dropdown (design-preview parity).
            // The original buttons are kept (Visible=false) so their existing handlers stay wired; the dropdown triggers them — no logic dup.
            foreach (var b in new Control[] { changePwdBtn, themeBtn, addSessionBtn, addToBookBtn, exportBtn, psBtn, cmdBtn })
                b.Visible = false;

            // v3.9 — the "Инструменты" dropdown now lives in the sidebar
            // (below "Пароли"), styled like the other sidebar nav buttons.
            // The menu opens to the right of the sidebar button.
            var toolsMenu = new Helpers.ThemedContextMenu();
            toolsMenu.AddHeader("Инструменты");
            toolsMenu.AddItem("CMD", "terminal", () => launchCmd());
            toolsMenu.AddItem("PowerShell (PS)", "powershell", () => launchPs());
            // D — "Встроить ММС" as dynamic submenu (no second popup, no Close+BeginInvoke race).
            // Child is rebuilt on every click so recent list is always fresh; first click now works.
            toolsMenu.AddDynamicSubmenu("Встроить ММС", "gear", child => PopulateEmbedMenu(child));
            toolsMenu.AddSeparator();
            toolsMenu.AddItem("Экспорт/Импорт", "exchange", () =>
            {
                toolsMenu.Close();
                BeginInvoke(new Action(() => exportMenu.Show(this, _toolsTab.PointToScreen(new Point(_toolsTab.Width, 0)))));
            });
            toolsMenu.AddItem("Сессия", "add", () => addSession());
            toolsMenu.AddItem("В книгу", "book", () => addToBook());
            toolsMenu.AddSeparator();
            // v4.7 — менеджер встраиваемых модулей (TODO.шная и будущие).
            toolsMenu.AddItem("Модули…", "tasks", () =>
            {
                toolsMenu.Close();
                BeginInvoke(new Action(() =>
                {
                    try { new Forms.ModuleManagerForm().Show(); } catch { }
                }));
            });
            toolsMenu.AddSeparator();
            toolsMenu.AddItem("Сменить пароль", "key", () => changePwd());
            toolsMenu.AddItem("Тема", "sun", () => switchTheme());
            // v3.9.3CY — все настройки цвета программы в одном окне.
            toolsMenu.AddItem("Настройки цвета…", "info", () =>
            {
                toolsMenu.Close();
                BeginInvoke(new Action(() =>
                {
                    try { Forms.ColorSettingsForm.Show(this); } catch { }
                }));
            });
            // v3.9.3 — аудит действий администратора (audit_ГГГГ-ММ.jsonl).
            toolsMenu.AddItem("Журнал действий…", "list", () =>
            {
                toolsMenu.Close();
                try { System.Diagnostics.Process.Start("notepad.exe", AppPaths.AuditLogPath); }
                catch (Exception ex) { Logger.Warn($"Не удалось открыть журнал действий: {ex.Message}"); }
            });
            _toolsTab.Click += (s, e) => toolsMenu.Show(this, _toolsTab.PointToScreen(new Point(_toolsTab.Width, 0)));

            // Recompute X position — right-anchored buttons.
            // Layout right-to-left: changePwdBtn (rightmost), addSessionBtn,
            // addToBookBtn, exportBtn (leftmost).
            header.Layout += (s, e) =>
            {
                // Left block, left→right: logo → title → status badge →
                // session pills → running-console pills.
                icon.Location = new Point(20, 6);
                title.Location = new Point(icon.Right + 8, 10);
                _statusBadge.Location = new Point(Math.Max(170, title.Right + 16), 10);
                LayoutHeaderLeftBlock();
            };

            // Add controls to form in REVERSE dock order:
            // Fill (body) first, then Top (header).
            Controls.Add(body);       // Fill
            Controls.Add(header);     // Top — 48px header

            // v4.0 — dashboard poller + balloon wiring (LAN-only, 1 user, 30min)
            try
            {
                Services.EventPollerService.Start();
                Services.EventPollerService.OnCritical += ev =>
                {
                    try
                    {
                        var s = Services.EventDashboardStore.LoadSettings();
                        if (ev.Level == "Critical" && !s.NotifyCritical) return;
                        if (ev.Level == "Error" && !s.NotifyError) return;
                        BeginInvoke(new Action(() =>
                        {
                            // mark notified to avoid duplicate balloon
                            var all = Services.EventDashboardStore.LoadEvents();
                            var idx = all.FindIndex(x => x.Computer == ev.Computer && x.EventId == ev.EventId && x.Time == ev.Time);
                            if (idx >= 0)
                            {
                                if (all[idx].Notified) return;
                                all[idx].Notified = true;
                                Services.EventDashboardStore.SaveEvents(all);
                            }
                            // show balloon via app tray icon if available
                            try { ShadowRdpAppContext.ShowBalloon($"Критическое событие — {ev.Computer}", $"{ev.Source} (ID {ev.EventId}): {ev.Message}  {ev.Time:dd.MM HH:mm}", ToolTipIcon.Warning); }
                            catch
                            {
                                // fallback: local notify icon
                                using var ni = new NotifyIcon { Visible = true, Icon = Icon, BalloonTipTitle = $"Критическое — {ev.Computer}", BalloonTipText = $"{ev.Source} {ev.EventId}: {ev.Message}" };
                                ni.ShowBalloonTip(4000);
                            }
                            if (s.NotifySound) System.Media.SystemSounds.Exclamation.Play();
                        }));
                    }
                    catch { }
                };
            }
            catch (Exception ex) { try { Logger.Warn($"Dashboard poller start failed: {ex.Message}"); } catch { } }
            try { Services.ZabbixAgentPollerService.Start(); } catch (Exception ex) { try { Logger.Warn($"ZabbixAgent poller start failed: {ex.Message}"); } catch { } }
            // v4.7 — ICMP-пинг хостов даша Z раз в час (колонка «Пинг»: OK/FAIL).
            try { Services.HostPingService.Start(); } catch (Exception ex) { try { Logger.Warn($"HostPing start failed: {ex.Message}"); } catch { } }
            // v4.6 — web-мониторинг даша Z (ряд WEB): старт поллера + баллун о крахе.
            try
            {
                Services.WebMonitorPollerService.Start();
                Services.WebMonitorPollerService.OnWebDown += pt =>
                {
                    try
                    {
                        BeginInvoke(new Action(() =>
                        {
                            string detail = pt.StatusCode > 0 ? $"HTTP {pt.StatusCode} за {pt.Ms}мс" : $"{pt.Error}";
                            try { ShadowRdpAppContext.ShowBalloon($"Ресурс недоступен — {pt.Url}", detail, ToolTipIcon.Warning); }
                            catch
                            {
                                using var ni = new NotifyIcon { Visible = true, Icon = Icon, BalloonTipTitle = $"Ресурс недоступен — {pt.Url}", BalloonTipText = detail };
                                ni.ShowBalloonTip(4000);
                            }
                            try { if (Services.EventDashboardStore.LoadSettings().NotifySound) System.Media.SystemSounds.Exclamation.Play(); } catch { }
                        }));
                    }
                    catch { }
                };
            }
            catch (Exception ex) { try { Logger.Warn($"WebMonitor poller start failed: {ex.Message}"); } catch { } }
            try { Services.FtpUploadService.Start(); } catch (Exception ex) { try { Logger.Warn($"FtpUploadService start failed: {ex.Message}"); } catch { } }

            FormClosing += (s, e) =>
            {
                // Persist current sessions so the main-form "RDP Manager"
                // button can restore them on next open.
                PersistSessions();
                // v3.6.8 — the X button is intercepted by ShadowRdpAppContext
                // AFTER this handler runs and only HIDES the window to tray.
                // Skipping the destructive cleanup on that path keeps sessions
                // connected and embedded consoles alive, and _instance intact
                // — otherwise restoring from the tray recreated a brand-new
                // EMPTY form (no sidebar, no sessions, no embedded consoles).
                if (!ShadowRdpAppContext.IsRealExit && e.CloseReason == CloseReason.UserClosing)
                    return;
                foreach (var sess in _sessions)
                    try { sess.Client?.Disconnect(); } catch { }
                // v3.5.4 — kill embedded console sessions.
                _psSession?.KillProcess();
                _cmdSession?.KillProcess();
                // v3.6.8 — kill embedded consoles.
                foreach (var es in _embeddedSessions)
                    try { es.Control.KillProcess(); } catch { }
                _instance = null;
            };

            // Resize handler — keeps embedded RDP windows in sync.
            SizeChanged += (s, e) =>
            {
                foreach (var sess in _sessions)
                    sess.Client?.RefreshSize();
            };
        }

        // v3.0.7: autoOpen=false adds a sidebar entry WITHOUT creating an
        // RdpClient or connecting. Used by RestoreSessions for sessions
        // where the user unchecked "Автозагрузка → Да". The user activates
        // them later by clicking the tab (lazy activation).
        // v3.1.2: _batchRender flag — when RestoreSessions is bulk-adding
        // many sessions, we skip the per-call RenderSidebar and render once
        // at the end. Otherwise each AddSession destroys+recreates the whole
        // sidebar, visible as a ~20s flicker on app launch.
        private bool _batchRender;

        // v3.8.x — drag&drop sidebar reorder state.
        private Control? _dragControl;   // the session item being dragged
        private int _dragStartScreenY;   // cursor Y at MouseDown (screen coords)
        private bool _isDragging;
        private bool _suppressClick;     // swallow the Click raised after a drag
        private string _dragGroup = "";  // group key of the dragged item
        // v4.9.28 — drop target group under cursor (may differ from
        // _dragGroup → cross-group move). Reset on MouseDown.
        private string _dropGroup = "";
        private int _dropIndicatorY = -1; // insertion-slot Y in _sidebarList coords (-1 = hidden)

        // v3.8.x — next free SortOrder within a group. Numbering is shared
        // across RDP and custom sessions so a mixed drag&drop order survives
        // restart. New sessions append below everything else in the group.
        private int NextSortOrder(string group)
        {
            var max = 0;
            foreach (var s in _sessions)
                if (string.Equals(s.Group, group, StringComparison.OrdinalIgnoreCase) && s.SortOrder > max)
                    max = s.SortOrder;
            foreach (var c in _customSessions)
                if (string.Equals(c.Group ?? "", group, StringComparison.OrdinalIgnoreCase) && c.SortOrder > max)
                    max = c.SortOrder;
            return max + 1;
        }

        public void AddSession(string computer, string? ip, string? group = null, bool autoOpen = true, int sortOrder = -1, string? displayName = null, string? notes = null, bool transient = false)
        {
            var target = !string.IsNullOrEmpty(ip) ? ip : computer;

            var sess = new RdpSession
            {
                Computer = computer,
                DisplayName = displayName ?? "",
                Notes = notes ?? "",
                Target = target,
                Ip = ip ?? "",
                Group = group ?? "",
                Client = null,
                AutoOpen = autoOpen,
                Transient = transient,
                // v3.8.x — new sessions append to the bottom of their group.
                SortOrder = sortOrder >= 0 ? sortOrder : NextSortOrder(group ?? "")
            };
            _sessions.Add(sess);
            // v3.9.3 — transient: вкладка без следа (не в сайдбар, не на диск).
            if (!transient && !_batchRender) RenderSidebar();

            if (autoOpen)
            {
                // v3.7.1 — during app-startup batch restore, only auto-activate
                // sessions whose credentials are already available (KeePass or
                // per-session override). Sessions without credentials stay
                // pending (grey dot) so the user is prompted on tab click
                // instead of getting N credential dialogs on launch.
                // Outside batch restore (user just added the session), always
                // activate — the picker is expected in that flow.
                bool hasKp = KeePassVault.IsOpen
                    && KeePassVault.FindLogin(KeePassVault.RdpGroupPath, computer) != null;
                bool hasOverride = _rdpPerSessionCreds.ContainsKey(computer);
                if (hasKp || hasOverride || !_batchRender)
                {
                    ActivateSession(sess);
                }
                else
                {
                    Logger.Info($"RDP Manager: session '{computer}' left pending — no credentials, awaiting tab click");
                }
            }

            if (WindowState == FormWindowState.Normal && _sessions.Count == 1)
                WindowState = FormWindowState.Maximized;

            if (!Visible) Show();
            else BringToFront();

            if (!_batchRender)
            {
                if (!transient) PersistSessions();
                RaiseSessionListChanged();
            }
        }

        // v3.0.7: materialises RdpClient + Connects if not yet activated.
        // Called from AddSession (autoOpen=true), sidebar tab click handler
        // (lazy activation), RestartSession, etc. Idempotent — if Client
        // already exists, just ShowSession.
        private void ActivateSession(RdpSession sess)
        {
            if (sess.Client != null)
            {
                ShowSession(sess);
                return;
            }

            // v3.7.0 — credential resolution order: per-session override →
            // KeePass vault (PupSeekRDP/RDP, entry named after the computer)
            // → in-memory shared cache / prompt.
            string? login = null, password = null;
            var kp = KeePassVault.IsOpen
                ? KeePassVault.FindLogin(KeePassVault.RdpGroupPath, sess.Computer)
                : null;
            if (sess.LoginOverride != null)
            {
                login = sess.LoginOverride;
                password = sess.PasswordOverride;
            }
            else if (kp != null)
            {
                login = kp.Value.Login;
                password = kp.Value.Password;
            }
            else
            {
                // v3.7.1 — per-computer picker (no shared start-up prompt).
                // User chooses "save to KeePass" or "one-off". Cancel aborts
                // the activation — the session stays in the sidebar as pending.
                var picked = RdpPasswordPrompt.ShowForComputer(this, sess.Computer);
                if (picked == null)
                {
                    Logger.Info($"RDP Manager: credential picker cancelled, aborting session for {sess.Computer}");
                    return;
                }
                login = picked.Login;
                password = picked.Password;
            }

            var rdp = new RdpClient();
            _mainArea.Controls.Add(rdp);
            sess.Client = rdp;
            // v3.0.15 — flip the status dot from grey → green now that Client
            // exists. Done in place (no RenderSidebar) so the sidebar doesn't
            // flicker.
            UpdateSessionStatusDot(sess, Helpers.UiTheme.StatusOnline);

            ShowSession(sess);

            try
            {
                Logger.Info($"RDP Manager: connecting to {sess.Target} ({sess.Computer}) as {login}");
                rdp.Connect(sess.Target, login, password);
            }
            catch (Exception ex)
            {
                Logger.Warn($"RDP Manager: connect failed for {sess.Target}: {ex.Message}");
            }

            // v3.0.11 — no RenderSidebar here. ShowSession already updated
            // the status dot colour (grey → green) for this tab in place.
            // Full re-render was the source of the sidebar flicker on click.
            RaiseSessionListChanged();
        }

        // Restores all saved sessions from rdp_sessions.json. Called from
        // the main-form RDP Manager button when the manager isn't open yet.
        // No-op if the saved list is empty — the manager still opens so the
        // user can add sessions via the address-book / context-menu entry
        // points.
        public void RestoreSessions()
        {
            var entries = RdpSessionStore.Load();
            // v2.1.83: load custom sessions regardless of RDP sessions.
            _customSessions.Clear();
            _customSessions.AddRange(Services.CustomSessionStore.Load());
            if (_customSessions.Count > 0)
                Logger.Info($"RDP Manager: loaded {_customSessions.Count} custom session(s)");
            if (entries.Count == 0)
            {
                Logger.Info("RDP Manager: no saved sessions to restore");
                if (!Visible) Show();
                else BringToFront();
                // Even with no RDP sessions, re-render sidebar to show
                // custom shortcuts.
                if (_customSessions.Count > 0) RenderSidebar();
                return;
            }
            Logger.Info($"RDP Manager: restoring {entries.Count} session(s)");
            _batchRender = true;
            try
            {
                foreach (var e in entries)
                {
                    // v3.0.7: pass AutoOpen. Sessions with AutoOpen=false are
                    // added as sidebar-only (no mstsc connect) — user activates
                    // them on demand by clicking the tab.
                    // v3.8.x: restore SortOrder so a reordered sidebar
                    // survives restart.
                    AddSession(e.Computer, e.Ip, e.Group, autoOpen: e.AutoOpen, sortOrder: e.SortOrder, displayName: e.DisplayName, notes: e.Notes);
                    // v2.2.28: restore per-session creds from cache.
                    if (_rdpPerSessionCreds.TryGetValue(e.Computer, out var cached))
                    {
                        var sess = _sessions.LastOrDefault(s =>
                            string.Equals(s.Computer, e.Computer, StringComparison.OrdinalIgnoreCase));
                        if (sess != null)
                        {
                            sess.LoginOverride = cached.Login;
                            sess.PasswordOverride = cached.Password;
                        }
                    }
                }
            }
            finally
            {
                _batchRender = false;
                // v3.1.2 — single render at the end of the bulk restore,
                // regardless of how many entries were added. Was the source
                // of a ~20-second sidebar flicker on launch.
                RenderSidebar();
                PersistSessions();
                RaiseSessionListChanged();
            }
        }

        // Single entry point for the main-form RDP Manager button. If the
        // manager is already open (with live sessions), just bring it to
        // the front — restoring would re-launch mstsc for every saved
        // session, which is destructive. Only restore when the singleton
        // doesn't exist yet (first open after app start, or after the user
        // closed the manager and the singleton was disposed).
        public static void OpenOrRestore()
        {
            if (IsOpen)
            {
                Helpers.UiButtons.BringToForeground(Instance);
                return;
            }
            Instance.RestoreSessions();
        }

        // Open RDP Manager (creating if needed) on the Sessions tab. This
        // is what the main-form "RDP Manager" button calls.
        public static void OpenSessions()
        {
            OpenOrRestore();
            Instance.SwitchTab(SidebarTab.Sessions);
        }

        // Open RDP Manager (creating if needed) on the Book tab. This is
        // what the main-form "Книга" button calls — the book is now a
        // sidebar tab, not a standalone window.
        public static void OpenBook()
        {
            OpenOrRestore();
            Instance.SwitchTab(SidebarTab.Book);
        }

        // Switch the main area between RDP sessions and the embedded
        // address book. Lazy-creates the AddressBookForm on first switch
        // to Book — saves startup time if the user never opens it.
        private void SwitchTab(SidebarTab tab)
        {
            _activeTab = tab;
            _activeModuleId = null;
            HideModuleHosts();

            if (tab == SidebarTab.Book)
                EnsureBookEmbedded();
            else if (tab == SidebarTab.Dashboard)
                EnsureDashEmbedded();

            _bookHost.Visible = (tab == SidebarTab.Book);
            _dashHost.Visible = (tab == SidebarTab.Dashboard);
            if (tab == SidebarTab.Book) _bookHost.BringToFront();
            else if (tab == SidebarTab.Dashboard) _dashHost.BringToFront();

            if (tab == SidebarTab.Sessions)
            {
                foreach (var s in _sessions)
                    s.Client?.RefreshSize();
            }

            UpdateTabVisuals();
        }

        private void EnsureBookEmbedded()
        {
            if (_bookForm != null && !_bookForm.IsDisposed) return;
            _bookForm = AddressBookForm.Instance;
            _bookForm.PrepareForEmbedding();
            _bookHost.Controls.Add(_bookForm);
            _bookForm.Show();
            Logger.Info("RDP Manager: AddressBookForm embedded into _bookHost");
        }

        // v4.8 — единый дашборд (Events/Monitor/Web внутри одной формы).
        private void EnsureDashEmbedded()
        {
            if (_dashForm != null && !_dashForm.IsDisposed) return;
            _dashForm = new Forms.ZabbixAgentDashboardForm();
            _dashForm.TopLevel = false;
            _dashForm.FormBorderStyle = FormBorderStyle.None;
            _dashForm.Dock = DockStyle.Fill;
            _dashForm.Visible = true;
            _dashHost.Controls.Add(_dashForm);
            _dashForm.Show();
            Logger.Info("RDP Manager: unified dashboard embedded into _dashHost");
        }

        // v4.8 — открыть единый дашборд сразу на нужной вьюхе.
        internal void OpenDash(Forms.ZabbixAgentDashboardForm.DashView view)
        {
            SwitchTab(SidebarTab.Dashboard);
            try { _dashForm?.SwitchView(view); } catch { }
        }

        public static void OpenModule(string id)
        {
            OpenOrRestore();
            try { Instance.SwitchToModule(id); } catch { }
        }

        // v4.7 — навигация/встраивание вкладок модулей (обобщённая бывшая
        // EnsureTodoEmbedded). Хосты модулей создаются лениво, скрываются
        // при переключении на штатные вкладки (HideModuleHosts).
        internal void SwitchToModule(string id)
        {
            ShadowRDP.Models.AppModule? m = null;
            try { m = Services.ModuleManager.Get(id); } catch { }
            if (m == null) return;
            try { if (!Services.ModuleManager.IsEnabled(m.Id)) return; } catch { return; }
            EnsureModuleEmbedded(m.Id);
            _activeModuleId = m.Id;
            foreach (var kv in _moduleHosts)
                kv.Value.Visible = string.Equals(kv.Key, m.Id, System.StringComparison.OrdinalIgnoreCase);
            _bookHost.Visible = false;
            _dashHost.Visible = false;
            try { _moduleHosts[m.Id].BringToFront(); } catch { }
            UpdateTabVisuals();
        }

        private void HideModuleHosts()
        {
            try
            {
                foreach (var kv in _moduleHosts)
                    kv.Value.Visible = false;
            }
            catch { }
        }

        private void EnsureModuleEmbedded(string id)
        {
            ShadowRDP.Models.AppModule? m = null;
            try { m = Services.ModuleManager.Get(id); } catch { }
            if (m == null) return;
            try
            {
                if (_moduleForms.TryGetValue(id, out var f) && f != null && !f.IsDisposed) return;
            }
            catch { }
            Control? content = null;
            try { content = m.CreateTabContent(); } catch { }
            if (content == null) return;
            try
            {
                if (content is Form cf)
                {
                    cf.TopLevel = false;
                    cf.FormBorderStyle = FormBorderStyle.None;
                    cf.Dock = DockStyle.Fill;
                    cf.Visible = true;
                }
                else
                {
                    content.Dock = DockStyle.Fill;
                    content.Visible = true;
                }
                if (!_moduleHosts.TryGetValue(id, out var host) || host == null || host.IsDisposed)
                {
                    host = new Panel { Dock = DockStyle.Fill, BackColor = Helpers.UiTheme.Background, Visible = false };
                    _moduleHosts[id] = host;
                    _mainArea.Controls.Add(host);
                }
                host.Controls.Clear();
                host.Controls.Add(content);
                _moduleForms[id] = content;
                if (content is Form cf2) { try { cf2.Show(); } catch { } }
                Logger.Info($"RDP Manager: module '{id}' embedded");
            }
            catch { }
        }

        private void RebuildModuleTabs()
        {
            try
            {
                if (_modulesPanel == null || _modulesPanel.IsDisposed) return;
                foreach (Control c in _modulesPanel.Controls) { try { c.Dispose(); } catch { } }
                _modulesPanel.Controls.Clear();
                _moduleTabButtons.Clear();
                System.Collections.Generic.List<ShadowRDP.Models.AppModule> enabled;
                try { enabled = new System.Collections.Generic.List<ShadowRDP.Models.AppModule>(Services.ModuleManager.GetEnabled()); }
                catch { return; }
                // Dock=Top: добавляем в обратном порядке, чтобы визуальный
                // порядок совпал с порядком каталога.
                for (int i = enabled.Count - 1; i >= 0; i--)
                {
                    var m = enabled[i];
                    if (m == null || !m.ShowInSidebar) continue;
                    string text;
                    try { text = m.SidebarText(); } catch { text = m.Name; }
                    var btn = Helpers.UiButtons.MakeFilled(text, m.IconKey,
                        ShadowRDP.Helpers.UiTheme.SidebarTab, width: 166, radius: 0);
                    btn.Dock = DockStyle.Top;
                    btn.Height = 36;
                    btn.Margin = Padding.Empty;
                    var mid = m.Id;
                    btn.Click += (s, e) => SwitchToModule(mid);
                    _modulesPanel.Controls.Add(btn);
                    _moduleTabButtons[mid] = btn;
                }
                int vis = _moduleTabButtons.Count;
                _modulesPanel.Height = vis * 36;
                _modulesPanel.Visible = vis > 0;
            }
            catch { }
        }

        // v4.9.4 — TodoStore.DataChanged → освежить тексты модулей в сайдбаре
        // (бейдж TODO) из любого потока через BeginInvoke.
        private void OnTodoDataChanged()
        {
            try
            {
                if (IsDisposed || Disposing) return;
                if (InvokeRequired) { try { BeginInvoke(new Action(OnTodoDataChanged)); } catch { } return; }
                RefreshModuleButtons();
            }
            catch { }
        }

        private void RefreshModuleButtons()
        {
            try
            {
                foreach (var kv in _moduleTabButtons)
                {
                    ShadowRDP.Models.AppModule? m = null;
                    try { m = Services.ModuleManager.Get(kv.Key); } catch { }
                    if (m == null) continue;
                    string text;
                    try { text = m.SidebarText(); } catch { text = m.Name; }
                    if (kv.Value.Text != text) kv.Value.Text = text;
                    try { kv.Value.Invalidate(); } catch { }
                }
            }
            catch { }
        }

        private void OnModuleStateChanged(string id, bool enabled)
        {
            if (IsDisposed || Disposing) return;
            try
            {
                if (InvokeRequired) { try { BeginInvoke(new Action(() => OnModuleStateChanged(id, enabled))); } catch { } return; }
                RebuildModuleTabs();
                if (!enabled)
                {
                    try
                    {
                        if (_moduleHosts.TryGetValue(id, out var host) && host != null && !host.IsDisposed)
                            host.Visible = false;
                        if (_moduleForms.TryGetValue(id, out var f) && f != null && !f.IsDisposed)
                        {
                            try { host?.Controls.Remove(f); } catch { }
                            try { f.Dispose(); } catch { }
                        }
                        _moduleForms.Remove(id);
                    }
                    catch { }
                    // Выключили активный модуль — вернуться на сессии.
                    if (string.Equals(_activeModuleId, id, System.StringComparison.OrdinalIgnoreCase))
                    {
                        _activeModuleId = null;
                        SwitchTab(SidebarTab.Sessions);
                    }
                    else RefreshModuleButtons();
                }
            }
            catch { }
        }

        // Updates the visual state of both sidebar tabs based on _activeTab.
        // Book tab: always #296AA0 filled (per user's spec). Sessions tab:
        // constant muted styling — no active/inactive colour change. Both
        // tabs invalidate so the active-arrow Paint handler redraws.
        // v2.3.12: arrows removed, but keeping the invalidation call since
        // future Paint hooks may need it.
        private void UpdateTabVisuals()
        {
            _bookTab.Invalidate();
            _zabbixAgentTab.Invalidate();
            _passwordsTab.Invalidate();
            RefreshModuleButtons();
        }

        // v3.7.1 — sidebar "Пароли" button opens the KeePass vault manager
        // (same singleton as the tray menu). Form is non-modal so the user
        // can keep it open while interacting with RDP Manager.
        private void OpenPasswordVault()
        {
            var form = Forms.KeePassVaultForm.Instance;
            form.Show();
            form.Activate();
        }

        // Lets the address-book grid check whether a session is currently
        // live for a given computer — used by the RDP indicator column.
        public bool HasSessionFor(string computer)
        {
            if (string.IsNullOrEmpty(computer)) return false;
            foreach (var s in _sessions)
            {
                if (string.Equals(s.Computer, computer, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        // All session computers (connected or pending) — used by the address
        // book to build its RDP-indicator lookup set. Mirrors HasSessionFor
        // semantics exactly so the indicator set matches the per-cell check.
        public List<string> GetSessionComputers()
        {
            var list = new List<string>(_sessions.Count);
            foreach (var s in _sessions) list.Add(s.Computer);
            return list;
        }

        // Fires whenever a session is added or closed. The address-book
        // grid subscribes so it can invalidate its RDP indicator column.
        public static event EventHandler? SessionListChanged;
        private static void RaiseSessionListChanged()
        {
            try { SessionListChanged?.Invoke(null, EventArgs.Empty); } catch { }
        }

        // Restart a single session — disconnect + reconnect to the same
        // target. Used by the sidebar context-menu "Перезапустить" item
        // and by double-click on a tab. Uses the cached shared password
        // (no re-prompt — if the password was wrong, the user already
        // found out on the first attempt). Falls back to per-session
        // override if set (v2.1.53).
        //
        // v2.1.55: async + 700ms delay between Disconnect and Connect.
        // ActiveX.Disconnect() is async — the underlying RDP transport
        // takes time to fully tear down. Calling Connect() immediately
        // after races with the disconnecting state and leaves a blank
        // white panel (user has to click Restart again to recover).
        private async void RestartSession(RdpSession sess)
        {
            // v3.0.7: if never activated yet (AutoOpen=false), just activate.
            if (sess.Client == null)
            {
                ActivateSession(sess);
                return;
            }
            try
            {
                Logger.Info($"RDP Manager: restarting session {sess.Computer} ({sess.Target})");
                sess.Client.Disconnect();
            }
            catch (Exception ex)
            {
                Logger.Warn($"RDP Manager: disconnect during restart failed: {ex.Message}");
            }
            // Let the ActiveX fully disconnect before reconnecting. Without
            // this, the next Connect() can be a no-op and the panel stays
            // white until the user clicks Restart a second time.
            try { await Task.Delay(700); } catch { }
            try
            {
                // v2.1.53: per-session override beats shared cache.
                string? login;
                string? password;
                if (sess.LoginOverride != null)
                {
                    login = sess.LoginOverride;
                    password = sess.PasswordOverride;
                    Logger.Info($"RDP Manager: using per-session override '{login}' for {sess.Computer}");
                }
                else
                {
                    var c = RdpCredentialStore.GetCached();
                    login = c?.Login;
                    password = c?.Password;
                }
                sess.Client.Connect(sess.Target, login, password);
            }
            catch (Exception ex)
            {
                Logger.Warn($"RDP Manager: reconnect during restart failed: {ex.Message}");
            }
        }

        // v2.1.53 — prompt for new login+password and restart the session
        // with them. The override lives only on the in-memory RdpSession
        // object — closing/reopening the manager clears it, so the next
        // launch uses the shared creds again (per user request).
        private void RestartWithDifferentCreds(RdpSession sess)
        {
            var newCreds = RdpPasswordPrompt.ShowPerSession(this, sess.Computer);
            if (newCreds == null) return;
            sess.LoginOverride = newCreds.Login;
            sess.PasswordOverride = newCreds.Password;
            _rdpPerSessionCreds[sess.Computer] = (newCreds.Login, newCreds.Password);
            // v3.7.0 — persist per-session creds into the KeePass vault when
            // it's open, so the next launch finds them in PupSeekRDP/RDP.
            if (KeePassVault.IsOpen)
                KeePassVault.SaveLogin(KeePassVault.RdpGroupPath, sess.Computer, newCreds.Login, newCreds.Password);
            RestartSession(sess);
        }

        // Closes a single session — disconnect, remove panel + sidebar item
        // + entry from _sessions. Persisted list updates so the session
        // doesn't come back on next manager open.
        private void CloseSession(RdpSession sess)
        {
            // v3.0.7: Client may be null for AutoOpen=false sessions never activated.
            if (sess.Client != null)
            {
                try
                {
                    Logger.Info($"RDP Manager: closing session {sess.Computer} ({sess.Target})");
                    sess.Client.Disconnect();
                }
                catch (Exception ex)
                {
                    Logger.Warn($"RDP Manager: disconnect during close failed: {ex.Message}");
                }
                _mainArea.Controls.Remove(sess.Client);
                sess.Client.Dispose();
            }

            _sessions.Remove(sess);

            // v2.1.49 — full sidebar re-render instead of trying to find
            // the individual item. Groups that no longer have any sessions
            // also disappear, and "Без группы" stays at the bottom.
            RenderSidebar();

            // If we had an active session and just removed it, activate another.
            if (_sessions.Count > 0)
                ShowSession(_sessions[0]);
            else if (Visible)
            {
                // No sessions left → hide to tray. Closing the form would
                // call FormClosing which clears _instance; we want the
                // singleton to survive so the user can re-open it via the
                // main-form button.
                Hide();
            }

            PersistSessions();
            RaiseSessionListChanged();
        }

        private void PersistSessions()
        {
            try
            {
                // v3.9.3 — transient-сессии (подключения из книги) на диск
                // не пишутся: ни следа после закрытия.
                RdpSessionStore.Save(_sessions.Where(s => !s.Transient).Select(s =>
                    new RdpSessionStore.Entry {
                        Computer = s.Computer,
                        Ip = string.IsNullOrEmpty(s.Ip) ? null : s.Ip,
                        Group = string.IsNullOrEmpty(s.Group) ? null : s.Group,
                        DisplayName = string.IsNullOrEmpty(s.DisplayName) ? null : s.DisplayName,
                        Notes = string.IsNullOrEmpty(s.Notes) ? null : s.Notes,
                        AutoOpen = s.AutoOpen,
                        SortOrder = s.SortOrder
                    }));
            }
            catch (Exception ex)
            {
                Logger.Warn($"RDP Manager: PersistSessions failed: {ex.Message}");
            }
        }

        // v3.8.x — "Экспорт/Импорт" menu actions.

        private void ExportAddressBook()
        {
            var entries = Services.AddressBookStore.Load();
            if (entries.Count == 0)
            {
                MessageBox.Show(this, "Книга пуста.", "Экспорт",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var groups = entries.Select(x => x.Group ?? "");
            var choice = Forms.ExportBookDialog.Show(this, groups);
            if (choice == null) return;
            var (groupFilter, columns) = choice.Value;

            using var sfd = new SaveFileDialog
            {
                Filter = "CSV (*.csv)|*.csv|Все файлы (*.*)|*.*",
                FileName = string.IsNullOrWhiteSpace(groupFilter)
                    ? "addressbook.csv"
                    : $"addressbook_{groupFilter}.csv",
                DefaultExt = "csv"
            };
            if (ShadowRDP.Helpers.UiModal.ShowDimmed(this, sfd) != DialogResult.OK) return;
            try
            {
                Services.BookCsvExporter.ExportToCsv(entries, groupFilter, columns, sfd.FileName);
                Logger.Info($"RDP Manager: exported entries (group='{groupFilter}', cols={columns.Count}) to {sfd.FileName}");
                MessageBox.Show(this, $"Экспортировано в:\n{sfd.FileName}", "Экспорт",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Logger.Warn($"RDP Manager: export failed: {ex.Message}");
                MessageBox.Show(this, $"Ошибка экспорта: {ex.Message}", "Экспорт",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ExportSessions()
        {
            if (_sessions.Count == 0 && _customSessions.Count == 0)
            {
                MessageBox.Show(this, "В сайдбаре нет сессий.", "Экспорт сессий",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using var sfd = new SaveFileDialog
            {
                Filter = "JSON (*.json)|*.json|Все файлы (*.*)|*.*",
                FileName = "sessions.json",
                DefaultExt = "json"
            };
            if (ShadowRDP.Helpers.UiModal.ShowDimmed(this, sfd) != DialogResult.OK) return;
            try
            {
                var rdp = _sessions.Where(s => !s.Transient).Select(s => new RdpSessionStore.Entry
                {
                    Computer = s.Computer,
                    Ip = string.IsNullOrEmpty(s.Ip) ? null : s.Ip,
                    Group = string.IsNullOrEmpty(s.Group) ? null : s.Group,
                    AutoOpen = s.AutoOpen
                });
                Services.SessionExportService.Write(sfd.FileName, rdp, _customSessions);
                Logger.Info($"RDP Manager: exported {rdp.Count()} RDP + {_customSessions.Count} custom session(s) to {sfd.FileName}");
                MessageBox.Show(this, $"Экспортировано в:\n{sfd.FileName}", "Экспорт сессий",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Logger.Warn($"RDP Manager: sessions export failed: {ex.Message}");
                MessageBox.Show(this, $"Ошибка экспорта: {ex.Message}", "Экспорт сессий",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ImportAddressBook()
        {
            using var ofd = new OpenFileDialog
            {
                Filter = "CSV (*.csv)|*.csv|Все файлы (*.*)|*.*",
                DefaultExt = "csv",
                Title = "Импорт книги из CSV"
            };
            if (ShadowRDP.Helpers.UiModal.ShowDimmed(this, ofd) != DialogResult.OK) return;

            List<Models.AddressBookEntry> imported;
            try
            {
                imported = Services.BookCsvImporter.ImportFromCsv(ofd.FileName);
            }
            catch (Exception ex)
            {
                Logger.Warn($"RDP Manager: book import read failed: {ex.Message}");
                MessageBox.Show(this, $"Не удалось прочитать файл: {ex.Message}", "Импорт книги",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (imported.Count == 0)
            {
                MessageBox.Show(this, "В файле нет ни одной записи.", "Импорт книги",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var entries = Services.AddressBookStore.Load();
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in entries)
                existing.Add($"{e.Computer}\u0001{e.User}");

            int added = 0, skipped = 0;
            foreach (var e in imported)
            {
                if (existing.Contains($"{e.Computer}\u0001{e.User}")) { skipped++; continue; }
                entries.Add(e);
                existing.Add($"{e.Computer}\u0001{e.User}");
                added++;
            }
            if (added == 0)
            {
                MessageBox.Show(this, $"Новых записей нет.\nПропущено: {skipped} (уже в книге).",
                    "Импорт книги", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Services.AddressBookStore.Save(entries);
            if (Forms.AddressBookForm.IsOpen) Forms.AddressBookForm.Instance.ReloadFromStore();
            Logger.Info($"RDP Manager: imported {added} entry(ies) from {ofd.FileName} ({skipped} skipped)");
            MessageBox.Show(this, $"Импортировано: +{added} новых, пропущено {skipped} (уже в книге).",
                "Импорт книги", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ImportSessions()
        {
            using var ofd = new OpenFileDialog
            {
                Filter = "JSON (*.json)|*.json|Все файлы (*.*)|*.*",
                DefaultExt = "json",
                Title = "Импорт сессий из JSON"
            };
            if (ShadowRDP.Helpers.UiModal.ShowDimmed(this, ofd) != DialogResult.OK) return;

            List<RdpSessionStore.Entry> importedRdp;
            List<Models.CustomSession> importedCustom;
            try
            {
                (importedRdp, importedCustom) = Services.SessionExportService.Read(ofd.FileName);
            }
            catch (Exception ex)
            {
                Logger.Warn($"RDP Manager: sessions import read failed: {ex.Message}");
                MessageBox.Show(this, $"Не удалось прочитать файл: {ex.Message}", "Импорт сессий",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (importedRdp.Count == 0 && importedCustom.Count == 0)
            {
                MessageBox.Show(this, "В файле нет ни одной сессии.", "Импорт сессий",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            int rdpAdded = 0, rdpSkipped = 0, customAdded = 0, customSkipped = 0;

            // RDP sessions: reuse existing, otherwise add as sidebar-only
            // (no mstsc connect — import shouldn't launch a batch of windows).
            foreach (var e in importedRdp)
            {
                var exists = _sessions.Any(s =>
                    string.Equals(s.Computer, e.Computer, StringComparison.OrdinalIgnoreCase));
                if (exists) { rdpSkipped++; continue; }
                AddSession(e.Computer, e.Ip, e.Group, autoOpen: false);
                rdpAdded++;
            }

            // SSH/Web shortcuts: merge, dedup by Kind+Target.
            foreach (var c in importedCustom)
            {
                var exists = _customSessions.Any(x =>
                    x.Kind == c.Kind &&
                    string.Equals(x.Target, c.Target, StringComparison.OrdinalIgnoreCase));
                if (exists) { customSkipped++; continue; }
                if (string.IsNullOrEmpty(c.Id)) c.Id = Guid.NewGuid().ToString("N");
                _customSessions.Add(c);
                customAdded++;
            }

            Services.CustomSessionStore.Save(_customSessions);
            PersistSessions();
            RenderSidebar();

            Logger.Info($"RDP Manager: imported {rdpAdded} RDP + {customAdded} custom session(s) from {ofd.FileName}");
            MessageBox.Show(this,
                $"Импортировано:\n  RDP: +{rdpAdded} новых, пропущено {rdpSkipped}\n  SSH/Web: +{customAdded} новых, пропущено {customSkipped}",
                "Импорт сессий", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ShowSession(RdpSession active)
        {
            _activeSession = active;
            UpdateSessionInfoPills(active);
            // v3.9.3CT — прячем всё, что может лежать поверх клиента:
            // дашборд, книгу, модули, SFTP (было: из Даши переход не виден).
            try { if (_dashHost != null && !_dashHost.IsDisposed) _dashHost.Visible = false; } catch { }
            try { if (_bookHost != null && !_bookHost.IsDisposed) _bookHost.Visible = false; } catch { }
            try { HideModuleHosts(); } catch { }
            try { if (_activeSftpForm != null && !_activeSftpForm.IsDisposed) _activeSftpForm.Hide(); } catch { }
            // v2.1.93: also hide all custom (SSH/Web) placeholder panels —
            // when the user clicks an RDP session item after viewing a Web
            // session, the WebView2 panel was staying visible because it
            // wasn't being hidden.
            _activeCustomSessionId = null;
            foreach (var c in _customPlaceholders.Values) c.Visible = false;
            // v3.3.20 — hide the embedded SFTP form so it doesn't cover the
            // newly-activated RDP session. SFTP is a Form embedded in
            // _mainArea with Dock=Fill; without this, switching tabs left it
            // on top of everything.
            if (_activeSftpForm != null && !_activeSftpForm.IsDisposed)
                _activeSftpForm.Visible = false;
            // v3.5.4 — hide embedded console sessions when switching to RDP.
            _psSession?.Hide();
            _cmdSession?.Hide();
            // v3.6.8 — hide embedded consoles too.
            _activeEmbedded = null;
            foreach (var es in _embeddedSessions)
                if (es.Control.IsEmbedded) es.Control.Visible = false;
            UpdateEmbedIcons();
            ClearConsoleActive();
            foreach (var s in _sessions)
            {
                if (s.Client == null) continue;
                s.Client.Visible = (s == active);
                if (s == active) s.Client.RefreshSize();
            }
            // Update sidebar highlight + repaint so the active-arrow shows.
            foreach (Control c in _sidebarList.Controls)
            {
                if (c.Tag is RdpSession rs)
                {
                    // v2.3.9: inactive bg = dark sidebar, active = Card (light).
                    // ForeColor must follow so text stays readable on either bg.
                    bool activeRow = rs == active;
                    c.BackColor = activeRow ? Helpers.UiTheme.Card : SidebarBg;
                    UpdateItemLabelColors(c, activeRow);
                    // v3.0.15 — do NOT recolour the status dot here. The dot
                    // reflects connection state (Client != null), not tab
                    // selection. It's updated only when Client changes — see
                    // ActivateSession (null → rdp, grey → green).
                    c.Invalidate();  // trigger Paint so arrow redraws
                }
                else if (c.Tag is Models.CustomSession cs)
                {
                    c.BackColor = SidebarBg;
                    UpdateItemLabelColors(c, active: false);
                    c.Invalidate();
                }
            }
        }
        private RdpSession? _activeSession;
        // v3.0.19 — header info pills next to "Подключено": show the active
        // session's computer name + IP. Different colour from "Подключено"
        // so they read as separate facts. Updated from ShowSession.
        private Label _statusBadge = null!;
        private Label _sessionNameLbl = null!;
        private Label _sessionIpLbl = null!;
        private Label _terminalLbl = null!;
        private Label _hdrTitle = null!;
        // v3.9.3CO — сессия за фактами шапки (клик → в её окно).
        private RdpSession? _pillSession;
        private Controls.ConsoleSessionControl? _activeConsole;

        // === v2.1.49 — Sidebar rendering with groups ===
        // Replaces the old per-item AddSidebarItem. Clears _sidebarList
        // and re-renders everything from scratch:
        //   for each unique group (custom groups alphabetically, then "Без группы"):
        //     - group header (clickable, collapses/expands)
        //     - session items inside (indented 14px), hidden if collapsed
        //
        // Called from AddSession, CloseSession, SetSessionGroup,
        // RenameGroup, DeleteGroup — anything that mutates the grouping.
        // v2.2.25: manual sidebar scroll — shifts _sidebarList.Location.Y
        // within _sidebarScrollHost. No AutoScroll, no scrollbar, no flicker.
        private void OnSidebarWheel(object? sender, MouseEventArgs e)
        {
            ScrollSidebar(e.Delta);
        }

        // Manual sidebar scroll — shifts _sidebarList.Y within
        // _sidebarScrollHost. No AutoScroll, no scrollbar, no flicker.
        // Returns true if the offset actually changed (so the global
        // WheelRouter knows whether to swallow the message).
        private bool ScrollSidebar(int delta)
        {
            if (_sidebarList.Controls.Count == 0) return false;
            int totalH = _sidebarList.Height;
            int visibleH = _sidebarScrollHost.Height;
            if (totalH <= visibleH) return false;  // nothing to scroll
            int maxScroll = totalH - visibleH;
            int d = delta > 0 ? 40 : -40;  // 40px per wheel notch
            int newOffset = Math.Clamp(_sidebarScrollOffset - d, 0, maxScroll);
            if (newOffset == _sidebarScrollOffset) return false;
            _sidebarScrollOffset = newOffset;
            _sidebarList.Location = new Point(0, -_sidebarScrollOffset);
            return true;
        }

        // v2.3.9 — Recolour the name/IP Labels inside a session row when its
        // active state changes. Names use the "nameLbl"/"ipLbl" Name tags set
        // in AddSessionItem / AddCustomSessionItem. Active row = dark text on
        // Card; inactive = white / light-blue-grey on dark sidebar.
        private static void UpdateItemLabelColors(Control item, bool active)
        {
            foreach (Control child in item.Controls)
            {
                if (child is not Label lbl) continue;
                if (lbl.Name == "nameLbl")
                    lbl.ForeColor = active ? Helpers.UiTheme.Foreground : Color.White;
                else if (lbl.Name == "ipLbl")
                    lbl.ForeColor = active ? Helpers.UiTheme.MutedForeground : SidebarSubFg;
            }
        }

        // v3.0.15 — Find this session's tab in the sidebar and recolour its
        // status dot in place. Used by ActivateSession after sess.Client is
        // materialised (grey → green) without forcing a full RenderSidebar.
        private void UpdateSessionStatusDot(RdpSession sess, Color color)
        {
            foreach (Control c in _sidebarList.Controls)
            {
                if (c.Tag is RdpSession rs && ReferenceEquals(rs, sess))
                {
                    foreach (Control child in c.Controls)
                    {
                        if (child.Name == "statusDot" || (child.Tag is string t && t == "statusDot"))
                        {
                            if (child.BackColor != color)
                            {
                                child.BackColor = color;
                                child.Refresh();
                            }
                            return;
                        }
                    }
                    return;
                }
            }
        }

        // v3.0.19 — refresh the header info pills (computer name + IP) for
        // the active session. Pills are placed to the right of "Подключено"
        // and repositioned each call because AutoSize width changes with
        // the text. Null session → hide both pills.
        // v3.0.22 — split into a universal core (name + target strings) plus
        // overloads for RdpSession and CustomSession so Web/SSH tabs also
        // drive the header pills.
        private void UpdateSessionInfoPills(string? name, string? target)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                _sessionNameLbl.Visible = false;
                _sessionIpLbl.Visible = false;
                LayoutHeaderLeftBlock();
                return;
            }
            _sessionNameLbl.Tag = name!;
            _sessionNameLbl.Visible = true;
            SizeHeaderFact(_sessionNameLbl);

            if (!string.IsNullOrWhiteSpace(target))
            {
                _sessionIpLbl.Tag = target!;
                _sessionIpLbl.Visible = true;
                SizeHeaderFact(_sessionIpLbl);
            }
            else
            {
                _sessionIpLbl.Visible = false;
            }

            LayoutHeaderLeftBlock();
        }

        private void UpdateSessionInfoPills(RdpSession? sess)
        {
            _pillSession = sess;
            if (sess == null || string.IsNullOrWhiteSpace(sess.Computer))
            {
                UpdateSessionInfoPills(null, null);
                return;
            }
            UpdateSessionInfoPills(SessName(sess), sess.Ip);
        }

        // v3.9.3CO — клик по Name/IP: в окно открытой сессии этой записи.
        private void JumpToPillSession()
        {
            try
            {
                var sess = _pillSession;
                if (sess == null)
                {
                    Logger.Info("RDP Manager: pill click — no session attached");
                    return;
                }
                Logger.Info($"RDP Manager: pill click → {sess.Computer}");
                ActivateSession(sess);
            }
            catch (Exception ex) { Logger.Warn("RDP Manager: pill click failed: " + ex.Message); }
        }

        // v3.5.4 — show which terminal (PowerShell / CMD) is active.
        private void ShowTerminalIndicator(string title)
        {
            _terminalLbl.Tag = title;
            _terminalLbl.Visible = true;
            SizeHeaderFact(_terminalLbl);
            LayoutHeaderLeftBlock();
        }

        private void HideTerminalIndicator()
        {
            _terminalLbl.Visible = false;
            LayoutHeaderLeftBlock();
        }

        // v3.9.3CM — эксперимент: факты шапки текстом с иконкой (без фонов),
        // цвет текста — от фона области (как в Даше).
        private void PaintHeaderFact(Label lbl, Graphics g, string iconKey)
        {
            try
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                bool light = Helpers.UiTheme.RdpHeaderFg.ToArgb() != Color.White.ToArgb();
                Image? glyph = null;
                try
                {
                    var raw = Helpers.MenuIcons.Get(iconKey);
                    glyph = light ? Helpers.MenuIcons.Darken(raw) : Helpers.UiButtons.TintWhite(raw);
                }
                catch { }
                if (glyph != null)
                    g.DrawImage(glyph, new Rectangle(0, (lbl.Height - 16) / 2, 16, 16));
                string txt = lbl.Tag as string ?? "";
                TextRenderer.DrawText(g, txt, lbl.Font,
                    new Rectangle(20, 0, Math.Max(0, lbl.Width - 20), lbl.Height),
                    Helpers.UiTheme.RdpHeaderFg,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            }
            catch { }
        }

        private static void SizeHeaderFact(Label lbl)
        {
            try
            {
                int tw = TextRenderer.MeasureText(lbl.Tag as string ?? "", lbl.Font).Width;
                lbl.Size = new Size(20 + tw + 2, 22);
            }
            catch { }
        }

        private void ApplyRdpHeaderColors()
        {
            try
            {
                if (_headerPanel != null && !_headerPanel.IsDisposed)
                    _headerPanel.BackColor = Helpers.UiTheme.RdpHeaderBg;
                if (_hdrTitle != null && !_hdrTitle.IsDisposed)
                    _hdrTitle.ForeColor = Helpers.UiTheme.RdpHeaderFg;
                foreach (var lbl in new[] { _statusBadge, _sessionNameLbl, _sessionIpLbl, _terminalLbl })
                {
                    if (lbl == null || lbl.IsDisposed) continue;
                    lbl.ForeColor = Helpers.UiTheme.RdpHeaderFg;
                    lbl.BackColor = Color.Transparent;
                    try { lbl.Invalidate(); } catch { }
                }
            }
            catch { }
        }

        // v3.6.8 — lays out the header's left block left→right: session
        // name/ip pills → terminal pill → "Встроить ММС" button →
        // running-console pill buttons. Called from header.Layout and every
        // time pill visibility/text changes so the MMC button always sits
        // right after the last pill (user request: after Иконка / RDP
        // Менеджер / Подключено / Сеанс / IP).
        private void LayoutHeaderLeftBlock()
        {
            // v3.9.3CO — факты вплотную к «Подключено» (было +8/+6).
            // v3.9.3CP — Name/IP на 5px левее и 2px выше; наверх z-порядка.
            // v3.9.3CT — ещё на 5px левее.
            int x = _statusBadge.Right - 6;
            if (_sessionNameLbl.Visible)
            {
                _sessionNameLbl.Location = new Point(x, 8);
                try { _sessionNameLbl.BringToFront(); } catch { }
                x = _sessionNameLbl.Right + 4;
            }
            if (_sessionIpLbl.Visible)
            {
                _sessionIpLbl.Location = new Point(x, 8);
                try { _sessionIpLbl.BringToFront(); } catch { }
                x = _sessionIpLbl.Right + 4;
            }
            // v3.9 — each open console (CMD/PS/MMC…) gets its own header pill,
            // laid out left→right and persisting while the console is open
            // (clicking a pill re-activates that console; ✕ closes it).
            foreach (var pill in _consolePills.Values)
            {
                pill.Location = new Point(x, 0);
                x = pill.Right;
            }
            // v3.9 — "Встроить ММС" moved into the Инструменты dropdown, so
            // running-console tabs now grow directly right of the session
            // pills (full header height, active one touching the bottom edge).
            // Tabs sit flush against each other (no gap between open consoles).
            int px = x;
            foreach (var es in _embeddedSessions)
            {
                if (es.PillButton == null) continue;
                es.PillButton.Location = new Point(px, 0);
                px = es.PillButton.Right;
            }
        }

        // v3.0.22 — Custom (Web/SSH) sessions. Host is extracted from URL
        // for Web so we don't show the path; SSH shows the raw Target
        // (host or host:port).
        private void UpdateSessionInfoPills(Models.CustomSession? sess)
        {
            _pillSession = null;
            if (sess == null || string.IsNullOrWhiteSpace(sess.Name))
            {
                UpdateSessionInfoPills(null, null);
                return;
            }
            string target = sess.Target ?? "";
            if (sess.Kind == Models.SessionKind.Web && !string.IsNullOrWhiteSpace(target))
            {
                try
                {
                    if (Uri.TryCreate(target, UriKind.Absolute, out var uri)) target = uri.Host;
                    else if (Uri.TryCreate("https://" + target, UriKind.Absolute, out var uri2)) target = uri2.Host;
                }
                catch { /* leave target as-is */ }
            }
            UpdateSessionInfoPills(sess.Name, target);
        }

        // v2.3.10 — Returns a copy of `src` where each RGB channel is
        // interpolated toward white by `amount` (0 = original, 1 = full
        // white). Used for SSH/Web icons so they read against the dark
        // sidebar. Alpha is preserved.
        private static Image Lighten(Image src, float amount = 0.5f)
        {
            var bmp = new Bitmap(src.Width, src.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.Transparent);
            using var attrs = new System.Drawing.Imaging.ImageAttributes();
            var k = 1f - amount;
            var matrix = new System.Drawing.Imaging.ColorMatrix(new[]
            {
                new[] { k, 0f, 0f, 0f, 0f },
                new[] { 0f, k, 0f, 0f, 0f },
                new[] { 0f, 0f, k, 0f, 0f },
                new[] { 0f, 0f, 0f, 1f, 0f },
                new[] { amount, amount, amount, 0f, 1f }
            });
            attrs.SetColorMatrix(matrix);
            var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            g.DrawImage(src, rect, 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
            return bmp;
        }

        // v2.3.11 — Circular status dot for session items. Region-clipped
        // to ellipse so the otherwise-square Panel paints as a circle.
        // v3.0.14 — subclass removed (Refresh/OnPaint/Region all tried);
        // back to plain Panel + Region, with explicit Refresh() called by
        // the consumer after BackColor change (see ShowSession).
        // v2.3.12: right padding 8 → 13 (+5px per user request).
        // v3.0.14: default size 6 → 8.
        private static Panel CreateStatusDot(Color color, int size, int itemHeight, int itemWidth)
        {
            const int rightPad = 13;
            var dot = new Panel
            {
                Name = "statusDot",
                Tag = "statusDot",
                Size = new Size(size, size),
                // v3.0.16: shift 3px up per user request (was vertical-centre).
                Location = new Point(itemWidth - size - rightPad, (itemHeight - size) / 2 - 3),
                BackColor = Color.Transparent
            };
            // v4.8.1 — круг гладкой отрисовкой (был Region: лесенка).
            var dotColor = color;
            dot.Paint += (s, e) =>
            {
                try
                {
                    e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    using var br = new SolidBrush(dotColor);
                    e.Graphics.FillEllipse(br, 0, 0, size - 1, size - 1);
                }
                catch { }
            };
            return dot;
        }

        private void RenderSidebar()
        {
            _sidebarScrollOffset = 0;  // v2.2.25: reset scroll on re-render
            _sidebarList.SuspendLayout();
            try
            {
                // Dispose old controls so we don't leak Paint/Click handlers.
                _sidebarList.Controls.Clear();

                // v2.1.83: combine RDP + custom sessions for grouping.
                // Both share the Group field, so they mix naturally.
                // v3.9.3 — transient-сессии в сайдбар не попадают.
                var orderedGroups = _sessions
                    .Where(s => !s.Transient)
                    .Select(s => s.Group ?? "")
                    .Concat(_customSessions.Select(c => c.Group ?? ""))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(g => g == "" ? "~~" : g.ToUpperInvariant())
                    .ToList();

                // v2.3.10: index used to pass isFirst to AddGroupHeader (no
                // top margin for the topmost group).
                for (int gi = 0; gi < orderedGroups.Count; gi++)
                {
                    var group = orderedGroups[gi];
                    var rdpInGroup = _sessions
                        .Where(s => !s.Transient && string.Equals(s.Group ?? "", group, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    var customInGroup = _customSessions
                        .Where(c => string.Equals(c.Group ?? "", group, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    var total = rdpInGroup.Count + customInGroup.Count;
                    if (total == 0) continue;

                    var groupName = string.IsNullOrEmpty(group) ? "Без группы" : group;
                    var collapsed = _collapsedGroups.Contains(group);

                    AddGroupHeader(group, groupName, total, collapsed, isFirst: gi == 0);
                    if (!collapsed)
                    {
                        // v3.8.x: merge RDP + custom and sort by SortOrder so a
                        // drag&drop reorder is respected. Legacy data (all
                        // SortOrder=0) falls back to RDP-first-then-custom with
                        // the original within-type order, matching pre-3.8.
                        var orderedItems = rdpInGroup
                            .Select((s, i) => ((RdpSession?)s, (Models.CustomSession?)null, false, i))
                            .Concat(customInGroup
                                .Select((c, i) => ((RdpSession?)null, (Models.CustomSession?)c, true, i)))
                            .OrderBy(x => x.Item1?.SortOrder ?? x.Item2!.SortOrder)
                            .ThenBy(x => x.Item3 ? 1 : 0)
                            .ThenBy(x => x.Item4)
                            .ToList();
                        foreach (var x in orderedItems)
                        {
                            if (x.Item3) AddCustomSessionItem(x.Item2!);
                            else AddSessionItem(x.Item1!);
                        }
                    }
                }
            }
            finally
            {
                _sidebarList.ResumeLayout(true);
            }
        }

        // v3.8.x — collapse/expand a group WITHOUT rebuilding the sidebar:
        // toggle the flag, update the header arrow in place and hide/show the
        // group's session items. FlowLayoutPanel re-flows around invisible
        // controls automatically, so nothing is recreated and the current
        // scroll position is kept (clamped) instead of resetting to the top.
        private void ToggleGroupCollapse(string groupKey, Panel header)
        {
            bool collapsed;
            if (_collapsedGroups.Contains(groupKey))
            {
                _collapsedGroups.Remove(groupKey);
                collapsed = false;
            }
            else
            {
                _collapsedGroups.Add(groupKey);
                collapsed = true;
            }

            foreach (Control c in header.Controls)
            {
                if (c is not Label lbl) continue;
                // v3.8.x: collapsed headers get the warm highlight colour.
                if (lbl.Name == "arrowLbl")
                {
                    lbl.Text = collapsed ? "▶" : "▼";
                    lbl.ForeColor = collapsed ? GroupTitleCollapsedFg : GroupTitleFg;
                }
                else if (lbl.Name == "titleLbl")
                    lbl.ForeColor = collapsed ? GroupTitleCollapsedFg : GroupTitleFg;
            }

            foreach (Control c in _sidebarList.Controls)
            {
                string? itemGroup = (c.Tag as RdpSession)?.Group
                    ?? (c.Tag as Models.CustomSession)?.Group;
                if (itemGroup == null) continue;
                if (string.Equals(itemGroup, groupKey, StringComparison.OrdinalIgnoreCase))
                    c.Visible = !collapsed;
            }

            int maxScroll = Math.Max(0, _sidebarList.Height - _sidebarScrollHost.Height);
            if (_sidebarScrollOffset > maxScroll) _sidebarScrollOffset = maxScroll;
            _sidebarList.Location = new Point(0, -_sidebarScrollOffset);
            _sidebarList.PerformLayout();
        }

        // Group header — bold label with ▼ (expanded) / ▶ (collapsed) +
        // session count. Click toggles collapse state (in-place since v3.8.x).
        // Right-click → context menu (Rename / Delete).
        private void AddGroupHeader(string groupKey, string displayName, int count, bool collapsed, bool isFirst = false)
        {
            // v2.1.51: group header gets a DARKER band than the sidebar bg
            // so groups read as section dividers, not just another row.
            // v2.3.9: with dark sidebar (#1E3A5F), band was #152F4D, text white.
            // v2.3.10: band 26 → 18, title font 8F → 6.5F (third smaller per
            // user), ForeColor → muted grey, band bg darkened to #0A1A2E,
            // margins 15 top (except first group) + 15 bottom.
            // Hierarchy: Card (lightest) = active session, SidebarBg =
            // sidebar bg + inactive sessions, SidebarGroupBg = group bands.
            var header = new Panel
            {
                Width = 166,
                Height = 18,
                Margin = new Padding(0, isFirst ? 0 : 15, 0, 15),
                BackColor = SidebarGroupBg,
                Cursor = Cursors.Hand,
                Tag = groupKey  // empty string for "Без группы"
            };
            var arrow = new Label
            {
                Name = "arrowLbl",
                Text = collapsed ? "▶" : "▼",
                Location = new Point(8, 2),
                Size = new Size(16, 14),
                Font = new Font("Segoe UI", 7F),
                ForeColor = collapsed ? GroupTitleCollapsedFg : GroupTitleFg,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft
            };
            header.Controls.Add(arrow);
            var title = new Label
            {
                Name = "titleLbl",
                Text = $"{displayName.ToUpperInvariant()}  ({count})",
                Location = new Point(26, 2),
                Size = new Size(130, 14),
                // v2.3.10: 8F → 6.5F (third smaller).
                Font = new Font("Segoe UI", 6.5F),
                ForeColor = collapsed ? GroupTitleCollapsedFg : GroupTitleFg,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft
            };
            header.Controls.Add(title);

            // Click toggles collapse for this group (skip "Без группы" —
            // no point hiding the catch-all).
            EventHandler toggleHandler = (s, e) =>
            {
                if (string.IsNullOrEmpty(groupKey)) return;
                // v3.8.x: in-place toggle (no full sidebar rebuild).
                ToggleGroupCollapse(groupKey, header);
            };
            header.Click += toggleHandler;
            arrow.Click += toggleHandler;
            title.Click += toggleHandler;

            // v3.9.0 — right-click menu shown as a themed popup.
            if (!string.IsNullOrEmpty(groupKey))
            {
                MouseEventHandler groupDown = (s, e) =>
                {
                    if (e.Button != MouseButtons.Right) return;
                    var menu = new Helpers.ThemedContextMenu();
                    menu.AddHeader("Группа", groupKey);
                    menu.AddItem("Переименовать", "info", () => RenameGroup(groupKey));
                    menu.AddItem("Удалить", "delete", () => DeleteGroup(groupKey));
                    var ctl = (Control)s;
                    menu.Show(this, ctl.PointToScreen(new Point(e.X, e.Y)));
                };
                header.MouseDown += groupDown;
                arrow.MouseDown += groupDown;
                title.MouseDown += groupDown;
            }

            _sidebarList.Controls.Add(header);
        }

        // Single session item — same look as the old AddSidebarItem but
        // indented 14px to read as a child of its group header.
        private void AddSessionItem(RdpSession sess)
        {
            const int itemWidth = 166;
            // v2.3.4: 42 → 38 (−2 top + −2 bottom per user request). Hover
            // bg is the panel itself, so it shrinks automatically.
            const int itemHeight = 38;

            var item = new Panel
            {
                Width = itemWidth,
                Height = itemHeight,
                // v2.1.51: was Padding(14, 0, 0, 4) — that left a 14px gap
                // on the left where the sidebar bg (Muted) showed through,
                // breaking the active highlight (Card #FAF7F4). The panel
                // must span the full 166px sidebar edge-to-edge so the
                // highlight reaches both borders; the visual indent for
                // "child of group header" comes from positioning the dot
                // at x=14 and the text at x=30 *inside* the panel.
                Margin = new Padding(0, 0, 0, 4),
                // v2.3.9: inactive bg = dark sidebar (was Muted).
                BackColor = _activeSession == sess ? Helpers.UiTheme.Card : SidebarBg,
                Cursor = Cursors.Hand,
                Tag = sess
            };

            // v2.3.11: server icon at left (was status dot); status moved to
            // right edge as a small circle. Icon lightened to read on dark bg.
            var icon = new PictureBox
            {
                Image = Lighten(Helpers.MenuIcons.Get("computer"), 0.5f),
                SizeMode = PictureBoxSizeMode.Normal,
                Size = new Size(16, 16),
                Location = new Point(10, (itemHeight - 16) / 2),
                BackColor = Color.Transparent
            };
            item.Controls.Add(icon);

            // Status dot — small circle at the right edge (v2.3.11).
            // v3.0.15: green if the session is connected (Client != null),
            // grey if pending (AutoOpen=false, not yet activated). Reflects
            // connection state, NOT tab selection.
            bool isConnected = sess.Client != null;
            item.Controls.Add(CreateStatusDot(
                isConnected ? Helpers.UiTheme.StatusOnline : Color.FromArgb(0x70, 0x80, 0x95),
                6, itemHeight, itemWidth));

            // Computer name — top line (v4.9.34: DisplayName иначе Computer).
            // v2.3.9: white text on dark sidebar; on active (Card) keep dark.
            // v2.3.11: width shortened so the right-edge dot doesn't overlap.
            bool isActive = _activeSession == sess;
            var name = new Label
            {
                Name = "nameLbl",
                Text = SessName(sess),
                Location = new Point(30, 5),
                Size = new Size(itemWidth - 30 - 24, 16),
                Font = new Font("Segoe UI", 9F),
                ForeColor = isActive ? Helpers.UiTheme.Foreground : Color.White,
                BackColor = Color.Transparent
            };
            item.Controls.Add(name);

            // IP — bottom line.
            if (!string.IsNullOrEmpty(sess.Ip))
            {
                var ipLbl = new Label
                {
                    Name = "ipLbl",
                    Text = sess.Ip,
                    Location = new Point(30, 22),
                    Size = new Size(itemWidth - 30 - 24, 13),
                    // v2.1.95: was 8F — slightly smaller so IP reads as a
                    // secondary line under the computer name.
                    Font = new Font("Consolas", 7.5F),
                    ForeColor = isActive ? Helpers.UiTheme.MutedForeground : SidebarSubFg,
                    BackColor = Color.Transparent
                };
                item.Controls.Add(ipLbl);
            }

            // v2.3.12: active-session arrow removed per user request.
            item.Paint += (s, e) => { };

            // Hover.
            item.MouseEnter += (s, e) =>
            {
                if (item.BackColor != Helpers.UiTheme.Card)
                    item.BackColor = SidebarItemHover;
            };
            item.MouseLeave += (s, e) =>
            {
                if (item.Tag is RdpSession rs && _activeSession != rs)
                    item.BackColor = SidebarBg;
            };

            // Click → switch session (+ switch to Sessions tab if on Book).
            // v3.0.7: ActivateSession creates RdpClient on first click for
            // AutoOpen=false sessions.
            EventHandler clickHandler = (s, e) =>
            {
                // v3.8.x: swallow the Click that follows a drag reorder.
                if (_suppressClick) { _suppressClick = false; return; }
                if (_activeTab != SidebarTab.Sessions)
                    SwitchTab(SidebarTab.Sessions);
                ActivateSession(sess);
            };
            item.Click += clickHandler;
            foreach (Control child in item.Controls)
            {
                child.Click += clickHandler;
                child.Cursor = Cursors.Hand;
            }

            // Double-click → restart this session.
            EventHandler dblClickHandler = (s, e) => RestartSession(sess);
            item.DoubleClick += dblClickHandler;
            foreach (Control child in item.Controls)
                child.DoubleClick += dblClickHandler;

            // v3.9.0 — right-click menu shown on demand as a themed popup.
            MouseEventHandler downHandler = (s, e) =>
            {
                if (e.Button != MouseButtons.Right) return;
                ActivateSession(sess);
                var menu = Helpers.ComputerContextMenu.BuildThemed(
                    owner: this,
                    computerResolver: () => sess.Computer);
                menu.InsertItem(2, "Войти под другой учётной записью…", "person", () => RestartWithDifferentCreds(sess));
                menu.InsertSubmenu(3, "Автозагрузка", "restart", sub =>
                {
                    sub.AddItem("Да", "on_green", () => SetSessionAutoOpen(sess, true), null, sess.AutoOpen);
                    sub.AddItem("Нет", "off_red", () => SetSessionAutoOpen(sess, false), null, !sess.AutoOpen);
                });
                // v4.9.29 — редактирование данных сессии (имя, IP, группа).
                menu.AddItem("Редактировать запись…", "info", () => EditRdpSession(sess));
                menu.AddSeparator();
                menu.AddSubmenu("Группа", "list", sub =>
                {
                    foreach (var g in _sessions
                        .Select(x => x.Group ?? "")
                        .Where(g => !string.IsNullOrEmpty(g))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(g => g.ToUpperInvariant()))
                    {
                        var captured = g;
                        sub.AddItem(g, "folder", () => SetSessionGroup(sess, captured), null,
                            string.Equals(g, sess.Group ?? "", StringComparison.OrdinalIgnoreCase));
                    }
                    sub.AddSeparator();
                    sub.AddItem("Без группы", "folder", () => SetSessionGroup(sess, ""), null,
                        string.IsNullOrEmpty(sess.Group));
                    sub.AddSeparator();
                    sub.AddItem("Создать новую…", "add", () =>
                    {
                        var name = PromptDialog.Show(this, "Новая группа", "Введите имя новой группы:", "");
                        if (string.IsNullOrWhiteSpace(name)) return;
                        SetSessionGroup(sess, name.Trim());
                    });
                });
                menu.AddSeparator();
                menu.AddItem("Перезапустить сеанс", "restart", () => RestartSession(sess));
                menu.AddItem("Закрыть", "delete", () => CloseSession(sess));
                menu.AddSeparator();
                // v4.9.34 — "+ Сессия" пунктом меню (меню пустой области убрано).
                menu.AddItem("+  Сессия…", "add", () => AddSessionViaSidebar());
                var ctl = (Control)s;
                menu.Show(this, ctl.PointToScreen(new Point(e.X, e.Y)));
            };
            item.MouseDown += downHandler;
            foreach (Control child in item.Controls)
                child.MouseDown += downHandler;

            // v3.8.x — drag&drop reorder (left button, >6px threshold).
            AttachDragReorder(item);

            _sidebarList.Controls.Add(item);
        }

        // v2.2.5/9 — Custom-styled "Информация о соединении" dialog using a
        // 2-column DataGridView like ComputerInfoForm (Параметр / Значение).
        // Was a stack of Label rows — user wanted the same Excel-like look.
        private void ShowWebConnectionInfo(Models.CustomSession sess)
        {
            // Parse host out of URL.
            string host = sess.Target;
            try
            {
                if (Uri.TryCreate(sess.Target, UriKind.Absolute, out var uri))
                    host = uri.Host;
                else if (!sess.Target.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    if (Uri.TryCreate("https://" + sess.Target, UriKind.Absolute, out var uri2))
                        host = uri2.Host;
                }
            }
            catch { }

            string ips;
            try
            {
                var addrs = System.Net.Dns.GetHostAddresses(host);
                ips = addrs.Length == 0 ? "(нет)" : string.Join(", ", addrs.Select(a => a.ToString()));
            }
            catch (Exception ex) { ips = $"(не удалось: {ex.Message})"; }

            string wvVersion = "(нет активного WebView2)";
            if (_customPlaceholders.TryGetValue(sess.Id, out var p))
            {
                foreach (Control c in p.Controls)
                {
                    if (c is Microsoft.Web.WebView2.WinForms.WebView2 wv && wv.CoreWebView2 != null)
                    {
                        wvVersion = wv.CoreWebView2.Environment.BrowserVersionString;
                        break;
                    }
                }
            }

            using var dlg = new Form
            {
                Text = "Информация о соединении",
                ClientSize = new Size(640, 470),  // v2.2.14: 460→470 (header +10px)
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                Font = new Font("Segoe UI", 9F),
                BackColor = Helpers.UiTheme.Background
            };

            // v2.2.14: header — bg #296AA0 (matches OK button), height 56→66
            // (+5 top +5 bottom per user), white text + white icon.
            var header = new Panel
            {
                Dock = DockStyle.Top,
                Height = 66,
                BackColor = ShadowRDP.Helpers.UiTheme.HeaderBar
            };
            // Tint the globe icon white so it reads against the blue bg.
            var whiteGlobe = Helpers.UiButtons.TintWhite(Helpers.MenuIcons.Get("globe"));
            var headerIcon = new PictureBox
            {
                Size = new Size(28, 28),
                Location = new Point(16, 19),
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Transparent,
                Image = whiteGlobe
            };
            header.Controls.Add(headerIcon);
            var headerTitle = new Label
            {
                Text = $"Информация о соединении — {sess.Name}",
                Location = new Point(52, 23),
                AutoSize = true,
                Font = new Font("Segoe UI Semibold", 11F),
                ForeColor = Color.White,
                BackColor = ShadowRDP.Helpers.UiTheme.HeaderBar
            };
            header.Controls.Add(headerTitle);
            dlg.Controls.Add(header);

            // Body — 2-column DataGridView. v2.2.11: 20px left/right padding.
            var body = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Helpers.UiTheme.Background,
                Padding = new Padding(20, 0, 20, 0)
            };
            dlg.Controls.Add(body);
            body.BringToFront();

            var grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                ReadOnly = true,
                RowHeadersVisible = false,
                ColumnHeadersVisible = false,  // v2.2.13: hide column headers — removes #F6F6F6 gray bar + no clickable headers
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor = Helpers.UiTheme.Background,
                BorderStyle = BorderStyle.None,
                ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
                CellBorderStyle = DataGridViewCellBorderStyle.None,
                GridColor = Helpers.UiTheme.Background,
                EnableHeadersVisualStyles = false,
                RowTemplate = { Height = 28 },
                DefaultCellStyle = {
                    BackColor = Helpers.UiTheme.Background,
                    ForeColor = Helpers.UiTheme.Foreground,
                    // v3.9.3BI — выбор #E8ECE8.
                    SelectionBackColor = Helpers.UiTheme.AccentSoft,
                    SelectionForeColor = Helpers.UiTheme.Accent
                }
            };
            var colParam = new DataGridViewTextBoxColumn
            {
                Name = "Param",
                HeaderText = "Параметр",
                SortMode = DataGridViewColumnSortMode.NotSortable,
                FillWeight = 35
            };
            var colValue = new DataGridViewTextBoxColumn
            {
                Name = "Value",
                HeaderText = "Значение",
                SortMode = DataGridViewColumnSortMode.NotSortable,
                FillWeight = 65
            };
            grid.Columns.Add(colParam);
            grid.Columns.Add(colValue);
            body.Controls.Add(grid);
            // v4.4.0 — своя тонкая полоса вместо системной.
            ShadowScroll.AttachGrid(grid);

            // Populate rows.
            grid.Rows.Add("Название", sess.Name);
            grid.Rows.Add("Тип", "Web");
            grid.Rows.Add("URL", sess.Target);
            grid.Rows.Add("Хост", host);
            grid.Rows.Add("IP", ips);
            grid.Rows.Add("WebView2", wvVersion);
            grid.Rows.Add("Группа", sess.Group ?? "(без группы)");
            grid.Rows.Add("Заметки", sess.Notes ?? "(нет)");
            // Bold the section-divider rows by giving them a separator look
            // (we just append text-only rows here — no grouping for v1).

            // Footer — OK button centered.
            var footer = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 56,
                BackColor = Helpers.UiTheme.Background
            };
            dlg.Controls.Add(footer);
            var okBtn = Helpers.UiButtons.MakeCompactFilled("OK", 100);
            okBtn.Location = new Point((footer.Width - 100) / 2, 10);
            footer.Controls.Add(okBtn);
            footer.Resize += (s, e) => okBtn.Location = new Point((footer.Width - 100) / 2, 10);
            var hiddenOk = new Button
            {
                Visible = true,
                TabStop = false,
                Location = new Point(-100, -100),
                Size = new Size(1, 1)
            };
            footer.Controls.Add(hiddenOk);
            dlg.AcceptButton = hiddenOk;
            okBtn.Click += (_, _) => dlg.DialogResult = DialogResult.OK;
            hiddenOk.Click += (_, _) => dlg.DialogResult = DialogResult.OK;

            ShadowRDP.Helpers.UiModal.ShowDimmed(this, dlg);
        }

        // v2.2.19 — Explicitly add a computer as an RDP session tab in the
        // sidebar. Called from the Книга context menu "Добавить в сессии".
        // Creates a new RdpSession with Group=null (БЕЗ ГРУППЫ), persists
        // to rdp_sessions.json, prompts for shared creds if needed.
        public void AddSessionFromBook(string computer)
        {
            // Reuse existing session if one exists for this computer.
            var existing = _sessions.FirstOrDefault(s =>
                string.Equals(s.Computer, computer, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                EnsureVisible();
                if (_activeTab != SidebarTab.Sessions) SwitchTab(SidebarTab.Sessions);
                ShowSession(existing);
                Logger.Info($"RDP session for '{computer}' already exists — activated");
                return;
            }
            // Look up IP from the address book.
            string? ip = null;
            try
            {
                var entries = Services.AddressBookStore.Load();
                var match = entries.FirstOrDefault(e =>
                    string.Equals(e.Computer, computer, StringComparison.OrdinalIgnoreCase));
                if (match != null && !string.IsNullOrWhiteSpace(match.IPs))
                    ip = match.IPs.Split(',')[0].Trim();
            }
            catch { }
            EnsureVisible();
            if (_activeTab != SidebarTab.Sessions) SwitchTab(SidebarTab.Sessions);
            AddSession(computer, ip, group: null);
            Logger.Info($"RDP session added for '{computer}' (ip={ip ?? "none"}) from Address Book menu");
        }

        // v4.9.34 — отображаемое имя сессии (DisplayName иначе Computer).
        private static string SessName(RdpSession s) =>
            string.IsNullOrWhiteSpace(s.DisplayName) ? s.Computer : s.DisplayName;

        // v4.9.34 — создание RDP-сессии из диалога "+ Сессия" (RDP-ветка):
        // сразу в нужную группу + активация вкладки сессий.
        // Reuse-or-create: существующая сессия обновляется значениями
        // диалога (чтобы не плодить дубликаты "передобавлением").
        public void AddRdpSessionFull(string computer, string? ip, string group, string? displayName, string? notes)
        {
            EnsureVisible();
            if (_activeTab != SidebarTab.Sessions) SwitchTab(SidebarTab.Sessions);
            var existing = _sessions.FirstOrDefault(s =>
                string.Equals(s.Computer, computer, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.DisplayName = displayName ?? "";
                existing.Group = group ?? "";
                existing.Notes = notes ?? "";
                if (!string.IsNullOrEmpty(ip)) { existing.Ip = ip; existing.Target = ip; }
                Logger.Info($"RDP Manager: reused RDP session '{computer}' (group='{group}') via +Сессия");
                PersistSessions();
                RenderSidebar();
                ShowSession(existing);
                return;
            }
            AddSession(computer, ip, group: group, displayName: displayName, notes: notes);
            Logger.Info($"RDP Manager: added RDP session '{computer}' (group='{group}') via +Сессия");
        }

        // v2.1.98 — Bring the RDP Manager window to front (show if hidden,
        // restore if minimized, flash via TopMost trick to bypass
        // SetForegroundWindow restrictions).
        public void EnsureVisible()
        {
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized)
                WindowState = FormWindowState.Normal;
            TopMost = true;
            TopMost = false;
            Activate();
        }

        // v2.1.98 — Quick-launch a Web or SSH session for a computer name
        // from outside the RDP Manager (e.g. from the Address Book context
        // menu). Reuses an existing custom session with the same target if
        // one exists; otherwise creates a new transient one (NOT persisted —
        // if the user wants it permanent they can "+ Сессия" from the
        // sidebar). Switches the manager to the Sessions tab + activates.
        public void LaunchWebForComputer(string computer)
        {
            var url = computer.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                      || computer.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? computer
                : "http://" + computer;
            var sess = _customSessions.FirstOrDefault(c =>
                c.Kind == Models.SessionKind.Web && c.Target == url);
            if (sess == null)
            {
                // v2.2.4: actually ADD to _customSessions so the new tab
                // shows up in the sidebar (was being created but never
                // added — user reported "auto-add doesn't work"). Group
                // is null = "БЕЗ ГРУППЫ". Persisted so it survives restart.
                sess = new Models.CustomSession
                {
                    Name = computer,
                    Kind = Models.SessionKind.Web,
                    Target = url,
                    Group = null,
                    Notes = "auto from Address Book"
                };
                sess.SortOrder = NextSortOrder("");
                _customSessions.Add(sess);
                Services.CustomSessionStore.Save(_customSessions);
            }
            EnsureVisible();
            if (_activeTab != SidebarTab.Sessions) SwitchTab(SidebarTab.Sessions);
            ActivateCustomSession(sess);
            RenderSidebar();
            Logger.Info($"WEB launched for '{computer}' → {url}");
        }

        public void LaunchSshForComputer(string computer)
        {
            var sess = _customSessions.FirstOrDefault(c =>
                c.Kind == Models.SessionKind.Ssh && c.Target == computer);
            if (sess == null)
            {
                sess = new Models.CustomSession
                {
                    Name = computer,
                    Kind = Models.SessionKind.Ssh,
                    Target = computer,
                    Group = null,
                    Notes = "auto from Address Book"
                };
                sess.SortOrder = NextSortOrder("");
                _customSessions.Add(sess);
                Services.CustomSessionStore.Save(_customSessions);
            }
            EnsureVisible();
            if (_activeTab != SidebarTab.Sessions) SwitchTab(SidebarTab.Sessions);
            ActivateCustomSession(sess);
            RenderSidebar();
            Logger.Info($"SSH launched for '{computer}'");
        }

        // v2.2.17 — Launch an embedded RDP session for a computer name
        // from the Address Book context menu. Creates a new RdpSession
        // (or reuses existing) with Group="" (БЕЗ ГРУППЫ), prompts for
        // shared creds if not cached, connects via AxHost.
        // v3.9.3 — показать существующую сессию (постоянную или transient).
        // Возвращает true, если такая есть и активирована.
        public bool TryShowSession(string computer)
        {
            var s = _sessions.FirstOrDefault(x =>
                string.Equals(x.Computer, computer, StringComparison.OrdinalIgnoreCase));
            if (s == null) return false;
            EnsureVisible();
            if (_activeTab != SidebarTab.Sessions) SwitchTab(SidebarTab.Sessions);
            ShowSession(s);
            return true;
        }

        public void LaunchRdpForComputer(string computer)
        {
            // Reuse existing session if one exists for this computer.
            var existing = _sessions.FirstOrDefault(s =>
                string.Equals(s.Computer, computer, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                EnsureVisible();
                if (_activeTab != SidebarTab.Sessions) SwitchTab(SidebarTab.Sessions);
                ShowSession(existing);
                return;
            }
            // Look up IP from the address book.
            string? ip = null;
            try
            {
                var entries = Services.AddressBookStore.Load();
                var match = entries.FirstOrDefault(e =>
                    string.Equals(e.Computer, computer, StringComparison.OrdinalIgnoreCase));
                if (match != null && !string.IsNullOrWhiteSpace(match.IPs))
                    ip = match.IPs.Split(',')[0].Trim();
            }
            catch { }
            EnsureVisible();
            if (_activeTab != SidebarTab.Sessions) SwitchTab(SidebarTab.Sessions);
            // AddSession handles cred prompting + RdpClient creation + sidebar render.
            AddSession(computer, ip, group: null);
            Logger.Info($"RDP launched for '{computer}' (ip={ip ?? "none"}) from Address Book");
        }

        // v2.1.91 — Shared handler for the "+" button in header (v4.9.34:
        // пункт "+ Сессия…" также в контекстных меню сессий; отдельное
        // меню на пустой области сайдбара убрано). Opens
        // CustomSessionEditForm to add a new SSH/Web shortcut.
        private void AddSessionViaSidebar()
        {
            var knownGroups = _sessions
                .Select(x => x.Group ?? "")
                .Concat(_customSessions.Select(x => x.Group ?? ""))
                .Where(g => !string.IsNullOrEmpty(g))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            var edited = Forms.CustomSessionEditForm.Edit(this, null, knownGroups);
            if (edited == null) return;
            edited.SortOrder = NextSortOrder(edited.Group ?? "");
            _customSessions.Add(edited);
            Services.CustomSessionStore.Save(_customSessions);
            RenderSidebar();
            Logger.Info($"RDP Manager: added custom session '{edited.Name}' ({edited.Kind}, {edited.Target})");
        }

        // v2.1.83 — Sidebar item for a non-RDP (SSH/Web) session shortcut.
        // Same look as AddSessionItem but: kind-specific icon (tasks for SSH,
        // globe for Web), Target as subtitle, click → launch external
        // process (wt.exe for SSH, default browser for Web).
        private void AddCustomSessionItem(Models.CustomSession sess)
        {
            const int itemWidth = 166;
            // v2.3.4: 42 → 38 (−2 top + −2 bottom, matches AddSessionItem).
            const int itemHeight = 38;

            var item = new Panel
            {
                Width = itemWidth,
                Height = itemHeight,
                Margin = new Padding(0, 0, 0, 4),
                // v2.3.9: inactive bg = dark sidebar (was Muted).
                BackColor = _activeCustomSessionId == sess.Id ? Helpers.UiTheme.Card : SidebarBg,
                Cursor = Cursors.Hand,
                Tag = sess
            };

            // Icon — tasks for SSH, globe for Web, terminal for Telnet (16x16, already drawn by MenuIcons).
            // v2.3.10: lighten by 50% so the dark icons read on the dark sidebar.
            var iconKey = sess.Kind == Models.SessionKind.Web ? "globe" : sess.Kind == Models.SessionKind.Telnet ? "terminal" : "tasks";
            var icon = new PictureBox
            {
                Image = Lighten(Helpers.MenuIcons.Get(iconKey), 0.5f),
                SizeMode = PictureBoxSizeMode.Normal,
                Size = new Size(16, 16),
                Location = new Point(10, (itemHeight - 16) / 2),
                BackColor = Color.Transparent
            };
            item.Controls.Add(icon);

            // Status dot — small circle at the right edge (v2.3.11).
            item.Controls.Add(CreateStatusDot(Helpers.UiTheme.StatusOnline, 6, itemHeight, itemWidth));

            // Name — top line.
            // v2.3.9: white text on dark sidebar; on active (Card) keep dark.
            // v2.3.11: width shortened so the right-edge dot doesn't overlap.
            bool isActive = _activeCustomSessionId == sess.Id;
            var name = new Label
            {
                Name = "nameLbl",
                Text = sess.Name,
                Location = new Point(30, 5),
                Size = new Size(itemWidth - 30 - 24, 16),
                Font = new Font("Segoe UI", 9F),
                ForeColor = isActive ? Helpers.UiTheme.Foreground : Color.White,
                BackColor = Color.Transparent
            };
            item.Controls.Add(name);

            // Target — bottom line (host:port or URL).
            var sub = new Label
            {
                Name = "ipLbl",
                Text = sess.Target,
                Location = new Point(30, 22),
                Size = new Size(itemWidth - 30 - 24, 13),
                // v2.1.95: was 8F — slightly smaller (matches RDP session IP font).
                Font = new Font("Consolas", 7.5F),
                ForeColor = isActive ? Helpers.UiTheme.MutedForeground : SidebarSubFg,
                BackColor = Color.Transparent
            };
            item.Controls.Add(sub);

            // Hover.
            item.MouseEnter += (s, e) =>
            {
                if (item.BackColor != Helpers.UiTheme.Card)
                    item.BackColor = SidebarItemHover;
            };
            item.MouseLeave += (s, e) =>
            {
                if (item.Tag is Models.CustomSession cs && _activeCustomSessionId != cs.Id)
                    item.BackColor = SidebarBg;
            };

            // Click → activate + launch.
            EventHandler clickHandler = (s, e) =>
            {
                // v3.8.x: swallow the Click that follows a drag reorder.
                if (_suppressClick) { _suppressClick = false; return; }
                if (_activeTab != SidebarTab.Sessions)
                    SwitchTab(SidebarTab.Sessions);
                ActivateCustomSession(sess);
            };
            item.Click += clickHandler;
            foreach (Control child in item.Controls) { child.Click += clickHandler; child.Cursor = Cursors.Hand; }

            // v3.8.x: double-click on a Web session restarts it — drops the
            // cached placeholder (WebView2) and re-creates + re-navigates.
            if (sess.Kind == Models.SessionKind.Web)
            {
                EventHandler dblClick = (s, e) => RestartWebSession(sess);
                item.DoubleClick += dblClick;
                foreach (Control child in item.Controls) child.DoubleClick += dblClick;
            }

            // v3.9.0 — right-click menu shown as a themed popup.
            MouseEventHandler customDown = (s, e) =>
            {
                if (e.Button != MouseButtons.Right) return;
                var menu = new Helpers.ThemedContextMenu();
                menu.AddHeader("Сессия", sess.Name);
                menu.AddItem("Открыть", "play_green", () => LaunchCustomSession(sess));
                if (sess.Kind == Models.SessionKind.Web)
                {
                    menu.AddItem("Обновить", "restart", () =>
                    {
                        if (!_customPlaceholders.TryGetValue(sess.Id, out var p)) return;
                        foreach (Control c in p.Controls)
                        {
                            if (c is Microsoft.Web.WebView2.WinForms.WebView2 wv && wv.CoreWebView2 != null)
                            {
                                wv.CoreWebView2.Reload();
                                Logger.Info($"Web session reload: '{sess.Name}'");
                                return;
                            }
                        }
                    });
                    menu.AddItem("Информация о соединении…", "info", () => ShowWebConnectionInfo(sess));
                }
                if (sess.Kind == Models.SessionKind.Ssh)
                {
                    menu.AddItem("Перезапуск сессии", "restart", () =>
                    {
                        if (_customPlaceholders.TryGetValue(sess.Id, out var oldPanel))
                        {
                            _mainArea.Controls.Remove(oldPanel);
                            oldPanel.Dispose();
                            _customPlaceholders.Remove(sess.Id);
                        }
                        Logger.Info($"SSH restart: '{sess.Name}' (re-using cached creds)");
                        ActivateCustomSession(sess);
                    });
                    menu.AddItem("Зайти под другой учётной записью…", "person", () =>
                    {
                        _sshCredsCache.Remove(sess.Id);
                        _sshForcePrompt.Add(sess.Id);
                        if (_customPlaceholders.TryGetValue(sess.Id, out var oldPanel))
                        {
                            _mainArea.Controls.Remove(oldPanel);
                            oldPanel.Dispose();
                            _customPlaceholders.Remove(sess.Id);
                        }
                        ActivateCustomSession(sess);
                    });
                    menu.AddItem("Обмен файлами…", "list", () => OpenSftpFor(sess));
                }
                // v3.9.3 — Telnet: без учёток, только перезапуск встроенной вкладки.
                if (sess.Kind == Models.SessionKind.Telnet)
                {
                    menu.AddItem("Перезапуск сессии", "restart", () =>
                    {
                        if (_customPlaceholders.TryGetValue(sess.Id, out var oldPanel))
                        {
                            _mainArea.Controls.Remove(oldPanel);
                            oldPanel.Dispose();
                            _customPlaceholders.Remove(sess.Id);
                        }
                        Logger.Info($"Telnet restart: '{sess.Name}'");
                        ActivateCustomSession(sess);
                    });
                }
                menu.AddItem("Переименовать…", "person", () => RenameCustomSession(sess));
                menu.AddSubmenu("Группа", "list", sub =>
                {
                    foreach (var g in _customSessions.Select(c => c.Group ?? "")
                        .Concat(_sessions.Select(x => x.Group ?? ""))
                        .Where(g => !string.IsNullOrEmpty(g))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(g => g.ToUpperInvariant()))
                    {
                        var captured = g;
                        sub.AddItem(g, "folder", () =>
                        {
                            sess.Group = captured;
                            Services.CustomSessionStore.Save(_customSessions);
                            RenderSidebar();
                        }, null, string.Equals(g, sess.Group ?? "", StringComparison.OrdinalIgnoreCase));
                    }
                    sub.AddSeparator();
                    sub.AddItem("Без группы", "folder", () =>
                    {
                        sess.Group = null;
                        Services.CustomSessionStore.Save(_customSessions);
                        RenderSidebar();
                    }, null, string.IsNullOrEmpty(sess.Group));
                    sub.AddSeparator();
                    sub.AddItem("Создать новую…", "add", () =>
                    {
                        var newName = PromptDialog.Show(this, "Новая группа", "Имя группы:", "");
                        if (string.IsNullOrWhiteSpace(newName)) return;
                        sess.Group = newName.Trim();
                        Services.CustomSessionStore.Save(_customSessions);
                        RenderSidebar();
                    });
                });
                menu.AddItem("Удалить", "delete", () => DeleteCustomSession(sess));
                menu.AddSeparator();
                // v4.9.34 — "+ Сессия" пунктом меню (меню пустой области убрано).
                menu.AddItem("+  Сессия…", "add", () => AddSessionViaSidebar());
                var ctl = (Control)s;
                menu.Show(this, ctl.PointToScreen(new Point(e.X, e.Y)));
            };
            item.MouseDown += customDown;
            foreach (Control child in item.Controls) child.MouseDown += customDown;

            // v3.8.x — drag&drop reorder (left button, >6px threshold).
            AttachDragReorder(item);

            _sidebarList.Controls.Add(item);
        }

        // v3.8.x — Restart a Web session: dispose the cached placeholder
        // (WebView2) and re-create it via ActivateCustomSession, which
        // navigates to the target URL again (disconnect → reconnect).
        private void RestartWebSession(Models.CustomSession sess)
        {
            if (_customPlaceholders.TryGetValue(sess.Id, out var oldPanel))
            {
                _mainArea.Controls.Remove(oldPanel);
                oldPanel.Dispose();
                _customPlaceholders.Remove(sess.Id);
            }
            Logger.Info($"Web restart: '{sess.Name}' → {sess.Target}");
            ActivateCustomSession(sess);
        }

        // v3.8.x — drag&drop reorder. MouseDown (left) on the item OR its
        // children only records the gesture — the mouse is NOT captured yet,
        // so plain clicks keep working (children get their normal MouseUp →
        // Click). Capture is taken only once the >6px drag threshold is
        // crossed (MouseMove); from then on the whole gesture routes to the
        // item. MouseUp commits: SortOrder renumbered 0..N-1 for the whole
        // group, both stores saved, sidebar rebuilt.
        private void AttachDragReorder(Control item)
        {
            MouseEventHandler down = (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                if (item.Tag is RdpSession rs) _dragGroup = rs.Group ?? "";
                else if (item.Tag is Models.CustomSession cs) _dragGroup = cs.Group ?? "";
                else return;
                _dropGroup = _dragGroup;
                _dragControl = item;
                _dragStartScreenY = Cursor.Position.Y;
                _isDragging = false;
            };
            item.MouseDown += down;
            foreach (Control child in item.Controls) child.MouseDown += down;

            MouseEventHandler move = (s, e) =>
            {
                if (_dragControl != item) return;
                // Left button not pressed → stale gesture (e.g. the press was
                // consumed by a plain child click). Drop the drag state.
                if ((e.Button & MouseButtons.Left) == 0)
                {
                    if (_isDragging) EndDrag(item);
                    else _dragControl = null;
                    return;
                }
                if (!_isDragging)
                {
                    // 6px threshold — below it this is a plain click/double-click.
                    if (Math.Abs(Cursor.Position.Y - _dragStartScreenY) < 6) return;
                    _isDragging = true;
                    // Swallow the Click raised at drop — the framework fires it
                    // BEFORE MouseUp, so the flag must be set at drag start.
                    _suppressClick = true;
                    item.Capture = true;
                    Cursor.Current = Cursors.SizeNS;
                }
                AutoScrollWhileDragging();
                UpdateDragSlot(item);
            };
            item.MouseMove += move;
            foreach (Control child in item.Controls) child.MouseMove += move;

            item.MouseUp += (s, e) =>
            {
                if (_dragControl != item) return;
                bool wasDragging = _isDragging;
                var group = _dragGroup;
                EndDrag(item);
                if (wasDragging)
                {
                    // v4.9.28 — перенос в другую группу: меняем Group +
                    // сохраняем (раньше менялся только порядок внутри
                    // исходной группы и после рестарта всё возвращалось).
                    try
                    {
                        if (!string.Equals(_dropGroup, group, StringComparison.OrdinalIgnoreCase))
                        {
                            if (item.Tag is RdpSession rs)
                            {
                                rs.Group = _dropGroup;
                                rs.SortOrder = NextSortOrder(_dropGroup);
                            }
                            else if (item.Tag is Models.CustomSession cs)
                            {
                                cs.Group = _dropGroup;
                                cs.SortOrder = NextSortOrder(_dropGroup);
                            }
                            Logger.Info($"RDP Manager: drag '{(item.Tag is RdpSession r2 ? r2.Computer : (item.Tag as Models.CustomSession)?.Name)}' → group '{_dropGroup}'");
                        }
                    }
                    catch { }
                    CommitReorder(group);
                    try
                    {
                        if (!string.Equals(_dropGroup, group, StringComparison.OrdinalIgnoreCase))
                            CommitReorder(_dropGroup);
                    }
                    catch { }
                    // Click already ran before this MouseUp and either consumed
                    // _suppressClick or never fired — clear any leftover so the
                    // next real click isn't eaten.
                    BeginInvoke(new Action(() => _suppressClick = false));
                }
            };
        }

        // v3.8.x — session items of one group in current sidebar layout order
        // (the Controls collection order mirrors FlowLayoutPanel flow order).
        private List<Control> GetGroupSessionControls(string group)
        {
            var list = new List<Control>();
            foreach (Control c in _sidebarList.Controls)
            {
                if (c.Tag is RdpSession rs)
                {
                    if (string.Equals(rs.Group ?? "", group, StringComparison.OrdinalIgnoreCase))
                        list.Add(c);
                }
                else if (c.Tag is Models.CustomSession cs)
                {
                    if (string.Equals(cs.Group ?? "", group, StringComparison.OrdinalIgnoreCase))
                        list.Add(c);
                }
            }
            return list;
        }

        // v4.9.28 — группа под курсором (для межгруппового переноса):
        // заголовок группы (Tag = string) или сессия (её Group).
        // Ниже всех — последняя группа. Нет контролов — исходная группа.
        private string ResolveDropGroup(int cursorY)
        {
            string last = _dragGroup;
            foreach (Control c in _sidebarList.Controls)
            {
                if (c.Tag is string g) last = g ?? "";
                else if (c.Tag is RdpSession rs) last = rs.Group ?? "";
                else if (c.Tag is Models.CustomSession cs) last = cs.Group ?? "";
                else continue;
                if (cursorY < c.Bottom) return last;
            }
            return last;
        }

        // v3.8.x — recompute the insertion slot from the cursor, live-reorder
        // via SetChildIndex and reposition the 2px indicator line.
        private void UpdateDragSlot(Control item)
        {
            var groupItems = GetGroupSessionControls(_dragGroup);
            if (groupItems.Count == 0) { _dropIndicatorY = -1; _sidebarList.Invalidate(); return; }

            // Cursor Y in _sidebarList coords — PointToClient accounts for the
            // manual scroll offset because Location shifts with it.
            int cursorY = _sidebarList.PointToClient(Cursor.Position).Y;
            // v4.9.28 — запоминаем целевую группу (коммит в MouseUp).
            try { _dropGroup = ResolveDropGroup(cursorY); } catch { _dropGroup = _dragGroup; }

            // Count how many same-group items sit above the cursor (dragged
            // item excluded) and grab the first item below it.
            int before = 0;
            Control? below = null;
            foreach (var c in groupItems)
            {
                if (c == item) continue;
                int mid = c.Top + c.Height / 2;
                if (mid < cursorY) before++;
                else if (below == null) below = c;
            }

            // Target position within the full Controls collection. Items of
            // one group are contiguous, so the slot = first index + before.
            int first = _sidebarList.Controls.GetChildIndex(groupItems[0]);
            int targetIndex = first + before;
            int curIndex = _sidebarList.Controls.GetChildIndex(item);
            if (targetIndex != curIndex)
                _sidebarList.Controls.SetChildIndex(item, targetIndex);

            // Indicator line just above the item that will follow the dragged
            // one; if dropping at the very bottom, below the last item.
            int lineY = below != null
                ? below.Top - 1
                : groupItems[groupItems.Count - 1].Bottom - 1;
            _dropIndicatorY = Math.Max(0, lineY);
            _sidebarList.Invalidate();
        }

        private void DrawDropIndicator(PaintEventArgs e)
        {
            if (!_isDragging || _dropIndicatorY < 0) return;
            using var brush = new SolidBrush(Color.FromArgb(0xFF, 0xD6, 0x00));
            e.Graphics.FillRectangle(brush, 2, _dropIndicatorY, 162, 2);
        }

        // v3.8.x — commit a reorder: renumber SortOrder 0..N-1 across the
        // whole group (RDP + custom), persist both stores, rebuild the sidebar.
        private void CommitReorder(string group)
        {
            int order = 0;
            foreach (var c in GetGroupSessionControls(group))
            {
                if (c.Tag is RdpSession rs) rs.SortOrder = order++;
                else if (c.Tag is Models.CustomSession cs) cs.SortOrder = order++;
            }
            Services.CustomSessionStore.Save(_customSessions);
            PersistSessions();
            RenderSidebar();
            Logger.Info("RDP Manager: sidebar sessions reordered");
        }

        private void EndDrag(Control item)
        {
            // Note: do NOT touch item.Capture here — WinForms releases capture
            // itself at the end of WmMouseUp, and clearing it inside MouseUp
            // would suppress the framework's Click generation for plain clicks.
            _dragControl = null;
            _isDragging = false;
            _dropIndicatorY = -1;
            _sidebarList.Invalidate();
            Cursor.Current = Cursors.Default;
        }

        // v3.8.x — while dragging near the sidebar's top/bottom edge, scroll
        // so the user can drop into an off-screen slot.
        private void AutoScrollWhileDragging()
        {
            int y = _sidebarScrollHost.PointToClient(Cursor.Position).Y;
            int h = _sidebarScrollHost.Height;
            if (y < 24) ScrollSidebar(120);
            else if (y > h - 24) ScrollSidebar(-120);
        }

        // v2.1.83 — Highlight the item as active + show placeholder in main area.
        private void ActivateCustomSession(Models.CustomSession sess)
        {
            _activeCustomSessionId = sess.Id;
            // v3.0.22 — drive the header info pills for Web/SSH sessions too.
            UpdateSessionInfoPills(sess);
            // Hide all RDP client panels.
            foreach (var s in _sessions)
            {
                if (s.Client != null) s.Client.Visible = false;
            }
            // Hide all other custom placeholders.
            foreach (var c in _customPlaceholders.Values) c.Visible = false;
            // v3.3.20 — hide the embedded SFTP form so it doesn't cover the
            // newly-activated SSH/Web session.
            if (_activeSftpForm != null && !_activeSftpForm.IsDisposed)
                _activeSftpForm.Visible = false;
            // v3.5.4 — hide embedded console sessions when switching to custom session.
            _psSession?.Hide();
            _cmdSession?.Hide();
            // v3.6.8 — hide embedded consoles too.
            _activeEmbedded = null;
            foreach (var es in _embeddedSessions)
                if (es.Control.IsEmbedded) es.Control.Visible = false;
            UpdateEmbedIcons();
            ClearConsoleActive();
            // Show (or create) this one.
            var panel = GetOrCreateCustomPlaceholder(sess);
            panel.Visible = true;
            panel.BringToFront();
            // Repaint sidebar.
            foreach (Control c in _sidebarList.Controls)
            {
                if (c.Tag is Models.CustomSession cs)
                {
                    // v2.3.9: inactive bg = dark sidebar, active = Card (light).
                    bool activeRow = cs.Id == sess.Id;
                    c.BackColor = activeRow ? Helpers.UiTheme.Card : SidebarBg;
                    UpdateItemLabelColors(c, activeRow);
                    c.Invalidate();
                }
                else if (c.Tag is RdpSession rs)
                {
                    // RDP row became inactive when a Custom session activated.
                    c.BackColor = SidebarBg;
                    UpdateItemLabelColors(c, active: false);
                    c.Invalidate();
                }
            }
        }

        // === v3.6.8 — embedded MMC consoles ("Встроить ММС") ===

        // v3.6.8 — "Открыть консоль MMC…": file picker limited to .msc
        // consoles (arbitrary programs are NOT embedded anymore). The
        // console is embedded the same way as "Добавить MMC" (via mmc.exe).
        private void OpenEmbedPicker()
        {
            using var dlg = new OpenFileDialog
            {
                Title = "Выберите консоль MMC",
                Filter = "Консоли MMC (*.msc)|*.msc|Все файлы (*.*)|*.*",
                InitialDirectory = _lastEmbedDir ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
            };
            if (ShadowRDP.Helpers.UiModal.ShowDimmed(this, dlg) != DialogResult.OK) return;
            _lastEmbedDir = System.IO.Path.GetDirectoryName(dlg.FileName);
            var entry = new Models.EmbeddedAppEntry
            {
                Name = DescribeMmcConsole(dlg.FileName),
                Path = dlg.FileName,
                Arguments = ""
            };
            var recent = Services.EmbeddedAppsStore.Load();
            recent.RemoveAll(e => string.Equals(e.Path, entry.Path, StringComparison.OrdinalIgnoreCase));
            recent.Insert(0, entry);
            if (recent.Count > 20) recent = recent.Take(20).ToList();
            Services.EmbeddedAppsStore.Save(recent);
            OpenEmbedProgram(entry);
        }

        // v3.6.8 — true when the file is an MMC console (.msc). MMC consoles
        // are launched through mmc.exe (not executed directly), so embedding
        // can track the actual mmc process.
        private static bool IsMscFile(string path) =>
            string.Equals(System.IO.Path.GetExtension(path), ".msc", StringComparison.OrdinalIgnoreCase);

        // Rebuilds the "Встроить ММС" menu: recent consoles (with an
        // icon + a ✕ to remove each one from history) + "Открыть консоль
        // MMC…" + "Добавить MMC".
        private void PopulateEmbedMenu(ContextMenuStrip menu)
        {
            menu.Items.Clear();
            var recent = Services.EmbeddedAppsStore.Load();
            if (recent.Count == 0)
            {
                var none = new ToolStripMenuItem("Нет недавних консолей MMC");
                none.Enabled = false;
                menu.Items.Add(none);
            }
            else
            {
                foreach (var entry in recent)
                {
                    var item = new Helpers.EmbedRecentItem(entry)
                    {
                        ProgramIcon = GetEmbedIcon(entry.Path)
                    };
                    item.OnOpen = e => OpenEmbedProgram(e);
                    item.OnDelete = e => { DeleteEmbedRecent(e); try { menu.Close(); } catch { } };
                    menu.Items.Add(item);
                }
                menu.Items.Add(new ToolStripSeparator());
            }
            menu.Items.Add("Открыть консоль MMC…", null, (s2, e2) => OpenEmbedPicker());
            // v3.6.8 — MMC Management Console as a built-in entry (no file
            // picker needed): launches mmc.exe and embeds it like a console.
            menu.Items.Add("Добавить MMC", null, (s2, e2) => OpenMmcConsole());

            // v3.6.8 — stretch EVERY row/item to the widest one so the ✕ sits
            // flush with the right edge in one aligned column, then size the
            // menu itself to exactly that content (Width = maxW, Height = sum
            // of the rows) so the drop-down can't drift into a different size
            // (which moved the ✕) or overflow into up/down scroll arrows.
            int maxW2 = 0;
            foreach (ToolStripItem it in menu.Items)
            {
                int w = it.GetPreferredSize(Size.Empty).Width;
                if (w > maxW2) maxW2 = w;
            }
            foreach (ToolStripItem it in menu.Items)
            {
                if (it is Helpers.EmbedRecentItem ri)
                {
                    ri.AutoSize = false;
                    ri.Size = new Size(maxW2, 26);
                }
                else if (it is ToolStripMenuItem mi)
                {
                    mi.AutoSize = false;
                    mi.Size = new Size(maxW2, mi.GetPreferredSize(Size.Empty).Height);
                }
            }
            // For plain ContextMenuStrip (legacy) we fix both Width+Height to avoid scroll arrows.
            // For ThemedContextMenu as submenu, fixing Height breaks DropDown positioning (appears at 0,0 or flipped);
            // keep Height auto, only fix Width for ✕ alignment.
            if (menu is Helpers.ThemedContextMenu)
            {
                menu.AutoSize = false;
                menu.Width = maxW2 + menu.Padding.Horizontal;
                // Height left to auto-layout (PerformLayout will compute); ensure layout is fresh.
                menu.PerformLayout();
            }
            else
            {
                int menuH2 = menu.Padding.Vertical;
                foreach (ToolStripItem it in menu.Items)
                {
                    int h = it.Height;
                    if (h <= 0) h = it.GetPreferredSize(Size.Empty).Height;
                    menuH2 += h;
                }
                menu.AutoSize = false;
                menu.Width = maxW2 + menu.Padding.Horizontal;
                menu.Height = menuH2;
            }
        }

        // ✕ in the recent-console menu: removes the entry from history.
        private void DeleteEmbedRecent(Models.EmbeddedAppEntry entry)        {
            if (MessageBox.Show(this, $"Удалить «{entry.Name}» из списка недавних консолей MMC?",
                "Встроенная консоль MMC", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            var recent = Services.EmbeddedAppsStore.Load();
            recent.RemoveAll(e => string.Equals(e.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
            Services.EmbeddedAppsStore.Save(recent);
            Logger.Info($"RDP Manager: removed '{entry.Name}' from recent embedded consoles");
        }

        // Launches (or reactivates) an embedded MMC console.
        private void OpenEmbedProgram(Models.EmbeddedAppEntry entry)
        {
            // Already running — just bring it to front.
            var existing = _embeddedSessions.FirstOrDefault(es => string.Equals(es.Entry.Path, entry.Path, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                ActivateEmbeddedProgram(existing);
                return;
            }

            // v3.6.8 — MMC consoles (.msc) launch through mmc.exe with the
            // console file as its argument; entry.Path stays the .msc path so
            // re-opening it from the list relaunches the SAME console directly.
            string launchPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "mmc.exe");
            string launchArgs = $"\"{entry.Path}\"";

            var control = new Controls.EmbeddedProgramControl(launchPath, launchArgs, entry.Name);
            control.ProcessExited += (s, e) => RemoveEmbeddedProgram(control);
            control.EmbedFailed += (s, e) => RemoveEmbeddedProgram(control);
            var es = new EmbeddedProgramSession { Entry = entry, Control = control };
            _embeddedSessions.Add(es);

            // v3.6.8 — consoles rename themselves to their first snap-in when
            // the window title changes (snap-in added/saved, console renamed).
            if (IsMscFile(entry.Path))
                control.WindowTitleChanged += (s, title) => OnEmbeddedMmcTitleChanged(es, title);

            control.Dock = DockStyle.Fill;
            _mainArea.Controls.Add(control);
            AddEmbedPill(es);
            ActivateEmbeddedProgram(es);
            control.Start();
            Logger.Info($"RDP Manager: embedding '{entry.Name}' ({entry.Path})");
        }

        // v3.6.8 — "Добавить MMC": opens a NEW app-owned MMC console and
        // embeds it as its own tab. Each click creates a fresh console file
        // (%PROGRAMDATA%\PupSeekRDP\mmc\MMC.msc, MMC-2.msc, …) so several
        // consoles can be open at once — MMC only activates a console already
        // open from the same file, so separate files are required. Snap-ins
        // the user adds (File → Add/Remove Snap-in) persist in that file
        // after File → Save, and the tab/recent entry is renamed to the
        // console's first snap-in (e.g. "Active Directory Users and
        // Computers"). Clicking a recent entry reopens the SAME saved console
        // (or reactivates it if already running). Reuses OpenEmbedProgram,
        // which routes .msc files through mmc.exe.
        private void OpenMmcConsole()
        {
            string path = CreateNewMmcConsoleFile();
            var entry = new Models.EmbeddedAppEntry
            {
                Id = "mmc",
                Name = DescribeMmcConsole(path),
                Path = path,
                Arguments = ""
            };
            var recent = Services.EmbeddedAppsStore.Load();
            recent.RemoveAll(e => string.Equals(e.Path, entry.Path, StringComparison.OrdinalIgnoreCase));
            recent.Insert(0, entry);
            if (recent.Count > 20) recent = recent.Take(20).ToList();
            Services.EmbeddedAppsStore.Save(recent);
            OpenEmbedProgram(entry);
        }

        // v3.6.8 — returns the path of a NEW app-owned MMC console file,
        // created from the embedded template resource (SessionSpy.mmc.empty
        // .msc — a genuine empty author-mode console saved by MMC itself, so
        // mmc.exe opens it without complaints). The base name is reused only
        // while missing or if it's a broken leftover under 5 KB (the
        // hand-crafted template written by an early 3.6.8 build); otherwise
        // the next free MMC-<n>.msc is used so multiple consoles don't
        // collide. Best-effort: if extraction fails, mmc.exe will just report
        // the missing file.
        private static string CreateNewMmcConsoleFile()
        {
            string path = AppPaths.MmcConsolePath;
            try
            {
                if (!System.IO.Directory.Exists(AppPaths.MmcDirPath))
                    System.IO.Directory.CreateDirectory(AppPaths.MmcDirPath);
                int n = 1;
                while (System.IO.File.Exists(path)
                       && new System.IO.FileInfo(path).Length >= 5000)
                {
                    n++;
                    path = System.IO.Path.Combine(AppPaths.MmcDirPath, $"MMC-{n}.msc");
                }
                using var stream = typeof(RdpManagerForm).Assembly
                    .GetManifestResourceStream("SessionSpy.mmc.empty.msc");
                if (stream != null)
                {
                    using var fs = new System.IO.FileStream(path, System.IO.FileMode.Create, System.IO.FileAccess.Write);
                    stream.CopyTo(fs);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"CreateNewMmcConsoleFile failed: {ex.Message}");
            }
            return path;
        }

        // v3.6.8 — friendly name for a console file: the display name of its
        // first snap-in, read from the .msc XML (root's first child node →
        // StringTable). A bare console (no snap-ins) falls back to
        // "Консоль MMC".
        private static string DescribeMmcConsole(string path)
        {
            try
            {
                var doc = new System.Xml.XmlDocument();
                doc.Load(path);
                var strings = new System.Collections.Generic.Dictionary<string, string>();
                var stringNodes = doc.SelectNodes("//MMC_ConsoleFile/StringTables/StringTable/Strings/String");
                if (stringNodes != null)
                {
                    foreach (System.Xml.XmlNode sn in stringNodes)
                    {
                        var id = sn.Attributes?["ID"]?.Value;
                        var text = sn.InnerText?.Trim();
                        if (id != null && !string.IsNullOrEmpty(text)) strings[id] = text;
                    }
                }
                var rootNode = doc.SelectSingleNode("//MMC_ConsoleFile/ScopeTree/Nodes/Node");
                if (rootNode != null)
                {
                    var firstChild = rootNode.SelectSingleNode("Nodes/Node");
                    if (firstChild != null)
                    {
                        var nameEl = firstChild.SelectSingleNode("String[@Name='Name']");
                        var idRef = nameEl?.Attributes?["ID"]?.Value;
                        if (idRef != null && strings.TryGetValue(idRef, out var s))
                            return TrimConsoleSuffix(s);
                    }
                }
            }
            catch { }
            return "Консоль MMC";
        }

        // v3.6.8 — strips the MMC " [ target ]" suffix some snap-ins append
        // to their display name (e.g. "Active Directory Users and Computers
        // [ srv-dc03.Contoso.local ]" → "Active Directory Users and
        // Computers") so tabs/recent entries stay short and readable.
        private static string TrimConsoleSuffix(string name)
        {
            int i = name.LastIndexOf(" [", StringComparison.Ordinal);
            if (i > 0 && name.EndsWith("]", StringComparison.Ordinal))
                return name.Substring(0, i);
            return name;
        }

        // v3.6.8 — fired when an embedded console's window title changes
        // (snap-in added + saved, console renamed, …). Re-derives the name
        // from the console file and refreshes the tab, title bar and the
        // recent-list entry when it changed.
        private void OnEmbeddedMmcTitleChanged(EmbeddedProgramSession es, string title)
        {
            if (es.Control.HasExited) return;
            string name = DescribeMmcConsole(es.Entry.Path);
            if (string.Equals(name, es.Entry.Name, StringComparison.Ordinal)) return;
            es.Entry.Name = name;
            es.Control.SetDisplayName(name);
            UpdateEmbedRecentName(es.Entry);
            RebuildEmbedPill(es);
            Logger.Info($"RDP Manager: MMC console renamed to '{name}'");
        }

        // v3.6.8 — updates the matching recent entry's name in
        // embedded_apps.json (same console file).
        private static void UpdateEmbedRecentName(Models.EmbeddedAppEntry entry)
        {
            var recent = Services.EmbeddedAppsStore.Load();
            var found = recent.FirstOrDefault(e => string.Equals(e.Path, entry.Path, StringComparison.OrdinalIgnoreCase));
            if (found != null && !string.Equals(found.Name, entry.Name, StringComparison.Ordinal))
            {
                found.Name = entry.Name;
                Services.EmbeddedAppsStore.Save(recent);
            }
        }

        // v3.6.8 — recreates the header pill for a session (used when the
        // console's display name changed, since the pill sizes itself to the
        // name).
        private void RebuildEmbedPill(EmbeddedProgramSession es)
        {
            if (es.PillButton != null)
            {
                _headerPanel.Controls.Remove(es.PillButton);
                es.PillButton.Dispose();
                es.PillButton = null;
            }
            AddEmbedPill(es);
        }

        // Shows the embedded console in the main area, hiding everything else.
        private void ActivateEmbeddedProgram(EmbeddedProgramSession es)
        {
            _activeEmbedded = es;
            _activeSession = null;
            _activeCustomSessionId = null;
            foreach (var s in _sessions)
                if (s.Client != null) s.Client.Visible = false;
            foreach (var c in _customPlaceholders.Values) c.Visible = false;
            if (_activeSftpForm != null && !_activeSftpForm.IsDisposed) _activeSftpForm.Visible = false;
            _bookHost.Visible = false;
            _psSession?.Hide();
            _cmdSession?.Hide();
            _activeConsole = null;
            es.Control.Visible = true;
            es.Control.BringToFront();
            es.Control.RestoreWindow();
            UpdateEmbedIcons();
            foreach (Control c in _sidebarList.Controls)
            {
                c.BackColor = SidebarBg;
                UpdateItemLabelColors(c, active: false);
                c.Invalidate();
            }
        }

        // Removes an embedded console (kills its process) and cleans up UI.
        private void RemoveEmbeddedProgram(Controls.EmbeddedProgramControl control)
        {
            var es = _embeddedSessions.FirstOrDefault(x => x.Control == control);
            if (es == null) return;
            _embeddedSessions.Remove(es);
            control.KillProcess();
            _mainArea.Controls.Remove(control);
            if (es.PillButton != null && _headerPanel.Controls.Contains(es.PillButton))
                _headerPanel.Controls.Remove(es.PillButton);
            control.Dispose();
            if (_activeEmbedded == es) _activeEmbedded = null;
            UpdateEmbedIcons();
            LayoutHeaderLeftBlock();
            Logger.Info($"RDP Manager: embedded console '{es.Entry.Name}' closed");
        }

        // Adds the header tab for a running embedded console. Full-height
        // (36px) tab with rounded top corners sitting flush with the bottom
        // of the header; shows a hover ✕ to close. Left click switches to
        // the console, right click or the ✕ closes it.
        private void AddEmbedPill(EmbeddedProgramSession es)
        {
            var font = new Font("Segoe UI", 8.5F);
            var name = es.Entry.Name;
            var nameWidth = TextRenderer.MeasureText(name, font).Width;
            if (nameWidth > 160) nameWidth = 160;
            const int iconW = 16;
            const int tabH = 36;
            int width = 6 + iconW + 5 + nameWidth + 6 + 18;

            var pill = new Label
            {
                Text = "",
                AutoSize = false,
                Size = new Size(width, tabH),
                Cursor = Cursors.Hand,
                Margin = Padding.Empty,
                BackColor = Color.Transparent
            };
            es.PillButton = pill;
            // v3.6.8 — light tabs matching the header (UiTheme.Card #FAF7F4):
            // 1px border in the same cream, dark (inverted) text, subtle
            // darker cream on hover/active. Tabs sit flush against each other.
            var normalBg = Color.FromArgb(0xFA, 0xF7, 0xF4);
            var hoverBg = Color.FromArgb(0xF0, 0xEB, 0xE4);
            var activeBg = Color.FromArgb(0xED, 0xE8, 0xE2);
            var borderColor = Color.FromArgb(0xFA, 0xF7, 0xF4);
            var textColor = Color.FromArgb(0x1C, 0x19, 0x17);
            // v3.6.8 — ✕ without a background: muted grey normally, red on
            // hover (the hover state is the only visual feedback).
            var closeColor = Color.FromArgb(0x78, 0x71, 0x6C);
            var closeHoverColor = Color.FromArgb(0xDC, 0x26, 0x26);
            var closeRect = new Rectangle(width - 6 - 14, (tabH - 14) / 2, 14, 14);
            var isHovered = false;
            var closeHovered = false;

            pill.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                var r = new Rectangle(0, 0, pill.Width, pill.Height);
                // v3.9.3CV — без скругления (как табы консолей).
                bool isActive = ReferenceEquals(es, _activeEmbedded);
                using var brush = new SolidBrush(isActive ? activeBg : (isHovered ? hoverBg : normalBg));
                e.Graphics.FillRectangle(brush, r);
                using var pen = new Pen(borderColor, 1);
                e.Graphics.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
                var icon = GetEmbedIcon(es.Entry.Path);
                var iconY = (tabH - iconW) / 2;
                if (icon != null)
                {
                    // v3.9.3 — bicubic-масштаб системной иконки (был Default).
                    e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    e.Graphics.DrawImage(icon, new Rectangle(6, iconY, iconW, iconW));
                }
                else
                {
                    TextRenderer.DrawText(e.Graphics, "⚙", font,
                        new Rectangle(5, iconY, iconW, iconW), textColor,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
                var txtRect = new Rectangle(6 + iconW + 5, 0, closeRect.X - (6 + iconW + 5), tabH);
                TextRenderer.DrawText(e.Graphics, name, font, txtRect, textColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                if (isHovered)
                {
                    // v3.6.8 — no red background, just a smaller glyph;
                    // red colour appears on hover over the ✕ itself.
                    TextRenderer.DrawText(e.Graphics, "✕", new Font("Segoe UI", 7F), closeRect,
                        closeHovered ? closeHoverColor : closeColor,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
            };
            pill.MouseEnter += (s, e) => { isHovered = true; pill.Invalidate(); };
            pill.MouseLeave += (s, e) => { isHovered = false; closeHovered = false; pill.Invalidate(); };
            pill.MouseMove += (s, e) =>
            {
                bool over = closeRect.Contains(e.Location);
                if (over != closeHovered) { closeHovered = over; pill.Invalidate(); }
                pill.Cursor = over ? Cursors.Default : Cursors.Hand;
            };
            pill.MouseUp += (s, e) =>
            {
                if (es.Control.HasExited) { RemoveEmbeddedProgram(es.Control); return; }
                if (e.Button == MouseButtons.Left && closeRect.Contains(e.Location))
                {
                    if (MessageBox.Show(this, $"Закрыть консоль «{es.Entry.Name}»?", "Встроенная консоль MMC",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    {
                        es.Control.Close();
                    }
                    return;
                }
                if (e.Button == MouseButtons.Right)
                {
                    if (MessageBox.Show(this, $"Закрыть консоль «{es.Entry.Name}»?", "Встроенная консоль MMC",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    {
                        es.Control.Close();
                    }
                    return;
                }
                ActivateEmbeddedProgram(es);
            };
            _toolTip.SetToolTip(pill, es.Entry.Name);
            _headerPanel.Controls.Add(pill);
            pill.BringToFront();
            LayoutHeaderLeftBlock();
        }

        // Redraws pill buttons so the active embedded console highlights.
        private void UpdateEmbedIcons()
        {
            foreach (var es in _embeddedSessions)
                es.PillButton?.Invalidate();
        }

        // v3.9 — header pill for an embedded CMD/PS console session. Mirrors
        // the embedded-MMC tab style so all running consoles read as one row.
        private void AddConsolePill(Controls.ConsoleSessionControl session, string title, Color accent)
        {
            var font = new Font("Segoe UI", 8.5F);
            var nameWidth = TextRenderer.MeasureText(title, font).Width;
            if (nameWidth > 160) nameWidth = 160;
            const int iconW = 10;
            const int tabH = 36;
            int width = 8 + iconW + 5 + nameWidth + 6 + 18;

            var pill = new Label
            {
                Text = "",
                AutoSize = false,
                Size = new Size(width, tabH),
                Cursor = Cursors.Hand,
                Margin = Padding.Empty,
                BackColor = Color.Transparent
            };
            // v3.9.3CU — табы консолей: без скругления, #F5E3B3 (актив темнее).
            var normalBg = Color.FromArgb(0xF5, 0xE3, 0xB3);
            var hoverBg = Color.FromArgb(0xEF, 0xDA, 0xA4);
            var activeBg = Color.FromArgb(0xE8, 0xCF, 0x93);
            var borderColor = Color.FromArgb(0xD9, 0xC3, 0x86);
            var textColor = Color.FromArgb(0x1C, 0x19, 0x17);
            var closeColor = Color.FromArgb(0x78, 0x71, 0x6C);
            var closeHoverColor = Color.FromArgb(0xDC, 0x26, 0x26);
            var closeRect = new Rectangle(width - 6 - 14, (tabH - 14) / 2, 14, 14);
            var isHovered = false;
            var closeHovered = false;

            pill.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                var r = new Rectangle(0, 0, pill.Width, pill.Height);
                bool isActive = ReferenceEquals(session, _activeConsole);
                using var brush = new SolidBrush(isActive ? activeBg : (isHovered ? hoverBg : normalBg));
                e.Graphics.FillRectangle(brush, r);
                using var pen = new Pen(borderColor, 1);
                e.Graphics.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
                using var dotBrush = new SolidBrush(accent);
                e.Graphics.FillEllipse(dotBrush, new Rectangle(8, (tabH - iconW) / 2, iconW, iconW));
                var txtRect = new Rectangle(8 + iconW + 5, 0, closeRect.X - (8 + iconW + 5), tabH);
                TextRenderer.DrawText(e.Graphics, title, font, txtRect, textColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                if (isHovered)
                    TextRenderer.DrawText(e.Graphics, "✕", new Font("Segoe UI", 7F), closeRect,
                        closeHovered ? closeHoverColor : closeColor,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            };
            pill.MouseEnter += (s, e) => { isHovered = true; pill.Invalidate(); };
            pill.MouseLeave += (s, e) => { isHovered = false; closeHovered = false; pill.Invalidate(); };
            pill.MouseMove += (s, e) =>
            {
                bool over = closeRect.Contains(e.Location);
                if (over != closeHovered) { closeHovered = over; pill.Invalidate(); }
                pill.Cursor = over ? Cursors.Default : Cursors.Hand;
            };
            pill.MouseUp += (s, e) =>
            {
                if (session.IsDisposed) { RemoveConsolePill(session); return; }
                if (e.Button == MouseButtons.Left && closeRect.Contains(e.Location))
                {
                    if (MessageBox.Show(this, $"Закрыть консоль «{title}»?", "Консоль",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                        session.KillProcess();
                    return;
                }
                if (e.Button == MouseButtons.Right)
                {
                    if (MessageBox.Show(this, $"Закрыть консоль «{title}»?", "Консоль",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                        session.KillProcess();
                    return;
                }
                ActivateConsoleSession(session);
            };
            _toolTip.SetToolTip(pill, title);
            _headerPanel.Controls.Add(pill);
            pill.BringToFront();
            _consolePills[session] = pill;
            LayoutHeaderLeftBlock();
        }

        private void EnsureConsolePill(Controls.ConsoleSessionControl session, string title, Color accent)
        {
            if (_consolePills.ContainsKey(session)) { LayoutHeaderLeftBlock(); return; }
            AddConsolePill(session, title, accent);
        }

        private void RemoveConsolePill(Controls.ConsoleSessionControl session)
        {
            if (_consolePills.TryGetValue(session, out var pill))
            {
                _headerPanel.Controls.Remove(pill);
                pill.Dispose();
                _consolePills.Remove(session);
            }
            if (ReferenceEquals(_activeConsole, session)) _activeConsole = null;
            LayoutHeaderLeftBlock();
        }

        // Shows a CMD/PS console panel, hiding every other panel — but NOT the
        // console pills (they persist for the lifetime of the process).
        private void ActivateConsoleSession(Controls.ConsoleSessionControl session)
        {
            _activeEmbedded = null;
            _activeSession = null;
            _activeCustomSessionId = null;
            _activeConsole = session;
            foreach (var s in _sessions)
                if (s.Client != null) s.Client.Visible = false;
            foreach (var c in _customPlaceholders.Values) c.Visible = false;
            if (_activeSftpForm != null && !_activeSftpForm.IsDisposed) _activeSftpForm.Visible = false;
            _bookHost.Visible = false;
            if (_cmdSession != null && _cmdSession != session) _cmdSession.Hide();
            if (_psSession != null && _psSession != session) _psSession.Hide();
            foreach (var es in _embeddedSessions)
                if (es.Control.IsEmbedded) es.Control.Visible = false;
            session.Visible = true;
            session.BringToFront();
            session.Activate();
            UpdateEmbedIcons();
            foreach (var p in _consolePills.Values) p.Invalidate();
            foreach (Control c in _sidebarList.Controls)
            {
                c.BackColor = SidebarBg;
                UpdateItemLabelColors(c, active: false);
                c.Invalidate();
            }
        }

        private void ClearConsoleActive()
        {
            _activeConsole = null;
            foreach (var p in _consolePills.Values) p.Invalidate();
        }

        // v2.1.86: Web sessions no longer launch an external browser — the
        // embedded WebBrowser control (created in GetOrCreateCustomPlaceholder)
        // IS the in-app view. LaunchCustomSession now handles SSH only.
        // v3.9.3 — Telnet: внешнего telnet.exe может не быть (опциональная
        // фича Windows), поэтому «Открыть» ведёт на встроенную вкладку.
        private void LaunchCustomSession(Models.CustomSession sess)
        {
            if (sess.Kind == Models.SessionKind.Telnet)
            {
                EnsureVisible();
                if (_activeTab != SidebarTab.Sessions) SwitchTab(SidebarTab.Sessions);
                ActivateCustomSession(sess);
                RenderSidebar();
                return;
            }
            try
            {
                // SSH — try Windows Terminal first, fallback to cmd /k ssh.
                var wtPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
                Logger.Info($"Custom session (SSH): wt.exe path = '{wtPath}', exists = {System.IO.File.Exists(wtPath)}");
                if (System.IO.File.Exists(wtPath))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(wtPath)
                    {
                        Arguments = $"ssh {sess.Target}",
                        UseShellExecute = true
                    });
                }
                else
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
                    {
                        Arguments = $"/k ssh {sess.Target}",
                        UseShellExecute = true
                    });
                }
                Logger.Info($"Custom session (SSH): launched ssh {sess.Target}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"Custom session launch failed: {ex.Message}");
                MessageBox.Show(this, $"Не удалось открыть сессию: {ex.Message}",
                    "Сессия", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void RenameCustomSession(Models.CustomSession sess)
        {
            var knownGroups = _sessions.Select(x => x.Group ?? "")
                .Concat(_customSessions.Select(x => x.Group ?? ""))
                .Where(g => !string.IsNullOrEmpty(g));
            var edited = Forms.CustomSessionEditForm.Edit(this, sess, knownGroups);
            if (edited == null) return;
            Services.CustomSessionStore.Save(_customSessions);
            RenderSidebar();
            Logger.Info($"Custom session renamed: '{edited.Name}'");
        }

        // v3.5.4 — Embedded console sessions (PowerShell / CMD).
        // These are hidden sessions that don't appear in the sidebar.
        // Clicking the PS/CMD button toggles visibility of the same session.

        private Controls.ConsoleSessionControl ShowOrCreateConsoleSession(
            Controls.ConsoleSessionControl? session,
            string exePath, string arguments, string title,
            System.Text.Encoding? encoding = null,
            string? welcomeText = null,
            Color? accentColor = null)
        {
            // If session exists and is alive — just show it.
            if (session != null && !session.IsDisposed)
            {
                EnsureConsolePill(session, title, accentColor ?? (title.Contains("Power", StringComparison.OrdinalIgnoreCase) ? Color.FromArgb(0x01, 0x24, 0x56) : Color.FromArgb(0x3C, 0x3C, 0x3C)));
                ActivateConsoleSession(session);
                return session;
            }

            // Create new console session.
            var newSession = new Controls.ConsoleSessionControl(exePath, arguments, title, encoding, accentColor)
            {
                Dock = DockStyle.Fill,
                WelcomeText = welcomeText
            };

            // When closed via X button — remove from parent and hide indicator.
            newSession.SessionClosed += (s, e) =>
            {
                newSession.KillProcess();
                _mainArea.Controls.Remove(newSession);
                RemoveConsolePill(newSession);
                if (newSession == _psSession) _psSession = null;
                if (newSession == _cmdSession) _cmdSession = null;
            };

            _mainArea.Controls.Add(newSession);
            newSession.BringToFront();

            EnsureConsolePill(newSession, title, accentColor ?? (title.Contains("Power", StringComparison.OrdinalIgnoreCase) ? Color.FromArgb(0x01, 0x24, 0x56) : Color.FromArgb(0x3C, 0x3C, 0x3C)));
            ActivateConsoleSession(newSession);
            newSession.StartProcess();
            Logger.Info($"RDP Manager: opened embedded console session '{title}'");
            return newSession;
        }

        public void CloseConsoleSession(Controls.ConsoleSessionControl session)
        {
            session.KillProcess();
            session.Dispose();
            RemoveConsolePill(session);
        }

        private void HideAllSessionPanels()
        {
            foreach (var s in _sessions)
                if (s.Client != null) s.Client.Visible = false;
            foreach (var c in _customPlaceholders.Values) c.Visible = false;
            _bookHost.Visible = false;
            _activeSftpForm?.Hide();
            _psSession?.Hide();
            _cmdSession?.Hide();
            // v3.6.8 — hide embedded consoles too.
            _activeEmbedded = null;
            foreach (var es in _embeddedSessions)
                if (es.Control.IsEmbedded) es.Control.Visible = false;
            UpdateEmbedIcons();
        }

        // v3.2.2 — open SFTP exchange dialog for an SSH custom session.
        // v3.3.15 — embedded directly into _mainArea (like RDP/SSH sessions)
        // instead of opening as a modal dialog. Stays alive until the user
        // closes it via its Close button.
        private SftpForm? _activeSftpForm;

        private void OpenSftpFor(Models.CustomSession sess)
        {
            var parts = sess.Target.Split(':');
            if (parts.Length == 0 || string.IsNullOrWhiteSpace(parts[0]))
            {
                MessageBox.Show(this, "Не указан хост.", "SFTP",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string host = parts[0].Trim();
            int port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 22;

            if (!_sshCredsCache.TryGetValue(sess.Id, out var cached))
            {
                var creds = Helpers.SshCredsDialog.Prompt(this, host);
                if (creds == null) return;
                cached = (creds.User, creds.Password);
                _sshCredsCache[sess.Id] = cached;
            }

            try
            {
                var sftp = new Renci.SshNet.SftpClient(host, port, cached.User, cached.Password);
                sftp.Connect();
                // v3.9.3 — аудит подключений (пароль — никогда).
                Services.AuditLogger.Write("sftp.connect", $"{cached.User}@{host}:{port}", "ok");
                // Reuse existing embedded SFTP form if one is open.
                if (_activeSftpForm != null && !_activeSftpForm.IsDisposed)
                {
                    _activeSftpForm.SwapClient(sftp, sess.Name);
                    // v3.3.21 — ShowSession/ActivateCustomSession hide the
                    // embedded form when switching tabs. When the user
                    // reopens SFTP we must re-show it explicitly —
                    // BringToFront alone does NOT flip Visible back to true.
                    _activeSftpForm.Visible = true;
                }
                else
                {
                    _activeSftpForm = new SftpForm(sftp, sessionName: sess.Name, parent: this);
                    _activeSftpForm.TopLevel = false;
                    _activeSftpForm.FormBorderStyle = FormBorderStyle.None;
                    _activeSftpForm.Dock = DockStyle.Fill;
                    // v3.3.17 — remember the SSH session that opened this SFTP
                    // so we can reactivate it when the user closes the panel.
                    var sourceSession = sess;
                    _mainArea.Controls.Add(_activeSftpForm);
                    _activeSftpForm.FormClosing += (s, e) =>
                    {
                        _mainArea.Controls.Remove(_activeSftpForm);
                        _activeSftpForm = null;
                        // v3.3.17 — switch back to the SSH session that
                        // launched SFTP so the user isn't left staring at a
                        // blank main area.
                        ActivateCustomSession(sourceSession);
                    };
                    _activeSftpForm.Show();
                }
                HideOtherSessionPanelsForSftp();
                _activeSftpForm?.BringToFront();
            }
            catch (Exception ex)
            {
                Logger.Warn($"SFTP open failed for '{sess.Target}': {ex.Message}");
                MessageBox.Show(this, $"Не удалось подключиться по SFTP:\n{ex.Message}",
                    "SFTP", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // Hide RDP + custom session panels while SFTP is up, mirroring how
        // ShowSession hides other panels when one becomes active.
        private void HideOtherSessionPanelsForSftp()
        {
            foreach (var s in _sessions)
                if (s.Client != null) s.Client.Visible = false;
            foreach (var c in _customPlaceholders.Values) c.Visible = false;
            _bookHost.Visible = false;
        }

        private void DeleteCustomSession(Models.CustomSession sess)
        {
            var r = MessageBox.Show(this,
                $"Удалить сессию «{sess.Name}»?",
                "Удалить сессию", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return;
            _customSessions.Remove(sess);
            // Remove its placeholder panel if cached.
            if (_customPlaceholders.TryGetValue(sess.Id, out var panel))
            {
                _mainArea.Controls.Remove(panel);
                panel.Dispose();
                _customPlaceholders.Remove(sess.Id);
            }
            if (_activeCustomSessionId == sess.Id)
            {
                _activeCustomSessionId = null;
                // v3.0.22 — clear the header pills since the active session is gone.
                UpdateSessionInfoPills(null, null);
            }
            Services.CustomSessionStore.Save(_customSessions);
            RenderSidebar();
            Logger.Info($"Custom session deleted: '{sess.Name}'");
        }

        // v2.1.89 — Async WebView2 init. Ensures the CoreWebView2 controller
        // (binds to the Evergreen runtime on the user's machine), then
        // navigates to the URL. Wrapped in try/catch + logged — if runtime
        // is missing, the placeholder panel is left empty and a MessageBox
        // is shown so the user knows why.
        //
        // v3.7.2 — Also wires the KeePass auto-login flow:
        //   - BasicAuthenticationRequested: silent fill from KeePass for
        //     HTTP Basic / NTLM auth (closed admin panels, routers, etc.).
        //   - AddScriptToExecuteOnDocumentCreatedAsync: injects a tiny JS
        //     shim that watches for focusin on input[type=password] (and the
        //     text/email input paired with one) and posts a message back to
        //     C# so we can show the "Подтянуть из базы" popup.
        //   - WebMessageReceived: routes those messages to ShowWebCredPopup.
        private async Task InitWebViewAsync(Microsoft.Web.WebView2.WinForms.WebView2 webView, string url)
        {
            try
            {
                await webView.EnsureCoreWebView2Async();

                // v3.7.2 — KeePass Web integration.
                WireKeePassWebHandlers(webView);

                webView.CoreWebView2.Navigate(url);
                Logger.Info($"WebView2 navigated to {url}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"WebView2 navigation failed for {url}: {ex.Message}");
                // Defer the MessageBox to UI thread; we're already on UI
                // thread so just show it.
                try
                {
                    MessageBox.Show(this,
                        $"WebView2 недоступен. Убедитесь, что установлен Microsoft Edge WebView2 Runtime.\n\nПодробности: {ex.Message}",
                        "WebView2", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                catch { }
            }
        }

        // v3.7.2 — Wire KeePass auto-login for a WebView2 instance. Called
        // once after EnsureCoreWebView2Async. Idempotent — safe to call
        // multiple times (handlers are tagged via CoreWebView2.Tag).
        private void WireKeePassWebHandlers(Microsoft.Web.WebView2.WinForms.WebView2 webView)
        {
            var core = webView.CoreWebView2;
            if (core == null) return;

            // Basic auth / NTLM — silent fill if KeePass has an entry.
            core.BasicAuthenticationRequested += (s, e) =>
            {
                try
                {
                    var host = ExtractHostFromUri(e.Uri);
                    if (string.IsNullOrEmpty(host)) return;
                    if (!Services.KeePassVault.EnsureReopened()) return;
                    var creds = Services.KeePassVault.FindLogin(Services.KeePassVault.WebGroupPath, host);
                    if (creds == null) return;
                    e.Response.UserName = creds.Value.Login;
                    e.Response.Password = creds.Value.Password;
                    Logger.Info($"WebView2 BasicAuth: filled from KeePass for {host}");
                }
                catch (Exception ex)
                {
                    Logger.Warn($"WebView2 BasicAuth handler failed: {ex.Message}");
                }
            };

            // Inject focus-detect JS — runs on every new document load (main
            // frame + iframes). Posts a message back to C# when a login form
            // input gets focus.
            _ = core.AddScriptToExecuteOnDocumentCreatedAsync(WebLoginDetectScript());

            webView.WebMessageReceived += (s, e) =>
            {
                try
                {
                    // v3.7.2 — JS postMessage with an OBJECT ({kind:'loginFocus',...})
                    // comes through as JSON via WebMessageAsJson. TryGetWebMessageAsString
                    // throws "Value does not fall within the expected range" for
                    // non-string messages — that was the bug behind "no popup ever
                    // appeared". WebMessageAsJson is always a JSON-encoded string
                    // (object → {...}, string → "..." with quotes), so JsonDocument
                    // handles both shapes uniformly.
                    var json = e.WebMessageAsJson;
                    if (string.IsNullOrEmpty(json)) return;
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    if (!doc.RootElement.TryGetProperty("kind", out var kindProp)) return;
                    var kind = kindProp.GetString();
                    if (kind != "loginFocus") return;
                    if (!doc.RootElement.TryGetProperty("url", out var urlProp)) return;
                    var pageUrl = urlProp.GetString() ?? "";
                    var host = ExtractHostFromUri(pageUrl);
                    if (string.IsNullOrEmpty(host)) return;
                    Logger.Info($"WebView2 loginFocus: host={host}, url={pageUrl}");
                    ShowWebCredPopup(webView, host);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"WebView2 WebMessageReceived failed: {ex.Message}");
                }
            };
        }

        // Tiny helper — Uri.TryCreate is fussy about scheme-less input.
        private static string ExtractHostFromUri(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            try
            {
                if (!raw.Contains("://"))
                {
                    if (Uri.TryCreate("https://" + raw, UriKind.Absolute, out var h2)) return h2.Host;
                }
                if (Uri.TryCreate(raw, UriKind.Absolute, out var u)) return u.Host;
            }
            catch { }
            return "";
        }

        // v3.7.2 — JS shim injected into every WebView2 document. Posts
        // "loginFocus" to C# when the user focuses a password input (or a
        // text/email input that has a password sibling nearby). Also listens
        // for "fill" commands from C# and patches the form values in place.
        // Returns the script source as a string.
        //
        // We keep the script intentionally minimal — no React/Vue/Angular
        // framework sniffing beyond dispatching input+change events after
        // setting .value, which is what every modern framework expects.
        private static string WebLoginDetectScript()
        {
            return @"
(function(){
  if (window.__pupseek_login_detect) return;
  window.__pupseek_login_detect = true;

  var lastFocused = null;

  function hasPasswordSibling(el){
    var f = el.form || el.closest('form') || document;
    return !!f.querySelector('input[type=""password""]');
  }

  function fieldSig(el){
    return ((el.name||'') + ' ' + (el.id||'') + ' ' + (el.getAttribute('autocomplete')||'') + ' ' + (el.placeholder||'') + ' ' + (el.getAttribute('aria-label')||'')).toLowerCase();
  }

  function isLoginField(el){
    if (!el || el.tagName !== 'INPUT') return false;
    var type = (el.type || '').toLowerCase();
    if (type !== 'text' && type !== 'email' && type !== 'search' && type !== 'url' && type !== 'tel') return false;
    if (el.readOnly || el.disabled) return false;
    if (el.getAttribute('role') === 'combobox') return false;
    if (el.hasAttribute('list')) return false;
    if (/(lang|language|locale|язык|поиск|search)/.test(fieldSig(el))) return false;
    return true;
  }

  function findLogin(form){
    var inputs = form.querySelectorAll('input[type=""email""], input[type=""text""], input[type=""search""], input[type=""url""], input[type=""tel""]');
    var best = null, bestScore = -1;
    for (var i = 0; i < inputs.length; i++){
      var el = inputs[i];
      if (!isLoginField(el)) continue;
      var score = 0;
      if ((el.type || '').toLowerCase() === 'email') score += 3;
      if (el.getAttribute('autocomplete') === 'username') score += 5;
      if (/(user|login|account|email|логин|пользовател|имя)/.test(fieldSig(el))) score += 5;
      if (score > bestScore){ bestScore = score; best = el; }
    }
    return best;
  }

  document.addEventListener('focusin', function(ev){
    var t = ev.target;
    if (!t || t.tagName !== 'INPUT') return;
    var type = (t.type || '').toLowerCase();
    var isPassword = type === 'password';
    var isLogin = isLoginField(t) && hasPasswordSibling(t);
    if (!isPassword && !isLogin) return;
    lastFocused = t;
    try {
      window.chrome.webview.postMessage({kind:'loginFocus', url: location.href});
    } catch(e){}
  }, true);

  window.chrome.webview.addEventListener('message', function(ev){
    var d = ev.data || {};
    if (d.kind !== 'fill') return;
    var pwd = (lastFocused && lastFocused.tagName === 'INPUT' && (lastFocused.type || '').toLowerCase() === 'password' && document.contains(lastFocused)) ? lastFocused : document.querySelector('input[type=""password""]');
    if (!pwd) return;
    var form = pwd.form || pwd.closest('form') || document;
    var login = null;
    if (lastFocused && document.contains(lastFocused) && isLoginField(lastFocused) && (lastFocused.form === form || form.contains(lastFocused))) {
      login = lastFocused;
    } else {
      login = findLogin(form);
    }
    function setVal(el, v){
      var proto = el.tagName === 'TEXTAREA' ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
      var setter = Object.getOwnPropertyDescriptor(proto, 'value').set;
      setter.call(el, v);
      el.dispatchEvent(new Event('input', {bubbles:true}));
      el.dispatchEvent(new Event('change', {bubbles:true}));
    }
    if (login) setVal(login, d.login || '');
    setVal(pwd, d.password || '');
  });
})();
";
        }

        // v3.7.2 — Popup that appears when the user focuses a login form
        // input in a Web session. Two modes:
        //   - Entry exists in KeePass → "Подтянуть пароль для {host}" → fill
        //     via JS.
        //   - No entry → "Сохранить пароль для {host} в базу?" → open the
        //     RdpPasswordPrompt.ShowForWeb dialog, then fill on OK.
        //
        // Implemented as a borderless TopMost Form so it floats over the
        // WebView2 without taking focus (ShowWithoutActivation). Auto-closes
        // on action, Esc, click-outside, or 30s timeout.
        private void ShowWebCredPopup(Microsoft.Web.WebView2.WinForms.WebView2 webView, string host)
        {
            // Defer to UI thread; WebMessageReceived can fire on continuations.
            if (InvokeRequired) { BeginInvoke((Action)(() => ShowWebCredPopupCore(webView, host))); }
            else ShowWebCredPopupCore(webView, host);
        }

        private void ShowWebCredPopupCore(Microsoft.Web.WebView2.WinForms.WebView2 webView, string host)
        {
            // Already showing for this host on this webView → skip.
            if (_webCredPopupForm != null && _webCredPopupHost == host && _webCredPopupForm.Visible) return;
            // User already filled or ✕'d this host on this webView → stay quiet.
            if (_webPopupDismissed.TryGetValue(webView, out var dismissed) && dismissed.Contains(host)) return;

            // Resolve KeePass state out-of-band so the popup appears instantly.
            Services.KeePassVault.EnsureReopened();
            var found = Services.KeePassVault.FindLogin(Services.KeePassVault.WebGroupPath, host);

            // Close any prior popup.
            try { _webCredPopupForm?.Close(); _webCredPopupForm?.Dispose(); } catch { }

            var popup = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                ShowInTaskbar = false,
                TopMost = true,
                Size = new Size(300, 120),
                BackColor = Helpers.UiTheme.Background,
                ControlBox = false,
                Text = ""
            };

            // 30s auto-close timer — declared early so all control handlers
            // below can stop it.
            var autoClose = new System.Windows.Forms.Timer { Interval = 30_000 };
            autoClose.Tick += (s, e) => { autoClose.Stop(); autoClose.Dispose(); try { popup.Close(); } catch { } };

            // ShowWithoutActivation via CreateParams — WinForms Form doesn't
            // expose it directly, but a TopMost borderless Form with
            // ControlBox=false works well enough.
            var lbl = new Label
            {
                Text = found == null
                    ? $"Сохранить пароль для\n{host} в базу?"
                    : $"Подтянуть пароль для\n{host} из базы?",
                Font = new Font("Segoe UI", 9F),
                ForeColor = Helpers.UiTheme.Foreground,
                TextAlign = ContentAlignment.MiddleLeft,
                Bounds = new Rectangle(12, 10, 252, 44)
            };
            popup.Controls.Add(lbl);

            // v3.7.2 — explicit ✕ in the top-right corner. Distinct from
            // "Отмена": ✕ marks the host as dismissed → popup won't appear
            // again on this WebView2 until dispose. "Отмена" is "not now" —
            // popup can reappear on the next focus.
            var closeBtn = new Label
            {
                Text = "✕",
                Font = new Font("Segoe UI", 9F),
                ForeColor = Helpers.UiTheme.MutedForeground,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand,
                Size = new Size(20, 20),
                Location = new Point(popup.ClientSize.Width - 24, 4)
            };
            closeBtn.MouseEnter += (s, e) => closeBtn.ForeColor = Helpers.UiTheme.Foreground;
            closeBtn.MouseLeave += (s, e) => closeBtn.ForeColor = Helpers.UiTheme.MutedForeground;
            closeBtn.Click += (s, e) =>
            {
                autoClose.Stop();
                DismissWebHost(webView, host);
                try { popup.Close(); } catch { }
            };
            popup.Controls.Add(closeBtn);

            // Position near top-right of the webView's top-level form.
            // WebView2 inherits from Control (no ParentForm property); use
            // FindForm() to walk the parent chain.
            // v3.8.x — 30px further left per user request.
            var host2 = webView.FindForm();
            if (host2 != null)
            {
                popup.Location = new Point(host2.Left + host2.Width - popup.Width - 54, host2.Top + 120);
            }

            // v3.7.2 — small compact filled buttons, matching the KeePass UI
            // style (MakeCompactFilled). Primary action on the right.
            var primaryBtn = Helpers.UiButtons.MakeCompactFilled(
                found == null ? "Ввести и сохранить" : "Заполнить",
                TextRenderer.MeasureText(found == null ? "Ввести и сохранить" : "Заполнить",
                    new Font("Segoe UI Semibold", 9F)).Width + 28);
            primaryBtn.Location = new Point(popup.ClientSize.Width - primaryBtn.Width - 12, 70);
            popup.Controls.Add(primaryBtn);

            var cancelBtn = Helpers.UiButtons.MakeCompactFilled("Отмена", 100,
                bgOverride: Helpers.UiTheme.MutedForeground);
            cancelBtn.Location = new Point(primaryBtn.Left - cancelBtn.Width - 8, 70);
            popup.Controls.Add(cancelBtn);

            // A 1px border around the popup so it reads as a floating card.
            popup.Paint += (s, e) =>
            {
                using var pen = new Pen(Helpers.UiTheme.Border, 1);
                var r = new Rectangle(0, 0, popup.ClientSize.Width - 1, popup.ClientSize.Height - 1);
                e.Graphics.DrawRectangle(pen, r);
            };

            autoClose.Start();

            // Esc closes (treat as "Отмена" — not dismissed).
            popup.KeyPreview = true;
            popup.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) { autoClose.Stop(); popup.Close(); }
            };

            // Click-outside closes — Deactivate fires when user clicks
            // anywhere outside the popup. Treated as "Отмена" (not dismissed).
            popup.Deactivate += (s, e) => { autoClose.Stop(); try { popup.Close(); } catch { } };

            cancelBtn.Click += (s, e) => { autoClose.Stop(); popup.Close(); };

            primaryBtn.Click += (s, e) =>
            {
                autoClose.Stop();
                popup.Close();
                // Fill path → mark host as dismissed so the popup doesn't
                // reappear on the next focusin (the form is already filled).
                DismissWebHost(webView, host);
                if (found != null)
                {
                    // Fill from KeePass.
                    FillWebLogin(webView, found.Value.Login, found.Value.Password);
                }
                else
                {
                    // Open the entry dialog.
                    var res = RdpPasswordPrompt.ShowForWeb(this, host);
                    if (res != null)
                    {
                        FillWebLogin(webView, res.Login, res.Password);
                    }
                }
            };

            _webCredPopupForm = popup;
            _webCredPopupHost = host;
            popup.FormClosed += (s, e) =>
            {
                if (ReferenceEquals(_webCredPopupForm, popup)) { _webCredPopupForm = null; _webCredPopupHost = null; }
            };

            popup.Show(this);
        }

        // v3.7.2 — mark a host as dismissed for a specific WebView2. Subsequent
        // loginFocus events for this host/webView pair are silently ignored
        // until the WebView2 is disposed. Used by the ✕ button and after a
        // successful fill.
        private void DismissWebHost(Microsoft.Web.WebView2.WinForms.WebView2 webView, string host)
        {
            if (!_webPopupDismissed.TryGetValue(webView, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _webPopupDismissed[webView] = set;
            }
            set.Add(host);
        }

        private void FillWebLogin(Microsoft.Web.WebView2.WinForms.WebView2 webView, string login, string password)
        {
            try
            {
                // v3.7.2 — host→page channel is CoreWebView2.PostWebMessageAsJson.
                // The injected JS shim listens on window.chrome.webview's
                // 'message' event, which fires for messages sent via this API.
                // (Calling window.postMessage(...) through ExecuteScriptAsync
                // would deliver to window.addEventListener('message') in the
                // page's JS context instead — different channel, listener
                // never saw it. That was the bug behind "Заполнить does
                // nothing".)
                var json = System.Text.Json.JsonSerializer.Serialize(new { kind = "fill", login = login, password = password });
                webView.CoreWebView2.PostWebMessageAsJson(json);
                Logger.Info($"WebView2 fill: posted for login '{login}'");
            }
            catch (Exception ex)
            {
                Logger.Warn($"FillWebLogin failed: {ex.Message}");
            }
        }

        // v2.1.83 — Lazy-create (or return cached) placeholder panel for a
        // custom session. Shown in main area when the user clicks a custom
        // session item. Replaces the RDP client panel.
        //
        // v2.1.86: Web sessions now embed a WebBrowser control directly in
        // the main area (was: external browser). SSH sessions still launch
        // externally (wt.exe / cmd) because embedding a terminal requires
        // WebView2 + WinUI hosting which is too complex for v1.
        private Panel GetOrCreateCustomPlaceholder(Models.CustomSession sess)
        {
            if (_customPlaceholders.TryGetValue(sess.Id, out var existing))
            {
                existing.Visible = false;
                return existing;
            }
            var panel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Helpers.UiTheme.Background,
                Visible = false
            };

            if (sess.Kind == Models.SessionKind.Web)
            {
                // v2.1.89: WebView2 (Chromium/Edge engine) instead of the
                // legacy WebBrowser (IE). Renders modern sites (Grafana,
                // webmin, React/Vue apps) without errors.
                //
                // Requires WebView2 Runtime (Evergreen) — on Win10/11 with
                // Edge installed, always present. If missing, the control
                // raises an InitializationFailed event we handle below.
                var url = sess.Target.Trim();
                if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    url = "https://" + url;
                }
                try
                {
                    var webView = new Microsoft.Web.WebView2.WinForms.WebView2
                    {
                        Dock = DockStyle.Fill
                    };
                    panel.Controls.Add(webView);
                    // Async-init: EnsureCoreWebView2Async then navigate.
                    // The control shows blank until init completes (~100ms
                    // with Evergreen runtime, longer on first launch).
                    _ = InitWebViewAsync(webView, url);
                    Logger.Info($"Custom session (Web): WebView2 created for {url}");
                }
                catch (Exception ex)
                {
                    Logger.Warn($"WebView2 init failed, falling back to legacy WebBrowser: {ex.Message}");
                    // Fallback to old WebBrowser control if WebView2 runtime
                    // is missing or init fails.
                    var browser = new WebBrowser
                    {
                        Dock = DockStyle.Fill,
                        Url = new Uri(url)
                    };
                    panel.Controls.Add(browser);
                }
            }
            else if (sess.Kind == Models.SessionKind.Telnet)
            {
                // v3.9.3 — Telnet: встроенный TelnetXtermControl, учётка
                // вводится интерактивно в терминале (login:/Password:).
                // Target: "host" или "host:port" (по умолчанию 23).
                var parts = sess.Target.Split(':');
                var host = parts[0].Trim();
                var port = 23;
                if (parts.Length > 1 && int.TryParse(parts[1], out var p)) port = p;
                if (string.IsNullOrWhiteSpace(host))
                {
                    var hint = new Label
                    {
                        Text = "Не указан хост.",
                        Dock = DockStyle.Fill,
                        Font = new Font("Segoe UI", 10F),
                        ForeColor = Helpers.UiTheme.MutedForeground,
                        BackColor = Helpers.UiTheme.Background,
                        TextAlign = ContentAlignment.MiddleCenter
                    };
                    panel.Controls.Add(hint);
                }
                else
                {
                    var term = new ShadowRDP.Controls.TelnetXtermControl
                    {
                        Dock = DockStyle.Fill,
                        Host = host,
                        Port = port
                    };
                    panel.Controls.Add(term);
                    _ = term.ConnectAsync().ContinueWith(t =>
                    {
                        if (t.IsFaulted)
                        {
                            try
                            {
                                Invoke(new Action(() =>
                                {
                                    panel.Controls.Remove(term);
                                    term.Cleanup();
                                    var err = new Label
                                    {
                                        Text = $"Не удалось подключиться: {t.Exception?.GetBaseException().Message}",
                                        Dock = DockStyle.Fill,
                                        Font = new Font("Segoe UI", 10F),
                                        ForeColor = Helpers.UiTheme.Danger,
                                        BackColor = Helpers.UiTheme.Background,
                                        TextAlign = ContentAlignment.MiddleCenter
                                    };
                                    panel.Controls.Add(err);
                                }));
                            }
                            catch { }
                        }
                    }, TaskScheduler.Default);
                }
            }
            else
            {
                // v2.1.92: SSH — embedded SshTerminalControl. Prompt for
                // username + password, then connect via SSH.NET and render
                // the shell in the main area. Creds in memory only.
                // Target format: "host" or "host:port" — split off port
                // if present.
                var parts = sess.Target.Split(':');
                var host = parts[0];
                var port = 22;
                if (parts.Length > 1 && int.TryParse(parts[1], out var p)) port = p;
                // v3.7.0 — KeePass entry key: "host" for the default port,
                // "host:port" otherwise.
                var sshId = port == 22 ? host : $"{host}:{port}";

                // v3.7.0 — resolution order: in-memory session cache →
                // KeePass vault (PupSeekRDP/SSH) → prompt. Credentials typed
                // into the prompt are saved to the vault when it's open.
                // A pending _sshForcePrompt (from "Зайти под другой учётной
                // записью") skips the vault so the prompt always appears.
                var forcePrompt = _sshForcePrompt.Remove(sess.Id);
                var kpCreds = !forcePrompt && KeePassVault.IsOpen
                    ? KeePassVault.FindLogin(KeePassVault.SshGroupPath, sshId)
                    : null;
                var creds = _sshCredsCache.TryGetValue(sess.Id, out var cached)
                    ? new Helpers.SshCredsDialog.Creds { User = cached.User, Password = cached.Password }
                    : kpCreds != null
                        ? new Helpers.SshCredsDialog.Creds { User = kpCreds.Value.Login, Password = kpCreds.Value.Password }
                        : Helpers.SshCredsDialog.Prompt(this, host);
                if (creds == null)
                {
                    // Cancelled — show a small "no creds" hint panel.
                    var cancel = new Label
                    {
                        Text = "Подключение отменено. Кликни сессию снова.",
                        Dock = DockStyle.Fill,
                        Font = new Font("Segoe UI", 10F),
                        ForeColor = Helpers.UiTheme.MutedForeground,
                        BackColor = Helpers.UiTheme.Background,
                        TextAlign = ContentAlignment.MiddleCenter
                    };
                    panel.Controls.Add(cancel);
                }
                else
                {
                    // v3.2.3 — SshXtermControl (xterm.js via WebView2) replaces
                    // SshTerminalControl. Full TUI support — MC, vim, nano,
                    // less now work because xterm.js implements cursor moves,
                    // alternate screen, mouse events. Was plain RichTextBox
                    // that strip'd ANSI and couldn't position the cursor.
                    var term = new ShadowRDP.Controls.SshXtermControl
                    {
                        Dock = DockStyle.Fill,
                        Host = host,
                        Port = port,
                        Username = creds.User,
                        Password = creds.Password
                    };
                    // v2.1.96: cache creds so "Перезапуск сессии" can reuse
                    // them without re-prompting. Cleared on app exit + when
                    // the user clicks "Зайти под другой учётной записью".
                    _sshCredsCache[sess.Id] = (creds.User, creds.Password);
                    // v3.7.0 — creds came from the prompt (not cache/vault):
                    // persist them to KeePass so next launches auto-fill.
                    // Skipped when the prompt was forced via "Зайти под
                    // другой учётной записью" — those are throwaway creds.
                    if (kpCreds == null && !forcePrompt && KeePassVault.IsOpen)
                        KeePassVault.SaveLogin(KeePassVault.SshGroupPath, sshId, creds.User, creds.Password);
                    panel.Controls.Add(term);
                    // Fire-and-forget connect — the control shows its own
                    // status + error messages.
                    _ = term.ConnectAsync().ContinueWith(t =>
                    {
                        if (t.IsFaulted)
                        {
                            try
                            {
                                Invoke(new Action(() =>
                                {
                                    panel.Controls.Remove(term);
                                    term.Cleanup();
                                    var err = new Label
                                    {
                                        Text = $"Не удалось подключиться: {t.Exception?.GetBaseException().Message}",
                                        Dock = DockStyle.Fill,
                                        Font = new Font("Segoe UI", 10F),
                                        ForeColor = Helpers.UiTheme.Danger,
                                        BackColor = Helpers.UiTheme.Background,
                                        TextAlign = ContentAlignment.MiddleCenter
                                    };
                                    panel.Controls.Add(err);
                                }));
                            }
                            catch { }
                        }
                    }, TaskScheduler.Default);
                }
            }

            _mainArea.Controls.Add(panel);
            // Send to back so when shown, it covers the RDP client panels
            // (which are also Fill-docked).
            panel.SendToBack();
            _customPlaceholders[sess.Id] = panel;
            return panel;
        }

        // Add a "Группа ▶" submenu to a custom session's context menu.
        private void AddGroupSubmenuForCustom(ContextMenuStrip parent, Models.CustomSession sess)
        {
            parent.Items.Add(new ToolStripSeparator());
            var miParent = (ToolStripMenuItem)parent.Items.Add("Группа", Helpers.MenuIcons.Get("list"));
            foreach (var g in _sessions.Select(s => s.Group ?? "")
                .Concat(_customSessions.Select(c => c.Group ?? ""))
                .Where(g => !string.IsNullOrEmpty(g))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.ToUpperInvariant()))
            {
                var mi = (ToolStripMenuItem)miParent.DropDownItems.Add(g);
                mi.Checked = string.Equals(sess.Group ?? "", g, StringComparison.OrdinalIgnoreCase);
                var captured = g;
                mi.Click += (s, e) =>
                {
                    sess.Group = captured;
                    Services.CustomSessionStore.Save(_customSessions);
                    RenderSidebar();
                };
            }
            miParent.DropDownItems.Add(new ToolStripSeparator());
            var miNoGroup = (ToolStripMenuItem)miParent.DropDownItems.Add("Без группы");
            miNoGroup.Checked = string.IsNullOrEmpty(sess.Group);
            miNoGroup.Click += (s, e) =>
            {
                sess.Group = null;
                Services.CustomSessionStore.Save(_customSessions);
                RenderSidebar();
            };
            var miNew = miParent.DropDownItems.Add("Создать новую…");
            miNew.Click += (s, e) =>
            {
                var newName = PromptDialog.Show(this, "Новая группа", "Имя группы:", "");
                if (string.IsNullOrWhiteSpace(newName)) return;
                sess.Group = newName.Trim();
                Services.CustomSessionStore.Save(_customSessions);
                RenderSidebar();
            };
        }

        // "Группа ▶" submenu — lists all existing groups (with check on
        // current), plus separator, plus "Без группы" and "Создать новую…".
        private void AddGroupSubmenu(ContextMenuStrip parent, RdpSession sess)
        {
            var miParent = (ToolStripMenuItem)parent.Items.Add("Группа", Helpers.MenuIcons.Get("list"));
            // Existing groups (excluding "Без группы").
            foreach (var g in _sessions
                         .Select(s => s.Group ?? "")
                         .Where(g => !string.IsNullOrEmpty(g))
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(g => g.ToUpperInvariant()))
            {
                var mi = (ToolStripMenuItem)miParent.DropDownItems.Add(g);
                mi.Checked = string.Equals(g, sess.Group, StringComparison.OrdinalIgnoreCase);
                mi.Click += (s, e) => SetSessionGroup(sess, g);
            }
            miParent.DropDownItems.Add(new ToolStripSeparator());

            var miNone = (ToolStripMenuItem)miParent.DropDownItems.Add("Без группы");
            miNone.Checked = string.IsNullOrEmpty(sess.Group);
            miNone.Click += (s, e) => SetSessionGroup(sess, "");

            miParent.DropDownItems.Add(new ToolStripSeparator());

            var miNew = miParent.DropDownItems.Add("Создать новую…");
            miNew.Click += (s, e) =>
            {
                var name = PromptDialog.Show(this, "Новая группа",
                    "Введите имя новой группы:", "");
                if (string.IsNullOrWhiteSpace(name)) return;
                SetSessionGroup(sess, name.Trim());
            };
        }

        // Reassign a session to a different group + persist + re-render.
        private void SetSessionGroup(RdpSession sess, string newGroup)
        {
            if (string.Equals(sess.Group ?? "", newGroup, StringComparison.OrdinalIgnoreCase))
                return;
            sess.Group = newGroup ?? "";
            Logger.Info($"RDP Manager: '{sess.Computer}' → group '{newGroup}'");
            PersistSessions();
            RenderSidebar();
        }

        // v4.9.29 — «Редактировать запись»: имя компьютера, IP, группа.
        // Target пересчитывается как в AddSession (IP иначе имя).
        // Активная сессия продолжает работать на старом подключении до
        // перезапуска; переподключение подхватит новые данные.
        private void EditRdpSession(RdpSession sess)
        {
            var knownGroups = _sessions.Select(x => x.Group ?? "")
                .Concat(_customSessions.Select(x => x.Group ?? ""))
                .Where(g => !string.IsNullOrEmpty(g))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.ToUpperInvariant());
            var res = Forms.RdpSessionEditForm.Edit(this, sess.DisplayName, sess.Computer, sess.Ip, sess.Group ?? "", sess.Notes, knownGroups);
            if (res == null) return;
            var (displayName, computer, ip, group, notes) = res.Value;
            if (!string.Equals(sess.Computer, computer, StringComparison.OrdinalIgnoreCase)
                && _sessions.Any(s => !ReferenceEquals(s, sess) && string.Equals(s.Computer, computer, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show(this, $"Сессия '{computer}' уже существует.", "Редактировать запись", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            sess.DisplayName = displayName ?? "";
            sess.Computer = computer;
            sess.Ip = ip ?? "";
            sess.Target = !string.IsNullOrEmpty(ip) ? ip : computer;
            sess.Group = group ?? "";
            sess.Notes = notes ?? "";
            Logger.Info($"RDP Manager: session edited → '{computer}' (name='{displayName}', ip={ip}, group='{group}')");
            PersistSessions();
            RenderSidebar();
        }

        // v3.0.8 — "Автозагрузка ▶" submenu: Да (on_green) / Нет (off_red),
        // radio-style — the item matching sess.AutoOpen is Checked. Toggle
        // only affects the next RestoreSessions (app restart); a live session
        // is left running as-is.
        // v3.0.9 — returns ToolStripMenuItem instead of adding to parent, so
        // the caller can choose the position via Items.Insert (was added at
        // the bottom of the menu, now the 4th item right after miOtherUser).
        private ToolStripMenuItem BuildAutoOpenMenuItem(RdpSession sess)
        {
            var miParent = new ToolStripMenuItem("Автозагрузка", Helpers.MenuIcons.Get("restart"));
            miParent.ToolTipText = "Управление запуском сессии при открытии программы";

            var miYes = (ToolStripMenuItem)miParent.DropDownItems.Add("Да", Helpers.MenuIcons.Get("on_green"));
            miYes.Checked = sess.AutoOpen;
            miYes.Click += (s, e) => SetSessionAutoOpen(sess, true);

            var miNo = (ToolStripMenuItem)miParent.DropDownItems.Add("Нет", Helpers.MenuIcons.Get("off_red"));
            miNo.Checked = !sess.AutoOpen;
            miNo.Click += (s, e) => SetSessionAutoOpen(sess, false);

            return miParent;
        }

        // Persist a new AutoOpen value. No sidebar rerender — visible status
        // dot still reflects connection state, this setting only matters at
        // the next app launch.
        private void SetSessionAutoOpen(RdpSession sess, bool value)
        {
            if (sess.AutoOpen == value) return;
            sess.AutoOpen = value;
            Logger.Info($"RDP Manager: '{sess.Computer}' автозагрузка → {(value ? "Да" : "Нет")}");
            PersistSessions();
        }

        // Rename a group across all its sessions.
        private void RenameGroup(string oldGroup)
        {
            var newName = PromptDialog.Show(this, "Переименовать группу",
                $"Новое имя для группы «{oldGroup}»:", oldGroup);
            if (string.IsNullOrWhiteSpace(newName) ||
                string.Equals(newName, oldGroup, StringComparison.OrdinalIgnoreCase))
                return;
            // Preserve collapse state under the new name.
            if (_collapsedGroups.Contains(oldGroup))
            {
                _collapsedGroups.Remove(oldGroup);
                _collapsedGroups.Add(newName);
            }
            foreach (var s in _sessions)
                if (string.Equals(s.Group ?? "", oldGroup, StringComparison.OrdinalIgnoreCase))
                    s.Group = newName.Trim();
            Logger.Info($"RDP Manager: renamed group '{oldGroup}' → '{newName}'");
            PersistSessions();
            RenderSidebar();
        }

        // Delete a group — its sessions fall back to "Без группы".
        private void DeleteGroup(string group)
        {
            var r = MessageBox.Show(this,
                $"Удалить группу «{group}»?\nСессии внутри перейдут в «Без группы».",
                "Удалить группу", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return;
            foreach (var s in _sessions)
                if (string.Equals(s.Group ?? "", group, StringComparison.OrdinalIgnoreCase))
                    s.Group = "";
            _collapsedGroups.Remove(group);
            Logger.Info($"RDP Manager: deleted group '{group}'");
            PersistSessions();
            RenderSidebar();
        }

        private sealed class RdpSession
        {
            public string Computer { get; set; } = "";
            // v4.9.34 — отображаемое название ("" = Computer). Заметки.
            public string DisplayName { get; set; } = "";
            public string Notes { get; set; } = "";
            public string Target { get; set; } = "";
            public string Ip { get; set; } = "";
            public string Group { get; set; } = "";  // v2.1.49 — user-defined group, "" = Без группы
            // v3.0.7: Client is null when AutoOpen=false and the user hasn't
            // clicked the tab yet. ActivateSession() materialises it.
            public RdpClient? Client { get; set; }
            // v3.0.7: when false, RestoreSessions adds as sidebar-only
            // (no live RDP connection). User picks via context menu.
            public bool AutoOpen { get; set; } = true;
            // v2.1.53 — per-session credential override. When non-null,
            // RestartSession connects with these instead of the shared
            // RdpCredentialStore creds. NOT persisted — on app restart the
            // field is null and the user must re-enter via the menu item.
            public string? LoginOverride { get; set; }
            public string? PasswordOverride { get; set; }
            // v3.8.x — drag&drop sidebar ordering. Explicit position within the
            // session's group (RDP + custom sessions share the numbering).
            public int SortOrder { get; set; }
            // v3.9.3 — transient: подключение из книги без следа (вкладка
            // открывается, но в сайдбар и на диск запись НЕ попадает).
            public bool Transient { get; set; }
        }

        // v2.1.53 — Public DTO for outside callers (tray menu, global
        // Ctrl+middle-click menu). Exposes just what they need (Computer +
        // Ip) without leaking the private RdpSession type.
        public sealed class SessionInfo
        {
            public string Computer { get; set; } = "";
            public string Ip { get; set; } = "";
        }

        // Snapshot of the current session list, in the same order shown in
        // the sidebar (alphabetical by group, then "Без группы" at the
        // bottom). Returns an empty list if there are no sessions.
        public List<SessionInfo> GetActiveSessions()
        {
            // v3.0.7: only sessions with a live RdpClient — pending
            // (AutoOpen=false) sessions aren't actually connected.
            var list = new List<SessionInfo>(_sessions.Count);
            foreach (var s in _sessions)
            {
                if (s.Client != null)
                    list.Add(new SessionInfo { Computer = s.Computer, Ip = s.Ip });
            }
            return list;
        }

        // Find a session by computer name (case-insensitive) and bring it
        // to the foreground: switch to Sessions tab, call ShowSession
        // (which paints the active highlight + arrow + shows the RDP
        // client panel). No-op if no such session exists.
        public void ActivateSessionByComputer(string computer)
        {
            if (string.IsNullOrEmpty(computer)) return;
            RdpSession? found = null;
            foreach (var s in _sessions)
            {
                if (string.Equals(s.Computer, computer, StringComparison.OrdinalIgnoreCase))
                {
                    found = s;
                    break;
                }
            }
            if (found == null) return;
            if (_activeTab != SidebarTab.Sessions)
                SwitchTab(SidebarTab.Sessions);
            ActivateSession(found);
        }
}
}
