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
        private Panel _filterPanelW = null!;
        private string _selectedSource = "";
        // v4.9.21 — кастомный диапазон периода + группировка + последние
        // показанные строки (для экспорта CSV).
        private DateTime _customFrom = DateTime.Now.AddDays(-2);
        private DateTime _customTo = DateTime.Now;
        private bool _grouped = false;
        private List<string[]> _lastRows = new List<string[]>();
        private List<DashboardEvent> _lastTags = new List<DashboardEvent>();
        // v4.8 — шапка вынесена в поле: режим «только контент» для встраивания
        // вьюхой Events единого дашборда (шапка прячется, остаётся фильтр+грид).
        private Panel _headerW = null!;

        // v4.8 — подготовить как контент вьюхи Events: убрать свою шапку
        // (единая шапка у дашборда), оставить пилы/хосты/грид/статус.
        // v4.9.21 — фильтры живут в hero единой шапки: свою панель фильтров
        // тоже прячем во встроенном режиме.
        public void PrepareForEmbeddedEvents()
        {
            try { if (_headerW != null && !_headerW.IsDisposed) _headerW.Visible = false; } catch { }
            try { if (_filterPanelW != null && !_filterPanelW.IsDisposed) _filterPanelW.Visible = false; } catch { }
        }

        private static readonly Font FontHeader = new Font("Segoe UI Semibold", 8.5f);
        private static readonly Font FontCell = new Font("Segoe UI", 8.5f);
        // v3.9.3BG — hero #48302F. v3.9.3BH — hero #36391E.
        private static readonly Color DashHeader = Color.FromArgb(0x36, 0x39, 0x1E);
        // v3.9.3H — активный хост: пил #4A6D8C + белый шрифт.
        // v3.9.3J — ВСЕ пилы #4A6D8C + белый шрифт; активная темнее.
        // v3.9.3L — пилы по CSS пользователя: фон #F5F1E8, шрифт чёрный,
        // рамка белая 4px; активная #3D9C95 + белый шрифт.
        // v3.9.3CA — синяя тема: стальные пилы переключаются
        // (Olive — беж как было, Blue — стальные синие аналоги).
        private static bool IsBlue => Helpers.UiTheme.CurrentName != "Olive";
        private static Color SteelPillBg => IsBlue ? Color.FromArgb(0xE8, 0xEE, 0xF5) : Color.FromArgb(0xF5, 0xF1, 0xE8);
        private static Color SteelPillActive => IsBlue ? Color.FromArgb(0xB9, 0xCD, 0xE0) : Color.FromArgb(0xCF, 0xCF, 0xCF);
        private static Color SteelPillHover => IsBlue ? Color.FromArgb(0xD8, 0xE2, 0xEE) : Color.FromArgb(0xE9, 0xE2, 0xD2);
        private static readonly Color SteelPillBorder = Color.FromArgb(0xFF, 0xFF, 0xFF);
        private static readonly Color SteelPillText = Color.FromArgb(0x00, 0x00, 0x00);

        public DashboardForm()
        {
            Text = "Дашбоард событий";
            BackColor = UiTheme.Background;
            FormBorderStyle = FormBorderStyle.None;
            Dock = DockStyle.Fill;

            // Header band — цвет hero выбирается пользователем (v3.9.3BH).
            var header = new Panel { Dock = DockStyle.Top, Height = 48, BackColor = Helpers.UiTheme.HeroBg };
            _headerW = header;
            _title = new Label { Text = "ДАШБОРД СОБЫТИЙ", Font = new Font("Segoe UI Semibold", 11f), ForeColor = Helpers.UiTheme.HeroFg, BackColor = Color.Transparent, AutoSize = true, Location = new Point(16, 14) };
            header.Controls.Add(_title);
            // v3.9.3CY — выбор цвета hero перенесён в «Инструменты → Настройки цвета».
            Helpers.UiTheme.HeroColorChanged += RefreshHeroColors;
            header.Disposed += (s, e) => { try { Helpers.UiTheme.HeroColorChanged -= RefreshHeroColors; } catch { } };
            void RefreshHeroColors()
            {
                try
                {
                    if (header.IsDisposed) return;
                    header.BackColor = Helpers.UiTheme.HeroBg;
                    _title.ForeColor = Helpers.UiTheme.HeroFg;
                    header.Invalidate(true);
                }
                catch { }
            }

            var btnRefresh = UiButtons.MakeCompactFilled("Обновить", 90);
            btnRefresh.Location = new Point(220, 13);
            btnRefresh.Click += async (s, e) => await RefreshNowAsync();
            header.Controls.Add(btnRefresh);

            // AI button — custom Label that reads lbl.Text in Paint
            Color _aiBg = Helpers.UiTheme.HeroBg;
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
        using var path = UiButtons.RoundedRect(r, 5);   // v4.9.4: 1 → 5
        using var fill = new SolidBrush(_aiBg);
        e.Graphics.FillPath(fill, path);
                TextRenderer.DrawText(e.Graphics, _btnAi.Text, _btnAi.Font, r, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            };
            _btnAi.MouseEnter += (s, e) => { _aiBg = ControlPaint.Dark(_aiBg, 0.08f); _btnAi.Invalidate(); };
            _btnAi.MouseLeave += (s, e) => { _aiBg = _btnAi.BackColor != Color.Transparent ? _btnAi.BackColor : Helpers.UiTheme.HeroBg; _btnAi.Invalidate(); };
            _btnAi.Location = new Point(btnRefresh.Right + 8, 13);
            // v4.0 — ИИ-расшифровка платная.
            _btnAi.Click += (s, e) => { if (!Helpers.ModuleGate.Require(this, "ai")) return; OllamaInstaller.ShowInstallGuide(this); };
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
                            _btnAi.Click += (s2, e2) => { if (!Helpers.ModuleGate.Require(this, "ai")) return; MessageBox.Show(this, "Ollama доступен. Модель llama3.2 готова к расшифровке событий.", "ИИ", MessageBoxButtons.OK, MessageBoxIcon.Information); };
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

            var btnFtp = UiButtons.MakeCompactFilled("FTP", 60);
            btnFtp.Location = new Point(btnSettings.Right + 8, 13);
            btnFtp.Click += (s, e) => Forms.FtpSettingsForm.Show(this);
            header.Controls.Add(btnFtp);

            Controls.Add(header);

            // Filter pills area (standalone; во встроенном режиме скрыта —
            // фильтры живут в hero единой шапки, см. PrepareForEmbeddedEvents).
            var filterPanel = new Panel { Dock = DockStyle.Top, Height = 56, BackColor = UiTheme.Card, Padding = new Padding(12, 8, 12, 8) };
            _filterPanelW = filterPanel;
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

            // Host bar — classic MMC tab strip (v3.9.1): Card background,
            // tabs flush to the bottom, 1px baseline. Active tab (white)
            // covers it and merges into the white page below.
            // Хосты — сегмент-пилы (v4.9.13): активный оливковый #626F5E,
            // белый жирный текст, рамка olive; неактивный — карточный фон,
            // muted-текст, рамка линии (как задано в CSS).
            // v4.9.15 — пилы подняты на 8px (паддинг сверху убран), белая
            // полоса под ними (sep) удалена, таблица подтянулась наверх.
            // v4.9.22 — хосты фразами (олива + подложка, без рамки).
            // v4.9.23 — зазор фразы→таблица ×3: панель 64px (36px под фразами).
            // v4.9.24 — фразы внизу у таблицы: зазор 36px сверху (к hero).
            // v4.9.25 — строчка серверов поднята на 10px (паддинг 26px).
            // v3.9.5 — зазор пилы→таблица как в «Даше Монитор»: 16px
            // (панель 70 = 26 сверху + пилы 28 + 16 снизу; было 90/36).
            _hostPanel = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 70, BackColor = UiTheme.Card, Padding = new Padding(8, 26, 12, 16), AutoSize = false };
            Controls.Add(_hostPanel);

            // Grid — double-buffered (DoubleBufferedDataGridView) for smooth
            // scrolling, matching the "Открытые файлы" grid. Cell painting is
            // only custom for the Level badge; everything else uses the
            // standard renderer so double-buffering stays artifact-free.
            _grid.Dock = DockStyle.Fill;
            UiGridStyle.ApplyUnified(_grid, 36, 36);
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Time", HeaderText = "Время", Width = 130, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 6, 0) } });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Level", HeaderText = "Уровень", Width = 80, MinimumWidth = 80, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleCenter, Padding = Padding.Empty } });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Source", HeaderText = "Источник", Width = 120, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 6, 0) } });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "EventId", HeaderText = "ID", Width = 60, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 6, 0) } });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Computer", HeaderText = "Хост", Width = 110, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 6, 0) } });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Message", HeaderText = "Сообщение", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 6, 0) } });

            _grid.CellFormatting += OnCellFormatting;
            _grid.CellMouseDown += OnCellMouseDown;
            _grid.CellDoubleClick += OnCellDoubleClick;
            _grid.ColumnWidthChanged += (s, e) => ScheduleColumnWidthSave();

            // Status bar with NyanCat progress — подкручен: толще + текст процента/ETA (зеленый бар только для опроса, Nyan только для ИИ)
            var statusPanel = new Panel { Dock = DockStyle.Bottom, Height = 62, BackColor = UiTheme.Card };
            _progressBar = new ProgressBar { Dock = DockStyle.Top, Height = 8, Style = ProgressBarStyle.Continuous, Visible = false };
            _progressBar.HandleCreated += (s, e) => SetProgressBarColor(_progressBar.Handle, Color.FromArgb(0xF0, 0xA0, 0x50));
            _nyanBar.Dock = DockStyle.Top;
            _nyanBar.Height = 36;
            _nyanBar.Visible = false;
            _progressLabel = new Label { Dock = DockStyle.Top, Height = 14, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI Semibold", 7.5f), ForeColor = Color.FromArgb(0xF0, 0xA0, 0x50), BackColor = UiTheme.Card, Visible = false, Padding = new Padding(0) };
            _status = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI", 8f), ForeColor = UiTheme.MutedForeground, Padding = new Padding(12, 2, 0, 0) };
            statusPanel.Controls.Add(_status);
            statusPanel.Controls.Add(_progressLabel);
            statusPanel.Controls.Add(_nyanBar);
            statusPanel.Controls.Add(_progressBar);
            Controls.Add(statusPanel);

            Controls.Add(_grid);
            // v4.4.0 — своя тонкая полоса вместо системной.
            ShadowScroll.AttachGrid(_grid);

            Controls.SetChildIndex(header, 5);
            Controls.SetChildIndex(filterPanel, 4);
            Controls.SetChildIndex(_hostPanel, 3);
            Controls.SetChildIndex(_grid, 2);
            Controls.SetChildIndex(statusPanel, 0);

            Load += async (s, e) => { await LoadEventsAsync(); EventPollerService.OnNewEvents += OnNewEvents; EventPollerService.OnCritical += OnCritical; EventPollerService.OnProgress += OnProgress; };
            // v3.9.3 Фаза 4 — OnNewEvents не трогает скрытый грид: при
            // возврате на вьюху обновляем его здесь.
            VisibleChanged += (s, e) => { if (Visible && !IsDisposed && IsHandleCreated) { try { ApplyFilters(); } catch { } } };
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
            // v3.9.1 — БЕЗ BringToFront(): во FlowLayoutPanel он меняет порядок
            // элементов, и активная пила перескакивает на первое место. Пилы
            // выделяются только стилем, как в Книге.
            foreach (var kv in _levelPills) Helpers.UiPills.Apply(kv.Value, kv.Key == _level);
            foreach (var kv in _periodPills) Helpers.UiPills.Apply(kv.Value, kv.Key == _period);
        }

        private Label CreatePill(string text, bool active) => Helpers.UiPills.Create(text, active);

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
                string tabText = total > 0 ? $"{h}  • {total}" : h;

                var tab = new Label
                {
                    Text = "",
                    AutoSize = false,
                    Font = new Font("Segoe UI Semibold", 9F),
                    TextAlign = ContentAlignment.MiddleCenter,
                    Cursor = Cursors.Hand,
                    Margin = new Padding(0, 0, 10, 0),
                    BackColor = Color.Transparent
                };
                int textW = TextRenderer.MeasureText(tabText, tab.Font).Width;
                int w = textW + 46;
                int tabH = 28;
                tab.Size = new Size(w, tabH);
                // Точки статуса на синем фоне: крит — красный, есть события —
                // янтарный, нет событий — светло-серый (как «недоступен» в Мониторе).
                Color statusDot = hasCrit ? UiTheme.Danger : hasAny ? Color.FromArgb(0xFF, 0xC4, 0x4D) : Color.FromArgb(0xF9, 0xF8, 0xF6);
                tab.Tag = false;
                tab.MouseEnter += (s, e) => { tab.Tag = true; try { tab.Invalidate(); } catch { } };
                tab.MouseLeave += (s, e) => { tab.Tag = false; try { tab.Invalidate(); } catch { } };
                tab.Paint += (s, e) =>
                {
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    bool hov = tab.Tag is true;
                    // v3.9.5 — как в «Даше Монитор»: плоская синяя пила #296AA0,
                    // радиус 5, h28, белый текст, рамка #1E4C78 (hover/active —
                    // белая). Были градиент-чипы r19 h38 (v3.9.3CK).
                    var rr = new Rectangle(0, 0, tab.Width - 1, tab.Height - 1);
                    using var path = UiButtons.RoundedRect(rr, 5);
                    using var bg = new SolidBrush(Color.FromArgb(0x29, 0x6A, 0xA0));
                    e.Graphics.FillPath(bg, path);
                    using var pen = new Pen((hov || isActive) ? Color.White : Color.FromArgb(0x1E, 0x4C, 0x78), 1) { Alignment = System.Drawing.Drawing2D.PenAlignment.Inset };
                    e.Graphics.DrawPath(pen, path);
                    using var dot = new SolidBrush(statusDot);
                    int ds = 8, dx = 11 + (11 - ds) / 2, dy = (tab.Height - ds) / 2;
                    e.Graphics.FillEllipse(dot, dx, dy, ds, ds);
                    var textRect = new Rectangle(31, 0, Math.Max(0, tab.Width - 46), tab.Height);
                    TextRenderer.DrawText(e.Graphics, tabText, tab.Font, textRect, Color.White, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
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
            DateTime from;
            DateTime to = now;
            // v4.9.21 — «Кастом»: свой диапазон дат (диалог из hero-пилы).
            if (_period == "Кастом") { from = _customFrom; to = _customTo; }
            else
            {
                TimeSpan periodSpan = TimeSpan.FromDays(2);
                if (_period == "30 мин") periodSpan = TimeSpan.FromMinutes(30);
                else if (_period == "1 час") periodSpan = TimeSpan.FromHours(1);
                else if (_period == "24 часа") periodSpan = TimeSpan.FromHours(24);
                from = now - periodSpan;
            }

            var filtered = _all.Where(e => e.Time >= from && e.Time <= to).Where(e => _level == "Все" || string.Equals(e.Level, _level, StringComparison.OrdinalIgnoreCase)).Where(e => string.IsNullOrEmpty(_selectedHost) || string.Equals(e.Computer, _selectedHost, StringComparison.OrdinalIgnoreCase)).Where(e => string.IsNullOrEmpty(_selectedSource) || string.Equals(e.Source, _selectedSource, StringComparison.OrdinalIgnoreCase)).OrderByDescending(e => e.Time).ToList();

            // v4.9.21 — «Группировать»: одинаковые (уровень, источник, ID,
            // сообщение) схлопываются в одну строку с префиксом [×N].
            var rows = new List<string[]>();
            var tags = new List<DashboardEvent>();
            if (_grouped)
            {
                foreach (var g in filtered.GroupBy(e => (e.Level ?? "") + "|" + (e.Source ?? "") + "|" + e.EventId + "|" + (e.Message ?? "")).OrderByDescending(g => g.Max(e => e.Time)).Take(500))
                {
                    var top = g.OrderByDescending(e => e.Time).First();
                    string msg = g.Count() > 1 ? $"[×{g.Count()}] {top.Message}" : top.Message;
                    rows.Add(new[] { top.Time.ToString("dd.MM HH:mm"), top.Level, top.Source, top.EventId.ToString(), top.Computer, msg });
                    tags.Add(top);
                }
            }
            else
            {
                foreach (var ev in filtered.Take(500))
                {
                    rows.Add(new[] { ev.Time.ToString("dd.MM HH:mm"), ev.Level, ev.Source, ev.EventId.ToString(), ev.Computer, ev.Message });
                    tags.Add(ev);
                }
            }
            _lastRows = rows;
            _lastTags = tags;

            _grid.SuspendLayout();
            _grid.Rows.Clear();
            for (int i = 0; i < rows.Count; i++)
            {
                int idx = _grid.Rows.Add(rows[i]);
                _grid.Rows[idx].Tag = tags[i];
            }
            _grid.ResumeLayout();
            if (_grid.Rows.Count > 0)
            {
                try { _grid.FirstDisplayedScrollingRowIndex = 0; _grid.ClearSelection(); } catch { }
            }
            UpdateStatus(filtered.Count);
        }

        // v4.9.21 — публичное API фильтров для hero-пил единой шапки.
        public void SetLevelFilter(string level) { _level = level; UpdatePills(); ApplyFilters(); }
        public void SetSourceFilter(string source) { _selectedSource = source == "Все" ? "" : source; UpdatePills(); ApplyFilters(); }
        public void SetGrouped(bool grouped) { _grouped = grouped; ApplyFilters(); }
        // Возвращает false, если пользователь отменил диалог Кастом.
        public bool SetPeriodFilter(string period)
        {
            if (period == "Кастом" && !ShowCustomPeriodDialog()) return false;
            _period = period;
            UpdatePills();
            ApplyFilters();
            return true;
        }

        // v4.9.21 — диалог своего диапазона дат для периода «Кастом».
        public bool ShowCustomPeriodDialog()
        {
            try
            {
                using var dlg = new Form
                {
                    Text = "Период — свой диапазон",
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    StartPosition = FormStartPosition.CenterParent,
                    MaximizeBox = false,
                    MinimizeBox = false,
                    ShowInTaskbar = false,
                    ClientSize = new Size(360, 222),
                    BackColor = UiTheme.DialogBackground
                };
                var hero = UiModal.Hero("Свой диапазон", "Период событий для фильтра «Кастом»", "dashboard");
                hero.Dock = DockStyle.Top;
                dlg.Controls.Add(hero);
                var card = UiModal.Card(12, 90, 336, 88, "Диапазон", "dashboard", withHeader: false);
                var lblFrom = UiModal.FieldLabel("С:", 16, 16);
                var dtFrom = new DateTimePicker { Location = new Point(16, 34), Width = 150, Format = DateTimePickerFormat.Custom, CustomFormat = "dd.MM.yyyy HH:mm", ShowUpDown = true, Value = _customFrom };
                var lblTo = UiModal.FieldLabel("По:", 174, 16);
                var dtTo = new DateTimePicker { Location = new Point(174, 34), Width = 150, Format = DateTimePickerFormat.Custom, CustomFormat = "dd.MM.yyyy HH:mm", ShowUpDown = true, Value = _customTo };
                card.Controls.Add(lblFrom);
                card.Controls.Add(dtFrom);
                card.Controls.Add(lblTo);
                card.Controls.Add(dtTo);
                dlg.Controls.Add(card);
                var btnOk = UiButtons.MakeCompactFilled("OK", 80);
                btnOk.Location = new Point(188, 186);
                btnOk.Click += (s, e) => { dlg.DialogResult = DialogResult.OK; dlg.Close(); };
                var btnCancel = UiButtons.MakeCompactFilled("Отмена", 80, danger: true);
                btnCancel.Location = new Point(276, 186);
                btnCancel.Click += (s, e) => { dlg.DialogResult = DialogResult.Cancel; dlg.Close(); };
                dlg.Controls.Add(btnOk);
                dlg.Controls.Add(btnCancel);
                dlg.AcceptButton = null;
                if (UiModal.ShowDimmed(this, dlg) != DialogResult.OK) return false;
                if (dtFrom.Value > dtTo.Value) { MessageBox.Show(this, "Дата «С» позже даты «По».", "Период", MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }
                _customFrom = dtFrom.Value;
                _customTo = dtTo.Value;
                return true;
            }
            catch { return false; }
        }

        // v4.9.21 — выгрузка показанных строк в CSV.
        public void ExportFilteredCsv()
        {
            try
            {
                if (_lastRows.Count == 0) { MessageBox.Show(this, "Нет строк для экспорта.", "Экспорт CSV", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                using var sfd = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = $"events_{DateTime.Now:yyyyMMdd_HHmm}.csv" };
                if (sfd.ShowDialog(this) != DialogResult.OK) return;
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("Время;Уровень;Источник;ID;Хост;Сообщение");
                foreach (var r in _lastRows) sb.AppendLine(string.Join(";", r.Select(c => CsvCell(c))));
                System.IO.File.WriteAllText(sfd.FileName, sb.ToString(), new System.Text.UTF8Encoding(true));
                MessageBox.Show(this, $"Сохранено строк: {_lastRows.Count}\n{sfd.FileName}", "Экспорт CSV", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { MessageBox.Show(this, "Ошибка экспорта:\n" + ex.Message, "Экспорт CSV", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        private static string CsvCell(string? v)
        {
            v ??= "";
            if (v.Contains(';') || v.Contains('"') || v.Contains('\n') || v.Contains('\r')) return "\"" + v.Replace("\"", "\"\"") + "\"";
            return v;
        }

        // v4.8 — публичные обёртки для единого дашборда (вьюха Events вызывает
        // их со своих кнопок шапки/футера вместо приватных методов).
        public async Task RefreshEventsNowAsync() => await RefreshNowAsync();
        public void ShowEventsSettings() => ShowSettings();
        public string GetStatusText() { try { return _status.Text ?? ""; } catch { return ""; } }

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
            var owner = this.FindForm() ?? this.TopLevelControl as Form ?? this;
            // Общий дизайн модалок: hero + карточки + футер (как FTP/календарь).
            using var dlg = new Form
            {
                Text = "Настройки — Дашборд событий",
                Size = new Size(640, 620),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                BackColor = UiTheme.DialogBackground,
                AutoScroll = false
            };
            dlg.KeyPreview = true;
            dlg.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { e.Handled = true; dlg.DialogResult = DialogResult.Cancel; dlg.Close(); } };

            const int ClientW = 624;
            var hero = UiModal.Hero("Настройки", "Дашборд событий — уведомления, интервал, хранение", "dashboard");

            // ——— Footer ———
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = UiTheme.Card };
            footer.Paint += (s, e) => { using var pen = new Pen(UiTheme.Border, 1); e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };
            var btnOk = UiButtons.MakeCompactFilled("Сохранить", 100);
            btnOk.Click += (s, e) => { dlg.DialogResult = DialogResult.OK; dlg.Close(); };
            var btnCancel = UiButtons.MakeCompactFilled("Отмена", 100, bgOverride: UiTheme.MutedForeground);
            btnCancel.Click += (s, e) => { dlg.DialogResult = DialogResult.Cancel; dlg.Close(); };
            var hiddenOk = new Button { DialogResult = DialogResult.OK, Visible = false, Location = new Point(-100, -100), Size = new Size(1, 1) };
            var hiddenCancel = new Button { DialogResult = DialogResult.Cancel, Visible = false, Location = new Point(-100, -100), Size = new Size(1, 1) };
            footer.Controls.Add(hiddenOk);
            footer.Controls.Add(hiddenCancel);
            dlg.AcceptButton = hiddenOk;
            dlg.CancelButton = hiddenCancel;
            footer.Controls.Add(btnOk);
            footer.Controls.Add(btnCancel);

            var settings = EventDashboardStore.LoadSettings();

            // ——— Content: карточки ———
            var content = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = UiTheme.Background };

            // Карточка «Уведомления».
            var cardNotif = UiModal.Card(14, 14, 300, 264, "Уведомления", "msg");
            var chkCrit = new CheckBox { Text = "Уведомлять о Critical", Checked = settings.NotifyCritical, Location = new Point(16, 56), AutoSize = true, ForeColor = UiTheme.Foreground, BackColor = Color.Transparent };
            var chkErr = new CheckBox { Text = "Уведомлять о Error", Checked = settings.NotifyError, Location = new Point(16, 92), AutoSize = true, ForeColor = UiTheme.Foreground, BackColor = Color.Transparent };
            var chkSound = new CheckBox { Text = "Звук", Checked = settings.NotifySound, Location = new Point(16, 128), AutoSize = true, ForeColor = UiTheme.Foreground, BackColor = Color.Transparent };
            var chkNyan = new CheckBox { Text = "Прогресс-бар Nyan Cat", Checked = settings.ShowNyanCat, Location = new Point(16, 164), AutoSize = true, ForeColor = UiTheme.Foreground, BackColor = Color.Transparent };
            cardNotif.Controls.AddRange(new Control[] { chkCrit, chkErr, chkSound, chkNyan });

            // Карточка «Опрос».
            var cardPoll = UiModal.Card(326, 14, 284, 170, "Опрос", "restart");
            var numInterval = new EscNumericUpDown { Minimum = 5, Maximum = 240, Value = settings.PollIntervalMinutes };
            var lInterval = UiModal.FieldLabel("Интервал (мин):", 16, 56);
            var wInterval = UiModal.Field(numInterval, 76);
            wInterval.Location = new Point(16, 74);
            var numRet = new EscNumericUpDown { Minimum = 1, Maximum = 7, Value = settings.RetentionDays };
            var lRet = UiModal.FieldLabel("Хранение (дн):", 16, 110);
            var wRet = UiModal.Field(numRet, 76);
            wRet.Location = new Point(16, 128);
            cardPoll.Controls.AddRange(new Control[] { lInterval, wInterval, lRet, wRet });

            // ——— assemble ———
            content.Controls.AddRange(new Control[] { cardNotif, cardPoll });
            // v3.9.3Y — UiModal.Card гладкая сама, Region давал лесенку.
            foreach (var cd in new[] { cardNotif, cardPoll }) { try { cd.Region?.Dispose(); cd.Region = null; } catch { } }
            ShadowScroll.AttachPanel(content);
            dlg.Controls.Add(content);
            dlg.Controls.Add(hero);
            dlg.Controls.Add(footer);

            var dpiScale = UiModal.ApplyDpiScale(dlg);
            if (dpiScale > 1.001f)
                dlg.ClientSize = new Size((int)Math.Round(dlg.ClientSize.Width * dpiScale), (int)Math.Round(dlg.ClientSize.Height * dpiScale));

            dlg.Shown += (a, b) =>
            {
                btnOk.Location = new Point(footer.Width - 233, 15);
                btnCancel.Location = new Point(footer.Width - 125, 15);
                try { dlg.BeginInvoke(new Action(() => { try { dlg.Refresh(); } catch { } })); } catch { }
            };

            if (UiModal.ShowDimmed(owner, dlg) == DialogResult.OK)
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
            // v4.0 — ИИ-расшифровка платная.
            if (!Helpers.ModuleGate.Require(this, "ai")) return;
            bool useNyan = EventDashboardStore.LoadSettings().ShowNyanCat;
            if (useNyan) _nyanBar.Start(); else ShowMarqueeBar();
            _status.Text = "ИИ думает...";
            try
            {
                string decoded = await EventDecoderService.DecodeAsync(ev);
                if (useNyan) _nyanBar.FadeOut(); else HideMarqueeBar();
                await Task.Delay(250);
                using var dlg = new AiAnswerForm(ev.EventId, ev.Source, ev.Level, ev.Computer, decoded);
                UiModal.ShowDimmed(this, dlg);
            }
            catch (Exception ex)
            {
                if (useNyan) _nyanBar.FadeOut(); else HideMarqueeBar();
                await Task.Delay(250);
                using var dlg = new AiAnswerForm(ev.EventId, ev.Source, ev.Level, ev.Computer, $"Ошибка ИИ: {ex.Message}");
                UiModal.ShowDimmed(this, dlg);
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
            UiModal.ShowDimmed(this, dlg);
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

        // Header painting now handled by UiGridStyle.ApplyUnified — white bg, dark text, Segoe UI Semibold 9F, gap 3px.

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

        private void OnNewEvents(List<DashboardEvent> list)
        {
            // v3.9.5 — гард от teardown-креша: поллер статический, событие
            // может прийти после смерти хендла (голый BeginInvoke кидал
            // InvalidOperationException в финализацию процесса).
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(new Action(() => { _all = EventDashboardStore.LoadEvents(); if (Visible && !IsDisposed) ApplyFilters(); })); }
            catch { }
        }
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
