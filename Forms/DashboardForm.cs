using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ShadowRDP.Helpers;
using ShadowRDP.Models;
using ShadowRDP.Services;
using ShadowRDP.Controls;
namespace ShadowRDP.Forms
{
    public class DashboardForm : Form
    {
        private readonly DataGridView _grid = new DoubleBufferedDataGridView();
        private FlowLayoutPanel _hostPanel = new FlowLayoutPanel();
        private Label _status = new Label();
        private Label _title = new Label();
        private ProgressBar _progressBar = new ProgressBar();
        private NyanCatProgressBar _nyanBar = new NyanCatProgressBar();
        private Label _progressLabel = new Label();
        private DateTime _pollStart;
        private FlowLayoutPanel _filterLevel = new FlowLayoutPanel();
        private FlowLayoutPanel _filterPeriod = new FlowLayoutPanel();
        private List<DashboardEvent> _all = new List<DashboardEvent>();
        private string _level = "Все";
        private string _period = "2 дня";
        private string _selectedHost = "";
        private Dictionary<string, Label> _levelPills = new Dictionary<string, Label>();
        private Dictionary<string, Label> _periodPills = new Dictionary<string, Label>();
        private System.Windows.Forms.Timer _pollUiTimer = new System.Windows.Forms.Timer();
        private Label _btnAi = null!;
        private DashboardChartPanel _chartPanel = null!;
        private string _selectedSource = "";

        private static readonly Font FontHeader = new Font("Segoe UI Semibold", 8.5f);
        private static readonly Font FontCell = new Font("Segoe UI", 8.5f);
        private static readonly Color DashHeader = Color.FromArgb(0x62, 0x6F, 0x5E);

        public DashboardForm()
        {
            Text = "Дашбоард событий";
            BackColor = UiTheme.Background;
            FormBorderStyle = FormBorderStyle.None;
            Dock = DockStyle.Fill;

            // Header band — #626F5E per request
            var header = new Panel { Dock = DockStyle.Top, Height = 48, BackColor = DashHeader };
            _title = new Label { Text = "ДАШБОРД СОБЫТИЙ", Font = new Font("Segoe UI Semibold", 11f), ForeColor = Color.White, BackColor = Color.Transparent, AutoSize = true, Location = new Point(16, 14) };
            header.Controls.Add(_title);

            var btnRefresh = UiButtons.MakeCompactFilled("Обновить", 90);
            btnRefresh.Location = new Point(220, 13);
            btnRefresh.Click += async (s, e) => await RefreshNowAsync();
            header.Controls.Add(btnRefresh);

            // AI button — custom Label that reads lbl.Text in Paint
            Color _aiBg = DashHeader;
            _btnAi = new Label
            {
                Text = "Проверка ИИ...",
                AutoSize = false,
                Size = new Size(120, 22),
                Font = new Font("Segoe UI Semibold", 9f),
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand,
                BackColor = Color.Transparent,
                ForeColor = Color.White
            };
            _btnAi.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                var r = new Rectangle(0, 0, _btnAi.Width, _btnAi.Height);
                using var path = UiButtons.RoundedRect(r, 1);
                using var fill = new SolidBrush(_aiBg);
                e.Graphics.FillPath(fill, path);
                TextRenderer.DrawText(e.Graphics, _btnAi.Text, _btnAi.Font, r, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            };
            _btnAi.MouseEnter += (s, e) => { _aiBg = ControlPaint.Dark(_aiBg, 0.08f); _btnAi.Invalidate(); };
            _btnAi.MouseLeave += (s, e) => { _aiBg = _btnAi.BackColor != Color.Transparent ? _btnAi.BackColor : DashHeader; _btnAi.Invalidate(); };
            _btnAi.Location = new Point(btnRefresh.Right + 8, 13);
            _btnAi.Click += (s, e) => OllamaInstaller.ShowInstallGuide(this);
            header.Controls.Add(_btnAi);

            // Fire-and-forget availability check
            Task.Run(async () =>
            {
                var ok = await EventDecoderService.IsOllamaAvailableAsync();
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (ok)
                        {
                            _btnAi.Text = "ИИ Работает";
                            _aiBg = UiTheme.StatusOnline;
                            _btnAi.BackColor = UiTheme.StatusOnline;
                            _btnAi.Invalidate();
                            _btnAi.Click -= (s2, e2) => OllamaInstaller.ShowInstallGuide(this);
                            _btnAi.Click += (s2, e2) => MessageBox.Show(this, "Ollama доступен. Модель llama3.2 готова к расшифровке событий.", "ИИ", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                        else
                        {
                            _btnAi.Text = "ИИ Недоступен";
                            _aiBg = UiTheme.Danger;
                            _btnAi.BackColor = UiTheme.Danger;
                            _btnAi.Invalidate();
                        }
                    }));
                }
                catch { }
            });

            var btnSettings = UiButtons.MakeCompactFilled("Настройки", 90);
            btnSettings.Location = new Point(_btnAi.Right + 8, 13);
            btnSettings.Click += (s, e) => ShowSettings();
            header.Controls.Add(btnSettings);

            Controls.Add(header);

            // Filter pills area
            var filterPanel = new Panel { Dock = DockStyle.Top, Height = 56, BackColor = UiTheme.Card, Padding = new Padding(12, 8, 12, 8) };
            _filterLevel = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.Transparent };
            foreach (var lv in new[] { "Все", "Critical", "Error" })
            {
                var pill = CreatePill(lv, lv == _level);
                pill.Click += (s, e) => { _level = lv; UpdatePills(); ApplyFilters(); };
                _levelPills[lv] = pill;
                _filterLevel.Controls.Add(pill);
            }
            filterPanel.Controls.Add(_filterLevel);

            _filterPeriod = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.Transparent };
            foreach (var p in new[] { "30 мин", "1 час", "24 часа", "2 дня" })
            {
                var pill = CreatePill(p, p == _period);
                pill.Click += (s, e) => { _period = p; UpdatePills(); ApplyFilters(); };
                _periodPills[p] = pill;
                _filterPeriod.Controls.Add(pill);
            }
            filterPanel.Controls.Add(_filterPeriod);
            Controls.Add(filterPanel);

            // Host bar
            _hostPanel = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, BackColor = UiTheme.Background, Padding = new Padding(12, 6, 12, 6), AutoSize = false };
            Controls.Add(_hostPanel);

            var sep = new Panel { Dock = DockStyle.Top, Height = 4, BackColor = UiTheme.BorderSubtle };
            Controls.Add(sep);

            // Charts — before grid (все графики, интерактив, до таблицы)
            _chartPanel = new DashboardChartPanel();
            _chartPanel.OnChartClick += (kind, key) =>
            {
                if (kind == "Level") { _level = string.Equals(_level, key, StringComparison.OrdinalIgnoreCase) ? "Все" : key; UpdatePills(); ApplyFilters(); }
                else if (kind == "Host") { _selectedHost = string.Equals(_selectedHost, key, StringComparison.OrdinalIgnoreCase) ? "" : key; RebuildHostBar(); ApplyFilters(); }
                else if (kind == "Source") { _selectedSource = string.Equals(_selectedSource, key, StringComparison.OrdinalIgnoreCase) ? "" : key; ApplyFilters(); }
                else if (kind == "Time") { _status.Text = $"Выбран интервал: {key}"; }
            };
            Controls.Add(_chartPanel);

            // Grid — double-buffered (DoubleBufferedDataGridView) for smooth
            // scrolling, matching the "Открытые файлы" grid. Cell painting is
            // only custom for the Level badge; everything else uses the
            // standard renderer so double-buffering stays artifact-free.
            _grid.Dock = DockStyle.Fill;
            _grid.BackgroundColor = UiTheme.Card;
            _grid.BorderStyle = BorderStyle.None;
            _grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            _grid.AdvancedCellBorderStyle.All = DataGridViewAdvancedCellBorderStyle.None;
            _grid.AdvancedCellBorderStyle.Top = DataGridViewAdvancedCellBorderStyle.None;
            _grid.AdvancedCellBorderStyle.Bottom = DataGridViewAdvancedCellBorderStyle.None;
            _grid.AdvancedCellBorderStyle.Left = DataGridViewAdvancedCellBorderStyle.None;
            _grid.AdvancedCellBorderStyle.Right = DataGridViewAdvancedCellBorderStyle.None;
            _grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            _grid.AdvancedColumnHeadersBorderStyle.All = DataGridViewAdvancedCellBorderStyle.None;
            _grid.AdvancedColumnHeadersBorderStyle.Bottom = DataGridViewAdvancedCellBorderStyle.None;
            _grid.RowHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            _grid.GridColor = UiTheme.BorderSubtle;
            _grid.RowHeadersVisible = false;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.AllowUserToResizeRows = false;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _grid.ReadOnly = true;
            _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
            _grid.RowTemplate.Height = 36;
            _grid.ColumnHeadersHeight = 28;
            _grid.EnableHeadersVisualStyles = false;
            _grid.ColumnHeadersDefaultCellStyle.BackColor = UiTheme.HeaderBar;
            _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
            _grid.ColumnHeadersDefaultCellStyle.Font = FontHeader;
            _grid.DefaultCellStyle.Font = FontCell;
            _grid.DefaultCellStyle.BackColor = UiTheme.Card;
            _grid.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
            _grid.DefaultCellStyle.Padding = new Padding(6, 0, 6, 0);
            _grid.DefaultCellStyle.WrapMode = DataGridViewTriState.False;
            _grid.DefaultCellStyle.SelectionBackColor = UiTheme.AccentSoft;
            _grid.DefaultCellStyle.SelectionForeColor = UiTheme.Foreground;
            _grid.AlternatingRowsDefaultCellStyle.BackColor = UiTheme.RowAlt;
            _grid.AlternatingRowsDefaultCellStyle.SelectionBackColor = UiTheme.AccentSoft;
            _grid.AlternatingRowsDefaultCellStyle.SelectionForeColor = UiTheme.Foreground;
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Time", HeaderText = "Время", Width = 130, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 6, 0) } });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Level", HeaderText = "Уровень", Width = 80, MinimumWidth = 80, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleCenter, Padding = Padding.Empty } });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Source", HeaderText = "Источник", Width = 120, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 6, 0) } });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "EventId", HeaderText = "ID", Width = 60, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 6, 0) } });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Computer", HeaderText = "Хост", Width = 110, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 6, 0) } });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Message", HeaderText = "Сообщение", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 6, 0) } });

            _grid.CellPainting += OnCellPainting;
            _grid.CellFormatting += OnCellFormatting;
            _grid.CellMouseDown += OnCellMouseDown;
            _grid.CellDoubleClick += OnCellDoubleClick;
            _grid.ColumnWidthChanged += (s, e) => ScheduleColumnWidthSave();

            // Status bar with NyanCat progress — подкручен: толще + текст процента/ETA (зеленый бар только для опроса, Nyan только для ИИ)
            var statusPanel = new Panel { Dock = DockStyle.Bottom, Height = 62, BackColor = UiTheme.Card };
            _progressBar = new ProgressBar { Dock = DockStyle.Top, Height = 8, Style = ProgressBarStyle.Continuous, Visible = false };
            _progressBar.HandleCreated += (s, e) => SetProgressBarColor(_progressBar.Handle, Color.FromArgb(0x10, 0xB9, 0x81));
            _nyanBar.Dock = DockStyle.Top;
            _nyanBar.Height = 36;
            _nyanBar.Visible = false;
            _progressLabel = new Label { Dock = DockStyle.Top, Height = 14, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI Semibold", 7.5f), ForeColor = Color.FromArgb(0x10, 0xB9, 0x81), BackColor = UiTheme.Card, Visible = false, Padding = new Padding(0) };
            _status = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI", 8f), ForeColor = UiTheme.MutedForeground, Padding = new Padding(12, 2, 0, 0) };
            statusPanel.Controls.Add(_status);
            statusPanel.Controls.Add(_progressLabel);
            statusPanel.Controls.Add(_nyanBar);
            statusPanel.Controls.Add(_progressBar);
            Controls.Add(statusPanel);

            Controls.Add(_grid);

            Controls.SetChildIndex(header, 6);
            Controls.SetChildIndex(filterPanel, 5);
            Controls.SetChildIndex(_hostPanel, 4);
            Controls.SetChildIndex(sep, 3);
            Controls.SetChildIndex(_chartPanel, 2);
            Controls.SetChildIndex(_grid, 1);
            Controls.SetChildIndex(statusPanel, 0);

            Load += async (s, e) => { await LoadEventsAsync(); EventPollerService.OnNewEvents += OnNewEvents; EventPollerService.OnCritical += OnCritical; EventPollerService.OnProgress += OnProgress; };
            FormClosing += (s, e) => SaveColumnWidths();
            FormClosed += (s, e) => { EventPollerService.OnNewEvents -= OnNewEvents; EventPollerService.OnCritical -= OnCritical; EventPollerService.OnProgress -= OnProgress; };

            LoadColumnWidths();
            _colWidthDebounce.Tick += (s, e) =>
            {
                _colWidthDebounce.Stop();
                if (IsDisposed) return;
                SaveColumnWidths();
            };
        }

        private const string ColumnWidthsFormKey = "dashboard";
        private readonly System.Windows.Forms.Timer _colWidthDebounce = new System.Windows.Forms.Timer { Interval = 400 };

        private void ScheduleColumnWidthSave()
        {
            if (_colWidthDebounce.Enabled) _colWidthDebounce.Stop();
            _colWidthDebounce.Start();
        }

        private void LoadColumnWidths()
        {
            try
            {
                var path = AppPaths.ColumnWidthsPath;
                if (!System.IO.File.Exists(path)) return;
                var json = System.IO.File.ReadAllText(path);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty(ColumnWidthsFormKey, out var formEntry)) return;
                foreach (var prop in formEntry.EnumerateObject())
                {
                    if (!_grid.Columns.Contains(prop.Name)) continue;
                    int w = prop.Value.GetInt32();
                    if (w < 40) continue;
                    _grid.Columns[prop.Name]!.Width = w;
                }
            }
            catch { }
        }

        private void SaveColumnWidths()
        {
            try
            {
                Dictionary<string, Dictionary<string, int>> data;
                var path = AppPaths.ColumnWidthsPath;
                if (System.IO.File.Exists(path))
                {
                    var existing = System.IO.File.ReadAllText(path);
                    data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(existing)
                        ?? new Dictionary<string, Dictionary<string, int>>();
                }
                else
                {
                    data = new Dictionary<string, Dictionary<string, int>>();
                }
                var perForm = new Dictionary<string, int>();
                foreach (DataGridViewColumn c in _grid.Columns)
                {
                    perForm[c.Name] = c.Width;
                }
                data[ColumnWidthsFormKey] = perForm;

                var opts = new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(data, opts));
            }
            catch { }
        }

        private void OnProgress(string host, int current, int total)
        {
            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (string.IsNullOrEmpty(host))
                    {
                        _title.Text = "ДАШБОРД СОБЫТИЙ";
                        _progressBar.Visible = false;
                        _progressBar.Value = 0;
                        _progressLabel.Visible = false;
                        _progressLabel.Text = "";
                        UpdateStatus();
                        return;
                    }
                    if (current == 1) _pollStart = DateTime.Now;
                    int pct = total > 0 ? (int)Math.Round((double)current / total * 100) : 0;
                    var elapsed = DateTime.Now - _pollStart;
                    string eta = "";
                    if (current > 0 && current < total && elapsed.TotalSeconds > 1)
                    {
                        double avg = elapsed.TotalSeconds / current;
                        double remain = avg * (total - current);
                        if (remain < 60) eta = $" • осталось ~{Math.Max(1, (int)Math.Round(remain))}с";
                        else eta = $" • осталось ~{Math.Round(remain / 60)}м {Math.Round(remain % 60)}с";
                    }
                    else if (current == total) eta = " • готово";
                    _title.Text = $"ДАШБОРД — {host} ({current}/{total} • {pct}%)";
                    _status.Text = $"Опрос {current} из {total} ({pct}%){eta}: {host}";
                    _progressLabel.Text = $"{current} / {total}  •  {pct}%{eta}  •  {host}";
                    _progressLabel.Visible = true;
                    // Зеленый бар — только для опроса, Nyan не трогаем (только для ИИ)
                    _progressBar.Visible = true;
                    _progressBar.Maximum = total;
                    _progressBar.Value = Math.Clamp(current, 0, total);
                }));
            }
            catch { }
        }

        private void UpdatePills()
        {
            foreach (var kv in _levelPills) ApplyPillStyle(kv.Value, kv.Key == _level);
            foreach (var kv in _periodPills) ApplyPillStyle(kv.Value, kv.Key == _period);
        }

        private Label CreatePill(string text, bool active)
        {
            var lbl = new Label { Text = text, AutoSize = true, Font = active ? new Font("Segoe UI Semibold", 9f) : new Font("Segoe UI", 9f), TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand, Padding = new Padding(10, 6, 10, 6), Margin = new Padding(4, 0, 4, 0) };
            ApplyPillStyle(lbl, active);
            lbl.Paint += (s, e) =>
            {
                if (lbl.BackColor == Color.Transparent) return;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using var path = UiButtons.RoundedRect(new Rectangle(0, 0, lbl.Width - 1, lbl.Height - 1), 8);
                using var brush = new SolidBrush(lbl.BackColor);
                e.Graphics.FillPath(brush, path);
                TextRenderer.DrawText(e.Graphics, lbl.Text, lbl.Font, lbl.ClientRectangle, lbl.ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            };
            return lbl;
        }

        private void ApplyPillStyle(Label lbl, bool active)
        {
            if (active) { lbl.BackColor = UiTheme.PillActiveBg; lbl.ForeColor = UiTheme.PillActiveFg; lbl.Font = new Font("Segoe UI Semibold", 9f); }
            else { lbl.BackColor = Color.Transparent; lbl.ForeColor = UiTheme.MutedForeground; lbl.Font = new Font("Segoe UI", 9f); }
            lbl.Invalidate();
        }

        private async Task LoadEventsAsync()
        {
            _all = EventDashboardStore.LoadEvents();
            RebuildHostBar();
            ApplyFilters();
            UpdateStatus();
            if (_all.Count == 0 && EventDashboardStore.GetMonitoredComputers().Count > 0)
                await RefreshNowAsync();
        }

        private void RebuildHostBar()
        {
            _hostPanel.Controls.Clear();
            var hosts = EventDashboardStore.GetMonitoredComputers();
            if (hosts.Count == 0)
            {
                var lbl = new Label { Text = "Нет отслеживаемых хостов — добавьте в Книге ПКМ → Отслеживать события", AutoSize = true, ForeColor = UiTheme.MutedForeground, Font = new Font("Segoe UI", 8.5f) };
                _hostPanel.Controls.Add(lbl);
                return;
            }
            var counts = _all.GroupBy(e => e.Computer, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            var critCounts = _all.Where(e => e.Level == "Critical").GroupBy(e => e.Computer, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            foreach (var h in hosts)
            {
                int total = counts.TryGetValue(h, out var c) ? c : 0;
                int crit = critCounts.TryGetValue(h, out var cc) ? cc : 0;
                bool hasCrit = crit > 0;
                bool hasAny = total > 0;
                bool isActive = string.Equals(_selectedHost, h, StringComparison.OrdinalIgnoreCase);

                var tab = new Label
                {
                    Text = "",
                    AutoSize = false,
                    Size = new Size(130, 24),
                    Font = new Font("Segoe UI Semibold", 8.5f),
                    TextAlign = ContentAlignment.MiddleCenter,
                    Cursor = Cursors.Hand,
                    Margin = new Padding(0, 0, 2, 0),
                    BackColor = Color.Transparent,
                    ForeColor = Color.White
                };
                Color baseBg = hasCrit ? Color.FromArgb(0xB0, 0x7A, 0x72) : hasAny ? Color.FromArgb(0xB8, 0x9A, 0x7A) : UiTheme.Card;
                Color tabBg = isActive ? ControlPaint.Dark(baseBg, 0.15f) : baseBg;
                Color tabFg = hasAny ? Color.White : UiTheme.Foreground;
                Color tabBorder = isActive ? Color.FromArgb(0x48, 0x30, 0x2F) : hasAny ? tabBg : UiTheme.BorderSubtle;
                string tabText = total > 0 ? $"{h}  • {total}" : h;
                tab.Text = tabText;
                tab.ForeColor = tabFg;
                int borderW = isActive ? 2 : 1;
                tab.Paint += (s, e) =>
                {
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    var r = new Rectangle(0, 0, tab.Width - 1, tab.Height - 1);
                    using var path = UiButtons.TabRect(r, 6);
                    using var brush = new SolidBrush(tabBg);
                    using var pen = new Pen(tabBorder, borderW);
                    e.Graphics.FillPath(brush, path);
                    e.Graphics.DrawPath(pen, path);
                    TextRenderer.DrawText(e.Graphics, tabText, tab.Font, r, tabFg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                };
                string captured = h;
                tab.Click += (s, e) =>
                {
                    _selectedHost = string.Equals(_selectedHost, captured, StringComparison.OrdinalIgnoreCase) ? "" : captured;
                    RebuildHostBar();
                    ApplyFilters();
                };
                tab.DoubleClick += (s, e) =>
                {
                    _selectedHost = "";
                    RebuildHostBar();
                    ApplyFilters();
                };
                _hostPanel.Controls.Add(tab);
            }
            if (_all.Count == 0)
            {
                var hint = new Label { Text = "  • Событий пока нет — нажмите «Обновить» или проверьте WMI-доступ к хостам", AutoSize = true, ForeColor = UiTheme.MutedForeground, Font = new Font("Segoe UI", 8f), Margin = new Padding(12, 0, 0, 0) };
                _hostPanel.Controls.Add(hint);
            }
        }

        private void ApplyFilters()
        {
            var now = DateTime.Now;
            TimeSpan periodSpan = TimeSpan.FromDays(2);
            if (_period == "30 мин") periodSpan = TimeSpan.FromMinutes(30);
            else if (_period == "1 час") periodSpan = TimeSpan.FromHours(1);
            else if (_period == "24 часа") periodSpan = TimeSpan.FromHours(24);
            var cutoff = now - periodSpan;

            var filtered = _all.Where(e => e.Time >= cutoff).Where(e => _level == "Все" || string.Equals(e.Level, _level, StringComparison.OrdinalIgnoreCase)).Where(e => string.IsNullOrEmpty(_selectedHost) || string.Equals(e.Computer, _selectedHost, StringComparison.OrdinalIgnoreCase)).Where(e => string.IsNullOrEmpty(_selectedSource) || string.Equals(e.Source, _selectedSource, StringComparison.OrdinalIgnoreCase)).OrderByDescending(e => e.Time).ToList();

            _grid.SuspendLayout();
            _grid.Rows.Clear();
            foreach (var ev in filtered.Take(500))
            {
                int idx = _grid.Rows.Add(ev.Time.ToString("dd.MM HH:mm"), ev.Level, ev.Source, ev.EventId.ToString(), ev.Computer, ev.Message);
                _grid.Rows[idx].Tag = ev;
            }
            _grid.ResumeLayout();
            if (_grid.Rows.Count > 0)
            {
                try { _grid.FirstDisplayedScrollingRowIndex = 0; _grid.ClearSelection(); } catch { }
            }
            UpdateStatus(filtered.Count);
            try
            {
                var stats = DashboardStats.From(filtered, _period);
                _chartPanel?.Update(stats);
            }
            catch { }
        }

        private void UpdateStatus(int shown = -1)
        {
            var total = _all.Count;
            var crit = _all.Count(e => e.Level == "Critical");
            var err = _all.Count(e => e.Level == "Error");
            var hosts = EventDashboardStore.GetMonitoredComputers().Count;
            var shownStr = shown >= 0 ? $"Показано: {shown} | " : "";
            var settings = EventDashboardStore.LoadSettings();
            _status.Text = $"{shownStr}Всего: {total} | Critical: {crit} | Error: {err} | Хостов: {hosts} | Обновлено: {DateTime.Now:HH:mm} | Следующее: {DateTime.Now.AddMinutes(settings.PollIntervalMinutes):HH:mm}";
        }

        private async Task RefreshNowAsync()
        {
            _pollStart = DateTime.Now;
            _title.Text = "ДАШБОРД — обновление...";
            var hosts = EventDashboardStore.GetMonitoredComputers();
            int total = hosts.Count;
            // Зеленый бар — только для опроса (Nyan не трогаем, только для ИИ)
            _progressBar.Style = ProgressBarStyle.Continuous;
            _progressBar.Maximum = Math.Max(1, total);
            _progressBar.Value = 0;
            _progressBar.Visible = true;
            _progressLabel.Text = total > 0 ? $"0 / {total}  •  0%  •  старт..." : "Опрос...";
            _progressLabel.Visible = true;
            _status.Text = total > 0 ? $"Опрос 0 из {total} (0%) — старт..." : "Опрос хостов...";
            await EventPollerService.PollOnceAsync();
            _progressBar.Visible = false;
            _progressLabel.Visible = false;
            _all = EventDashboardStore.LoadEvents();
            ApplyFilters();
            RebuildHostBar();
            _title.Text = "ДАШБОРД СОБЫТИЙ";
            UpdateStatus();
        }

        private void ShowSettings()
        {
            using var dlg = new Form { Text = "Настройки дашборда", Size = new Size(380, 320), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, BackColor = UiTheme.Card };
            var settings = EventDashboardStore.LoadSettings();
            var chkCrit = new CheckBox { Text = "Уведомлять о Critical", Checked = settings.NotifyCritical, Location = new Point(16, 20), AutoSize = true, ForeColor = UiTheme.Foreground };
            var chkErr = new CheckBox { Text = "Уведомлять о Error", Checked = settings.NotifyError, Location = new Point(16, 50), AutoSize = true, ForeColor = UiTheme.Foreground };
            var chkSound = new CheckBox { Text = "Звук", Checked = settings.NotifySound, Location = new Point(16, 80), AutoSize = true, ForeColor = UiTheme.Foreground };
            var chkNyan = new CheckBox { Text = "Прогресс-бар Nyan Cat", Checked = settings.ShowNyanCat, Location = new Point(16, 110), AutoSize = true, ForeColor = UiTheme.Foreground };
            var lblInterval = new Label { Text = "Интервал (мин):", Location = new Point(16, 145), AutoSize = true, ForeColor = UiTheme.Foreground };
            var numInterval = new NumericUpDown { Value = settings.PollIntervalMinutes, Minimum = 5, Maximum = 240, Location = new Point(130, 143), Width = 60 };
            var lblRet = new Label { Text = "Хранение (дн):", Location = new Point(16, 175), AutoSize = true, ForeColor = UiTheme.Foreground };
            var numRet = new NumericUpDown { Value = settings.RetentionDays, Minimum = 1, Maximum = 7, Location = new Point(130, 173), Width = 60 };

            var btnOk = UiButtons.MakeCompactFilled("Сохранить", 100);
            btnOk.Location = new Point(16, 210);
            btnOk.Click += (s, e) => { dlg.DialogResult = DialogResult.OK; dlg.Close(); };

            var btnCancel = UiButtons.MakeCompactFilled("Отмена", 80, bgOverride: UiTheme.Muted);
            btnCancel.Location = new Point(126, 210);
            btnCancel.Click += (s, e) => { dlg.DialogResult = DialogResult.Cancel; dlg.Close(); };

            dlg.Controls.AddRange(new Control[] { chkCrit, chkErr, chkSound, chkNyan, lblInterval, numInterval, lblRet, numRet, btnOk, btnCancel });
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                settings.NotifyCritical = chkCrit.Checked;
                settings.NotifyError = chkErr.Checked;
                settings.NotifySound = chkSound.Checked;
                settings.ShowNyanCat = chkNyan.Checked;
                settings.PollIntervalMinutes = (int)numInterval.Value;
                settings.RetentionDays = (int)numRet.Value;
                EventDashboardStore.SaveSettings(settings);
                EventPollerService.Restart();
            }
        }

        // === Context menu: ПКМ ===
        private void OnCellMouseDown(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right || e.RowIndex < 0) return;
            _grid.ClearSelection();
            _grid.Rows[e.RowIndex].Selected = true;
            _grid.CurrentCell = _grid.Rows[e.RowIndex].Cells[0];

            var ev = _grid.Rows[e.RowIndex].Tag as DashboardEvent;
            if (ev == null) return;

            var menu = new ThemedContextMenu();
            menu.AddItem("Спросить ИИ", "globe", async () => await AskAiAsync(ev));
            menu.AddItem("Найти в Google", "search", () => OpenGoogleSearch(ev));
            menu.Show(this, Cursor.Position);
        }

        private void OpenGoogleSearch(DashboardEvent ev)
        {
            var query = Uri.EscapeDataString($"Windows EventID {ev.EventId} {ev.Source} {ev.Message}");
            Process.Start(new ProcessStartInfo($"https://www.google.com/search?q={query}") { UseShellExecute = true });
        }

        private async Task AskAiAsync(DashboardEvent ev)
        {
            bool useNyan = EventDashboardStore.LoadSettings().ShowNyanCat;
            if (useNyan) _nyanBar.Start(); else ShowMarqueeBar();
            _status.Text = "ИИ думает...";
            try
            {
                string decoded = await EventDecoderService.DecodeAsync(ev);
                if (useNyan) _nyanBar.FadeOut(); else HideMarqueeBar();
                await Task.Delay(250);
                using var dlg = new AiAnswerForm(ev.EventId, ev.Source, ev.Level, ev.Computer, decoded);
                dlg.ShowDialog(this);
            }
            catch (Exception ex)
            {
                if (useNyan) _nyanBar.FadeOut(); else HideMarqueeBar();
                await Task.Delay(250);
                using var dlg = new AiAnswerForm(ev.EventId, ev.Source, ev.Level, ev.Computer, $"Ошибка ИИ: {ex.Message}");
                dlg.ShowDialog(this);
            }
            finally
            {
                if (useNyan) _nyanBar.Stop(); else HideMarqueeBar();
                UpdateStatus();
            }
        }

        private void ShowMarqueeBar()
        {
            _progressBar.Visible = true;
            _progressBar.Style = ProgressBarStyle.Marquee;
            _progressBar.Invalidate();
        }

        private void HideMarqueeBar()
        {
            _progressBar.Visible = false;
            _progressBar.Style = ProgressBarStyle.Continuous;
            _progressBar.Value = 0;
        }

        // === Double-click: EventLog with server ===
        private async void OnCellDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;
            var ev = _grid.Rows[e.RowIndex].Tag as DashboardEvent;
            if (ev == null) return;

            _status.Text = "Загрузка события...";
            string detail = "";
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                detail = await Task.Run(() => ReadEventFromServer(ev), cts.Token);
            }
            catch { }
            if (string.IsNullOrWhiteSpace(detail)) detail = ev.Message;
            _status.Text = "";

            using var dlg = new EventDetailForm(ev.EventId, ev.Source, ev.Level, ev.Computer, ev.Message, detail);
            dlg.ShowDialog(this);
        }

        private string ReadEventFromServer(DashboardEvent ev)
        {
            try
            {
                var computer = ev.Computer;
                bool isLocal = string.Equals(computer, ".", StringComparison.OrdinalIgnoreCase) || string.Equals(computer, "localhost", StringComparison.OrdinalIgnoreCase) || string.Equals(computer, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
                EventLogSession session = isLocal ? new EventLogSession() : new EventLogSession(computer);
                string logName = string.IsNullOrEmpty(ev.LogName) ? "System" : ev.LogName;
                string xpath = $"*[System[EventID={ev.EventId}]]";
                var query = new EventLogQuery(logName, PathType.LogName, xpath) { Session = session, TolerateQueryErrors = true };
                using var reader = new EventLogReader(query);
                for (EventRecord rec = reader.ReadEvent(); rec != null; rec = reader.ReadEvent())
                {
                    try
                    {
                        if (rec.TimeCreated.HasValue && Math.Abs((rec.TimeCreated.Value - ev.Time).TotalMinutes) < 5)
                        {
                            string msg = "";
                            try { msg = rec.FormatDescription() ?? ""; } catch { }
                            if (string.IsNullOrWhiteSpace(msg)) msg = rec.ToString() ?? "";
                            return msg;
                        }
                    }
                    finally { rec.Dispose(); }
                }
            }
            catch { }
            return "";
        }

        private void OnCellPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            // Only the header is drawn custom (dark band + sort glyph). All
            // body cells use the native renderer so double-buffering (and thus
            // smooth scrolling) works cleanly, exactly like "Открытые файлы".
            if (e.RowIndex == -1 && e.ColumnIndex >= 0)
            {
                e.Handled = true;
                using var bgBrush = new SolidBrush(UiTheme.HeaderBar);
                e.Graphics.FillRectangle(bgBrush, e.CellBounds);
                var col = _grid.Columns[e.ColumnIndex];
                var headerText = col.HeaderText;
                int glyphSpace = col.HeaderCell.SortGlyphDirection != SortOrder.None ? 16 : 0;
                int textX = e.CellBounds.X + 8;
                int textWAvail = e.CellBounds.Width - 8 - glyphSpace;
                var textRect = new Rectangle(textX, e.CellBounds.Y, textWAvail, e.CellBounds.Height);
                TextRenderer.DrawText(e.Graphics, headerText, FontHeader, textRect, Color.White, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                if (col.HeaderCell.SortGlyphDirection != SortOrder.None)
                {
                    int gx = e.CellBounds.Right - 18;
                    int gy = e.CellBounds.Y + (e.CellBounds.Height - 8) / 2;
                    var glyphColor = Color.FromArgb(0x62, 0x6F, 0x5E);
                    using var glyphBrush = new SolidBrush(glyphColor);
                    if (col.HeaderCell.SortGlyphDirection == SortOrder.Ascending)
                    {
                        var pts = new[] { new Point(gx + 4, gy), new Point(gx + 8, gy + 6), new Point(gx, gy + 6) };
                        e.Graphics.FillPolygon(glyphBrush, pts);
                    }
                    else
                    {
                        var pts = new[] { new Point(gx, gy), new Point(gx + 8, gy), new Point(gx + 4, gy + 6) };
                        e.Graphics.FillPolygon(glyphBrush, pts);
                    }
                }
            }
        }

        private void OnCellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex != _grid.Columns["Level"].Index) return;
            var lvl = e.Value?.ToString() ?? "";
            e.CellStyle.ForeColor = lvl == "Critical" ? Color.FromArgb(0xB0, 0x7A, 0x72)
                : lvl == "Error" ? Color.FromArgb(0xE0, 0x5A, 0x3D)
                : UiTheme.Foreground;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        private static void SetProgressBarColor(IntPtr hWnd, Color color)
        {
            const int PBM_SETBARCOLOR = 0x0409;
            SendMessage(hWnd, PBM_SETBARCOLOR, IntPtr.Zero, (IntPtr)ColorTranslator.ToWin32(color));
        }

        private void OnNewEvents(List<DashboardEvent> list) => BeginInvoke(new Action(() => { _all = EventDashboardStore.LoadEvents(); ApplyFilters(); }));
        private void OnCritical(DashboardEvent ev)
        {
            try
            {
                var settings = EventDashboardStore.LoadSettings();
                if (!settings.NotifyCritical && ev.Level == "Critical") return;
                if (!settings.NotifyError && ev.Level == "Error") return;
                var all = EventDashboardStore.LoadEvents();
                var idx = all.FindIndex(x => x.Computer == ev.Computer && x.EventId == ev.EventId && x.Time == ev.Time);
                if (idx >= 0 && !all[idx].Notified) { all[idx].Notified = true; EventDashboardStore.SaveEvents(all); }
            }
            catch { }
        }
    }
}
