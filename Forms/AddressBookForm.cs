using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ShadowRDP.Controls;
using ShadowRDP.Models;
using ShadowRDP.Services;

namespace ShadowRDP.Forms
{
    // Address book dialog. Mirror of Show-AddressBook from the PowerShell
    // version. Shows all entries from AddressBookStore in a DataGridView,
    // supports live search, manual delete, and inline editing of FullName +
    // Phone. Async refresh / AD import come in later modules.
    //
    // Layout: top search row, full-height grid, bottom button row (Delete +
    // Close). Size is restored from WindowStateStore (module 18 — for now
    // uses fixed default). All children are positioned by SyncLayout which
    // runs on Shown + SizeChanged — WinForms anchor math doesn't fire
    // reliably when Windows restores saved placement.
    public class AddressBookForm : Form
    {
        private readonly CaretHidingTextBox _searchBox;
        private readonly Label _searchPlaceholder;
        private readonly Panel _searchWrapper;
        // v3.0.23 — "✕" clear button inside the search wrapper, shown only
        // when the search box has text. Click → empty the box + re-filter.
        private Label _searchClearBtn = null!;
        private readonly DataGridView _grid;
        private DataGridViewColumn? _fillerCol;
        private bool _suppressWidthSave;
        private readonly Label _statusLabel;
        private readonly Panel _tabsPanel;

        // v2.1.63 — static caches shared across all CellPainting calls.
        // GDI+ Font construction is expensive (~0.1ms each, allocates a
        // native HFONT and a managed wrapper). OnCellPainting was creating
        // new Font("Segoe UI Semibold", 9F) per cell × per paint — for
        // 80 rows × ~6 visible cols × ~30 paints/scroll that's ~14k Font
        // allocations per scroll gesture, which is exactly the "как будто
        // 1000000 записей" lag the user reported. Caching them once as
        // static fields turns each paint into a hash lookup + GDI call.
        private static readonly Font _headerFont = new Font("Segoe UI Semibold", 9F);
        private static readonly Font _groupBadgeFont = new Font("Segoe UI Semibold", 8.5F);
        private static readonly Brush _headerGapBrush = new SolidBrush(Color.White);
        private static readonly Brush _headerBgBrush = new SolidBrush(Color.White);
        private static readonly Brush _headerTextBrush = new SolidBrush(Helpers.UiTheme.Foreground);
        private static readonly Brush _pingOnlineBrush = new SolidBrush(Helpers.UiTheme.StatusOnline);
        private static readonly Brush _pingOfflineBrush = new SolidBrush(Helpers.UiTheme.StatusOffline);
        private static readonly Brush _rdpActiveBrush = new SolidBrush(Helpers.UiTheme.StatusActive);
        // v3.1.x: RDP dot colour (same #296AA0 the painting used before).
        private static readonly Brush _rdpDotBrush = new SolidBrush(ShadowRDP.Helpers.UiTheme.HeaderBar);

        // v3.1.x: cached snapshot of RdpManagerForm's live sessions. Paint
        // used to call HasSessionFor(computer) per painted cell — that's a
        // linear scan over all sessions for EVERY RDP cell on every repaint.
        // Rebuilt lazily + on SessionListChanged; lookup is O(1) afterwards.
        private readonly HashSet<string> _rdpActiveSessions = new(StringComparer.OrdinalIgnoreCase);
        private bool _rdpSessionsCached;

        // v3.1.x: group badge GDI objects are cached per group key instead
        // of being allocated (SolidBrush/Pen/GraphicsPath) for every painted
        // cell. The rounded path is pure geometry — cached by size so it can
        // be reused across rows after a TranslateTransform.
        private static readonly Dictionary<string, (SolidBrush bg, Pen border)> _badgeGdiCache = new();
        private static readonly Dictionary<(int w, int h), System.Drawing.Drawing2D.GraphicsPath> _badgePathCache = new();

        // v3.1.x: search-box debounce — ApplySearchFilter used to run on
        // EVERY keystroke (loop over all rows × all cells + status recount).
        // A short timer collapses a typing burst into one filter pass.
        private readonly System.Windows.Forms.Timer _searchDebounce;

        // v2.1.78: WM_SETREDRAW for batch updates in ReloadGrid.
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        private const int WM_SETREDRAW = 0x000B;
        // v2.0.6: bottom buttons are flat Labels with bold font (pill-style).
        // Different roles get different colours:
        //   _deleteBtn    → Danger (red)
        //   _refreshAllBtn→ Accent (indigo, primary action — gets soft bg)
        //   others        → MutedForeground (grey)
        private readonly Label _deleteBtn;
        private readonly Label _fullNameBtn;
        private readonly Label _importAdBtn;
        private readonly Label _refreshAllBtn;
        private readonly Label _cancelBtn;
        private readonly Label _closeBtn;

        // Filter row buttons — one per group + "Все" + "Без группы".
        // v2.0.5: switched from NeoButton.Pill to plain Label because the
        // Button base class kept drawing a flat-style background even with
        // OnPaintBackground overridden. Label has no such baggage —
        // BackColor=Transparent is genuinely transparent.
        private readonly Dictionary<string, Label> _filterButtons = new();
        private string _groupFilter = "";
        // v3.8.x — right-click group-management menu on the filter pills.
        private string _ctxGroupKey = "";

        // v2.x — "Открытые файлы" button in the filter strip. Only visible
        // while the "Серверы" tab is active (see OnFilterButtonClicked).
        // Opens the domain-wide open-files scanner (OpenFilesForm).
        private readonly Label _openFilesBtn;
        // v3.8.x — "Сообщение всем" button, same style. Only visible while
        // the "Пользователи" tab is active. Sends a popup message to the
        // computers of the users currently shown in that tab.
        private readonly Label _messageAllBtn;
        // Inventory button — "Инвентаризация ПО". Visible on the "Все" tab.
        // Opens SoftwareInventoryForm to scan all address-book computers.
        // v4.0 — dashboard monitoring indicator (set of Computer names that are monitored)
        private HashSet<string> _monitoredSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Label _inventoryBtn;

        // Sort state for column-header click. Null = no sort (load order).
        private string? _sortColumn;
        private bool _sortAscending = true;

        // Cancellation for the currently-running async operation (Refresh All
        // or Update FullName). Null when nothing is running.
        private CancellationTokenSource? _cts;

        // Singleton — only one address book window exists at a time. The
        // book is now opened non-modal (Show, not ShowDialog) so the user
        // can interact with RDP Manager and the main form while it's open.
        // Clicking the book button while an instance is already open just
        // brings the existing one to the front.
        private static AddressBookForm? _instance;
        public static bool IsOpen => _instance != null && !_instance.IsDisposed;

        // v2.2.10 — Public reload API for callers (e.g. RdpManagerForm's
        // "+ В книгу" button after adding a new entry) so they can refresh
        // the grid without needing direct access to ReloadGrid.
        public void ReloadFromStore()
        {
            if (IsDisposed) return;
            ReloadGrid();
        }
        public static AddressBookForm Instance
        {
            get
            {
                if (_instance == null || _instance.IsDisposed)
                    _instance = new AddressBookForm();
                return _instance;
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (_instance == this) _instance = null;
            base.OnFormClosed(e);
        }

        // Repurpose this Form as a child control of a hosting panel — used
        // by RdpManagerForm to embed the book as a sidebar tab. TopLevel=false
        // is the WinForms trick that lets a Form live inside another control;
        // FormBorderStyle=None strips the title bar; Dock=Fill makes it fill
        // the host. After calling this, add `this` to the host's Controls
        // and call Show() (which now just sets Visible=true).
        public void PrepareForEmbedding()
        {
            TopLevel = false;
            FormBorderStyle = FormBorderStyle.None;
            Dock = DockStyle.Fill;
            // Start hidden — RdpManagerForm shows it only on Book tab.
            Visible = false;
        }

        public AddressBookForm()
        {
            Text = "Адресная книга";
            ClientSize = new Size(1296, 720);
            MinimumSize = new Size(900, 480);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9F);
            // v2.0 — Figma redesign. Warm cream background replaces the cool
            // gray of v1.x. All child controls pull their colours from
            // UiTheme so the look stays coherent.
            BackColor = Helpers.UiTheme.Background;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;

            // === Header panel ===
            // Figma layout: a strip with logo on the left, "Адресная книга"
            // title, then a search box pushed to the right edge. Count info
            // lives in the footer status label, NOT here — putting it in the
            // header caused a rendering artefact where the text showed
            // through transparent pill buttons.
            var header = new Panel
            {
                Location = new Point(0, 0),
                Size = new Size(1296, Helpers.UiTheme.HeaderHeight),
                BackColor = Helpers.UiTheme.Card,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            header.Paint += DrawHeaderLogo;
            Controls.Add(header);

            var title = new Label
            {
                Text = "Адресная книга",
                Location = new Point(48, 18),
                AutoSize = true,
                Font = new Font("Segoe UI Semibold", 11F),
                ForeColor = Helpers.UiTheme.Foreground,
                BackColor = Helpers.UiTheme.Card
            };
            header.Controls.Add(title);

            // (Removed "RDP Manager" button — the book is now a sidebar
            // tab inside RDP Manager, so a button to open the manager
            // from inside the book would be circular.)

            // Search box wrapped in a Panel so we can give it rounded corners
            // (WinForms TextBox has no border-radius property; the wrapper's
            // Region clips the TextBox to a rounded rectangle).
            // v2.0.6: height reduced 40 → 32 (-20%), corner radius 6px.
            var searchWrapper = new Panel
            {
                Location = new Point(640, 12),
                Size = new Size(640, 32),
                BackColor = Helpers.UiTheme.Muted,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            using (var path = RoundedRect(new Rectangle(0, 0, 640, 32), 6))
            {
                searchWrapper.Region = new System.Drawing.Region(path);
            }
            _searchWrapper = searchWrapper;

            // v2.1.62: CaretHidingTextBox subclass — hides the blinking |
            // cursor while focused. Was plain TextBox + event-based
            // HideCaret (unreliable).
            _searchBox = new ShadowRDP.Controls.CaretHidingTextBox
            {
                Location = new Point(10, 4),
                // v3.0.23: width 620 → 590 to leave room for the clear button
                // on the right (24×24 + 6px gap + 10px right margin).
                Size = new Size(590, 24),
                // Multiline=true is the ONLY way to make a WinForms TextBox
                // honour its Size.Height — single-line boxes auto-size to the
                // font and ignore Height. We don't actually want line breaks
                // in a search field, but TextChanged fires per keystroke and
                // we don't gate on Enter, so Multiline's behaviour is fine.
                Multiline = true,
                BorderStyle = BorderStyle.None,
                BackColor = Helpers.UiTheme.Muted,
                ForeColor = Helpers.UiTheme.Foreground,
                Font = new Font("Segoe UI", 12F),
                TextAlign = HorizontalAlignment.Left
            };
            // v3.1.x: debounce — typing collapses into one ApplySearchFilter.
            _searchDebounce = new System.Windows.Forms.Timer { Interval = 120 };
            _searchDebounce.Tick += (s, e) =>
            {
                _searchDebounce.Stop();
                if (IsDisposed || _grid == null || _grid.IsDisposed) return;
                ApplySearchFilter();
            };

            _searchBox.TextChanged += (s, e) =>
            {
                _searchDebounce.Stop();
                _searchDebounce.Start();
                var hasText = !string.IsNullOrEmpty(_searchBox.Text);
                // Show/hide placeholder overlay — EM_SETCUEBANNER doesn't
                // work on Multiline TextBoxes (Win32 limitation), so we
                // emulate it with a Label that floats over the input.
                if (_searchPlaceholder != null)
                {
                    _searchPlaceholder.Visible = !hasText;
                }
                // v3.0.23 — toggle the clear button.
                if (_searchClearBtn != null)
                {
                    _searchClearBtn.Visible = hasText;
                }
            };
            // v2.1.52: focus feedback — lighten the field on Enter, restore
            // the default Muted bg on Leave. Background (#F5F0EB) is ~10%
            // lighter than Muted (#EDE8E2): noticeably different, but not
            // the stark Card (#FAF7F4) white we had before.
            // v2.1.62: caret (|) hiding now handled by CaretHidingTextBox
            // subclass — see Controls/OptimizedControls.cs.
            _searchBox.Enter += (s, e) =>
            {
                searchWrapper.BackColor = Helpers.UiTheme.Background;
                _searchBox.BackColor = Helpers.UiTheme.Background;
            };
            _searchBox.Leave += (s, e) =>
            {
                searchWrapper.BackColor = Helpers.UiTheme.Muted;
                _searchBox.BackColor = Helpers.UiTheme.Muted;
            };
            searchWrapper.Controls.Add(_searchBox);

            // Placeholder overlay — looks like cue banner but works on Multiline.
            _searchPlaceholder = new Label
            {
                Text = "Поиск…",
                // v2.1.90: was AutoSize=true (default) → text not vertically
                // aligned with typed text in the TextBox below. Now AutoSize
                // =false + Size matching TextBox + TextAlign=MiddleLeft.
                Location = new Point(14, 6),
                Size = new Size(200, 22),
                ForeColor = Helpers.UiTheme.MutedForeground,
                // v2.1.52: Transparent so it inherits searchWrapper's bg,
                // which flips between Muted (default) and Card (focused).
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 12F),
                Cursor = Cursors.IBeam
            };
            _searchPlaceholder.Click += (s, e) => _searchBox.Focus();
            searchWrapper.Controls.Add(_searchPlaceholder);
            // Z-order: placeholder added AFTER textbox so it sits on top.
            _searchPlaceholder.BringToFront();

            // v3.0.23 — clear button. Sits at the right edge of the wrapper,
            // visible only when the search box has text. Click → wipe + focus.
            _searchClearBtn = new Label
            {
                Text = "✕",
                Location = new Point(606, 4),
                Size = new Size(24, 24),
                Font = new Font("Segoe UI Symbol", 11F, FontStyle.Bold),
                ForeColor = Helpers.UiTheme.MutedForeground,
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand,
                TextAlign = ContentAlignment.MiddleCenter,
                Visible = false
            };
            _searchClearBtn.Click += (s, e) =>
            {
                _searchBox.Text = "";
                _searchBox.Focus();
            };
            _searchClearBtn.MouseEnter += (s, e) =>
                _searchClearBtn.ForeColor = Helpers.UiTheme.Foreground;
            _searchClearBtn.MouseLeave += (s, e) =>
                _searchClearBtn.ForeColor = Helpers.UiTheme.MutedForeground;
            searchWrapper.Controls.Add(_searchClearBtn);
            _searchClearBtn.BringToFront();

            header.Controls.Add(searchWrapper);

            // === Filter row (pills) ===
            // Figma: tab strip directly below the header, light beige background,
            // pills with soft indigo when active, transparent when inactive.
            var tabsPanel = new Panel
            {
                Location = new Point(0, Helpers.UiTheme.HeaderHeight),
                Size = new Size(1296, Helpers.UiTheme.TabsHeight),
                BackColor = Helpers.UiTheme.Card,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            Controls.Add(tabsPanel);
            _tabsPanel = tabsPanel;

            // Build the six pill toggle buttons inside the tabs panel.
            // Sequential left-to-right placement using each pill's ACTUAL
            // computed width + 8px gap. v2.0 bug: pills used a fixed 110px
            // slot advance, but "Пользователи" is ~123px wide so it bled
            // into the next slot and the whole strip looked like a stack of
            // overlapping buttons.
            // v2.2.1: pillGap 18 → 15 — gap between Книга tabs reduced by 3px.
            int fx = BuildFilterPills();

            // "Открытые файлы" action button — sits right after the group
            // pills. Hidden until the "Серверы" tab is selected.
            _openFilesBtn = MakeOpenFilesButton("Открытые файлы", fx, 10);
            _openFilesBtn.Click += (s, e) => OnOpenFilesClicked();
            _openFilesBtn.Visible = false;
            _tabsPanel.Controls.Add(_openFilesBtn);

            // "Сообщение всем" action button — same style, left edge aligned
            // to the "Открытые файлы" button (the two are never visible at
            // the same time, so they share the same slot). Hidden until the
            // "Пользователи" tab is selected.
            _messageAllBtn = MakeOpenFilesButton("Сообщение всем", fx, 10);
            _messageAllBtn.Click += (s, e) => OnMessageAllClicked();
            _messageAllBtn.Visible = false;
            _tabsPanel.Controls.Add(_messageAllBtn);

            // "Инвентаризация ПО" action button — same style. Visible on
            // the "Все" tab. Opens the domain-wide software inventory scanner.
            _inventoryBtn = MakeOpenFilesButton("Инвентаризация ПО", fx, 10);
            _inventoryBtn.Click += (s, e) => OnInventoryClicked();
            _inventoryBtn.Visible = true;  // "Все" is the default active filter
            _tabsPanel.Controls.Add(_inventoryBtn);

            // v3.9.0 — group-management context menu is built on demand in
            // ShowGroupMenu() as a themed popup (see below).

            // === Grid ===
            // Sits directly below the tabs panel. Y = HeaderHeight + TabsHeight.
            // v2.1.69: was DoubleBufferedDataGridView subclass — persisted
            // rendering artifacts (faint #C0C0C0 column lines + #BDBCBB row
            // lines) that previously cleared on scroll. Plain DataGridView +
            // reflection-set DoubleBuffered avoids the subclass code path
            // that triggered the persistent-buffer issue.
            _grid = new DataGridView
            {
                Location = new Point(3, Helpers.UiTheme.HeaderHeight + Helpers.UiTheme.TabsHeight + 5),
                Size = new Size(1296, 524),
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                ReadOnly = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = true,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                // v2.1.77: revert to v2.1.62 working state. Was experimenting
                // with None + GridColor match to make borders invisible, but
                // every variant (None, AdvancedCellBorderStyle.All=None,
                // DividerWidth=0, manual paint) either didn't kill the borders
                // or broke other things. The v2.1.62 setup with SingleHorizontal
                // + BorderSubtle worked (borders were transient, cleared on
                // scroll). Restoring that.
                BackgroundColor = Helpers.UiTheme.RowBase,
                BorderStyle = BorderStyle.None,
                ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
                GridColor = Helpers.UiTheme.BorderSubtle,
                // Row height: 26 base + 10 top + 10 bottom. Was 26+30 in v2.0
                // but user feedback said rows were too tall. v2.01: -10 (5 each).
                RowTemplate = { Height = 26 + 20 },
                DefaultCellStyle = {
                    BackColor = Helpers.UiTheme.RowBase,  // v2.1.77: restore original
                    ForeColor = Helpers.UiTheme.Foreground,
                    SelectionBackColor = Helpers.UiTheme.AccentSoft,
                    SelectionForeColor = Helpers.UiTheme.GridSelectionFore
                },
                // v2.0 header band — Figma's muted beige, uppercase-ish
                // semibold caption in muted-foreground.
                EnableHeadersVisualStyles = false,
                ColumnHeadersDefaultCellStyle = {
                    BackColor = Helpers.UiTheme.GridHeaderBg,
                    ForeColor = Helpers.UiTheme.GridHeaderFg,
                    Font = new Font("Segoe UI Semibold", 9F),
                    Alignment = DataGridViewContentAlignment.MiddleLeft
                },
                ColumnHeadersHeight = 36,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing
            };
            // v2.1.77: DoubleBuffered (reflection-set or subclass) was the
            // reason the previously-transient #BDBCBB borders became
            // PERSISTENT — the offscreen buffer freezes them in place.
            // Removed; grid renders directly to screen, borders clear on
            // scroll/repaint like in v2.1.62.
            // v2.1.65: CellBorderStyle=None alone wasn't killing the faint
            // horizontal dividers — DataGridView has AdvancedCellBorderStyle
            // as a separate per-edge layer that's NOT controlled by
            // CellBorderStyle. Setting all 5 edges to None here nukes them.
            _grid.AdvancedCellBorderStyle.All = DataGridViewAdvancedCellBorderStyle.None;
            _grid.AdvancedCellBorderStyle.Top = DataGridViewAdvancedCellBorderStyle.None;
            _grid.AdvancedCellBorderStyle.Bottom = DataGridViewAdvancedCellBorderStyle.None;
            _grid.AdvancedCellBorderStyle.Left = DataGridViewAdvancedCellBorderStyle.None;
            _grid.AdvancedCellBorderStyle.Right = DataGridViewAdvancedCellBorderStyle.None;
            // Also kill the column-header bottom border (line between header
            // row and first body row).
            _grid.AdvancedColumnHeadersBorderStyle.All = DataGridViewAdvancedCellBorderStyle.None;
            _grid.AdvancedColumnHeadersBorderStyle.Bottom = DataGridViewAdvancedCellBorderStyle.None;

            // Ping status column (image). Hidden for now — populated when the
            // async refresh module is added. Kept so the column order matches
            // the .ps1 version.
            var colPing = new DataGridViewImageColumn
            {
                Name = "Ping",
                HeaderText = "В сети",
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
                FillWeight = 25,
                Width = 50,
                ImageLayout = DataGridViewImageCellLayout.Normal
            };
            _grid.Columns.Add(colPing);

            // RDP indicator column — blue dot when an RDP Manager session is
            // currently live for this computer. Reactive: OnCellPainting
            // queries RdpManagerForm.HasSessionFor every paint, and we
            // subscribe to SessionListChanged to invalidate when sessions
            // are added/closed.
            var colRdp = new DataGridViewImageColumn
            {
                Name = "Rdp",
                HeaderText = "RDP",
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
                FillWeight = 20,
                Width = 40,
                ImageLayout = DataGridViewImageCellLayout.Normal
            };
            _grid.Columns.Add(colRdp);

            // Group column — rendered as a colored badge via CellPainting.
            // The cell value is the raw group key ("Users"/"Servers"/…/"" );
            // the painter turns it into a colored pill on the fly so we don't
            // need to pre-render bitmaps per group.
            //
            // AutoSizeMode = None + Width persisted: user-resize is honoured
            // and survives close/reopen (see SaveColumnWidths / LoadColumnWidths).
            // Without None, AutoSizeColumnsMode.Fill on the grid would resize
            // this column to its FillWeight share on every layout pass,
            // discarding the user's manual width.
            var colGroup = new DataGridViewTextBoxColumn
            {
                Name = "Group",
                HeaderText = "Группа",
                FillWeight = 40,
                MinimumWidth = 70,
                Width = 110,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
                ReadOnly = true,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Alignment = DataGridViewContentAlignment.MiddleCenter,
                    SelectionBackColor = Helpers.UiTheme.AccentSoft,
                    SelectionForeColor = Helpers.UiTheme.GridSelectionFore
                }
            };
            _grid.Columns.Add(colGroup);

            AddTextColumn(_grid, "Computer", "Компьютер", 90);
            AddTextColumn(_grid, "FullName", "Имя (ФИО)", 180);
            AddTextColumn(_grid, "User", "Логин", 80);
            AddTextColumn(_grid, "Phone", "Телефон", 70);
            AddTextColumn(_grid, "IPs", "IP", 100);
            AddTextColumn(_grid, "LastSeen", "Последний сеанс", 60);
            AddTextColumn(_grid, "NewEvents", "События", 75);

            // 15px left padding on data columns so cell text isn't crammed
            // against the column separator. (Ping and Group columns have
            // their own centered alignment, no padding needed.)
            foreach (DataGridViewColumn c in _grid.Columns)
            {
                if (c.Name == "Ping" || c.Name == "Group" || c.Name == "Rdp") continue;
                c.DefaultCellStyle.Padding = new Padding(15, 0, 0, 0);
            }
            // v2.1.67: nuke column + row dividers — these are separate from
            // CellBorderStyle and were the source of the #B8B4B1 / #BDBCBB
            // lines the user kept seeing between rows AND between columns.
            // DividerWidth/Height defaults to 0 but DataGridView sometimes
            // renders 1px separators anyway; setting them explicitly to 0
            // here plus the row DividerHeight below makes the grid truly
            // borderless.
            foreach (DataGridViewColumn c in _grid.Columns)
            {
                c.DividerWidth = 0;
            }
            _grid.RowTemplate.DividerHeight = 0;

            // Per-column ReadOnly: only FullName + Phone are user-editable.
            _grid.Columns["Ping"].ReadOnly = true;
            _grid.Columns["Group"].ReadOnly = true;
            _grid.Columns["Computer"].ReadOnly = true;
            _grid.Columns["FullName"].ReadOnly = false;
            _grid.Columns["User"].ReadOnly = true;
            _grid.Columns["Phone"].ReadOnly = false;
            _grid.Columns["IPs"].ReadOnly = true;
            _grid.Columns["LastSeen"].ReadOnly = true;

            // Sort glyphs + click-to-sort. SortMode must be Programmatic
            // (we own the sort because we re-fetch + re-render from JSON).
            foreach (DataGridViewColumn c in _grid.Columns)
            {
                if (c.Name == "Ping") continue;
                c.SortMode = DataGridViewColumnSortMode.Programmatic;
            }
            _grid.ColumnHeaderMouseClick += OnColumnHeaderMouseClick;
            // v3.7.x: drag headers to reorder; order persisted per form.
            // Explicit key — the grid is embedded in RdpManagerForm, so
            // FindForm() alone would report the host form.
            Helpers.GridColumnOrder.Wire(_grid, "AddressBookForm");

            // v3.9 — trailing filler column (Fill) absorbs the leftover width so
            // the real columns keep their saved pixel widths instead of being
            // stretched by AutoSizeColumnsMode.Fill. Always stays last, not
            // user-resizable, not sortable, width not persisted.
            _fillerCol = new DataGridViewTextBoxColumn
            {
                Name = "_filler",
                HeaderText = "",
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                FillWeight = 1,
                ReadOnly = true,
                Resizable = DataGridViewTriState.False,
                SortMode = DataGridViewColumnSortMode.NotSortable
            };
            _grid.Columns.Add(_fillerCol);

            // v3.9 — persist column widths live as the user drags dividers.
            _grid.ColumnWidthChanged += OnGridColumnWidthChanged;

            // v2.1.77: restore v2.1.62 working setup — single OnCellPainting
            // handler with e.PaintBackground(CellBounds, false). RowPostPaint
            // approach triggered the bordered-paint path too.
            _grid.CellPainting += OnCellPainting;
            // v4.0 — auto-refresh "События" counts when new dashboard events arrive
            try { Services.EventPollerService.OnNewEvents += _ => { try { if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(() => { if (!IsDisposed) ReloadGrid(); })); } catch { } }; } catch { }

            // Commit the edit on Enter / click-away so CellValueChanged fires
            // immediately. Without this the user has to navigate away from
            // the cell to commit, which is confusing.
            _grid.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;
            _grid.CellValueChanged += OnCellValueChanged;

            // v3.9.0 — right-click menu is shown on demand in the CellMouseDown
            // handler below (themed ThemedContextMenu popup) instead of a fixed
            // ContextMenuStrip, so it stays in sync with the row under the cursor.

            // Show the context menu for the row under the mouse WITHOUT selecting/
            // activating that row (the user's current selection is left untouched).
            // All callbacks below target the hovered row explicitly via closures.
            _grid.CellMouseDown += (sender, args) =>
            {
                if (args.Button != MouseButtons.Right) return;
                if (args.RowIndex < 0) return;
                var hovered = _grid.Rows[args.RowIndex];
                if (hovered.IsNewRow) return;
                var hoveredComputer = hovered.Cells["Computer"].Value?.ToString() ?? "";
                var hoveredUser = hovered.Cells["User"].Value?.ToString() ?? "";
                var hoveredGroup = hovered.Cells["Group"].Value?.ToString() ?? "";
                var menu = Helpers.ComputerContextMenu.BuildThemed(
                    owner: this,
                    computerResolver: () => hoveredComputer,
                    userResolver: () => hoveredUser,
                    onEdit: () => OnEditRow(hovered),
                    onDelete: () => DeleteRows(new System.Collections.Generic.List<DataGridViewRow> { hovered }),
                    currentGroupResolver: () => hoveredGroup,
                    onAssignGroup: g => AssignGroupRows(new System.Collections.Generic.List<DataGridViewRow> { hovered }, g),
                    onLaunchWeb: null,   // v2.2.19: auto-add cancelled — separate menu item instead
                    onLaunchSsh: null,
                    onLaunchRdp: null,
                    onAddToSessions: c => Forms.RdpManagerForm.Instance.AddSessionFromBook(c));
                menu.Show(this, Control.MousePosition);
            };

            // Double-click on a row → "Редактировать..." (EditEntryForm).
            // Lets the user edit any field of the entry in a popup. Shadow
            // is still available via the context menu (right-click → Shadow).
            _grid.CellDoubleClick += (sender, args) =>
            {
                if (args.RowIndex < 0) return;
                // Force-select the row so OnEditClicked reads the right one
                // (CellDoubleClick doesn't change selection by itself).
                _grid.ClearSelection();
                _grid.Rows[args.RowIndex].Selected = true;
                OnEditClicked();
            };

            Controls.Add(_grid);

            // v2.1.52: when user clicks ANYWHERE outside the search field
            // and it's empty, blur the field so the bg reverts to Muted.
            // WinForms only transfers focus when you click a *focusable*
            // control — Panels and Labels absorb the click without taking
            // focus, so _searchBox.Leave never fires and the field stays
            // white. Recursive MouseDown hook on every non-search control
            // forces focus over to the grid, which kicks off Leave.
            MouseEventHandler blurIfEmpty = (s, e) =>
            {
                if (string.IsNullOrEmpty(_searchBox.Text) && _searchBox.Focused)
                {
                    _grid.Focus();
                }
            };
            void HookBlur(Control c)
            {
                if (ReferenceEquals(c, _searchWrapper) || ReferenceEquals(c, _searchBox) || ReferenceEquals(c, _searchPlaceholder) || ReferenceEquals(c, _searchClearBtn)) return;
                c.MouseDown += blurIfEmpty;
                foreach (Control child in c.Controls) HookBlur(child);
            }
            HookBlur(this);

            // RDP indicator column is reactive — when sessions are added or
            // closed in RdpManagerForm, invalidate the column so the blue
            // dot updates. Subscription is removed on FormClosed so we don't
            // leak handlers across multiple opens of the address book.
            Forms.RdpManagerForm.SessionListChanged += OnRdpSessionListChanged;
            FormClosed += (s, e) =>
            {
                Forms.RdpManagerForm.SessionListChanged -= OnRdpSessionListChanged;
                _searchDebounce?.Dispose();
            };

            // === Status label ===
            _statusLabel = new Label
            {
                Location = new Point(10, 530),
                Size = new Size(1000, 20),
                ForeColor = Color.Gray,
                Text = ""
            };
            Controls.Add(_statusLabel);

            // === Bottom buttons ===
            // v2.0.6: bottom buttons are flat Labels in pill style.
            //   Удалить     → red (danger)
            //   Обновить всё→ indigo bg + indigo text (primary action)
            //   others      → muted grey
            // Bold font everywhere. Click handlers attached via Label.Click.
            //
            // v2.1.48 (revised v2.1.50): footer bg painted directly on the
            // form via OnPaintBackground instead of via a sibling Panel —
            // the sibling approach hid buttons behind itself because of
            // z-order. Buttons keep their transparent BackColor and
            // inherit the footer colour through the form's bg.
            const int FooterH = 72;  // was 90 — −10% top + −10% bottom
            Paint += (s, e) =>
            {
                if (IsDisposed) return;
                var rect = new Rectangle(0, ClientSize.Height - FooterH, ClientSize.Width, FooterH);
                using var b = new SolidBrush(Color.FromArgb(0xFA, 0xF7, 0xF4));
                e.Graphics.FillRectangle(b, rect);
            };

            _deleteBtn = MakeActionButton("Удалить", ActionRole.Default);
            _deleteBtn.Click += (s, e) => OnDeleteClicked();
            Controls.Add(_deleteBtn);

            _fullNameBtn = MakeActionButton("Обновить ФИО", ActionRole.Default, Helpers.UiTheme.ButtonPrimary);
            _fullNameBtn.Click += async (s, e) => await OnUpdateFullNameClickedAsync();
            Controls.Add(_fullNameBtn);

            _importAdBtn = MakeActionButton("Импорт из AD", ActionRole.Default, Helpers.UiTheme.ButtonPrimary);
            _importAdBtn.Click += async (s, e) => await OnImportFromAdClickedAsync();
            Controls.Add(_importAdBtn);

            _cancelBtn = MakeActionButton("Прервать", ActionRole.Default);
            // v2.0.8: don't disable the Label — WinForms auto-dims disabled
            // Labels to GrayText (overriding ForeColor) AND blocks MouseEnter
            // so hover wouldn't fire. User feedback was that Прервать looked
            // washed-out vs neighbours and didn't hover. Now it always looks
            // the same; OnCancelClicked silently no-ops when there's nothing
            // to cancel.
            _cancelBtn.Click += (s, e) => OnCancelClicked();
            Controls.Add(_cancelBtn);

            _refreshAllBtn = MakeActionButton("Обновить всё", ActionRole.Default, Helpers.UiTheme.ButtonPrimary);
            _refreshAllBtn.Click += async (s, e) => await OnRefreshAllClickedAsync();
            Controls.Add(_refreshAllBtn);

            _closeBtn = MakeActionButton("Закрыть", ActionRole.Default);
            _closeBtn.Click += (s, e) => Close();
            Controls.Add(_closeBtn);

            // CancelButton normally takes an IButtonControl (Button implements
            // it, Label doesn't). v2.0.6 made Закрыть a Label, so we can't use
            // the form's CancelButton — ESC won't close the dialog. User can
            // still close via X or the Закрыть label.

            // Restore last-used window size — saved on close via FormClosed.
            WindowStateStore.Register(this, "addressBook");

            // Initial population.
            ReloadGrid();
            UpdateStatus();

            // Layout sync — same pattern as MainForm. Hook Shown + SizeChanged
            // to recompute control positions from current ClientSize.
            Shown += (s, e) =>
            {
                SyncLayout();
                // v2.1.71: force a repaint of the grid after first show.
                // Without this the grid renders in a "stale" pre-focus state
                // with faint #BDBCBB borders between every row + column;
                // clicking any cell clears them (because click → repaint
                // with focused state). Calling Focus + Invalidate here
                // replicates the click-induced repaint at first show, so
                // the borders never appear.
                try
                {
                    _grid.Focus();
                    _grid.Invalidate();
                    // Also re-assert CurrentCell after a tiny delay to catch
                    // the post-Shown layout pass that some DataGridView
                    // versions do asynchronously.
                    var t = new System.Windows.Forms.Timer { Interval = 50 };
                    t.Tick += (_, _) =>
                    {
                        t.Stop(); t.Dispose();
                        try { _grid.Invalidate(); } catch { }
                    };
                    t.Start();
                }
                catch { }
                // Auto-ping on open if at least one entry has stale (or no)
                // cache. Mirrors the .ps1 behaviour: the user opens the book
                // and within ~10-20 seconds sees the current online state,
                // without having to click "Обновить всё".
                _ = AutoPingStaleAsync();
            };
            SizeChanged += (s, e) => SyncLayout();

            // Cancel any in-flight async work when the form closes — without
            // this, the fire-and-forget AutoPingStaleAsync keeps running
            // against a disposed grid and crashes on Rows.Add with
            // "DataGridView has no columns" (grid is disposed, columns gone).
            FormClosing += (s, e) =>
            {
                try { _cts?.Cancel(); } catch { }
                try { SaveColumnWidths(); } catch { /* best-effort */ }
            };

            // Restore previously-saved column widths (currently only Group).
            LoadColumnWidths();
        }

        // === Per-form column width persistence ===
        //
        // Stored in column_widths.json next to windows.json. Schema:
        //   { "addressBook": { "Group": 110, ... } }
        //
        // Per-form key prefix lets us reuse the file for other grids later
        // (TaskManager, LocalAdmins, etc.) without collisions.

        private const string ColumnWidthsFormKey = "addressBook";

        private void LoadColumnWidths()
        {
            try
            {
                var path = AppPaths.ColumnWidthsPath;
                if (!File.Exists(path)) return;
                var json = File.ReadAllText(path);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty(ColumnWidthsFormKey, out var formEntry)) return;
                foreach (var prop in formEntry.EnumerateObject())
                {
                    if (prop.Name == "_filler") continue;
                    if (!_grid.Columns.Contains(prop.Name)) continue;
                    int w = prop.Value.GetInt32();
                    if (w < 50) continue;  // sanity floor — anything tiny is bogus
                    _suppressWidthSave = true;
                    try { _grid.Columns[prop.Name]!.Width = w; }
                    finally { _suppressWidthSave = false; }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"LoadColumnWidths failed: {ex.Message}");
            }
        }

        private void SaveColumnWidths()
        {
            try
            {
                Dictionary<string, Dictionary<string, int>> data;
                var path = AppPaths.ColumnWidthsPath;
                if (File.Exists(path))
                {
                    var existing = File.ReadAllText(path);
                    data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(existing)
                        ?? new Dictionary<string, Dictionary<string, int>>();
                }
                else
                {
                    data = new Dictionary<string, Dictionary<string, int>>();
                }
                // v2.1.21: save ALL column widths (was only AutoSizeMode=None).
                var perForm = new Dictionary<string, int>();
                foreach (DataGridViewColumn c in _grid.Columns)
                {
                    if (c.Name == "_filler") continue;
                    perForm[c.Name] = c.Width;
                }
                data[ColumnWidthsFormKey] = perForm;

                var opts = new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(data, opts));
            }
            catch (Exception ex)
            {
                Logger.Warn($"SaveColumnWidths failed: {ex.Message}");
            }
        }

        private void OnGridColumnWidthChanged(object? sender, DataGridViewColumnEventArgs e)
        {
            if (_suppressWidthSave) return;
            if (e.Column == _fillerCol) return;
            SaveColumnWidths();
        }

        // === Layout sync ===
        //
        // Same reason as MainForm: WinForms anchor math doesn't always fire
        // when Windows restores a saved window placement after the dialog is
        // already shown. We compute positions explicitly from ClientSize.
        private void SyncLayout()
        {
            var w = ClientSize.Width;
            var h = ClientSize.Height;
            if (w <= 0 || h <= 0) return;

            // v2.0 layout (Figma redesign):
            //   Header   Y=0..56    (logo + title + count + search)
            //   Tabs     Y=56..100  (pill filters)
            //   Grid     Y=100..H-56
            //   Footer   Y=H-56..H  (3 left buttons, counter, 2 right buttons)
            int headerH = Helpers.UiTheme.HeaderHeight;
            int tabsH = Helpers.UiTheme.TabsHeight;
            // v2.1.50 — footer height 72 (was 90): −10% top + −10% bottom.
            int footerH = 72;
            int gridTop = headerH + tabsH;  // v3.8.2: flush under the tabs, no gap
            int gridBottom = h - footerH;
            int gridH = gridBottom - gridTop;

            // Header & tabs panels are anchored Top|Left|Right so their WIDTH
            // auto-tracks the form. Their Height is fixed. Re-asserting here
            // is defensive — anchor math doesn't fire reliably when Windows
            // restores a saved window placement after Show().
            // Search wrapper (with rounded corners) — right-aligned with
            // 24px padding. v2.0.6: 640×32, was 640×40 in v2.0.5.
            const int searchW = 640;
            const int searchH = 32;
            _searchWrapper.Location = new Point(w - searchW - 24, (headerH - searchH) / 2);
            _searchWrapper.Size = new Size(searchW, searchH);
            using (var path = RoundedRect(new Rectangle(0, 0, searchW, searchH), 6))
            {
                _searchWrapper.Region = new System.Drawing.Region(path);
            }

            // Filter pills keep their construction-time X positions inside
            // the tabs panel (they're left-aligned). Only width/height need
            // reassertion in case the form was minimised and restored.
            const int pillH = 26;
            const int pillY = (44 - pillH) / 2;  // tabsH=44, vertically centered
            foreach (var kv in _filterButtons)
            {
                int pw = kv.Value.Tag is int w2 ? w2 : 70;
                kv.Value.Size = new Size(pw, pillH);
                kv.Value.Location = new Point(kv.Value.Location.X, pillY);
            }
            // "Открытые файлы" action button — same vertical centring.
            _openFilesBtn.Size = new Size(
                _openFilesBtn.Tag is int ow ? ow : MeasureButtonTextWidth("Открытые файлы", 28), pillH);
            _openFilesBtn.Location = new Point(_openFilesBtn.Location.X, pillY);
            // "Сообщение всем" action button — same vertical centring.
            _messageAllBtn.Size = new Size(
                _messageAllBtn.Tag is int mw ? mw : MeasureButtonTextWidth("Сообщение всем", 28), pillH);
            _messageAllBtn.Location = new Point(_messageAllBtn.Location.X, pillY);
            // "Инвентаризация ПО" action button — same vertical centring.
            _inventoryBtn.Size = new Size(
                _inventoryBtn.Tag is int iw ? iw : MeasureButtonTextWidth("Инвентаризация ПО", 28), pillH);
            _inventoryBtn.Location = new Point(_inventoryBtn.Location.X, pillY);

            // Grid fills the middle band, edge-to-edge (flush left/right/top).
            _grid.Location = new Point(0, gridTop);
            _grid.Size = new Size(w, gridH);

            // Footer: 56px tall. v2.0.7 — buttons are AutoSize Labels now,
            // so we walk the cluster sequentially using each label's ACTUAL
            // Width and a uniform 16px gap. No more hardcoded widths.
            int footerTop = h - footerH;
            int absBtnY = footerTop + (footerH - 22) / 2;  // 22=action-button height (v2.3.7)
            const int clusterGap = 16;

            // Left cluster: Удалить · Обновить ФИО · Импорт из AD · Прервать
            int lx = 24;
            _deleteBtn.Location = new Point(lx, absBtnY);
            lx += _deleteBtn.Width + clusterGap;
            _fullNameBtn.Location = new Point(lx, absBtnY);
            lx += _fullNameBtn.Width + clusterGap;
            _importAdBtn.Location = new Point(lx, absBtnY);
            lx += _importAdBtn.Width + clusterGap;
            _cancelBtn.Location = new Point(lx, absBtnY);
            int leftClusterRight = lx + _cancelBtn.Width;

            // Right cluster: Закрыть · Обновить всё (rightmost first, walk left)
            int rx = w - 24;
            _closeBtn.Location = new Point(rx - _closeBtn.Width, absBtnY);
            rx -= _closeBtn.Width + clusterGap;
            _refreshAllBtn.Location = new Point(rx - _refreshAllBtn.Width, absBtnY);
            int rightClusterLeft = rx - _refreshAllBtn.Width;

            // Centre counter — between the two clusters.
            int centreX = (leftClusterRight + rightClusterLeft) / 2;
            _statusLabel.Location = new Point(centreX - 100, absBtnY + 6);
            _statusLabel.Size = new Size(200, 20);
            _statusLabel.TextAlign = ContentAlignment.MiddleCenter;
            _statusLabel.Font = new Font("Consolas", 9F);
            _statusLabel.ForeColor = Helpers.UiTheme.MutedForeground;
            _statusLabel.BackColor = Helpers.UiTheme.Muted;
        }

        // === Load + render ===
        private void ReloadGrid()
        {
            // Bail if the form/grid has been disposed (AutoPingStaleAsync is
            // fire-and-forget and can outlive the form). NOTE: don't check
            // IsHandleCreated here — the form's handle isn't created until
            // Show/ShowDialog, but we call ReloadGrid from the constructor
            // where the handle is still null. Grid columns are populated in
            // the constructor before this is called.
            if (IsDisposed || _grid.IsDisposed || _grid.Columns.Count == 0) return;
            var entries = AddressBookStore.Load().ToList();
            _monitoredSet = new HashSet<string>(entries.Where(x => x.Monitored && !string.IsNullOrWhiteSpace(x.Computer)).Select(x => x.Computer.Trim()), StringComparer.OrdinalIgnoreCase);

            // Apply current sort (column-header click). Stable enough for our
            // purposes — entries with equal keys keep their JSON order.
            if (!string.IsNullOrEmpty(_sortColumn))
            {
                var sorted = _sortAscending
                    ? entries.OrderBy(e => SortKeyForColumn(e, _sortColumn))
                    : entries.OrderByDescending(e => SortKeyForColumn(e, _sortColumn));
                entries = sorted.ToList();
                // Reflect the sort direction in the column header glyph.
                foreach (DataGridViewColumn c in _grid.Columns)
                {
                    c.HeaderCell.SortGlyphDirection = c.Name == _sortColumn
                        ? (_sortAscending ? SortOrder.Ascending : SortOrder.Descending)
                        : SortOrder.None;
                }
            }

            _grid.Rows.Clear();
            var unknownIcon = Helpers.PingStatusIcon.Get(Helpers.PingStatusIcon.State.Unknown);
            var aliveIcon  = Helpers.PingStatusIcon.Get(Helpers.PingStatusIcon.State.Alive);
            var deadIcon   = Helpers.PingStatusIcon.Get(Helpers.PingStatusIcon.State.Dead);
            int idx = 0;
            // v2.1.78: wrap the row-add loop in WM_SETREDRAW off + Suspend/
            // Resume layout. Without this, DataGridView fires a repaint
            // PER ROW added (Rows.Add triggers OnRowsAdded → paint). For
            // 80 rows that's 80 repaints on a single ReloadGrid call. With
            // the wrap, only ONE repaint happens at the end. Doesn't
            // affect border rendering (DoubleBuffered stays off).
            var gridHandle = _grid.Handle;
            SendMessage(gridHandle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
            _grid.SuspendLayout();
            // v4.0 — cache new-events counts per computer for "События" column
            var newCounts = Services.EventDashboardStore.LoadEvents().Where(ev => !ev.Notified).GroupBy(ev => ev.Computer, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var e in entries)
            {
                Image icon = unknownIcon;
                if (!string.IsNullOrEmpty(e.Computer) &&
                    PingCache.TryGet(e.Computer, out var alive, out _))
                {
                    icon = alive ? aliveIcon : deadIcon;
                }
                // Column order: Ping, Rdp, Group, Computer, FullName, User, Phone, IPs, LastSeen, NewEvents.
                int newCnt = 0;
                if (!string.IsNullOrWhiteSpace(e.Computer) && newCounts.TryGetValue(e.Computer.Trim(), out var cc)) newCnt = cc;
                string newEventsText = newCnt > 0 ? newCnt.ToString() : "";
                // Rdp cell stays null — painted on the fly by OnCellPainting
                // (blue dot when RdpManagerForm has a live session for this computer).
                var rowIdx = _grid.Rows.Add(icon, null, e.Group ?? "", e.Computer, e.FullName, e.User, e.Phone, e.IPs, e.LastSeen, newEventsText);
                var row = _grid.Rows[rowIdx];
                if (e.Monitored) row.Cells["Computer"].ToolTipText = "Отслеживается в дашборде";
                if (newCnt > 0) row.Cells["NewEvents"].ToolTipText = $"{newCnt} новых событий — откройте Дашбоард";

                // v2.1.77: restore per-cell alternating row coloring. This
                // was the working setup in v2.1.62 — AlternatingRowsDefaultCellStyle
                // broke it (different paint path).
                if (idx % 2 == 1)
                {
                    foreach (DataGridViewCell cell in row.Cells)
                    {
                        cell.Style.BackColor = Helpers.UiTheme.RowAlt;
                    }
                }
                idx++;
            }
            }
            finally
            {
                _grid.ResumeLayout();
                SendMessage(gridHandle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
                _grid.Invalidate();
            }
            Logger.Info($"AddressBookForm: rendered {_grid.Rows.Count} rows (sort={_sortColumn ?? "none"}, dir={(_sortAscending ? "asc" : "desc")})");
            // Reapply the current filter so newly-loaded rows respect it.
            ApplySearchFilter();
            UpdateStatus();
            // v2.1.72: set CurrentCell after load. Without this the grid
            // renders in a pre-click "no current cell" state that draws
            // faint #BDBCBB borders between every row + column (cosmetic
            // quirk in DataGridView's pre-focus paint path). Setting
            // CurrentCell replicates the post-click state where borders
            // disappear. ClearSelection right after keeps the visual "no
            // selection" look without re-introducing the no-current-cell
            // paint path.
            try
            {
                if (_grid.Rows.Count > 0 && _grid.Columns.Count > 0)
                {
                    _grid.CurrentCell = _grid.Rows[0].Cells[0];
                    _grid.ClearSelection();
                }
            }
            catch { /* best-effort */ }
        }

        private static string SortKeyForColumn(AddressBookEntry e, string? col) => (col ?? "") switch
        {
            "Group" => e.Group ?? "",
            "Computer" => e.Computer ?? "",
            "FullName" => e.FullName ?? "",
            "User" => e.User ?? "",
            "Phone" => e.Phone ?? "",
            "IPs" => e.IPs ?? "",
            "LastSeen" => e.LastSeen ?? "",
            _ => ""
        };

        // Updates the status line and (optionally) pill counters. visibleCount
        // lets callers pass a precomputed visible-row count to skip a second
        // pass over the grid. refreshPills=false skips the JSON re-read +
        // pill region rebuild — used by the search path, where counts don't
        // change and doing disk I/O per keystroke is wasteful.
        private void UpdateStatus(int? visibleCount = null, bool refreshPills = true)
        {
            int total = _grid.Rows.Count;
            int visible = visibleCount ?? CountVisibleRows();
            _statusLabel.Text = total == 0
                ? "0 / 0 записей"
                : visible == total
                    ? $"{total} записей"
                    : $"{visible} / {total} записей";
            if (refreshPills) UpdatePillCounts();
        }

        private int CountVisibleRows()
        {
            int visible = 0;
            foreach (DataGridViewRow r in _grid.Rows)
            {
                if (r.Visible) visible++;
            }
            return visible;
        }

        // Updates filter pill labels with record counts per group.
        // Called from UpdateStatus which runs after every ReloadGrid / delete /
        // group-assign.
        private void UpdatePillCounts()
        {
            try
            {
                var entries = AddressBookStore.Load();
                int total = entries.Count;
                var perGroup = entries
                    .GroupBy(e => (e.Group ?? "").Trim(), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
                int none = perGroup.TryGetValue("", out var n) ? n : 0;

                var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    [""] = total,
                    ["None"] = none
                };
                foreach (var info in Helpers.ComputerGroup.All)
                    counts[info.Key] = perGroup.TryGetValue(info.Key, out var c) ? c : 0;

                foreach (var kv in _filterButtons)
                {
                    string baseLabel = kv.Key switch
                    {
                        "" => "Все",
                        "None" => "Без группы",
                        _ => Helpers.ComputerGroup.ByKey(kv.Key)?.Label ?? kv.Key
                    };
                    int count = counts.TryGetValue(kv.Key, out var c) ? c : 0;
                    string newText = $"{baseLabel} ({count})";
                    kv.Value.Text = newText;
                    int newWidth = MeasureButtonTextWidth(newText, 28);
                    kv.Value.Tag = newWidth;
                    kv.Value.Size = new Size(newWidth, 26);
                    // Re-apply rounded region for the new size.
                    if (!kv.Value.IsDisposed && newWidth > 0 && kv.Value.Height > 0)
                    {
                        kv.Value.Region?.Dispose();
                        using var path = RoundedRect(new Rectangle(0, 0, newWidth, kv.Value.Height), 2);
                        kv.Value.Region = new System.Drawing.Region(path);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"UpdatePillCounts failed: {ex.Message}");
            }
        }

        private void ApplySearchFilter()
        {
            var term = (_searchBox.Text ?? "").Trim().ToLowerInvariant();
            int visible = 0;
            // v3.1.x: SuspendLayout around the visibility loop + a single
            // visible counter — was a bare loop followed by a second full
            // pass inside UpdateStatus(), and each row.Visible= change
            // triggered layout work.
            _grid.SuspendLayout();
            try
            {
                foreach (DataGridViewRow row in _grid.Rows)
                {
                    if (row.IsNewRow) continue;
                    // Text match across all visible cells.
                    bool matchedText = string.IsNullOrEmpty(term);
                    if (!matchedText)
                    {
                        foreach (DataGridViewCell cell in row.Cells)
                        {
                            // Skip the Ping image column — its Value is an Image,
                            // ToString() gives "System.Drawing.Bitmap" which would
                            // match the literal text "system" etc.
                            if (_grid.Columns[cell.ColumnIndex].Name == "Ping") continue;
                            var v = cell.Value?.ToString()?.ToLowerInvariant() ?? "";
                            if (v.Contains(term)) { matchedText = true; break; }
                        }
                    }
                    // Group filter: "" (Все) shows everything; "None" shows only
                    // entries whose Group is empty; any other key matches that group.
                    var groupKey = row.Cells["Group"].Value?.ToString() ?? "";
                    bool matchedGroup = _groupFilter switch
                    {
                        "" => true,
                        "None" => string.IsNullOrEmpty(groupKey),
                        _ => string.Equals(groupKey, _groupFilter, StringComparison.OrdinalIgnoreCase)
                    };
                    row.Visible = matchedText && matchedGroup;
                    if (row.Visible) visible++;
                }
            }
            finally
            {
                _grid.ResumeLayout();
            }
            // v3.1.x: update the status line from the count computed above.
            // Skip UpdatePillCounts here — group counts don't change when the
            // user types (pill counts read the whole JSON from disk and
            // rebuild regions, which made every keystroke do disk I/O).
            UpdateStatus(visible, refreshPills: false);
        }

        // === Inline edit persistence ===
        private void OnCellValueChanged(object? sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            var colName = _grid.Columns[e.ColumnIndex].Name;
            if (colName != "FullName" && colName != "Phone") return;

            var row = _grid.Rows[e.RowIndex];
            var computer = row.Cells["Computer"].Value?.ToString() ?? "";
            var user = row.Cells["User"].Value?.ToString() ?? "";

            // Match by Computer+User (existing entries) OR by User alone
            // (imported AD entries with empty Computer).
            var entries = AddressBookStore.Load();
            bool changed = false;
            foreach (var entry in entries)
            {
                bool userMatch = !string.IsNullOrEmpty(user) &&
                    string.Equals(entry.User, user, StringComparison.OrdinalIgnoreCase);
                bool compMatch = !string.IsNullOrEmpty(computer) &&
                    string.Equals(entry.Computer, computer, StringComparison.OrdinalIgnoreCase);
                if (string.IsNullOrEmpty(computer))
                {
                    if (!userMatch) continue;
                }
                else
                {
                    if (!(compMatch && userMatch)) continue;
                }

                if (colName == "FullName")
                {
                    var newName = row.Cells["FullName"].Value?.ToString() ?? "";
                    if (entry.FullName != newName)
                    {
                        entry.FullName = newName;
                        changed = true;
                    }
                }
                else if (colName == "Phone")
                {
                    var newPhone = row.Cells["Phone"].Value?.ToString() ?? "";
                    if (entry.Phone != newPhone)
                    {
                        entry.Phone = newPhone;
                        changed = true;
                    }
                }
            }
            if (changed)
            {
                AddressBookStore.Save(entries);
                var label = colName == "FullName" ? "ФИО" : "Телефон";
                Logger.Info($"{label} edited inline: '{computer}' / '{user}'");
                _statusLabel.Text = $"{label} сохранено.";
            }
        }

        // Pull users from AD into the address book (entries without Computer),
        // then immediately scan AD computers in parallel to find each user's
        // current PC. Mirrors the .ps1 Import-UsersFromAD + Resolve-Computers
        // flow.
        private async Task OnImportFromAdClickedAsync()
        {
            if (_cts != null)
            {
                _statusLabel.Text = "Дождитесь завершения текущей операции.";
                return;
            }

            var r = MessageBox.Show(this,
                "Импорт пользователей из Active Directory.\n\n" +
                "Будут добавлены новые записи (User + FullName), существующие не затрагиваются.\n" +
                "После импорта автоматически запустится поиск ПК для новых пользователей.",
                "Импорт из AD", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return;

            // Step 1: pull users.
            _statusLabel.Text = "Импорт пользователей из AD...";
            Application.DoEvents();
            var entries = AddressBookStore.Load();
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in entries)
            {
                if (string.IsNullOrEmpty(e.User)) continue;
                var sam = e.User.Contains('\\') ? e.User.Substring(e.User.IndexOf('\\') + 1) : e.User;
                if (!string.IsNullOrEmpty(sam)) existing.Add(sam);
            }

            List<AdService.AdUser> users;
            try
            {
                users = await Task.Run(() => AdService.GetUsers(enabledOnly: true));
            }
            catch (Exception ex)
            {
                _statusLabel.Text = $"Ошибка AD: {ex.Message}";
                return;
            }

            int added = 0, skipped = 0;
            foreach (var u in users)
            {
                if (existing.Contains(u.SamAccountName)) { skipped++; continue; }
                entries.Add(new AddressBookEntry
                {
                    Computer = "",
                    User = u.SamAccountName,
                    FullName = u.DisplayName,
                    Phone = "",
                    IPs = "",
                    LastSeen = ""
                });
                existing.Add(u.SamAccountName);
                added++;
            }
            if (added > 0)
            {
                AddressBookStore.Save(entries);
                Logger.Info($"ImportFromAD: +{added} new, {skipped} skipped");
            }

            ReloadGrid();
            ApplySearchFilter();
            _statusLabel.Text = $"Импорт: +{added} новых, пропущено {skipped} (уже в книге). Всего: {users.Count}";
            Application.DoEvents();

            if (added == 0)
            {
                MessageBox.Show(this,
                    $"Новых пользователей нет.\nПропущено: {skipped}\nВсего в AD: {users.Count}",
                    "Импорт из AD", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Step 2: resolve PCs for the new entries by scanning all AD
            // computers in parallel and matching logged-on users.
            await OnResolveImportedUsersAsync();
        }

        // Scan all AD computers, query logged-on user via WMI, patch entries
        // that have empty Computer when their User matches.
        private async Task OnResolveImportedUsersAsync()
        {
            if (_cts != null)
            {
                _statusLabel.Text = "Дождитесь завершения текущей операции.";
                return;
            }

            List<string> computers;
            try
            {
                _statusLabel.Text = "Получение списка компьютеров из AD...";
                Application.DoEvents();
                computers = await Task.Run(() => AdService.GetComputers());
            }
            catch (Exception ex)
            {
                _statusLabel.Text = $"Ошибка AD: {ex.Message}";
                return;
            }
            if (computers.Count == 0)
            {
                _statusLabel.Text = "В AD не найдено компьютеров.";
                return;
            }

            BeginOperation("Поиск ПК");
            string kind = "Поиск ПК";
            int total = computers.Count, done = 0;
            int patched = 0;
            var entries = AddressBookStore.Load();
            var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            foreach (var e in entries) if (string.IsNullOrEmpty(e.LastSeen)) e.LastSeen = now;

            try
            {
                await ParallelForEachAsync(computers, maxConcurrency: 15, _cts!.Token, async (computer, ct) =>
                {
                    var alive = PingService.IsAlive(computer, 800);
                    PingCache.Set(computer, alive);
                    if (!alive) { Interlocked.Increment(ref done); return; }

                    var cs = await WmiService.GetComputerSystemAsync(computer, ct);
                    var userName = cs?.UserName ?? "";
                    if (string.IsNullOrEmpty(userName))
                    {
                        Interlocked.Increment(ref done);
                        UpdateStatusFromBackground($"{kind}: {done} / {total}");
                        return;
                    }
                    var sam = userName.Contains('\\') ? userName.Substring(userName.IndexOf('\\') + 1) : userName;

                    bool didPatch = false;
                    lock (entries)
                    {
                        foreach (var e in entries)
                        {
                            if (!string.IsNullOrEmpty(e.Computer)) continue;
                            var entrySam = e.User.Contains('\\') ? e.User.Substring(e.User.IndexOf('\\') + 1) : e.User;
                            if (!string.IsNullOrEmpty(entrySam) &&
                                string.Equals(entrySam, sam, StringComparison.OrdinalIgnoreCase))
                            {
                                e.Computer = computer;
                                if (string.IsNullOrEmpty(e.LastSeen)) e.LastSeen = now;
                                didPatch = true;
                            }
                        }
                    }
                    if (didPatch) Interlocked.Increment(ref patched);

                    Interlocked.Increment(ref done);
                    UpdateStatusFromBackground($"{kind}: {done} / {total} — {computer}");
                });

                AddressBookStore.Save(entries);
                ReloadGrid();
                ApplySearchFilter();
                Logger.Info($"ResolveImportedUsers: scanned={done}/{total}, patched={patched}");
            }
            catch (OperationCanceledException)
            {
                AddressBookStore.Save(entries);
                ReloadGrid();
            }
            catch (Exception ex)
            {
                Logger.Error($"ResolveImportedUsers failed: {ex.Message}");
            }
            finally
            {
                EndOperation(kind, done, total, _cts?.IsCancellationRequested ?? false);
                _statusLabel.Text = $"Поиск ПК: проверено {done}/{total}, найдено ПК для {patched} пользователей.";
            }
        }

        // === Async operations: Update FullName, Refresh All ===
        //
        // Both use the same parallel pattern (Task.WhenAll with bounded
        // concurrency via SemaphoreSlim), the same cancellation token, and
        // the same UI-state toggling. They replace ~250 lines of RunspacePool
        // + ConcurrentQueue + Timer plumbing from the .ps1 with about 60
        // lines of async/await.

        private void BeginOperation(string kind)
        {
            _cts = new CancellationTokenSource();
            _deleteBtn.Enabled = false;
            _fullNameBtn.Enabled = false;
            _refreshAllBtn.Enabled = false;
            // v2.0.8: don't toggle _cancelBtn.Enabled — Label auto-dims when
            // disabled and breaks hover. Always-enabled look; OnCancelClicked
            // no-ops when _cts is null.
            _statusLabel.Text = $"{kind}: запуск...";
        }

        private void EndOperation(string kind, int done, int total, bool cancelled)
        {
            _cts?.Dispose();
            _cts = null;
            _deleteBtn.Enabled = true;
            _fullNameBtn.Enabled = true;
            _refreshAllBtn.Enabled = true;
            _statusLabel.Text = cancelled
                ? $"{kind}: прервано ({done} / {total})."
                : $"{kind}: готово ({done} / {total}).";
        }

        private void OnCancelClicked()
        {
            // No-op when nothing's running — the label stays visually
            // identical regardless (see v2.0.8 note on _cancelBtn), so the
            // user can click it freely; we only act if there's actually a
            // cancellation token to cancel.
            if (_cts == null) return;
            try { _cts.Cancel(); } catch { }
            _statusLabel.Text = "Прерываю...";
        }

        // For each entry: look up FullName in AD by sAMAccountName (the User
        // field, stripped of "DOMAIN\" prefix). Skips entries that already
        // have FullName set (we don't overwrite user edits).
        private async Task OnUpdateFullNameClickedAsync()
        {
            if (_cts != null)
            {
                _statusLabel.Text = "Дождитесь завершения текущей операции.";
                return;
            }
            var entries = AddressBookStore.Load();
            if (entries.Count == 0)
            {
                MessageBox.Show("Книга пуста.", "Обновить ФИО",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            BeginOperation("ФИО");
            var kind = "ФИО";
            int total = entries.Count, done = 0;
            try
            {
                await ParallelForEachAsync(entries, maxConcurrency: 15, _cts!.Token, async (entry, ct) =>
                {
                    if (string.IsNullOrEmpty(entry.User)) { Interlocked.Increment(ref done); return; }
                    var sam = (entry.User.Contains('\\') ? entry.User.Substring(entry.User.IndexOf('\\') + 1) : entry.User);
                    var displayName = await Task.Run(() => AdService.GetDisplayName(sam), ct);
                    if (!string.IsNullOrEmpty(displayName) && entry.FullName != displayName)
                    {
                        entry.FullName = displayName;
                    }
                    Interlocked.Increment(ref done);
                    UpdateStatusFromBackground($"{kind}: {done} / {total}");
                });
                AddressBookStore.Save(entries);
                ReloadGrid();
                Logger.Info($"UpdateFullName: done ({done}/{total})");
            }
            catch (OperationCanceledException)
            {
                AddressBookStore.Save(entries);
                ReloadGrid();
            }
            catch (Exception ex)
            {
                Logger.Error($"UpdateFullName failed: {ex.Message}");
                MessageBox.Show($"Ошибка: {ex.Message}", "Обновить ФИО",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                EndOperation(kind, done, total, _cts?.IsCancellationRequested ?? false);
            }
        }

        // For each unique Computer: ping → if alive, query WMI for IPs +
        // logged-on User, then look up FullName in AD. Updates all matching
        // entries (per-user rows).
        private async Task OnRefreshAllClickedAsync()
        {
            if (_cts != null)
            {
                _statusLabel.Text = "Дождитесь завершения текущей операции.";
                return;
            }
            var entries = AddressBookStore.Load();
            if (entries.Count == 0)
            {
                MessageBox.Show("Книга пуста.", "Обновить всё",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Build unique computer list (multiple entries may share a Computer).
            var computers = entries
                .Select(e => e.Computer)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (computers.Count == 0)
            {
                MessageBox.Show("Нет компьютеров для обновления.", "Обновить всё",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            BeginOperation("Обновление");
            var kind = "Обновление";
            int total = computers.Count, done = 0;
            var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            foreach (var e in entries) if (string.IsNullOrEmpty(e.LastSeen)) e.LastSeen = now;

            try
            {
                await ParallelForEachAsync(computers, maxConcurrency: 15, _cts!.Token, async (computer, ct) =>
                {
                    var alive = PingService.IsAlive(computer, 800);
                    // Persist for next time the dialog opens.
                    PingCache.Set(computer, alive);
                    var pingState = alive
                        ? Helpers.PingStatusIcon.State.Alive
                        : Helpers.PingStatusIcon.State.Dead;
                    var pingBmp = Helpers.PingStatusIcon.Get(pingState);

                    // Patch grid Ping cells for this computer immediately so
                    // the user sees live progress as computers get pinged.
                    UpdateGridPingForComputer(computer, pingBmp);

                    if (!alive) { Interlocked.Increment(ref done); UpdateStatusFromBackground($"{kind}: {done} / {total} — {computer} offline"); return; }

                    var cs = await WmiService.GetComputerSystemAsync(computer, ct);
                    var net = await WmiService.GetNetworkConfigAsync(computer, ct);
                    var ipStr = net != null && net.IPv4Addresses.Count > 0
                        ? string.Join(", ", net.IPv4Addresses)
                        : "";
                    var userName = cs?.UserName ?? "";
                    var sam = userName.Contains('\\') ? userName.Substring(userName.IndexOf('\\') + 1) : userName;
                    var fullName = !string.IsNullOrEmpty(sam)
                        ? await Task.Run(() => AdService.GetDisplayName(sam), ct)
                        : "";

                    // Build strings we can pass across threads safely.
                    var ipCopy = ipStr;
                    var userCopy = userName;
                    var fullCopy = fullName;
                    lock (entries)
                    {
                        foreach (var e in entries)
                        {
                            if (!string.Equals(e.Computer, computer, StringComparison.OrdinalIgnoreCase)) continue;
                            if (!string.IsNullOrEmpty(ipCopy)) e.IPs = ipCopy;
                            if (!string.IsNullOrEmpty(userCopy) && string.IsNullOrEmpty(e.User)) e.User = userCopy;
                            if (!string.IsNullOrEmpty(fullCopy) && string.IsNullOrEmpty(e.FullName)) e.FullName = fullCopy;
                            e.LastSeen = now;
                        }
                    }

                    // Patch visible IP/User/FullName cells too.
                    UpdateGridFieldsForComputer(computer, ipCopy, userCopy, fullCopy);

                    Interlocked.Increment(ref done);
                    UpdateStatusFromBackground($"{kind}: {done} / {total} — {computer}");
                });
                AddressBookStore.Save(entries);
                ReloadGrid();
                Logger.Info($"RefreshAll: done ({done}/{total})");
            }
            catch (OperationCanceledException)
            {
                AddressBookStore.Save(entries);
                ReloadGrid();
            }
            catch (Exception ex)
            {
                Logger.Error($"RefreshAll failed: {ex.Message}");
                MessageBox.Show($"Ошибка: {ex.Message}", "Обновить всё",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                EndOperation(kind, done, total, _cts?.IsCancellationRequested ?? false);
            }
        }

        // Returns the Computer column value of the currently selected row,
        // or "" if nothing is selected. Used by the context menu to know
        // which entry to act on.
        private string GetSelectedComputer()
        {
            if (_grid.SelectedRows.Count == 0)
            {
                MessageBox.Show("Запись не выбрана.", "Адресная книга",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return "";
            }
            var v = _grid.SelectedRows[0].Cells["Computer"].Value?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(v))
            {
                MessageBox.Show("У этой записи не указан компьютер.", "Адресная книга",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return "";
            }
            return v;
        }

        // Returns the User column value. Used by "Поиск где залогинен" to
        // pre-populate the dialog with the user from the right-clicked row.
        // Unlike GetSelectedComputer, doesn't show a MessageBox if empty —
        // the user can still type a name manually.
        private string GetSelectedUser()
        {
            if (_grid.SelectedRows.Count == 0) return "";
            return _grid.SelectedRows[0].Cells["User"].Value?.ToString() ?? "";
        }

        // Auto-ping computers on dialog open, but throttled globally — at most
        // once per hour regardless of how many times the book is opened.
        //
        // Only pings (no WMI / AD) — sets alive/dead circles fast (~5-10s for
        // 80 hosts). WMI queries + FullName lookups are expensive and only
        // run when the user explicitly clicks "Обновить всё".
        private async Task AutoPingStaleAsync()
        {
            await Task.Delay(50);

            if (!PingCache.IsSweepStale(TimeSpan.FromHours(1)))
            {
                Logger.Info("AutoPing: skipped (sweep done <1h ago)");
                return;
            }

            var computers = AddressBookStore.Load()
                .Select(e => e.Computer)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (computers.Count == 0) return;

            PingCache.MarkSweepDone();

            // Run ping-only sweep — no WMI, no AD.
            await OnPingOnlyAsync(computers);
        }

        // Lightweight ping-only sweep: just alive/dead for each host, no WMI
        // queries or AD lookups. Runs in TWO phases:
        //
        //   Phase 1: ping all hosts in parallel (concurrency 50, fast I/O).
        //            Updates the Ping column in the grid + PingCache.
        //   Phase 2: for hosts that pinged alive AND have entries with empty
        //            User, query WMI Win32_ComputerSystem.UserName and fill
        //            it in. Concurrency 5 — WMI is heavier and we don't want
        //            to starve the DC or the local RPC port range.
        //
        // Splitting the phases keeps the ping sweep fast (3-5 sec for 80
        // hosts) and isolates WMI failures so a slow host doesn't get
        // drowned out by faster ones in the same parallel batch.
        //
        // All branches log to debug.log so we can diagnose "login didn't
        // appear" complaints — without these lines the failure modes are
        // invisible.
        private async Task OnPingOnlyAsync(List<string> computers)
        {
            if (_cts != null) return;

            BeginOperation("Пинг");
            int total = computers.Count, pingDone = 0;
            int loginsFilled = 0, loginAttempts = 0;
            _cts = new CancellationTokenSource();

            try
            {
                // === Phase 1: ping ===
                await ParallelForEachAsync(computers, maxConcurrency: 50, _cts.Token, async (computer, ct) =>
                {
                    var alive = await PingService.IsAliveAsync(computer);
                    PingCache.Set(computer, alive);
                    var pingBmp = alive
                        ? Helpers.PingStatusIcon.Get(Helpers.PingStatusIcon.State.Alive)
                        : Helpers.PingStatusIcon.Get(Helpers.PingStatusIcon.State.Dead);
                    UpdateGridPingForComputer(computer, pingBmp);
                    Interlocked.Increment(ref pingDone);
                    UpdateStatusFromBackground($"Пинг: {pingDone} / {total}");
                });
                Logger.Info($"PingOnly phase 1 (ping): done {pingDone}/{total}");

                // === Phase 2: WMI UserName for alive hosts needing login ===
                // Read PingCache to find alive ones; then filter to those
                // whose grid rows have at least one empty User.
                var aliveNeedLogin = computers
                    .Where(c => PingCache.TryGet(c, out var alive, out _) && alive)
                    .Where(AnyEntryNeedsLoginForComputer)
                    .ToList();
                Logger.Info($"PingOnly phase 2 (login): {aliveNeedLogin.Count} host(s) need WMI lookup");

                if (aliveNeedLogin.Count > 0)
                {
                    int loginDone = 0;
                    // Concurrency 5 — lower than the ping phase because
                    // qwinsta opens an RPC connection to each host and the
                    // local RPC client stack gets flaky above ~8 parallel
                    // calls (transient 0-char responses). At 5 we trade a
                    // few extra seconds of total time for reliability.
                    await ParallelForEachAsync(aliveNeedLogin, maxConcurrency: 5, _cts.Token, async (computer, ct) =>
                    {
                        Interlocked.Increment(ref loginAttempts);
                        var filled = await TryFetchAndFillLoginAsync(computer, ct);
                        if (filled) Interlocked.Increment(ref loginsFilled);
                        Interlocked.Increment(ref loginDone);
                        UpdateStatusFromBackground($"Логин: {loginDone} / {aliveNeedLogin.Count}");
                    });
                }

                Logger.Info($"PingOnly sweep done: pinged {pingDone}/{total}, login attempts {loginAttempts}, filled {loginsFilled}");
            }
            catch (OperationCanceledException) { /* swallowed */ }
            catch (Exception ex)
            {
                Logger.Error($"PingOnly failed: {ex.Message}");
            }
            finally
            {
                EndOperation("Пинг", pingDone, total, _cts?.IsCancellationRequested ?? false);
                try { _cts?.Dispose(); } catch { }
                _cts = null;
            }
        }

        // Quick scan: does any book entry for this computer have an empty
        // User field? Runs on the UI thread (cheap O(N) over current rows).
        // Used to short-circuit the WMI call when there's nothing to fill.
        private bool AnyEntryNeedsLoginForComputer(string computer)
        {
            if (string.IsNullOrEmpty(computer)) return false;
            if (InvokeRequired)
            {
                // Rare — caller is already on UI thread in practice. Fall
                // back to "true" so we err on the side of doing the lookup.
                return true;
            }
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (!string.Equals(row.Cells["Computer"].Value?.ToString(),
                    computer, StringComparison.OrdinalIgnoreCase)) continue;
                var user = row.Cells["User"].Value?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(user)) return true;
            }
            return false;
        }

        // Query the current logged-in user on a computer and fill the User
        // cell on every matching row that has an empty User. Returns true if
        // at least one row was updated. Also persists the change to disk so
        // it survives dialog close/reopen.
        //
        // Source: QWINSTA (the same call the main-form search uses). Earlier
        // versions used Win32_ComputerSystem.UserName via WMI, but on Win10/11
        // that property is unreliable for RDP sessions — it often returns
        // empty even when a user is actively logged in via Remote Interactive
        // (LogonType 10). qwinsta reads the same session list Windows shows
        // in Task Manager → Users, so it always agrees with what the user
        // sees in the main search.
        //
        // Pick order from the parsed sessions:
        //   1. First "Active" session (interactive use right now).
        //   2. Else first non-empty session (somebody is at least connected).
        //
        // Transient-empty retry: when 27 qwinsta processes run concurrently,
        // the local RPC client stack occasionally returns 0 chars + exit 0
        // for hosts that DO have sessions (verified by running qwinsta
        // manually right after). On that specific signature we retry once
        // after a short delay — fixes the "KUBYSHKINA didn't fill" case.
        //
        // After User is filled, also resolves FullName from AD so the user
        // doesn't have to click "Обновить ФИО" manually.
        private async Task<bool> TryFetchAndFillLoginAsync(string computer, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(computer)) return false;
            try
            {
                // Shorter timeout than the main search (5s vs 10s). The
                // sweep already confirmed the host is alive via ping, so
                // qwinsta should answer fast. Anything slower is a host
                // under load — not worth blocking the sweep.
                var result = await Task.Run(() => QwinstaService.Query(computer, 5000), ct);

                // Retry on the "0 chars + exit 0" signature — this is the
                // transient-RPC-starvation case, not a real "no sessions"
                // (which would come back as non-zero exit + stderr
                // "Не существуют сеансы для *").
                if (!result.TimedOut && result.Sessions.Count == 0 &&
                    result.ExitCode == 0 && string.IsNullOrEmpty(result.ErrorText))
                {
                    Logger.Info($"AutoLogin: {computer} → empty result, retrying in 1.5s");
                    await Task.Delay(1500, ct);
                    result = await Task.Run(() => QwinstaService.Query(computer, 5000), ct);
                }

                if (result.TimedOut)
                {
                    Logger.Info($"AutoLogin: {computer} → qwinsta timeout");
                    return false;
                }
                if (result.Sessions.Count == 0)
                {
                    Logger.Info($"AutoLogin: {computer} → no sessions (exit={result.ExitCode})");
                    return false;
                }

                // Prefer an Active session; fall back to any session.
                var pick = result.Sessions.FirstOrDefault(s =>
                    s.State.IndexOf("Active", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    s.State.IndexOf("Актив", StringComparison.OrdinalIgnoreCase) >= 0)
                    ?? result.Sessions[0];

                var sam = pick.UserName.Trim();
                if (string.IsNullOrWhiteSpace(sam))
                {
                    Logger.Info($"AutoLogin: {computer} → all sessions have empty UserName");
                    return false;
                }

                int patched = 0;
                if (InvokeRequired)
                {
                    try
                    {
                        Invoke(new Action(() => patched = PatchUserCellsForComputer(computer, sam)));
                    }
                    catch { return false; }
                }
                else
                {
                    patched = PatchUserCellsForComputer(computer, sam);
                }

                if (patched > 0)
                {
                    PersistUserUpdate(computer, sam);
                    // Best-effort FullName lookup against AD. Cheap (one LDAP
                    // query), and means the user doesn't have to click
                    // "Обновить ФИО" afterwards. Wrapped in try/catch so a
                    // flaky AD never blocks the User update we already did.
                    string fullName = "";
                    try { fullName = AdService.GetDisplayName(sam) ?? ""; }
                    catch (Exception ex) { Logger.Warn($"AutoLogin FullName lookup for {sam}: {ex.Message}"); }
                    if (!string.IsNullOrWhiteSpace(fullName))
                    {
                        if (InvokeRequired)
                        {
                            try { Invoke(new Action(() => PatchFullNameCellsForComputer(computer, fullName))); } catch { }
                        }
                        else
                        {
                            PatchFullNameCellsForComputer(computer, fullName);
                        }
                        PersistFullNameUpdate(computer, fullName);
                    }
                    Logger.Info($"AutoLogin: {computer} → '{sam}' via {pick.State}, ФИО='{fullName}' (patched {patched} row(s))");
                    return true;
                }
                Logger.Info($"AutoLogin: {computer} → '{sam}' but no rows needed patching (already filled?)");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Logger.Warn($"AutoLogin on {computer} failed: {ex.Message}");
            }
            return false;
        }

        // UI-thread helper: patch the FullName cell on every row matching
        // this computer that currently has an empty FullName.
        private int PatchFullNameCellsForComputer(string computer, string fullName)
        {
            if (IsDisposed || _grid.IsDisposed || _grid.Columns.Count == 0) return 0;
            int patched = 0;
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (!string.Equals(row.Cells["Computer"].Value?.ToString(),
                    computer, StringComparison.OrdinalIgnoreCase)) continue;
                var current = row.Cells["FullName"].Value?.ToString() ?? "";
                if (!string.IsNullOrWhiteSpace(current)) continue;
                row.Cells["FullName"].Value = fullName;
                patched++;
            }
            return patched;
        }

        // Persist auto-fetched FullName to addressbook.json. Updates entries
        // for this computer whose FullName is empty.
        private static void PersistFullNameUpdate(string computer, string fullName)
        {
            try
            {
                var entries = AddressBookStore.Load();
                bool changed = false;
                foreach (var e in entries)
                {
                    if (!string.Equals(e.Computer, computer, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.IsNullOrWhiteSpace(e.FullName)) continue;
                    e.FullName = fullName;
                    changed = true;
                }
                if (changed) AddressBookStore.Save(entries);
            }
            catch (Exception ex)
            {
                Logger.Warn($"PersistFullNameUpdate on {computer} failed: {ex.Message}");
            }
        }

        // UI-thread helper: patch the User cell on every row that matches
        // this computer and currently has an empty User. Returns the number
        // of rows patched. Must be called on the UI thread (touches _grid).
        private int PatchUserCellsForComputer(string computer, string sam)
        {
            if (IsDisposed || _grid.IsDisposed || _grid.Columns.Count == 0) return 0;
            int patched = 0;
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (!string.Equals(row.Cells["Computer"].Value?.ToString(),
                    computer, StringComparison.OrdinalIgnoreCase)) continue;
                var currentUser = row.Cells["User"].Value?.ToString() ?? "";
                if (!string.IsNullOrWhiteSpace(currentUser)) continue;
                row.Cells["User"].Value = sam;
                patched++;
            }
            return patched;
        }

        // Persist the auto-fetched login to addressbook.json so it survives
        // dialog close/reopen. Only updates entries for this computer whose
        // User field is empty in the file.
        private static void PersistUserUpdate(string computer, string sam)
        {
            try
            {
                var entries = AddressBookStore.Load();
                bool changed = false;
                foreach (var e in entries)
                {
                    if (!string.Equals(e.Computer, computer, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.IsNullOrWhiteSpace(e.User)) continue;
                    e.User = sam;
                    changed = true;
                }
                if (changed) AddressBookStore.Save(entries);
            }
            catch (Exception ex)
            {
                Logger.Warn($"PersistUserUpdate on {computer} failed: {ex.Message}");
            }
        }

        // Marshal a status string from a background task to the UI label.
        // WinForms requires Control.Invoke for cross-thread UI access.
        private void UpdateStatusFromBackground(string text)
        {
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<string>(UpdateStatusFromBackground), text); } catch { }
                return;
            }
            _statusLabel.Text = text;
        }

        // Update the Ping cell for every row matching this computer. Called
        // from a background thread — marshals to UI via BeginInvoke.
        private void UpdateGridPingForComputer(string computer, Image pingBmp)
        {
            if (IsDisposed || !IsHandleCreated || _grid.IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<string, Image>(UpdateGridPingForComputer), computer, pingBmp); } catch { }
                return;
            }
            if (_grid.Columns.Count == 0) return;
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (string.Equals(row.Cells["Computer"].Value?.ToString(),
                    computer, StringComparison.OrdinalIgnoreCase))
                {
                    row.Cells["Ping"].Value = pingBmp;
                }
            }
        }

        // Patch IP / User / FullName cells for every row matching this computer.
        // Only overwrites cells that are currently empty (matches the entry-
        // update policy — don't clobber user-edited values).
        private void UpdateGridFieldsForComputer(string computer, string ip, string user, string fullName)
        {
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<string, string, string, string>(UpdateGridFieldsForComputer),
                    computer, ip, user, fullName); } catch { }
                return;
            }
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (!string.Equals(row.Cells["Computer"].Value?.ToString(),
                    computer, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrEmpty(ip)) row.Cells["IPs"].Value = ip;
                if (!string.IsNullOrEmpty(user) && string.IsNullOrEmpty(row.Cells["User"].Value?.ToString()))
                    row.Cells["User"].Value = user;
                if (!string.IsNullOrEmpty(fullName) && string.IsNullOrEmpty(row.Cells["FullName"].Value?.ToString()))
                    row.Cells["FullName"].Value = fullName;
            }
        }

        // Bounded-concurrency parallel foreach. The SemaphoreSlim caps how many
        // tasks run at once — same role as RunspacePool's MaxConcurrency in the
        // .ps1 version.
        private static async Task ParallelForEachAsync<T>(
            IEnumerable<T> items, int maxConcurrency,
            CancellationToken ct, Func<T, CancellationToken, Task> body)
        {
            using var sem = new SemaphoreSlim(maxConcurrency);
            var tasks = items.Select(async item =>
            {
                await sem.WaitAsync(ct);
                try { await body(item, ct); }
                finally { sem.Release(); }
            });
            await Task.WhenAll(tasks);
        }

        // Edit the selected entry via the EditEntryForm popup.
        private void OnEditClicked()
        {
            if (_grid.SelectedRows.Count == 0)
            {
                MessageBox.Show("Запись не выбрана.", "Редактировать",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            OnEditRow(_grid.SelectedRows[0]);
        }

        private void OnEditRow(DataGridViewRow row)
        {
            if (row == null || row.IsNewRow) return;
            // Build an entry snapshot from the row's current cell values —
            // these include any inline edits the user already made.
            var snapshot = new AddressBookEntry
            {
                Computer = row.Cells["Computer"].Value?.ToString() ?? "",
                FullName = row.Cells["FullName"].Value?.ToString() ?? "",
                User     = row.Cells["User"].Value?.ToString() ?? "",
                Phone    = row.Cells["Phone"].Value?.ToString() ?? "",
                IPs      = row.Cells["IPs"].Value?.ToString() ?? "",
                LastSeen = row.Cells["LastSeen"].Value?.ToString() ?? ""
            };
            if (EditEntryForm.ShowEditDialog(this, snapshot))
            {
                // Saved — reload to reflect any changes.
                ReloadGrid();
                ApplySearchFilter();
                _statusLabel.Text = "Запись обновлена.";
            }
        }

        // === Delete ===
        private void OnDeleteClicked()
        {
            DeleteRows(GetSelectedBookRows());
        }

        private void DeleteRows(System.Collections.Generic.List<DataGridViewRow> rows)
        {
            if (rows == null || rows.Count == 0)
            {
                MessageBox.Show("Выберите записи для удаления.", "Удалить",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var count = rows.Count;
            var r = MessageBox.Show(
                $"Удалить {count} записей из адресной книги?",
                "Удалить",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return;

            // Build a set of (Computer, User) keys for the given rows.
            var toDrop = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DataGridViewRow row in rows)
            {
                if (row.IsNewRow) continue;
                var c = row.Cells["Computer"].Value?.ToString() ?? "";
                var u = row.Cells["User"].Value?.ToString() ?? "";
                toDrop.Add($"{c}|{u}");
            }

            var entries = AddressBookStore.Load();
            var kept = entries
                .Where(e => !toDrop.Contains($"{e.Computer}|{e.User}"))
                .ToList();
            AddressBookStore.Save(kept);

            // Remove from grid directly (avoids full reload losing scroll position).
            foreach (DataGridViewRow row in rows)
            {
                if (!row.IsNewRow) _grid.Rows.Remove(row);
            }

            Logger.Info($"Deleted {count} entries");
            UpdateStatus();
        }

        // === Helpers ===
        private static void AddTextColumn(DataGridView grid, string name, string header, int width)
        {
            var col = new DataGridViewTextBoxColumn
            {
                Name = name,
                HeaderText = header,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
                Width = width,
                MinimumWidth = 40
            };
            grid.Columns.Add(col);
        }

        // v2.0.7 — flat label-style action buttons. All uniform pill style:
        //   Danger  → red text (Удалить only)
        //   Default → muted grey text (everything else, including Обновить всё)
        // AutoSize=true so each label takes its natural width; SyncLayout
        // places them sequentially with uniform gaps.
        // Hover = accent indigo text on AccentSoft background — same visual
        // language as the active filter pill, so hover feels like
        // "preview-as-active".
        private enum ActionRole { Default, Danger }

        // v2.3.7: 1:1 match with the "+ Сессия" / "+ В книгу" header buttons
        // in RdpManagerForm — filled indigo (#296AA0) bg, white text,
        // Segoe UI Semibold 9F, radius 1, hover = ControlPaint.Dark(accent,
        // 0.08f). AutoSize=false with width pre-measured via MeasureText so
        // variable button labels ("Удалить" vs "Обновить ФИО") all share the
        // same height (22px) and visual weight. ActionRole.Danger kept for
        // API compatibility but no longer affects visuals (all indigo).
        private static Label MakeActionButton(string text, ActionRole role, Color? bgColor = null)
        {
            var font = new Font("Segoe UI Semibold", 9F);
            var textSize = TextRenderer.MeasureText(text, font,
                new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
            const int padX = 12;
            const int btnH = 22;
            var lbl = new Label
            {
                Text = "",
                AutoSize = false,
                Size = new Size(textSize.Width + padX * 2, btnH),
                Font = font,
                Cursor = Cursors.Hand,
                Margin = Padding.Empty,
                BackColor = Color.Transparent
            };
            const int radius = 1;
            Color bgBase = bgColor ?? ShadowRDP.Helpers.UiTheme.HeaderBar;
            Color bgHover = ControlPaint.Dark(bgBase, 0.08f);
            Color bgDisabled = Color.FromArgb(0x9A, 0xB5, 0xC9);  // faded indigo
            var bgVar = bgBase;
            lbl.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                var r = new Rectangle(0, 0, lbl.Width, lbl.Height);
                using var path = RoundedRect(r, radius);
                using var brush = new SolidBrush(bgVar);
                e.Graphics.FillPath(brush, path);
                TextRenderer.DrawText(e.Graphics, text, font, r,
                    Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            };
            lbl.MouseEnter += (s, e) =>
            {
                if (!lbl.Enabled) return;
                bgVar = bgHover;
                lbl.Invalidate();
            };
            lbl.MouseLeave += (s, e) =>
            {
                bgVar = lbl.Enabled ? bgBase : bgDisabled;
                lbl.Invalidate();
            };
            lbl.EnabledChanged += (s, e) =>
            {
                bgVar = lbl.Enabled ? bgBase : bgDisabled;
                lbl.Invalidate();
            };
            lbl.Tag = role;
            return lbl;
        }

        // Hook a Label (or any Control) so its visible Region tracks the
        // rounded shape — used for filter pills + action buttons so the
        // hover background gets the 2px corner radius requested in v2.1.5.
        // Updates on Resize because AutoSize labels change size after layout.
        private static void ApplyRoundedRegion(Control c, int radius)
        {
            EventHandler update = (_, _) =>
            {
                if (c.IsDisposed || c.Width <= 0 || c.Height <= 0) return;
                c.Region?.Dispose();
                using var path = RoundedRect(new Rectangle(0, 0, c.Width, c.Height), radius);
                c.Region = new System.Drawing.Region(path);
            };
            c.Resize += update;
            // Initial population — if Size is already set, fire once.
            if (c.Width > 0 && c.Height > 0) update(c, EventArgs.Empty);
        }

        // === Native Win32 helpers ===
        //
        // EM_SETCUEBANNER — sets the placeholder (cue banner) text on an Edit
        // control (TextBox). WinForms TextBox in .NET 8 has no managed
        // PlaceholderText property; this is the same mechanism the WinForms
        // source itself uses internally on .NET 9+ when you set PlaceholderText.
        // WPARAM=1 means "show cue banner even when focused".
        private static class NativeMethods
        {
            [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
            private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

            private const int EM_SETCUEBANNER = 0x1501;

            public static void SetCueBanner(IntPtr handle, string text)
            {
                try { SendMessage(handle, EM_SETCUEBANNER, (IntPtr)1, text ?? ""); }
                catch { /* best-effort — placeholder just won't show */ }
            }
        }

        // === Filter row ===

        // Adds one toggle label to the filter row. `key` is "" for "Все",
        // "None" for "Без группы", otherwise a ComputerGroup.All key. The
        // default ("Все") starts active.
        //
        // v2.0.5: Label-based. Inactive = transparent bg + muted text (no
        // border, no background). Active = AccentSoft bg + Accent text.
        // Label.Click gives us the toggle for free.
        private void AddFilterButton(string key, string label, int x, int y, bool isDefault)
        {
            int width = MeasureButtonTextWidth(label, 28);
            var lbl = new Label
            {
                Text = label,
                Location = new Point(x, y),
                Size = new Size(width, 26),
                AutoSize = false,
                Anchor = AnchorStyles.Top | AnchorStyles.Left,
                Tag = width,
                Font = new Font("Segoe UI", 9F),
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand
            };
            ApplyPillStyle(lbl, isDefault);
            ApplyRoundedRegion(lbl, 1);
            // Active state gets a 1px rounded border (PillActiveBorder). The
            // base Label already paints the bg/fg; we just overlay the border.
            lbl.Paint += (s, e) =>
            {
                if (lbl.ForeColor != Helpers.UiTheme.PillActiveFg) return;
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                // Draw the border INSIDE the edge so it isn't clipped by the
                // control's rounded Region (the outer half of a centred stroke
                // was being cut off — worst on the left side).
                using var path = Helpers.UiButtons.RoundedRect(new Rectangle(0, 0, lbl.Width - 1, lbl.Height - 1), 1);
                using var pen = new Pen(Helpers.UiTheme.PillActiveBorder, 1);
                pen.Alignment = System.Drawing.Drawing2D.PenAlignment.Inset;
                g.DrawPath(pen, path);
            };
            // Hover — AccentSoft bg INSET 5px each side so it doesn't touch
            // neighbouring pills. Region shrinks on enter, restores on leave.
            lbl.MouseEnter += (s, e) =>
            {
                if (_groupFilter == key) return;
                lbl.BackColor = Helpers.UiTheme.AccentSoft;
                lbl.ForeColor = Helpers.UiTheme.Accent;
            };
            lbl.MouseLeave += (s, e) =>
            {
                if (_groupFilter == key) return;
                ApplyPillStyle(lbl, active: false);
            };
            // v3.8.x — left-click filters the table; right-click opens the
            // group-management menu. Using MouseUp (not Click) keeps the two
            // buttons strictly separated so a right-click never filters.
            lbl.MouseUp += (s, e) =>
            {
                if (e.Button == MouseButtons.Left) OnFilterButtonClicked(key);
                else if (e.Button == MouseButtons.Right) ShowGroupMenu(key, lbl, e.Location);
            };
            _filterButtons[key] = lbl;
            _tabsPanel.Controls.Add(lbl);
        }

        // v3.8.x — builds (or rebuilds) the filter pills: "Все", one pill per
        // ComputerGroup, then "Без группы". Returns the x-position immediately
        // after the last pill so the caller can place the action buttons.
        private int BuildFilterPills()
        {
            const int pillGap = 18;
            const int pillStart = 24;
            int fx = pillStart;
            AddFilterButton("", "Все", fx, 10, true);
            fx += MeasureButtonTextWidth("Все", 28) + pillGap;
            foreach (var info in Helpers.ComputerGroup.All)
            {
                AddFilterButton(info.Key, info.Label, fx, 10, false);
                fx += MeasureButtonTextWidth(info.Label, 28) + pillGap;
            }
            AddFilterButton("None", "Без группы", fx, 10, false);
            fx += MeasureButtonTextWidth("Без группы", 28) + pillGap;
            return fx;
        }

        // v3.8.x — rebuilds the filter pills after a group is added / renamed /
        // deleted, then re-applies the active filter and re-lays the action
        // buttons. Does not change _groupFilter except when the deleted group
        // was active (handled by the caller).
        private void RebuildFilterPills()
        {
            foreach (var kv in _filterButtons)
            {
                var c = kv.Value;
                if (!c.IsDisposed) { _tabsPanel.Controls.Remove(c); c.Dispose(); }
            }
            _filterButtons.Clear();
            int fx = BuildFilterPills();
            _openFilesBtn.Location = new Point(fx, 10);
            _messageAllBtn.Location = new Point(fx, 10);
            foreach (var kv in _filterButtons)
                ApplyPillStyle(kv.Value, kv.Key == _groupFilter);
            if (_filterButtons.TryGetValue(_groupFilter, out var activePill))
                activePill.BringToFront();
        }

        // Pick black-ish or white text for legibility on an arbitrary background.
        private static Color ContrastColor(Color c)
        {
            var lum = c.R * 0.299 + c.G * 0.587 + c.B * 0.114;
            return lum > 150 ? Color.FromArgb(0x2B, 0x2A, 0x20) : Color.White;
        }

        // v3.9.0 — shows the group context menu (themed popup) for the pill.
        private void ShowGroupMenu(string key, Control sender, Point loc)
        {
            _ctxGroupKey = key;
            bool isGroup = Helpers.ComputerGroup.ByKey(key) != null;
            var label = isGroup ? Helpers.ComputerGroup.ByKey(key)!.Label : "Без группы";
            var menu = new Helpers.ThemedContextMenu();
            menu.AddHeader("Группа", label);
            menu.AddItem("Создать группу", "add", () => CreateGroup());
            if (isGroup)
            {
                menu.AddItem("Редактировать группу", "info", () => EditGroup(_ctxGroupKey));
                menu.AddItem("Удалить", "delete", () => DeleteGroup(_ctxGroupKey));
            }
            menu.AddSeparator();
            menu.AddItem("Изменить цвет пилы…", "info", () =>
            {
                using var dlg = new ColorDialog { AnyColor = true, FullOpen = true };
                try { dlg.Color = Helpers.UiTheme.PillActiveBg; } catch { }
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    Helpers.UiTheme.SetPillColors(dlg.Color, ContrastColor(dlg.Color), dlg.Color);
                    RebuildFilterPills();
                }
            });
            menu.Show(this, sender.PointToScreen(loc));
        }

        private void CreateGroup()
        {
            var res = Helpers.GroupEditDialog.Show(this, null);
            if (res == null) return;
            string key = Helpers.ComputerGroup.GenerateKey(res.Name);
            Helpers.ComputerGroup.AddGroup(key, res.Name, res.Accent);
            _badgeGdiCache.Clear();
            RebuildFilterPills();
            ReloadGrid();
        }

        private void EditGroup(string key)
        {
            var info = Helpers.ComputerGroup.ByKey(key);
            if (info == null) return;
            var res = Helpers.GroupEditDialog.Show(this, info);
            if (res == null) return;
            Helpers.ComputerGroup.UpdateGroup(key, res.Name, res.Accent);
            _badgeGdiCache.Clear();
            RebuildFilterPills();
            ReloadGrid();
        }

        private void DeleteGroup(string key)
        {
            var info = Helpers.ComputerGroup.ByKey(key);
            if (info == null) return;
            if (MessageBox.Show(this,
                    $"Удалить группу «{info.Label}»? Записи этой группы станут «Без группы».",
                    "Удаление группы", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            var entries = ShadowRDP.Services.AddressBookStore.Load();
            bool changed = false;
            foreach (var en in entries)
            {
                if (string.Equals(en.Group, key, StringComparison.OrdinalIgnoreCase))
                {
                    en.Group = "";
                    changed = true;
                }
            }
            if (changed) ShadowRDP.Services.AddressBookStore.Save(entries);
            Helpers.ComputerGroup.RemoveGroup(key);
            if (string.Equals(_groupFilter, key, StringComparison.OrdinalIgnoreCase))
                _groupFilter = "";
            _badgeGdiCache.Clear();
            RebuildFilterPills();
            ReloadGrid();
        }

        // Filled accent button for the filter strip ("Открытые файлы").
        // Distinct from filter pills: indigo background, white text, hover
        // darkens. Same height/vertical placement as the pills. Painted
        // manually (antialiased rounded fill) instead of a Region mask so
        // every corner stays smooth — the Region clip looked "bitten off"
        // at the top and square at the bottom (v3.7.x).
        private Label MakeOpenFilesButton(string text, int x, int y)
        {
            var font = new Font("Segoe UI Semibold", 9F);
            var lbl = new Label
            {
                Text = "",
                Location = new Point(x, y),
                Size = new Size(MeasureButtonTextWidth(text, 28), 26),
                AutoSize = false,
                Anchor = AnchorStyles.Top | AnchorStyles.Left,
                Tag = MeasureButtonTextWidth(text, 28),
                Font = font,
                Cursor = Cursors.Hand,
                BackColor = Color.Transparent
            };
            var bgVar = Helpers.UiTheme.Accent;
            lbl.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                var r = new Rectangle(0, 0, lbl.Width, lbl.Height);
                using var path = RoundedRect(r, 6);
                using var brush = new SolidBrush(bgVar);
                e.Graphics.FillPath(brush, path);
                TextRenderer.DrawText(e.Graphics, text, font, r,
                    Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            };
            lbl.MouseEnter += (s, e) =>
            {
                bgVar = Helpers.UiTheme.AccentHover;
                lbl.Invalidate();
            };
            lbl.MouseLeave += (s, e) =>
            {
                bgVar = Helpers.UiTheme.Accent;
                lbl.Invalidate();
            };
            return lbl;
        }

        private void OnOpenFilesClicked()
        {
            try
            {
                using var dlg = new OpenFilesForm();
                dlg.ShowDialog(this);
            }
            catch (Exception ex)
            {
                Logger.Error("OpenFilesForm failed: " + ex.Message);
            }
        }

        private void OnInventoryClicked()
        {
            try
            {
                using var dlg = new SoftwareInventoryForm();
                dlg.ShowDialog(this);
            }
            catch (Exception ex)
            {
                Logger.Error("SoftwareInventoryForm failed: " + ex.Message);
            }
        }

        // "Сообщение всем" — sends a popup message to the computers of the
        // users currently visible in the "Пользователи" tab (respects the
        // search-box filter, deduplicates hosts). Sends in the background
        // with a small concurrency cap so a large roster doesn't flood the
        // network or block the UI, then shows a summary.
        private async void OnMessageAllClicked()
        {
            var targets = new List<string>();
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.IsNewRow || !row.Visible) continue;
                if (!string.Equals(row.Cells["Group"].Value?.ToString(), Helpers.ComputerGroup.Users,
                        StringComparison.OrdinalIgnoreCase)) continue;
                var computer = row.Cells["Computer"].Value?.ToString();
                if (string.IsNullOrWhiteSpace(computer)) continue;
                if (targets.Any(t => string.Equals(t, computer, StringComparison.OrdinalIgnoreCase))) continue;
                targets.Add(computer);
            }

            if (targets.Count == 0)
            {
                MessageBox.Show("Нет компьютеров для рассылки.", "Сообщение всем",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var text = PromptDialog.Show(this, "Сообщение всем",
                $"Введите сообщение для {targets.Count} компьютеров:", "");
            if (string.IsNullOrEmpty(text)) return;

            _messageAllBtn.Enabled = false;
            try
            {
                var ok = 0;
                var failed = new List<string>();
                using var gate = new SemaphoreSlim(5);
                var tasks = targets.Select(async computer =>
                {
                    await gate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        if (RemoteActions.SendMessage(computer, text))
                        {
                            Interlocked.Increment(ref ok);
                        }
                        else
                        {
                            lock (failed) failed.Add(computer);
                        }
                    }
                    finally
                    {
                        gate.Release();
                    }
                });
                await Task.WhenAll(tasks);

                var summary = $"Отправлено: {ok} из {targets.Count}.";
                if (failed.Count > 0)
                    summary += "\nНе удалось: " + string.Join(", ", failed);
                MessageBox.Show(this, summary, "Сообщение всем",
                    MessageBoxButtons.OK, failed.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Logger.Error($"Сообщение всем failed: {ex.Message}");
            }
            finally
            {
                _messageAllBtn.Enabled = true;
            }
        }

        private static void ApplyPillStyle(Label lbl, bool active)
        {
            if (active)
            {
                lbl.BackColor = Helpers.UiTheme.PillActiveBg;
                lbl.ForeColor = Helpers.UiTheme.PillActiveFg;
                lbl.Font = new Font("Segoe UI Semibold", 9F);
            }
            else
            {
                lbl.BackColor = Color.Transparent;
                lbl.ForeColor = Helpers.UiTheme.MutedForeground;
                lbl.Font = new Font("Segoe UI", 9F);
            }
        }

        // Paint the app logo top-left of the header panel. Uses the embedded
        // logo (AppLogo.Load) so it stays in sync with the rest of the app
        // branding — the same mark shown in the splash, sidebar, tray and
        // exe icon. Drawn scaled into the 28×28 header slot.
        private void DrawHeaderLogo(object? sender, PaintEventArgs e)
        {
            var logo = Helpers.AppLogo.Load();
            if (logo == null) return;
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(logo, new Rectangle(14, 14, 28, 28));
        }

        // Measure rendered text width for a NeoButton (Segoe UI 10F — matches
        // NeoButton's constructor font). Used to AutoSize filter buttons.
        private static int MeasureButtonTextWidth(string label, int paddingPx)
        {
            if (string.IsNullOrEmpty(label)) return paddingPx;
            using var bmp = new Bitmap(1, 1);
            using var g = Graphics.FromImage(bmp);
            var size = TextRenderer.MeasureText(g, label, new Font("Segoe UI", 10F),
                Size.Empty, TextFormatFlags.NoPadding);
            return size.Width + paddingPx;
        }

        // Single-select radio behaviour: clicking a filter makes it the
        // active one and de-selects the rest.
        private void OnFilterButtonClicked(string key)
        {
            _groupFilter = key;
            foreach (var kv in _filterButtons)
            {
                ApplyPillStyle(kv.Value, kv.Key == key);
            }
            // Keep the active pill on top of its neighbours so its full border
            // (incl. the left edge) is never overpainted by the pill to its left.
            if (_filterButtons.TryGetValue(key, out var activePill))
                activePill.BringToFront();
            ApplySearchFilter();
            // "Открытые файлы" is a server-domain action → show only on the
            // "Серверы" tab. "Сообщение всем" is a user-domain action → show
            // only on the "Пользователи" tab.
            _openFilesBtn.Visible = string.Equals(key, Helpers.ComputerGroup.Servers, StringComparison.OrdinalIgnoreCase);
            _messageAllBtn.Visible = string.Equals(key, Helpers.ComputerGroup.Users, StringComparison.OrdinalIgnoreCase);
            _inventoryBtn.Visible = string.IsNullOrEmpty(key);
            Logger.Info($"Filter pill clicked: key='{key}'");
        }

        // === Group badge painting ===

        // RDP Manager session list changed — invalidate the RDP column so
        // the blue dot indicator refreshes for all visible rows, and rebuild
        // the cached session set (paint hits it per RDP cell per repaint).
        private void OnRdpSessionListChanged(object? sender, EventArgs e)
        {
            _rdpSessionsCached = false;
            if (_grid.IsDisposed) return;
            try
            {
                if (_grid.Columns.Contains("Rdp"))
                    _grid.InvalidateColumn(_grid.Columns["Rdp"]!.Index);
            }
            catch { /* best-effort */ }
        }

        // Rebuild the RDP session lookup set. Only sessions of an OPEN
        // manager count — matches the old guard
        // (Forms.RdpManagerForm.IsOpen && HasSessionFor(...)) exactly.
        private void RefreshRdpSessions()
        {
            _rdpSessionsCached = true;
            _rdpActiveSessions.Clear();
            try
            {
                if (Forms.RdpManagerForm.IsOpen)
                {
                    foreach (var c in Forms.RdpManagerForm.Instance.GetSessionComputers())
                        _rdpActiveSessions.Add(c);
                }
            }
            catch { /* best-effort */ }
        }

        // v2.1.77: restored v2.1.62 OnCellPainting. Single handler with
        // e.PaintBackground(CellBounds, false) for special cells. This was
        // the WORKING setup before perf optimization. Removed all my
        // experimental manual FillRectangle + RowPostPaint variants.
        private void OnCellPainting(object? sender, DataGridViewCellPaintingEventArgs e)
        {
            // === Header cells: white bg, dark text, 4px gap ===
            if (e.RowIndex == -1 && e.ColumnIndex >= 0)
            {
                e.Graphics!.FillRectangle(_headerGapBrush, e.CellBounds);
                var inner = new Rectangle(
                    e.CellBounds.X + 2, e.CellBounds.Y + 2,
                    e.CellBounds.Width - 3, e.CellBounds.Height - 4);
                e.Graphics.FillRectangle(_headerBgBrush, inner);
                var headerText = _grid.Columns[e.ColumnIndex].HeaderText;
                var textRect = new Rectangle(
                    inner.X + 12, inner.Y, inner.Width - 12, inner.Height);
                TextRenderer.DrawText(e.Graphics, headerText, _headerFont, textRect,
                    Helpers.UiTheme.Foreground,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.EndEllipsis);
                e.Handled = true;
                return;
            }

            // === Body cells ===
            if (e.ColumnIndex < 0 || e.RowIndex < 0) return;
            var colName = _grid.Columns[e.ColumnIndex].Name;

            // === Ping column: draw status dot over the bitmap icon ===
            if (colName == "Ping")
            {
                e.PaintBackground(e.CellBounds, false);
                var computer = _grid.Rows[e.RowIndex].Cells["Computer"].Value?.ToString() ?? "";
                Color dotColor;
                if (!string.IsNullOrEmpty(computer) &&
                    PingCache.TryGet(computer, out var alive, out _) && alive)
                {
                    dotColor = Helpers.UiTheme.StatusOnline;
                }
                else if (!string.IsNullOrEmpty(computer) &&
                         PingCache.TryGet(computer, out var dead, out _) && !dead)
                {
                    dotColor = Helpers.UiTheme.StatusOffline;
                }
                else
                {
                    dotColor = Helpers.UiTheme.StatusOffline;
                }
                int dotSize = 10;
                var dotRect = new Rectangle(
                    e.CellBounds.X + (e.CellBounds.Width - dotSize) / 2,
                    e.CellBounds.Y + (e.CellBounds.Height - dotSize) / 2,
                    dotSize, dotSize);
                var dotBrush = dotColor == Helpers.UiTheme.StatusOnline
                    ? _pingOnlineBrush : _pingOfflineBrush;
                var gstate2 = e.Graphics!.Save();
                e.Graphics!.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                e.Graphics.FillEllipse(dotBrush, dotRect);
                e.Graphics.Restore(gstate2);
                e.Handled = true;
                return;
            }

            // === RDP column: blue dot when a live session exists ===
            if (colName == "Rdp")
            {
                e.PaintBackground(e.CellBounds, false);
                var computer = _grid.Rows[e.RowIndex].Cells["Computer"].Value?.ToString() ?? "";
                if (!_rdpSessionsCached) RefreshRdpSessions();
                if (!string.IsNullOrEmpty(computer) && _rdpActiveSessions.Contains(computer))
                {
                    int dotSize = 11;
                    var dotRect = new Rectangle(
                        e.CellBounds.X + (e.CellBounds.Width - dotSize) / 2,
                        e.CellBounds.Y + (e.CellBounds.Height - dotSize) / 2,
                        dotSize, dotSize);
                    var gstate3 = e.Graphics!.Save();
                    e.Graphics!.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    e.Graphics.FillEllipse(_rdpDotBrush, dotRect);
                    e.Graphics.Restore(gstate3);
                }
                e.Handled = true;
                return;
            }

            // v4.0 — dashboard monitoring indicator on Computer cell
            if (colName == "Computer")
            {
                var comp = e.Value?.ToString() ?? "";
                if (!string.IsNullOrEmpty(comp) && _monitoredSet.Contains(comp))
                {
                    e.PaintBackground(e.CellBounds, true);
                    var textRect = new Rectangle(e.CellBounds.X + 4, e.CellBounds.Y, e.CellBounds.Width - 18, e.CellBounds.Height);
                    Color fore = e.CellStyle.ForeColor;
                    if ((e.State & DataGridViewElementStates.Selected) != 0) fore = e.CellStyle.SelectionForeColor;
                    if (fore == Color.Empty || fore.A == 0) fore = _grid.DefaultCellStyle.ForeColor;
                    var font = e.CellStyle.Font ?? _grid.DefaultCellStyle.Font ?? new Font("Segoe UI", 9f);
                    TextRenderer.DrawText(e.Graphics, comp, font, textRect, fore, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                    int dotSize = 6;
                    var dotRect = new Rectangle(e.CellBounds.Right - dotSize - 7, e.CellBounds.Y + (e.CellBounds.Height - dotSize) / 2, dotSize, dotSize);
                    var gstate = e.Graphics.Save();
                    e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    using var pen = new Pen(Color.FromArgb(0x48, 0x30, 0x2F), 1.4f);
                    // hollow dot — subtle, less clutter when many rows are monitored
                    e.Graphics.DrawEllipse(pen, dotRect);
                    e.Graphics.Restore(gstate);
                    e.Handled = true;
                    return;
                }
                return;
            }

            if (colName == "NewEvents")
            {
                var txt = e.Value?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(txt) || txt == "0") return;
                e.PaintBackground(e.CellBounds, true);
                int.TryParse(txt, out int cnt);
                Color bg = cnt > 0 ? Color.FromArgb(0x8B, 0x4A, 0x3E) : Helpers.UiTheme.PillActiveBg;
                int w = (e.CellBounds.Width - 16) / 2;
                int h = (e.CellBounds.Height - 12) / 2;
                var rect = new Rectangle(e.CellBounds.X + (e.CellBounds.Width - w) / 2, e.CellBounds.Y + (e.CellBounds.Height - h) / 2, w, h);
                using var path = Helpers.UiButtons.RoundedRect(rect, 6);
                using var brush = new SolidBrush(bg);
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                e.Graphics.FillPath(brush, path);
                TextRenderer.DrawText(e.Graphics, txt, new Font("Segoe UI Semibold", 7f), rect, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                e.Handled = true;
                return;
            }

            // === Group column: colored badge ===
            if (colName != "Group") return;

            e.PaintBackground(e.CellBounds, false);
            var key = e.Value?.ToString() ?? "";
            var info = Helpers.ComputerGroup.ByKey(key);
            var badgeColors = Helpers.ComputerGroup.BadgeFor(key);
            if (info != null && badgeColors != null)
            {
                if (!_badgeGdiCache.TryGetValue(key, out var gdi))
                {
                    gdi = (new SolidBrush(badgeColors.Background), new Pen(badgeColors.Border));
                    _badgeGdiCache[key] = gdi;
                }
                var (bgBrush, borderPen) = gdi;
                // v3.8.x — badge is 10% smaller than the cell-derived box,
                // kept centred within the cell.
                int w = (int)((e.CellBounds.Width - 56) * 0.81f);
                int h = (int)((e.CellBounds.Height - 18) * 0.81f);
                int offX = e.CellBounds.X + (e.CellBounds.Width - w) / 2;
                int offY = e.CellBounds.Y + (e.CellBounds.Height - h) / 2;
                if (!_badgePathCache.TryGetValue((w, h), out var path))
                {
                    path = RoundedRect(new Rectangle(0, 0, w, h), 5);
                    _badgePathCache[(w, h)] = path;
                }
                var state = e.Graphics!.Save();
                e.Graphics.TranslateTransform(offX, offY);
                e.Graphics.FillPath(bgBrush, path);
                e.Graphics.DrawPath(borderPen, path);
                e.Graphics.Restore(state);
                var badge = new Rectangle(offX, offY, w, h);
                TextRenderer.DrawText(e.Graphics, info.BadgeLabel, _groupBadgeFont, badge,
                    badgeColors.Foreground,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            }
            e.Handled = true;
        }

        private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            if (radius <= 0) { path.AddRectangle(r); return path; }
            int d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // === Sort ===

        private void OnColumnHeaderMouseClick(object? sender, DataGridViewCellMouseEventArgs e)
        {
            // v3.8.x — sorting is a left-click action only; right-click must
            // not reorder the grid (it's reserved for context menus).
            if (e.Button == MouseButtons.Right) return;
            var colName = _grid.Columns[e.ColumnIndex].Name;
            if (colName == "Ping") return;  // image column — not sortable
            if (_sortColumn == colName)
            {
                _sortAscending = !_sortAscending;
            }
            else
            {
                _sortColumn = colName;
                _sortAscending = true;
            }
            ReloadGrid();
        }

        // === Group assignment ===

        // Returns the Group key of the currently selected row, or "" if
        // nothing selected. Used by the context menu to mark the current
        // group with a checkmark.
        private string GetSelectedGroup()
        {
            if (_grid.SelectedRows.Count == 0) return "";
            return _grid.SelectedRows[0].Cells["Group"].Value?.ToString() ?? "";
        }

        private System.Collections.Generic.List<DataGridViewRow> GetSelectedBookRows()
        {
            var list = new System.Collections.Generic.List<DataGridViewRow>();
            foreach (DataGridViewRow r in _grid.SelectedRows)
                if (!r.IsNewRow) list.Add(r);
            return list;
        }

        // Assigns a group to every selected row and persists to JSON.
        // Multiple selection is supported — bulk-assign is a common pattern
        // when organizing a long list.
        private void OnAssignGroupClicked(string newGroup)
        {
            AssignGroupRows(GetSelectedBookRows(), newGroup);
        }

        private void AssignGroupRows(System.Collections.Generic.List<DataGridViewRow> rows, string newGroup)
        {
            if (rows == null || rows.Count == 0)
            {
                MessageBox.Show(this, "Выберите хотя бы одну запись.",
                    "Назначить группу", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Persist: load JSON, patch matching entries, save.
            var entries = AddressBookStore.Load();
            int patched = 0;
            foreach (DataGridViewRow row in rows)
            {
                var computer = row.Cells["Computer"].Value?.ToString() ?? "";
                if (string.IsNullOrEmpty(computer)) continue;
                foreach (var e in entries)
                {
                    if (!string.Equals(e.Computer, computer, StringComparison.OrdinalIgnoreCase)) continue;
                    if (e.Group == newGroup) continue;
                    e.Group = newGroup;
                    patched++;
                }
            }
            if (patched > 0)
            {
                AddressBookStore.Save(entries);
                Logger.Info($"Group assignment: {patched} entry(ies) → '{newGroup}'");
            }

            ReloadGrid();
        }
    }
}
