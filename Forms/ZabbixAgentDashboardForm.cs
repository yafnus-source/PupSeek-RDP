using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using ScottPlot;
using ScottPlot.WinForms;
using Splt = ScottPlot;
using WinForms = System.Windows.Forms;
using Drw = System.Drawing;
using ShadowRDP.Controls;
using ShadowRDP.Helpers;
using ShadowRDP.Models;
using ShadowRDP.Services;

namespace ShadowRDP.Forms
{
    public class ZabbixAgentDashboardForm : Form
    {
        private readonly DataGridView _grid = new DoubleBufferedDataGridView();
        private FlowLayoutPanel _hostPanel = new FlowLayoutPanel();
        // v4.6.3 — WEB отдельной таблицей под главной, со своими колонками
        // Хост|ip|статус|Последняя проверка (в едином гриде колонки общие,
        // поэтому web-строки там выглядели рвано — море "—").
        private Panel _webSection = new Panel();
        private DataGridView _webGrid = null!;
        private WinForms.Label _webTitle = null!;
        private bool _suppressSelSync;
        // v3.9.3N — троттлинг пересборок: подавляем churn выбора/деталей
        // во время программного перестроения строк (опросы поллера).
        private bool _rebuildingGrid;
        // v3.9.3AZ — сборка контента деталей для всплывающего окна
        // (раскладка нижней панели при этом не трогается).
        private bool _popupBuild;
        private string _detailsHost = "";
        private readonly Dictionary<string, Models.WebPoint?> _webLast = new(StringComparer.OrdinalIgnoreCase);
        private WinForms.Label _title = new WinForms.Label();
        private WinForms.Label _status = new WinForms.Label();
        private ProgressBar _progressBar = new ProgressBar();
        private WinForms.Label _progressLabel = new WinForms.Label();
        private List<ZabbixAgentData> _allLatest = new List<ZabbixAgentData>();
        private bool _suppressDetailsOnRightClick;
        private string _selectedHost = "";
        private string _filterText = "";
        private DateTime _pollStart;
        private System.Windows.Forms.Timer _filterDebounce = new System.Windows.Forms.Timer { Interval = 300 };

        // Details panel
        private Panel _detailsPanel;
        private FlowLayoutPanel _detailsContent;
        private WinForms.Label _detailsCloseBtn;
        // v4.6.4/3.9.2.0 — футер и контейнер таблиц вынесены в поля: док-порядок
        // переустанавливается в каждой раскладке, чтобы футер ВСЕГДА оставался
        // самой нижней полосой (как футер сайта), даже если что-то в рантайме
        // переставит Controls.
        private Panel _statusPanel;
        private Panel _tablesHost;
        // v4.8 — единый дашборд: три вьюхи (Events = таблица даша W,
        // Monitor = таблица хостов Z без web, Web = только web-мониторинг).
        // Переключатель — hero-пил в шапке справа.
        public enum DashView { Events, Monitor, Web }
        private DashView _view = DashView.Monitor;
        private Panel _header = null!;
        private Panel _filterPanel = null!;
        // v3.9.3A — ряд статистики Монитора (Всего/В сети/Ошибки/Внимание).
        private FlowLayoutPanel _statsFlow = null!;
        // v3.9.3AD — полоса статистики под таблицей (Всего/В сети/Ошибки/Внимание).
        private Panel _statsStrip = new Panel();
        private (Panel, WinForms.Label, WinForms.Label) _statTotal;
        private (Panel, WinForms.Label, WinForms.Label) _statOnline;
        private (Panel, WinForms.Label, WinForms.Label) _statErrors;
        private (Panel, WinForms.Label, WinForms.Label) _statWarn;
        // v3.9.3AE — наглядные пилы: диски, службы, web, свежесть опроса.
        private (Panel, WinForms.Label, WinForms.Label) _statDisks;
        private (Panel, WinForms.Label, WinForms.Label) _statSvc;
        private (Panel, WinForms.Label, WinForms.Label) _statWeb;
        private (Panel, WinForms.Label, WinForms.Label) _statFresh;
        private Panel _tablesFiller = null!;
        private WinForms.Label _btnRefresh = null!;
        private WinForms.Label _btnSettings = null!;
        private WinForms.Label _btnConfigure = null!;
        private WinForms.Label _btnFtp = null!;
        // Пил действий шапки (Обновить/Настройки/Настроить агентов/FTP).
        // Поля _btn* оставлены для совместимости, сами кнопки заменены пилом.
        private Helpers.UiSegmented.SegmentedControl? _actionPill;
        // v3.9.3BP — хост объёмных кнопок-пил + сами кнопки (для видимости).
        private Panel? _actHostPanel;
        private WinForms.Label[]? _actBtns;
        // v3.9.3A — правая группа hero (мокап): 3 пункта глиф+текст
        // (События/Монитор/Web), тёмная пила только на активном.
        private Panel _viewGroup = null!;
        private WinForms.Label[] _viewItems = System.Array.Empty<WinForms.Label>();
        private static readonly Drw.Color ViewDarkBg = Drw.Color.FromArgb(0x33, 0x33, 0x33);
        // v4.9.23 — фильтры Событий в том же ряду hero (уровень + период +
        // «Группировать»); ряд виден только в Events, шапка 48px.
        private FlowLayoutPanel _heroRow = null!;
        private Helpers.UiSegmented.SegmentedControl? _fltLevel;
        private Helpers.UiSegmented.SegmentedControl? _fltPeriod;
        private int _fltPeriodIdx = 3;
        private Panel _eventsHost = null!;
        private Forms.DashboardForm? _eventsForm;
        // Текст web-фильтра отдельно от хостового (один бокс, плейсхолдер по вьюхе).
        private string _filterTextWeb = "";
        private Panel _searchWrapper = null!;
        // Кэш total для строки «Мониторинг computers» (туда же едет прогресс).
        private int _hostsTotal = -1;

        // Прогресс — в строку «Мониторинг computers», тем же кеглем, чуть
        // светлее (MutedForeground). Hero всегда чистый «ДАШБОРД СОБЫТИЙ».
        private void SetHeaderProgress(string? msg)
        {
            try
            {
                // NB: здесь прямое присваивание, НЕ рекурсивный вызов!
                _title.Text = "ДАШБОРД СОБЫТИЙ";
                // v4.9.19 — надпись «Мониторинг computers» удалена (пилы и таблица
                // подняты на 10px). Прогресс больше не выводится в строку.
            }
            catch { }
        }
        // Ручная высота панели деталей (-1 = авто по контенту). Ставится
        // перетаскиванием верхнего края панели, живёт до закрытия окна.
        private int _manualDetailsHeight = -1;
        // v4.7 — какой web-URL сейчас в деталях ("" = детали хоста/закрыты).
        // Нужен, чтобы клик по уже выбранной web-строке (SelectionChanged не
        // fires — выбор не менялся) гарантированно открывал детали, а повторный
        // клик по открытой не пересобирал панель зря.
        private string _detailsWebUrl = "";

        // Потолок высоты деталей: низ должен оставить гриду минимум 140px.
        // Без этого web-секция (до 244) + детали (до 520) + статус давят грид
        // в ноль (было: bounds грида 145..145, строки недостижимы вообще).
        // v4.8 — верх и резерв зависят от вьюхи (скрытые бары не вычитаются;
        // без грида резерв меньше).
        // v3.9.3BM — верх считается по живым высотам панелей
        // (шапка 60 + фильтр + хосты), без эмпирических констант.
        private int ViewTops()
        {
            int h = 60;
            try { if (_header != null && !_header.IsDisposed) h = _header.Height; } catch { }
            if (_view == DashView.Events) return h;
            int t = h;
            try { if (_filterPanel != null && !_filterPanel.IsDisposed && _filterPanel.Visible) t += _filterPanel.Height; } catch { }
            if (_view == DashView.Web) return t;
            // v3.9.3DB — пилы хостов живут внутри filterPanel (высота уже учтена).
            try { if (_hostPanel != null && !_hostPanel.IsDisposed && _hostPanel.Visible && _hostPanel.Parent != _filterPanel) t += _hostPanel.Height; } catch { }
            return t;
        }
        // v4.8 — в Events единый футер скрыт (свой статус-бар у встроенного
        // даша W, иначе текст двоится), место под него не резервируем.
        private int FooterH() => _view == DashView.Events ? 0 : 48;
        private int MaxDetailsHeight()
        {
            int tops = ViewTops();
            int webH = _webSection.Visible ? _webSection.Height : 0;
            // v3.9.3AD — резерв под полосу статистики (82) поверх минимума грида.
            int reserve = _view == DashView.Monitor ? 224 : 24;
            int maxH = ClientSize.Height - tops - FooterH() - webH - reserve;
            return Math.Max(180, maxH);
        }

        // v4.6.4 — высота таблиц строго по контенту, остаток добирает filler.
        // Иначе Fill-грид растягивается и между таблицами зияет мёртвое поле.
        private bool _layouting;
        private void LayoutTables()
        {
            if (IsDisposed) return;
            if (_layouting) return;
            _layouting = true;
            try
            {
                // Порядок доков ПЕРЕустанавливается здесь (а не только в
                // конструкторе): Fill-контейнер обязан быть ПОСЛЕ Bottom-панелей
                // (меньший индекс), а футер — ПОСЛЕДНИМ снизу. Любая рантайм-
                // перестановка Controls (например, повторный Add панели) не
                // сможет поднять футер над деталями.
                if (_statusPanel != null) Controls.SetChildIndex(_statusPanel, 2);
                if (_detailsPanel != null) Controls.SetChildIndex(_detailsPanel, 1);
                if (_tablesHost != null) Controls.SetChildIndex(_tablesHost, 0);

                // v4.8 — сайзинг грида только в Monitor (иначе правим скрытый).
                if (_view != DashView.Monitor) return;
                int tops = ViewTops();
                int detailsH = _detailsPanel.Visible ? _detailsPanel.Height : 0;
                int webH = _webSection.Visible ? _webSection.Height : 0;
                // v3.9.3AD — полоса статистики под таблицей отъедает место у грида.
                int statsH = (_statsStrip != null && _statsStrip.Visible) ? _statsStrip.Height : 0;
                int avail = ClientSize.Height - tops - webH - FooterH() - detailsH - statsH;
                int needGrid = MonitorHeaderH + Math.Max(0, _grid.Rows.Count) * MonitorRowH + 4;
                int gridH = Math.Clamp(needGrid, 80, Math.Max(80, avail));
                if (_grid.Height != gridH) _grid.Height = gridH;
            }
            catch { }
            finally { _layouting = false; }
        }

        private static readonly Drw.Font FontHeader = new Drw.Font("Segoe UI Semibold", 9f);
        private static readonly Drw.Font FontCell = new Drw.Font("Segoe UI", 9f);
        // v3.9.3BG — hero #48302F. v3.9.3BH — hero #36391E.
        private static readonly Drw.Color DashHeader = Drw.Color.FromArgb(0x36, 0x39, 0x1E);
        // v4.9.27 — активный хост: пил #F0A050 + белый шрифт.
        // v3.9.3H — активный хост: пил #4A6D8C + белый шрифт.
        // v3.9.3J — ВСЕ пилы #4A6D8C + белый шрифт; активная темнее.
        private static readonly Drw.Color SteelPillBg = Drw.Color.FromArgb(0xF5, 0xF1, 0xE8);
        private static readonly Drw.Color SteelPillActive = Drw.Color.FromArgb(0xCF, 0xCF, 0xCF);
        private static readonly Drw.Color SteelPillHover = Drw.Color.FromArgb(0xE9, 0xE2, 0xD2);
        private static readonly Drw.Color SteelPillBorder = Drw.Color.FromArgb(0xFF, 0xFF, 0xFF);
        private static readonly Drw.Color SteelPillText = Drw.Color.FromArgb(0x00, 0x00, 0x00);
        private const int MonitorRowH = 54;
        private const int MonitorHeaderH = 44;
        // v3.9.3BM — палитра редизайна v2 (мокап monitor_dash2.html).
        // v3.9.3CA — синяя тема: структурные цвета переключаются
        // (Olive — как было 1:1, Blue — стальные синие аналоги);
        // семантика (ок/ошибки/статусы) общая для обеих тем.
        private static bool IsBlue => UiTheme.CurrentName != "Olive";
        private static Drw.Color ActBot => IsBlue ? Drw.Color.FromArgb(0xE3, 0xE9, 0xF1) : Drw.Color.FromArgb(0xF3, 0xEF, 0xE2);
        private static Drw.Color ActBorder => IsBlue ? Drw.Color.FromArgb(0xC2, 0xCD, 0xDB) : Drw.Color.FromArgb(0xD5, 0xCF, 0xB9);
        private static Drw.Color ActShadow => IsBlue ? Drw.Color.FromArgb(36, 47, 74, 99) : Drw.Color.FromArgb(36, 61, 66, 35);
        // v3.9.3CC — #F2EFE0 заменён: области и текст-призрак переключаются.
        private static Drw.Color AreaBg => IsBlue ? Drw.Color.FromArgb(0xE9, 0xEF, 0xF6) : Drw.Color.FromArgb(0xF2, 0xEF, 0xE0);
        private static Drw.Color ViewGhost => IsBlue ? Drw.Color.FromArgb(0xDC, 0xE6, 0xF2) : Drw.Color.FromArgb(0xF2, 0xEF, 0xE0);
        private static Drw.Color V2Shell => IsBlue ? Drw.Color.FromArgb(0xF4, 0xF7, 0xFA) : Drw.Color.FromArgb(0xFA, 0xF7, 0xF4);
        private static Drw.Color V2Card => IsBlue ? Drw.Color.FromArgb(0xFC, 0xFE, 0xFF) : Drw.Color.FromArgb(0xFF, 0xFD, 0xF8);
        private static Drw.Color V2RowA => IsBlue ? Drw.Color.FromArgb(0xFF, 0xFF, 0xFF) : Drw.Color.FromArgb(0xFB, 0xF7, 0xEE);
        private static Drw.Color V2RowB => IsBlue ? Drw.Color.FromArgb(0xF5, 0xF5, 0xF5) : Drw.Color.FromArgb(0xFF, 0xFD, 0xF8);
        private static Drw.Color V2Side => IsBlue ? Drw.Color.FromArgb(0xD3, 0xDC, 0xE8) : Drw.Color.FromArgb(0xE2, 0xDD, 0xC9);
        private static Drw.Color V2Ink => IsBlue ? Drw.Color.FromArgb(0x2F, 0x4A, 0x63) : Drw.Color.FromArgb(0x56, 0x5C, 0x30);
        private static Drw.Color V2InkDark => IsBlue ? Drw.Color.FromArgb(0x20, 0x36, 0x4E) : Drw.Color.FromArgb(0x3D, 0x42, 0x23);
        private static Drw.Color V2InkMute => IsBlue ? Drw.Color.FromArgb(0x6E, 0x7E, 0x96) : Drw.Color.FromArgb(0x8B, 0x90, 0x64);
        private static Drw.Color V2LineSoft => IsBlue ? Drw.Color.FromArgb(0xE6, 0xEB, 0xF2) : Drw.Color.FromArgb(0xF0, 0xED, 0xE3);
        private static Drw.Color V2Olive => IsBlue ? Drw.Color.FromArgb(0x3F, 0x70, 0x96) : Drw.Color.FromArgb(0x6E, 0x7C, 0x3C);
        private static Drw.Color V2Olive2 => IsBlue ? Drw.Color.FromArgb(0x5B, 0x8A, 0xB0) : Drw.Color.FromArgb(0x8A, 0x93, 0x50);
        private static readonly Drw.Color V2Amber = Drw.Color.FromArgb(0xB8, 0x89, 0x2B);
        private static readonly Drw.Color V2Brick = Drw.Color.FromArgb(0xA8, 0x45, 0x2F);
        private static Drw.Color V2Edge => IsBlue ? Drw.Color.FromArgb(0xC3, 0xCF, 0xDD) : Drw.Color.FromArgb(0xD8, 0xD3, 0xBD);
        private static Drw.Color V2Track => IsBlue ? Drw.Color.FromArgb(0xD9, 0xE0, 0xE9) : Drw.Color.FromArgb(0xE5, 0xE1, 0xD1);
        private static Drw.Color V2TabBox => IsBlue ? Drw.Color.FromArgb(0xE5, 0xEA, 0xF1) : Drw.Color.FromArgb(0xEF, 0xEB, 0xDF);
        private static Drw.Color V2BtnBot => IsBlue ? Drw.Color.FromArgb(0xEB, 0xF0, 0xF6) : Drw.Color.FromArgb(0xF6, 0xF3, 0xE9);
        private static Drw.Color V2HoverRow => IsBlue ? Drw.Color.FromArgb(0xE9, 0xEF, 0xF6) : Drw.Color.FromArgb(0xF7, 0xF4, 0xE9);
        private static Drw.Color V2FirstA => IsBlue ? Drw.Color.FromArgb(0xDC, 0xE3, 0xEB) : Drw.Color.FromArgb(0xE7, 0xE3, 0xD1);
        private static Drw.Color V2HeadBot => IsBlue ? Drw.Color.FromArgb(0xE7, 0xED, 0xF3) : Drw.Color.FromArgb(0xF4, 0xF1, 0xE6);
        private static Drw.Color V2FirstHead1 => IsBlue ? Drw.Color.FromArgb(0xE0, 0xE7, 0xF0) : Drw.Color.FromArgb(0xED, 0xEA, 0xD9);
        private static readonly Drw.Color V2OkBg1 = Drw.Color.FromArgb(0xED, 0xF0, 0xD8);
        private static readonly Drw.Color V2OkBg2 = Drw.Color.FromArgb(0xDF, 0xE4, 0xC2);
        private static readonly Drw.Color V2OkFg = Drw.Color.FromArgb(0x48, 0x52, 0x1F);
        private static readonly Drw.Color V2ErrBg1 = Drw.Color.FromArgb(0xF6, 0xE0, 0xD8);
        private static readonly Drw.Color V2ErrBg2 = Drw.Color.FromArgb(0xEE, 0xCA, 0xBE);
        private static readonly Drw.Color V2ErrFg = Drw.Color.FromArgb(0x7E, 0x32, 0x20);
        private static readonly Drw.Color V2SnmpBg1 = Drw.Color.FromArgb(0xE4, 0xEB, 0xE1);
        private static readonly Drw.Color V2SnmpBg2 = Drw.Color.FromArgb(0xD3, 0xDE, 0xD1);
        private static readonly Drw.Color V2SnmpFg = Drw.Color.FromArgb(0x48, 0x61, 0x4C);
        private static readonly Drw.Color V2PingOkBg = Drw.Color.FromArgb(0xE8, 0xEC, 0xD4);
        private static readonly Drw.Color V2PingFailBg = Drw.Color.FromArgb(0xF5, 0xDE, 0xD6);
        private static readonly Drw.Color V2PingFailFg = Drw.Color.FromArgb(0x95, 0x3D, 0x27);
        private static readonly Drw.Color V2AmberBg = Drw.Color.FromArgb(0xF3, 0xE9, 0xD2);
        private static readonly Drw.Color V2Amber2 = Drw.Color.FromArgb(0xD2, 0xA8, 0x54);
        private static readonly Drw.Color V2WarnFg = Drw.Color.FromArgb(0x96, 0x6A, 0x18);
        private static Drw.Color V2DotOff => IsBlue ? Drw.Color.FromArgb(0xB7, 0xC3, 0xD3) : Drw.Color.FromArgb(0xC6, 0xC2, 0xB4);
        private static readonly Drw.Font V2BodyFont = new Drw.Font("Segoe UI", 9.5f);
        private static readonly Drw.Font V2BoldFont = new Drw.Font("Segoe UI Semibold", 9.5f);
        private static readonly Drw.Font V2HeaderFont = new Drw.Font("Segoe UI Semibold", 8f);
        private static readonly Drw.Font V2BadgeFont = new Drw.Font("Segoe UI Semibold", 8.5f);
        private const string ColumnWidthsFormKey = "zabbix_agent";
        private readonly System.Windows.Forms.Timer _colWidthDebounce = new System.Windows.Forms.Timer { Interval = 400 };

        public ZabbixAgentDashboardForm()
        {
            Text = "Дашборд";
            BackColor = UiTheme.Background;
            FormBorderStyle = FormBorderStyle.None;
            Dock = DockStyle.Fill;

            // v3.9.3BM — шапка v2 ОТКАЧЕНА (остались таблица и пилы):
            // снова 48px, без лого. Цвет — выбираемый HeroBg (фича BH живёт).
            var header = new Panel { Dock = DockStyle.Top, Height = 48, BackColor = UiTheme.HeroBg };
            _header = header;
            _title = new WinForms.Label { Text = "ДАШБОРД СОБЫТИЙ", Font = new Drw.Font("Segoe UI Semibold", 11f), ForeColor = UiTheme.HeroFg, BackColor = Drw.Color.Transparent, AutoSize = true, Location = new Point(16, 14) };
            header.Controls.Add(_title);
            // v3.9.3BH — живой отклик на смену цвета hero (с отпиской).
            void RefreshHeroColors()
            {
                try
                {
                    if (header.IsDisposed) return;
                    header.BackColor = UiTheme.HeroBg;
                    _title.ForeColor = UiTheme.HeroFg;
                    header.Invalidate(true);
                }
                catch { }
            }
            Helpers.UiTheme.HeroColorChanged += RefreshHeroColors;
            header.Disposed += (s, e) => { try { Helpers.UiTheme.HeroColorChanged -= RefreshHeroColors; } catch { } };
            // v3.9.3CY — выбор цвета hero перенесён в «Инструменты → Настройки цвета».
            // v3.9.3BP — действия объёмными кнопками-пилами (градиент +
            // рамка + ховер), вместо плоского UiSegmented.
            // v3.9.3BR — компактные под шапку 48: h28, шрифт 8.5.
            var actHost = new Panel { Height = 28, BackColor = Drw.Color.Transparent };
            {
                var defs = new (string Text, string Icon, Action Act)[]
                {
                    ("Обновить", "restart", () => { try { _ = RefreshCurrentViewAsync(); } catch { } }),
                    ("Настройки", "gear", () => { try { ShowCurrentSettings(); } catch { } }),
                    ("Настроить агентов", "list", () => { try { _ = ConfigureAgentsAsync(); } catch { } }),
                    ("FTP", "folder", () => { try { Forms.FtpSettingsForm.Show(this); } catch { } }),
                };
                int ax = 0;
                var abtnFont = new Drw.Font("Segoe UI Semibold", 8.5f);
                foreach (var (atext, aicon, aact) in defs)
                {
                    Drw.Image? aglyph = null;
                    try
                    {
                var gg = Helpers.MenuIcons.GetScaled(aicon, 14);
                if (gg != null) aglyph = Helpers.MenuIcons.Darken(gg);
                    }
                    catch { }
                    int aw = TextRenderer.MeasureText(atext, abtnFont).Width + 10 + 6 + 10;
                    if (aglyph != null) aw += 20;
                    var ab = new WinForms.Label
                    {
                        Text = "",
                        AutoSize = false,
                        Size = new Size(aw, 28),
                        Location = new Point(ax, 0),
                        Font = abtnFont,
                        TextAlign = ContentAlignment.MiddleCenter,
                        Cursor = Cursors.Hand,
                        BackColor = Drw.Color.Transparent,
                        Tag = false
                    };
                    ab.Paint += (s, e) =>
                    {
                        try
                        {
                            bool hov = ab.Tag is true;
                            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                            e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                            var r = new Rectangle(0, 0, ab.Width - 1, ab.Height - 1);
                            // v3.9.3BZ — значения 1:1 из dasha_monitor.html
                            // (.apill): верх #FFFDF8, низ #F3EFE2, рамка
                            // #D5CFB9, блик/тень как inset box-shadow мокапа.
                            using var path = UiButtons.RoundedRect(r, 9);
                            using var lg = new System.Drawing.Drawing2D.LinearGradientBrush(r,
                                V2Card, ActBot, 90f);
                            e.Graphics.FillPath(lg, path);
                            using var pen = new Pen(hov ? V2Ink : ActBorder, 1)
                            { Alignment = System.Drawing.Drawing2D.PenAlignment.Inset };
                            e.Graphics.DrawPath(pen, path);
                            using var hi = new Pen(Drw.Color.FromArgb(115, 255, 255, 255), 1);
                            e.Graphics.DrawLine(hi, r.X + 2, r.Y + 1, r.Right - 2, r.Y + 1);
                            using var sh = new Pen(ActShadow, 1);
                            e.Graphics.DrawLine(sh, r.X + 2, r.Bottom - 1, r.Right - 2, r.Bottom - 1);
                            int tx = 10;
                            if (aglyph != null)
                            {
                                e.Graphics.DrawImage(aglyph, new Rectangle(10, (ab.Height - 14) / 2, 14, 14));
                                tx = 30;
                            }
                            var fg = hov ? V2InkDark : V2Ink;
                            TextRenderer.DrawText(e.Graphics, atext, ab.Font,
                                new Rectangle(tx, 0, Math.Max(0, ab.Width - tx - 10), ab.Height), fg,
                                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
                        }
                        catch { }
                    };
                    ab.MouseEnter += (s, e) => { ab.Tag = true; try { ab.Invalidate(); } catch { } };
                    ab.MouseLeave += (s, e) => { ab.Tag = false; try { ab.Invalidate(); } catch { } };
                    ab.Click += (s, e) => { try { aact(); } catch { } };
                    actHost.Controls.Add(ab);
                    ax += aw + 8;
                }
                actHost.Width = ax;
            }
            _actHostPanel = actHost;
            _actBtns = actHost.Controls.OfType<WinForms.Label>().ToArray();
            actHost.Location = new Point(200, 10);
            header.Controls.Add(actHost);
            // v3.9.3A — правая группа hero (мокап редизайна): прозрачный
            // контейнер, пункты глиф+текст; тёмная пила только на активном.
            // v3.9.3BN — откат v2 (таблица и пилы хостов остаются).
            // Пункты: колокол+События | Монитор | Web.
            _viewGroup = new Panel { Height = 32, BackColor = Drw.Color.Transparent, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            var viewDefs = new (string Text, string Icon, DashView View)[]
            {
                ("События", "bell", DashView.Events),
                ("Монитор", "computer", DashView.Monitor),
                ("Web", "globe", DashView.Web),
            };
            var viewFont = new Drw.Font("Segoe UI", 9f);
            var viewFontBold = new Drw.Font("Segoe UI Semibold", 9f);
            int vx = 0;
            _viewItems = new WinForms.Label[viewDefs.Length];
            for (int vi = 0; vi < viewDefs.Length; vi++)
            {
                var (vtext, vicon, vview) = viewDefs[vi];
                Drw.Image? glyphDark = null;
                Drw.Image? glyphWhite = null;
                try
                {
                    var g = Helpers.MenuIcons.Get(vicon);
                    if (g != null) { glyphDark = Helpers.MenuIcons.Darken(g); glyphWhite = Helpers.UiButtons.TintWhite(g); }
                }
                catch { }
                int vw = TextRenderer.MeasureText(vtext, viewFont).Width + 16 + 8 + 20;
                var item = new WinForms.Label
                {
                    Text = "",
                    AutoSize = false,
                    Size = new Size(vw, 32),
                    Location = new Point(vx, 0),
                    Font = viewFont,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Cursor = Cursors.Hand,
                    BackColor = Drw.Color.Transparent,
                    Tag = new object?[] { vview, false, false, glyphDark, vicon, glyphWhite }
                };
                item.Paint += (s, e) =>
                {
                    var tag = (object?[])item.Tag;
                    bool isActive = tag[1] is true;
                    bool isHover = tag[2] is true;
                    // v3.9.3BT — активная вьюха рыжая (#F0A050 градиент).
                    e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    var r = new Rectangle(0, 0, item.Width - 1, item.Height - 1);
                    // v3.9.3BT — активная вьюха рыжая; неактивные видны
                    // и на тёмном, и на светлом hero.
                    bool light = Helpers.UiTheme.HeroFg.ToArgb() != Drw.Color.White.ToArgb();
                    if (isActive)
                    {
                        // v3.9.3BS — объём как у кнопок.
                        // v3.9.3 — своя настройка цвета пил («Настройки цвета»):
                        // тогда активная вьюха красится ей, иначе остаётся рыжей.
                        bool custom = Helpers.UiTheme.PillColorsCustomized;
                        Drw.Color pillBg = custom ? Helpers.UiTheme.PillActiveBg
                            : Drw.Color.FromArgb(0xD9, 0x84, 0x2E);
                        Drw.Color pillTop = custom ? ControlPaint.Light(Helpers.UiTheme.PillActiveBg, 0.15f)
                            : Drw.Color.FromArgb(0xF0, 0xA0, 0x50);
                        Drw.Color pillBorder = custom ? Helpers.UiTheme.PillActiveBorder
                            : Drw.Color.FromArgb(0xB8, 0x6A, 0x20);
                        using var path = UiButtons.RoundedRect(r, 11);
                        using var lg = new System.Drawing.Drawing2D.LinearGradientBrush(r, pillTop, pillBg, 90f);
                        e.Graphics.FillPath(lg, path);
                        using var pen = new Pen(pillBorder, 1) { Alignment = System.Drawing.Drawing2D.PenAlignment.Inset };
                        e.Graphics.DrawPath(pen, path);
                    }
                    else if (isHover)
                    {
                        using var path = UiButtons.RoundedRect(r, 10);
                        using var br = new SolidBrush(light ? Drw.Color.FromArgb(24, 0, 0, 0) : Drw.Color.FromArgb(36, 255, 255, 255));
                        e.Graphics.FillPath(br, path);
                    }
                    // tag[3] — тёмный глиф, tag[5] — белый.
                    var gi = (isActive || !light) ? tag[5] as Drw.Image : tag[3] as Drw.Image;
                    if (gi != null)
                        e.Graphics.DrawImage(gi, new Rectangle(10, (item.Height - 16) / 2, 16, 16));
                    Drw.Color fg = isActive ? Drw.Color.White
                        : light ? Helpers.UiTheme.HeroFg
                        : ViewGhost;
                    TextRenderer.DrawText(e.Graphics, vtext, item.Font,
                        new Rectangle(30, 0, Math.Max(0, item.Width - 32), item.Height), fg,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
                };
                item.MouseEnter += (s, e) => { var tag = (object?[])item.Tag; if (tag[1] is true) return; tag[2] = true; try { item.Invalidate(); } catch { } };
                item.MouseLeave += (s, e) => { var tag = (object?[])item.Tag; if (tag[1] is true) return; tag[2] = false; try { item.Invalidate(); } catch { } };
                DashView vv = vview;
                item.Click += (s, e) => SwitchView(vv);
                _viewGroup.Controls.Add(item);
                _viewItems[vi] = item;
                vx += vw + 8;
            }
            _viewGroup.Width = vx;
            header.Controls.Add(_viewGroup);
            void LayoutPill()
            {
                // v4.9.24 — пилы строго по центру hero 48px: y=8 при высоте 32.
                // v3.9.3A — двигаем всю правую группу (колокол+События+Монитор|Web).
                try { _viewGroup.Location = new Point(Math.Max((_actHostPanel?.Right ?? 200) + 8, header.ClientSize.Width - _viewGroup.Width - 12), 8); } catch { }
            }
            void LayoutHeroRow()
            {
                try
                {
                    int nx = actHost.Right + 8;
                    int nw = Math.Max(0, _viewGroup.Left - 8 - nx);
                    // Присваиваем только при изменении: иначе Layout-событие
                    // зациклится (смена Location/Width снова шлёт Layout).
                    if (_heroRow.Location.X != nx) _heroRow.Location = new Point(nx, 6);
                    if (_heroRow.Width != nw) _heroRow.Width = nw;
                }
                catch { }
            }
            // v4.9.23 — единая раскладка шапки (порядок важен: сначала
            // view-пил, затем ряд фильтров от правого края действий).
            // Layout — самолечение: срабатывает при любом движении детей
            // шапки (Resize/Load/VISIBLE), порядок подписок не важен.
            void LayoutHero()
            {
                LayoutPill();
                LayoutHeroRow();
            }
            header.Resize += (s, e) => LayoutHero();
            header.Layout += (s, e) => LayoutHero();
            Load += (s, e) => { try { actHost.Location = new Point(_title.Right + 10, 10); } catch { } try { LayoutHero(); } catch { } try { SetViewActive(_view); } catch { } };

            // v4.9.23 — фильтры Событий в том же ряду hero (средний пил
            // источника убран): уровень — оливковый активный, период —
            // тёмный. Ряд виден только в Events.
            Drw.Color olive = IsBlue ? Drw.Color.FromArgb(0x3F, 0x70, 0x96) : Drw.Color.FromArgb(0x62, 0x6F, 0x5E);
            Drw.Color dark = Drw.Color.FromArgb(0x33, 0x33, 0x33);
            _heroRow = new FlowLayoutPanel { Height = 36, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Drw.Color.Transparent, Padding = new Padding(0, 2, 0, 0), Visible = false };
            // v4.9.28 — подсветка активного сегмента: UiSegmented сам её не
            // двигает, нужен явный SetActive (как у пила действий/вьюх).
            _fltLevel = Helpers.UiSegmented.Create(
                new[] { "Все", "Critical", "Error" }, 0,
                idx => { try { EnsureEventsEmbedded(); _eventsForm?.SetLevelFilter(new[] { "Все", "Critical", "Error" }[idx]); _fltLevel?.SetActive(idx); } catch { } },
                height: 32, activeBg: olive, fontSize: 8.5f, pad: 20);
            _fltLevel.Host.Margin = new Padding(0, 0, 7, 0);
            _heroRow.Controls.Add(_fltLevel.Host);
            string[] periods = { "30 мин", "1 час", "24 часа", "2 дня", "Кастом" };
            _fltPeriod = Helpers.UiSegmented.Create(
                periods, 3,
                idx =>
                {
                    try
                    {
                        EnsureEventsEmbedded();
                        if (_eventsForm == null) return;
                        if (_eventsForm.SetPeriodFilter(periods[idx])) { _fltPeriodIdx = idx; _fltPeriod?.SetActive(idx); }
                        else _fltPeriod?.SetActive(_fltPeriodIdx);
                    }
                    catch { }
                },
                height: 32, activeBg: dark, fontSize: 8.5f, pad: 20);
            _fltPeriod.Host.Margin = new Padding(0, 0, 4, 0);
            _heroRow.Controls.Add(_fltPeriod.Host);
            var chkGroup = new CheckBox { Text = "Группировать", AutoSize = true, Font = new Drw.Font("Segoe UI", 8.5f), ForeColor = Drw.Color.White, BackColor = Drw.Color.Transparent, Cursor = Cursors.Hand, Margin = new Padding(2, 6, 4, 0) };
            chkGroup.CheckedChanged += (s, e) => { try { EnsureEventsEmbedded(); _eventsForm?.SetGrouped(chkGroup.Checked); } catch { } };
            _heroRow.Controls.Add(chkGroup);
            header.Controls.Add(_heroRow);
            // v4.9.23 — страховка: эти события точно срабатывают при
            // показах/перемещениях (в отличие от порядка Load/Resize).
            _heroRow.VisibleChanged += (s, e) => { try { LayoutHero(); } catch { } };
            actHost.LocationChanged += (s, e) => { try { LayoutHero(); } catch { } };
            Controls.Add(header);

            // v3.9.3AL — откат v3.9.3AK: старый цвет (Card) от шапки до таблицы.
            // v3.9.3BN — откат v2 (таблица и пилы хостов остаются).
            // v3.9.3BU — фон #F2EFE0 от hero до таблицы.
            var filterPanel = new Panel { Dock = DockStyle.Top, Height = 56, BackColor = AreaBg, Padding = new Padding(8, 8, 12, 8) };
            _filterPanel = filterPanel;
            // v3.9.3DE — второй ряд: только пилы хостов (слева) и поиск
            // (справа); виды вернулись в шапку (откат DD — так задумано).
            // v3.9.3A — ряд статистики Монитора слева (мокап редизайна):
            // Всего / В сети / Ошибки / Внимание. Виден только в Monitor.
            _statsFlow = new FlowLayoutPanel { Dock = DockStyle.Left, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Drw.Color.Transparent, Padding = new Padding(0, 0, 0, 0), AutoSize = true };
            _statTotal = MakeStatCard("Всего", UiTheme.Foreground);
            _statOnline = MakeStatCard("В сети", Drw.Color.FromArgb(0x2E, 0x9E, 0x5B));
            _statErrors = MakeStatCard("Ошибки", Drw.Color.FromArgb(0xC0, 0x39, 0x2B));
            _statWarn = MakeStatCard("Внимание", Drw.Color.FromArgb(0xF0, 0xA0, 0x50));
            // v3.9.3AE — наглядные пилы под таблицей.
            _statDisks = MakeStatCard("Диски <20%", Drw.Color.FromArgb(0xC0, 0x60, 0x00));
            _statSvc = MakeStatCard("Службы стоп", Drw.Color.FromArgb(0xC0, 0x39, 0x2B));
            _statWeb = MakeStatCard("Web OK", Drw.Color.FromArgb(0x2E, 0x9E, 0x5B));
            _statFresh = MakeStatCard("Опрос", UiTheme.Foreground);
            _statsFlow.Controls.Add(_statTotal.Item1);
            _statsFlow.Controls.Add(_statOnline.Item1);
            _statsFlow.Controls.Add(_statErrors.Item1);
            _statsFlow.Controls.Add(_statWarn.Item1);
            _statsFlow.Controls.Add(_statDisks.Item1);
            _statsFlow.Controls.Add(_statSvc.Item1);
            _statsFlow.Controls.Add(_statWeb.Item1);
            _statsFlow.Controls.Add(_statFresh.Item1);
            filterPanel.Controls.Add(_statsFlow);
            var searchWrapperZ = Helpers.UiSearchBox.Create("Фильтр по хосту…", txt => { if (_view == DashView.Web) _filterTextWeb = txt; else _filterText = txt; _filterDebounce.Stop(); _filterDebounce.Start(); }, width: 320, height: 27, background: Drw.Color.White);
            _searchWrapper = searchWrapperZ;
            var txtSearch = Helpers.UiSearchBox.GetBox(searchWrapperZ);
            // v3.9.1 — белая форма поиска: placeholder чуть выше (на 2px вверх)
            if (searchWrapperZ.Tag is object[] swArr && swArr.Length >= 4 && swArr[1] is WinForms.Label ph)
                ph.Location = new Point(14, 3);
            _filterDebounce.Interval = 120;
            _filterDebounce.Tick += (s, e) => { _filterDebounce.Stop(); if (_view == DashView.Web) RefreshWebGrid(); else if (_view == DashView.Monitor) ApplyFilters(); };
            searchWrapperZ.Dock = DockStyle.None;
            searchWrapperZ.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            // 60% ширины (-40%), вправо, по вертикали по центру 64px панели
            void LayoutSearchZ()
            {
                int avail = filterPanel.ClientSize.Width;
                int w = (int)Math.Round(avail * 0.6);
                if (w < 180) w = Math.Min(avail, 180);
                if (w > 384) w = 384;
                searchWrapperZ.Size = new Size(w, 27);
                // v3.9.3DC — ряд 52px: поиск по центру (12 + 27).
                searchWrapperZ.Location = new Point(avail - w - 20, 12);
                // v4.8.1 — Region удалён: UiSearchBox красит фон гладко сам.
                try { searchWrapperZ.Invalidate(); } catch { }
            }
            filterPanel.Resize += (s, e) => LayoutSearchZ();
            filterPanel.Controls.Add(searchWrapperZ);
            // initial layout after handle created
            Load += (s, e) => { try { LayoutSearchZ(); } catch { } };
            Controls.Add(filterPanel);

            // v4.9.20 — зазор от пилов до таблицы как в Событиях: панель 40px,
            // пилы 28px (12px под пилами).
            // v3.9.3AL — откат v3.9.3AJ: старый цвет (Card).
            // v3.9.3BM — пилы v2: 38px, карточка + рамка, свечение активного.
            // v3.9.3BU — фон #F2EFE0 (сплошная область до таблицы).
            // v3.9.3DB — пилы хостов в ряду поиска (слева, поиск справа):
            // один уровень по горизонтали; центр пилы совпадает с центром поиска.
            // v3.9.3DC — пилы h28 (ряд 52px), отступ сверху 4.
            _hostPanel = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, AutoSizeMode = WinForms.AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Drw.Color.Transparent, Padding = new Padding(14, 4, 0, 4) };
            filterPanel.Controls.Add(_hostPanel);

            // v4.6.3 — WEB отдельной таблицей со своими колонками
            // Хост|ip|статус|Последняя проверка, в стиле главного грида.
            // v4.6.4 — секция впритык без боковых отступов (как главная таблица
            // в край): иначе таблицы читаются как разные. Высоты строк/шапки —
            // те же 36px, что в главной: ритм строк совпадает.
            _webSection = new Panel { Dock = DockStyle.Top, Height = 120, BackColor = UiTheme.Card, Padding = new Padding(0, 0, 0, 0) };
            _webSection.Paint += (s, e) => { using var pen = new Pen(UiTheme.BorderSubtle); e.Graphics.DrawLine(pen, 0, 0, _webSection.Width, 0); };
            _webTitle = new WinForms.Label { Text = "Мониторинг web", Dock = DockStyle.Top, Height = 24, Font = new Drw.Font("Segoe UI Semibold", 9f), ForeColor = UiTheme.Foreground, BackColor = UiTheme.Card, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(12, 0, 0, 0) };
            // v4.6.4 — панель-хост под грид web. Скроллбары ShadowScroll живут
            // в родителе грида и на BringToFront (ShadowScroll.RefreshGrid) крутят
            // z-порядок всей коллекции. Если грид лежит прямо в секции рядом с
            // заголовком (Top), после BringToFront грид (Fill) раскладывается
            // раньше заголовка и перекрывает шапку таблицы — заголовки колонок
            // исчезали. В изолированном хосте грид единственный док-контрол,
            // з-порядок скроллбаров на его раскладку не влияет.
            var webGridHost = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Background };
            _webSection.Controls.Add(_webTitle);
            _webGrid = new DoubleBufferedDataGridView
            {
                Dock = DockStyle.Fill,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                MultiSelect = false,
                ReadOnly = true,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false
            };
            _webGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Host", HeaderText = "Хост", FillWeight = 160, MinimumWidth = 120, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 6, 0) } });
            _webGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Ip", HeaderText = "ip", FillWeight = 220, MinimumWidth = 140, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 6, 0) } });
            _webGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status", HeaderText = "статус", FillWeight = 90, MinimumWidth = 80, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleCenter, Padding = Padding.Empty } });
            _webGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Checked", HeaderText = "Последняя проверка", FillWeight = 200, MinimumWidth = 150, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 6, 0) } });
            ShadowRDP.Helpers.UiGridStyle.ApplyUnified(_webGrid, 36, 36);
            _webGrid.DefaultCellStyle.BackColor = Drw.Color.FromArgb(0xFD, 0xFB, 0xF7);
            _webGrid.CellFormatting += OnWebGridCellFormatting;
            _webGrid.SelectionChanged += OnWebGridSelectionChanged;
            _webGrid.CellMouseDown += OnWebGridCellMouseDown;
            // v4.7 — клик по уже выбранной строке не меняет выбор и SelectionChanged
            // не fires: детали бы не открылись (было: первый сайт молчит, второй
            // открывается). CellClick дожимает открытие напрямую.
            _webGrid.CellClick += OnWebGridCellClick;
            webGridHost.Controls.Add(_webGrid);
            ShadowScroll.AttachGrid(_webGrid);
            // Порядок доков: Top-заголовок обязан раскладываться РАНЬШЕ Fill-
            // хоста (обработка идёт с конца коллекции), иначе грид занимает
            // всю секцию, а заголовок ложится поверх и закрывает шапку таблицы.
            _webSection.Controls.Add(webGridHost);
            _webSection.Controls.SetChildIndex(webGridHost, 0);
            _webSection.Controls.SetChildIndex(_webTitle, 1);

// Таблица — как в Даше W: двойная буферизация, чистые границы, padding.
            // v4.6.4 — Dock.Top с явной высотой по контенту (см. LayoutTables):
            // Fill растягивал грид на всё место и давал мёртвое поле.
            // v3.9.3I — рамок нет вообще (как в книге): None по всем слоям
            // ПОСЛЕ ApplyUnified (там внутри SingleHorizontal).
            // v3.9.3J — редизайн: строки 78px (стек дисков), тёмная шапка.
            _grid.Dock = DockStyle.Top;
            // v3.9.3BM — шапка 44px.
            UiGridStyle.ApplyUnified(_grid, MonitorRowH, MonitorHeaderH);
            // v3.9.3M — иконки ошибок ячеек выключены (красные «!» тоже
            // читаются как мусор поверх кастомных ячеек).
            _grid.ShowCellErrors = false;
            _grid.ShowRowErrors = false;
            _grid.BorderStyle = BorderStyle.None;
            // v3.9.3W — подложка грида (пустая область) тоже #F2EFE0.
            _grid.BackgroundColor = MonFillerBg;
            // v3.9.3AO — перетаскивание колонок выключено: микро-драг при
            // дабл-клике оставлял stale-индикатор дропа (тёмные 1px-линии,
            // переживающие репейнты). Порядок из сохранённых ширин не трогаем.
            _grid.AllowUserToOrderColumns = false;
            // v3.9.3AM — граница шапка/строки красится ПОСЛЕ всего (grid.Paint —
            // последний проход): перекрываем 1px-разделитель цветом шапки
            // каждой колонки. Белой давал полоску под Хостав, чёрный хром —
            // под остальными; теперь граница в цвет шапки всегда.
            // v3.9.3AT — то же для кромок ВСЕХ видимых ячеек: чёрная AA-кромка
            // фреймворка рисуется после CellPainting (частичные репейнты после
            // модалки) цветом #AEB1AE поверх заливок. Кроем 4 края цветом фона
            // ячейки (та же формула, что в OnMonitorCellPainting).
            _grid.Paint += (s, e) =>
            {
                try
                {
                    // v3.9.3BM — кавер v2: формула фона как в paint, разделители
                    // строк line-soft (кроме последней), ERR-ряды приглушены.
                    e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
                    try
                    {
                        int hh = _grid.ColumnHeadersHeight;
                        if (hh > 0 && _grid.Width > 0)
                        {
                            using var hb = new SolidBrush(V2Side);
                            e.Graphics.FillRectangle(hb, 0, hh - 1, _grid.Width, 1);
                        }
                    }
                    catch { }
                    int hov = -1;
                    try { hov = UiGridStyle.HoverRowFor(_grid); } catch { }
                    int first = 0;
                    try { if (_grid.Rows.Count > 0) first = _grid.FirstDisplayedScrollingRowIndex; } catch { }
                    int last = _grid.Rows.Count - 1;
                    foreach (var col in _grid.Columns.Cast<DataGridViewColumn>())
                    {
                        if (!col.Visible) continue;
                        bool isHost = col.Name == "Host";
                        for (int r = first; r < _grid.Rows.Count; r++)
                        {
                            var row = _grid.Rows[r];
                            if (row.IsNewRow) continue;
                            var rc = _grid.GetCellDisplayRectangle(col.Index, r, false);
                            if (rc.Width <= 0 || rc.Height <= 0) break;
                            bool sel = false;
                            try { sel = row.Selected; } catch { }
                            Drw.Color bg;
                            if (isHost) bg = (sel || hov == r) ? V2HoverRow : (r % 2 == 0 ? V2FirstA : V2Side);
                            else if (sel || hov == r) bg = V2HoverRow;
                            else bg = (r % 2 == 0 ? V2RowA : V2RowB);
                            using var br = new SolidBrush(bg);
                            e.Graphics.FillRectangle(br, rc.X, rc.Y, rc.Width, 1);
                            e.Graphics.FillRectangle(br, rc.X, rc.Y, 1, rc.Height);
                            e.Graphics.FillRectangle(br, rc.X + rc.Width - 1, rc.Y, 1, rc.Height);
                            if (r < last)
                            {
                                using var sep = new SolidBrush(V2LineSoft);
                                e.Graphics.FillRectangle(sep, rc.X, rc.Y + rc.Height - 1, rc.Width, 1);
                            }
                            else
                            {
                                e.Graphics.FillRectangle(br, rc.X, rc.Y + rc.Height - 1, rc.Width, 1);
                            }
                            try
                            {
                                var dd = row.Tag as ZabbixAgentData;
                                if (dd != null && dd.HasError)
                                {
                                    using var fade = new SolidBrush(Drw.Color.FromArgb(102, 255, 253, 248));
                                    e.Graphics.FillRectangle(fade, rc.X, rc.Y, rc.Width, rc.Height);
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            };
            _grid.CellBorderStyle = DataGridViewCellBorderStyle.None;
            _grid.GridColor = UiTheme.Background;
            _grid.AdvancedCellBorderStyle.All = DataGridViewAdvancedCellBorderStyle.None;
            _grid.AdvancedCellBorderStyle.Top = DataGridViewAdvancedCellBorderStyle.None;
            _grid.AdvancedCellBorderStyle.Bottom = DataGridViewAdvancedCellBorderStyle.None;
            _grid.AdvancedCellBorderStyle.Left = DataGridViewAdvancedCellBorderStyle.None;
            _grid.AdvancedCellBorderStyle.Right = DataGridViewAdvancedCellBorderStyle.None;
            _grid.AdvancedColumnHeadersBorderStyle.All = DataGridViewAdvancedCellBorderStyle.None;

            // v3.9.3BM — ширины и подписи v2 (мокап monitor_dash2).
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Host", HeaderText = "Хост", Width = 150, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 6, 0) } });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Ip", HeaderText = "IP", Width = 126, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 6, 0) } });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Agent", HeaderText = "Агент", Width = 78, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleCenter, Padding = Padding.Empty } });
            // v4.7 — ICMP-пинг раз в час (HostPingService): OK/FAIL.
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Ping", HeaderText = "Пинг", Width = 70, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleCenter, Padding = Padding.Empty } });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Cpu", HeaderText = "CPU %", Width = 124, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight, Padding = new Padding(6, 0, 6, 0) } });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "RamFree", HeaderText = "RAM свободно %", Width = 132, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight, Padding = new Padding(6, 0, 6, 0) } });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Disks", HeaderText = "Диски", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 150, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 6, 0) } });
            // v3.9.3AW — вторая колонка дисков: те, что не влезли в первую (3-й+).
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Disks2", HeaderText = "Диски +", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 130, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 6, 0) } });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Temp", HeaderText = "Темп", Width = 92, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight, Padding = new Padding(6, 0, 6, 0) } });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Uptime", HeaderText = "Uptime", Width = 100, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 6, 0) } });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Ver", HeaderText = "Версия", Width = 96, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 6, 0) } });
            // v3.9.3AY — колонка ДЕТАЛИ: кнопка «Смотреть детали» в каждой строке.
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Details", HeaderText = "Детали", Width = 140, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleCenter, Padding = Padding.Empty } });

            _grid.CellFormatting += OnCellFormatting;
            _grid.CellPainting += OnMonitorCellPainting;
            _grid.CellMouseDown += OnCellMouseDown;
            // v3.9.3AY — дабл-клик убран: запись открывается кнопкой ДЕТАЛИ.
            // v3.9.3AZ — дабл-клик возвращён: Редактировать запись.
            // Клик по кнопке + рука-курсор над ней.
            _grid.CellClick += OnGridCellClick;
            _grid.CellDoubleClick += OnGridCellDoubleClick;
            _grid.CellMouseMove += OnGridCellMouseMove;
            _grid.SelectionChanged += OnSelectionChanged;
            _grid.SelectionChanged += OnSelectionChanged;
            _grid.ColumnWidthChanged += (s, e) => ScheduleColumnWidthSave();

            // Details panel (hidden initially)
            _detailsPanel = new Panel { Dock = DockStyle.Bottom, Height = 0, Visible = false, BackColor = UiTheme.Background, Padding = new Padding(12, 8, 12, 8) };
            _detailsPanel.Paint += (s, e) => { using var pen = new Pen(UiTheme.BorderSubtle); e.Graphics.DrawLine(pen, 0, 0, _detailsPanel.Width, 0); };
            _detailsContent = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, AutoScroll = true, WrapContents = false, BackColor = UiTheme.Background };
            _detailsPanel.Controls.Add(_detailsContent);
            // v4.4.0 — своя тонкая полоса вместо системной.
            ShadowScroll.AttachPanel(_detailsContent);
            // Заметная кнопка закрытия (маленький бледный ✕ терялся в углу).
            var closeFont = new Drw.Font("Segoe UI Semibold", 9f);
            int closeW = TextRenderer.MeasureText("✕ Закрыть", closeFont).Width + 28;
            _detailsCloseBtn = UiButtons.MakeCompactFilled("✕ Закрыть", closeW);
            _detailsCloseBtn.Location = new Point(8, 6);
            _detailsCloseBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _detailsCloseBtn.TabStop = false;
            _detailsCloseBtn.Cursor = Cursors.Hand;
            _detailsCloseBtn.Click += (s, e) => HideDetails();
            _detailsPanel.Controls.Add(_detailsCloseBtn);
            // Кнопка обязана быть поверх всех: прозрачный grip и оверлей
            // скроллбара (он делает BringToFront при скролле) её перекрывали.
            // Плюс отступы от обеих зон: y=12 (ниже grip), правый край −24
            // (левее полосы скроллбара).
            _detailsCloseBtn.BringToFront();
            // Ползунок высоты: тяни за верхний край панели.
            var grip = new Panel { Dock = DockStyle.Top, Height = 9, Cursor = Cursors.SizeNS, BackColor = Drw.Color.Transparent };
            grip.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                int gw = 64;
                using var path = UiButtons.RoundedRect(new Rectangle((grip.Width - gw) / 2, 2, gw, 5), 2);
                using var br = new SolidBrush(UiTheme.Border);
                e.Graphics.FillPath(br, path);
            };
            bool gripDrag = false;
            int gripStartY = 0, gripStartH = 0;
            grip.MouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                gripDrag = true;
                gripStartY = Control.MousePosition.Y;
                gripStartH = _detailsPanel.Height;
                try { grip.Capture = true; } catch { }
            };
            grip.MouseMove += (s, e) =>
            {
                if (!gripDrag) return;
                int nh = Math.Clamp(gripStartH + (gripStartY - Control.MousePosition.Y), 140, MaxDetailsHeight());
                if (nh != _detailsPanel.Height)
                {
                    _detailsPanel.Height = nh;
                    _manualDetailsHeight = nh;
                    LayoutTables();
                }
            };
            grip.MouseUp += (s, e) => { gripDrag = false; try { grip.Capture = false; } catch { } };
            _detailsPanel.Controls.Add(grip);
            _detailsPanel.Resize += (s, e) =>
            {
                _detailsCloseBtn.Location = new Point(_detailsPanel.Width - _detailsCloseBtn.Width - 26, 12);
                _detailsContent.Padding = new Padding(0, 0, _detailsCloseBtn.Width + 28, 0);
                try { _detailsCloseBtn.BringToFront(); } catch { }
            };

            Controls.Add(_detailsPanel);

            // v3.9.3BN — откат v2 (таблица и пилы хостов остаются).
            var statusPanel = new Panel { Dock = DockStyle.Bottom, Height = 48, BackColor = UiTheme.Card };
            _statusPanel = statusPanel;
            _progressBar = new ProgressBar { Dock = DockStyle.Top, Height = 8, Style = ProgressBarStyle.Continuous, Visible = false };
            _progressBar.HandleCreated += (s, e) => SetProgressBarColor(_progressBar.Handle, Drw.Color.FromArgb(0xF0, 0xA0, 0x50));
            _progressLabel = new WinForms.Label { Dock = DockStyle.Top, Height = 14, TextAlign = ContentAlignment.MiddleCenter, Font = new Drw.Font("Segoe UI Semibold", 7.5f), ForeColor = Drw.Color.FromArgb(0xF0, 0xA0, 0x50), BackColor = UiTheme.Card, Visible = false };
            _status = new WinForms.Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = new Drw.Font("Segoe UI", 8f), ForeColor = UiTheme.MutedForeground, Padding = new Padding(12, 2, 0, 0) };
            statusPanel.Controls.Add(_status);
            statusPanel.Controls.Add(_progressLabel);
            statusPanel.Controls.Add(_progressBar);
            Controls.Add(statusPanel);

            // v4.6.4 — таблицы живут в tablesHost и идут компактно друг за
            // другом (грид, web, filler добирает остаток): мёртвого поля между
            // таблицами нет. Порядок доков как обычно критичен (см. ниже).
            // v3.9.3BN — откат v2: белая рамка возвращена (таблица и пилы остаются).
            var tablesHost = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Background };
            _tablesHost = tablesHost;
            // v3.9.3G — белая рамка контейнера таблицы (по просьбе).
            tablesHost.Paint += (s, e) =>
            {
                try
                {
                    if (_grid == null || _grid.IsDisposed || !_grid.Visible || _grid.Width <= 0 || _grid.Height <= 0) return;
                    e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    var r = new Rectangle(_grid.Left, _grid.Top, _grid.Width - 1, _grid.Height - 1);
                    using var path = UiButtons.RoundedRect(r, 12);
                    using var pen = new Pen(Drw.Color.White, 1);
                    e.Graphics.DrawPath(pen, path);
                }
                catch { }
            };
            _tablesHost = tablesHost;
            // v3.9.3BM — белая рамка убрана (у v2 её нет: карточка + тень).
            var tablesFiller = new Panel { Dock = DockStyle.Fill, BackColor = MonFillerBg };
            _tablesFiller = tablesFiller;
            // v4.8 — хост вьюхи Events (встроенный даш W в content-only режиме).
            _eventsHost = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Background, Visible = false };
            Controls.Add(tablesHost);
            tablesHost.Controls.Add(_grid);
            // v4.4.0 — своя тонкая полоса вместо системной.
            ShadowScroll.AttachGrid(_grid);
            tablesHost.Controls.Add(_webSection);
            tablesHost.Controls.Add(tablesFiller);
            tablesHost.Controls.Add(_eventsHost);
            // v3.9.3AD — полоса статистики под таблицей: забираем ряд пил
            // из верхней панели фильтров в низ tablesHost.
            // v3.9.3AI — полоса едет ВВЕРХ (Dock.Top сразу под гридом),
            // фон #FAF7F4 вместо #E8E4D2.
            _statsStrip = new Panel { Dock = DockStyle.Top, Height = 82, BackColor = Drw.Color.FromArgb(0xFA, 0xF7, 0xF4) };
            try { filterPanel.Controls.Remove(_statsFlow); } catch { }
            _statsFlow.Dock = DockStyle.Fill;
            _statsFlow.AutoSize = false;
            _statsFlow.Padding = new Padding(12, 6, 0, 6);
            _statsStrip.Controls.Add(_statsFlow);
            tablesHost.Controls.Add(_statsStrip);
            tablesHost.Controls.SetChildIndex(_grid, 4);
            tablesHost.Controls.SetChildIndex(_webSection, 3);
            tablesHost.Controls.SetChildIndex(_eventsHost, 2);
            tablesHost.Controls.SetChildIndex(_statsStrip, 1);
            tablesHost.Controls.SetChildIndex(tablesFiller, 0);

            // Порядок доков КРИТИЧЕН: раскладка идёт с конца коллекции, поэтому
            // Fill-контейнер обязан быть ПОСЛЕ Bottom-панелей (меньший индекс),
            // иначе он забирает всё место, а статус/детали ложатся поверх
            // его нижних строк (было: грид залезал под статус на 48px и
            // последние строки — всегда web — прятались под панелями).
            // v3.9.3DF — детей 5 (полоса хостов влита в filterPanel):
            // индексы строго 4..0; индекс 5 съезжал в конец и клал
            // filterPanel (пилы+поиск) НАД шапкой.
            Controls.SetChildIndex(header, 4);
            Controls.SetChildIndex(filterPanel, 3);
            // v3.9.3DB — _hostPanel теперь внутри filterPanel (SetChildIndex здесь ронял конструктор).
            Controls.SetChildIndex(statusPanel, 2);
            Controls.SetChildIndex(_detailsPanel, 1);
            Controls.SetChildIndex(tablesHost, 0);

            // v4.8 — стартовая вьюха Monitor: применить матрицу видимости
            // (иначе откроется старый вид Z с web-секцией).
            try { ApplyViewVisibility(); } catch { }
            try { UpdateHeaderButtons(); } catch { }
            try { UpdateFilterPlaceholder(); } catch { }

            Load += async (s, e) => { LoadColumnWidths(); await LoadAsync(); ZabbixAgentPollerService.OnNewData += OnNewData; ZabbixAgentPollerService.OnProgress += OnProgress; WebMonitorPollerService.OnWebDown += OnWebChanged; WebMonitorPollerService.OnWebUp += OnWebChanged; Services.HostPingService.OnPingUpdate += OnPingUpdate; };
            FormClosing += (s, e) => SaveColumnWidths();
            FormClosing += (s, e) => SaveColumnWidths();
            Resize += (s, e) => LayoutTables();
            FormClosed += (s, e) => { ZabbixAgentPollerService.OnNewData -= OnNewData; ZabbixAgentPollerService.OnProgress -= OnProgress; WebMonitorPollerService.OnWebDown -= OnWebChanged; WebMonitorPollerService.OnWebUp -= OnWebChanged; Services.HostPingService.OnPingUpdate -= OnPingUpdate; };

            _colWidthDebounce.Tick += (s2, e2) => { _colWidthDebounce.Stop(); if (IsDisposed) return; SaveColumnWidths(); };
        }

        private async Task LoadAsync()
        {
            _allLatest = ZabbixAgentStore.GetLatestPerHost();
            RebuildHostBar();
            ApplyFilters();
            RefreshWebGrid();
            UpdateStatus();
            if (_allLatest.Count == 0)
            {
                var hosts = EventDashboardStore.GetMonitoredComputers();
                if (hosts.Count > 0) await RefreshNowAsync();
            }
        }

        private void RebuildHostBar()
        {
            _hostPanel.Controls.Clear();
            var hosts = EventDashboardStore.GetMonitoredComputers();
            if (hosts.Count == 0)
            {
                _hostPanel.Controls.Add(new WinForms.Label { Text = "Нет отслеживаемых хостов — добавьте в Книге ПКМ → Отслеживать", AutoSize = true, ForeColor = UiTheme.MutedForeground, Font = new Drw.Font("Segoe UI", 8.5f) });
                return;
            }
            foreach (var h in hosts)
            {
                var data = _allLatest.FirstOrDefault(x => string.Equals(x.Host, h, StringComparison.OrdinalIgnoreCase));
                bool alive = data != null && data.AgentPing && string.IsNullOrEmpty(data.Error);
                bool isActive = string.Equals(_selectedHost, h, StringComparison.OrdinalIgnoreCase);
                // v3.9.3DB — плоская синяя пила #296AA0, белый шрифт,
                // радиус 5; v3.9.3DC — h28, кружок: активен зелёный,
                // недоступен #F9F8F6, ошибка — тёмно-серый.
                var tab = new WinForms.Label
                {
                    Text = "",
                    AutoSize = false,
                    Font = new Drw.Font("Segoe UI Semibold", 9f),
                    TextAlign = ContentAlignment.MiddleCenter,
                    Cursor = Cursors.Hand,
                    Margin = new Padding(0, 0, 10, 0),
                    BackColor = Drw.Color.Transparent
                };
                int textW = TextRenderer.MeasureText(h, tab.Font).Width;
                int w = textW + 11 + 9 + 15 + 11;
                int tabH = 28;
                tab.Size = new Size(w, tabH);
                Drw.Color dotMain = alive
                    ? Drw.Color.FromArgb(0x2E, 0x9E, 0x5B)
                    : Drw.Color.FromArgb(0xF9, 0xF8, 0xF6);
                bool isErrHost = data != null && data.HasError;
                if (isErrHost)
                {
                    dotMain = Drw.Color.FromArgb(0x5C, 0x60, 0x66);
                }
                tab.Tag = false;
                tab.MouseEnter += (s, e) => { tab.Tag = true; try { tab.Invalidate(); } catch { } };
                tab.MouseLeave += (s, e) => { tab.Tag = false; try { tab.Invalidate(); } catch { } };
                tab.Paint += (s, e) =>
                {
                    e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    bool hov = tab.Tag is true;
                    var rr = new Rectangle(0, 0, tab.Width - 1, tab.Height - 1);
                    using var path = UiButtons.RoundedRect(rr, 5);
                    using var bg = new SolidBrush(Drw.Color.FromArgb(0x29, 0x6A, 0xA0));
                    e.Graphics.FillPath(bg, path);
                    using var pen = new Pen((hov || isActive) ? Drw.Color.White : Drw.Color.FromArgb(0x1E, 0x4C, 0x78), 1) { Alignment = System.Drawing.Drawing2D.PenAlignment.Inset };
                    e.Graphics.DrawPath(pen, path);
                    using var dot = new SolidBrush(dotMain);
                    int ds = 8, dx = 11 + (11 - ds) / 2, dy = (tab.Height - ds) / 2;
                    e.Graphics.FillEllipse(dot, dx, dy, ds, ds);
                    var textRect = new Rectangle(11 + 11 + 9, 0, Math.Max(0, tab.Width - 11 - 11 - 9 - 15), tab.Height);
                    TextRenderer.DrawText(e.Graphics, h, tab.Font, textRect, Drw.Color.White, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                };
                string cap = h;
                tab.Click += (s, e) => { _selectedHost = string.Equals(_selectedHost, cap, StringComparison.OrdinalIgnoreCase) ? "" : cap; RebuildHostBar(); ApplyFilters(); };
                _hostPanel.Controls.Add(tab);
            }
        }

        // v3.9.3A — карточка статистики: крупная цифра + подпись.
        // v3.9.3AE — третья метка (подстрока-деталь). Возвращает (карточка, цифра, подстрока).
        // v3.9.3AF — Semibold-подпись; иконки убраны (v3.9.3AG).
        // v3.9.3AH — стиль по мокапу: 140x70, рамка #E2DDC9, радиус 15.
        // v3.9.3BN — откат v2 (таблица и пилы хостов остаются).
        private static (Panel, WinForms.Label, WinForms.Label) MakeStatCard(string caption, Drw.Color numColor)
        {
            var card = new Panel { Size = new Size(140, 70), BackColor = Drw.Color.Transparent, Margin = new Padding(0, 0, 8, 0) };
            card.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                var r = new Rectangle(0, 0, card.Width - 1, card.Height - 1);
                using var path = UiButtons.RoundedRect(r, 15);
                using var br = new SolidBrush(Drw.Color.White);
                e.Graphics.FillPath(br, path);
                using var pen = new Pen(MonFirstColBg, 1) { Alignment = System.Drawing.Drawing2D.PenAlignment.Inset };
                e.Graphics.DrawPath(pen, path);
            };
            var num = new WinForms.Label { Text = "0", AutoSize = true, Location = new Point(16, 8), Font = new Drw.Font("Segoe UI Semibold", 14f), ForeColor = numColor, BackColor = Drw.Color.Transparent };
            var cap = new WinForms.Label { Text = caption, AutoSize = true, Location = new Point(16, 32), Font = new Drw.Font("Segoe UI Semibold", 8f), ForeColor = UiTheme.Foreground, BackColor = Drw.Color.Transparent };
            var sub = new WinForms.Label { Text = "", AutoSize = false, Location = new Point(16, 47), Size = new Size(112, 12), Font = new Drw.Font("Segoe UI", 7f), ForeColor = UiTheme.MutedForeground, BackColor = Drw.Color.Transparent, AutoEllipsis = true };
            card.Controls.Add(num);
            card.Controls.Add(cap);
            card.Controls.Add(sub);
            card.Resize += (s, e) => { try { card.Invalidate(); } catch { } };
            return (card, num, sub);
        }

        // v3.9.3A — пересчёт статистики: Всего / В сети (агент+без ошибок) /
        // Ошибки (HasError) / Внимание (пинг FAIL).
        // v3.9.3AE — плюс Диски (хосты с free<20% + худший), Службы (упавшие
        // крит + первая), Web (OK/DOWN), Свежесть (самый старый опрос).
        private void UpdateStats()
        {
            try
            {
                var list = _allLatest;
                int total = list.Count;
                int errors = 0, online = 0, warn = 0;
                string firstErr = "", firstWarn = "";
                int diskHosts = 0;
                string worstDisk = "—";
                double worstP = 101;
                int svcDown = 0;
                string firstSvc = "—";
                DateTime oldest = DateTime.Now;
                bool hasOld = false;
                string oldestHost = "";
                foreach (var d in list)
                {
                    if (d.HasError) { errors++; if (firstErr == "") firstErr = d.Host; }
                    else if (d.AgentPing) online++;
                    if (Services.HostPingService.TryGet(d.Host, out var pt) && pt != null && !pt.Ok)
                    { warn++; if (firstWarn == "") firstWarn = d.Host; }
                    bool low = false;
                    foreach (var dk in d.Disks)
                    {
                        if (dk.PFree < worstP) { worstP = dk.PFree; worstDisk = $"{d.Host}:{dk.Mount} {dk.PFree:0}%"; }
                        if (dk.PFree < 20) low = true;
                    }
                    if (low) diskHosts++;
                    foreach (var s in d.Services)
                    {
                        if (!s.IsCritical) continue;
                        if (s.State == "running" || s.State == "0" || s.State == "4") continue;
                        svcDown++;
                        if (firstSvc == "—") firstSvc = $"{d.Host}/{s.Name}";
                    }
                    if (!hasOld || d.Time < oldest) { oldest = d.Time; oldestHost = d.Host; hasOld = true; }
                }
                _statTotal.Item2.Text = total.ToString();
                _statOnline.Item2.Text = online.ToString();
                _statErrors.Item2.Text = errors.ToString();
                _statErrors.Item3.Text = firstErr;
                _statWarn.Item2.Text = warn.ToString();
                _statWarn.Item3.Text = firstWarn;
                _statDisks.Item2.Text = diskHosts.ToString();
                _statDisks.Item3.Text = diskHosts > 0 ? worstDisk : "все ок";
                _statSvc.Item2.Text = svcDown.ToString();
                _statSvc.Item3.Text = svcDown > 0 ? firstSvc : "все бегут";
                int wTotal = _webLast.Count;
                int wOk = 0;
                foreach (var p in _webLast.Values) if (p != null && p.Ok) wOk++;
                _statWeb.Item2.Text = $"{wOk}/{wTotal}";
                _statWeb.Item3.Text = wTotal == 0 ? "нет ресурсов" : (wTotal - wOk) > 0 ? $"DOWN: {wTotal - wOk}" : "все в норме";
                if (hasOld)
                {
                    var age = DateTime.Now - oldest;
                    _statFresh.Item2.Text = age.TotalHours >= 1 ? $"{age.TotalHours:0} ч" : $"{Math.Max(1, age.TotalMinutes):0} мин";
                    _statFresh.Item3.Text = oldestHost;
                    _statFresh.Item2.ForeColor = age.TotalMinutes > 30 ? Drw.Color.FromArgb(0xC0, 0x60, 0x00) : UiTheme.Foreground;
                }
                else { _statFresh.Item2.Text = "—"; _statFresh.Item3.Text = "нет данных"; }
            }
            catch { }
        }


        // v3.9.3J — кастомный paint Монитора (TSX-палитра), попытка 3.
// БЕЗ линий и скруглений: только заливки и контент. Каждая ветка
// начинается с PaintCellBase. Подписан ПОСЛЕ ApplyUnified.
        private static readonly Drw.Color MonNav = Drw.Color.FromArgb(0x3E, 0x4C, 0x3A);
        private static readonly Drw.Color MonCard = Drw.Color.FromArgb(0xF0, 0xED, 0xE6);
        private static readonly Drw.Color MonAlt = Drw.Color.FromArgb(0xE8, 0xE4, 0xDB);
        private static readonly Drw.Color MonWarnBg = Drw.Color.FromArgb(0xF5, 0xED, 0xD6);
        private static readonly Drw.Color MonErrBg = Drw.Color.FromArgb(0xF5, 0xDD, 0xD8);
        private static readonly Drw.Color MonOk = Drw.Color.FromArgb(0x3A, 0x7D, 0x3D);
        private static readonly Drw.Color MonOkSoft = Drw.Color.FromArgb(0xD4, 0xEC, 0xD5);
        private static readonly Drw.Color MonOkOrange = Drw.Color.FromArgb(0xC4, 0x7A, 0x00);
        private static readonly Drw.Color MonErr = Drw.Color.FromArgb(0xC0, 0x30, 0x20);
        private static readonly Drw.Color MonTeal = Drw.Color.FromArgb(0x2A, 0x7A, 0x6A);
        private static readonly Drw.Color MonTealSoft = Drw.Color.FromArgb(0xC8, 0xE4, 0xE0);
        private static readonly Drw.Color MonWarnNum = Drw.Color.FromArgb(0xC0, 0x60, 0x00);
        private static readonly Drw.Color MonHeaderTextSolid = Drw.Color.FromArgb(0x9E, 0xA5, 0x9C);
        private static readonly Drw.Color MonMuted = Drw.Color.FromArgb(0x7A, 0x70, 0x60);
        private static readonly Drw.Color MonInk = Drw.Color.FromArgb(0x2A, 0x30, 0x26);
        private static readonly Drw.Color MonInk2 = Drw.Color.FromArgb(0x4A, 0x50, 0x46);
        private static readonly Drw.Color MonInk3 = Drw.Color.FromArgb(0x5A, 0x60, 0x50);
        private static readonly Drw.Color MonBarTrack = Drw.Color.FromArgb(0xC8, 0xDC, 0xCA);
        private static readonly Drw.Color MonDiskTrack = Drw.Color.FromArgb(0xD4, 0xD0, 0xC8);
        private static readonly Drw.Color MonDiskOk = Drw.Color.FromArgb(0x7A, 0x9A, 0x7A);
        private static readonly Drw.Color MonWarnTrack = Drw.Color.FromArgb(0xEE, 0xD8, 0xB8);
        private static readonly Drw.Color MonErrTrack = Drw.Color.FromArgb(0xEE, 0xD0, 0xCC);
        // v3.9.3R — шрифты как в Событиях (ApplyUnified): тело Segoe UI 9
        // обычный, хедер Segoe UI Semibold 9.
        private static readonly Drw.Font MonBadgeFont = new Drw.Font("Segoe UI", 9f);
        private static readonly Drw.Font MonSmallFont = new Drw.Font("Segoe UI", 9f);
        private static readonly Drw.Font MonDiskFont = new Drw.Font("Segoe UI", 9f);
        private static readonly Drw.Font MonHeaderFont = new Drw.Font("Segoe UI Semibold", 9f);
        private static readonly Drw.Font MonHostFont = new Drw.Font("Segoe UI", 9f);
        // v3.9.3U — первая колонка: жирный текст на #E2DDC9.
        private static readonly Drw.Font MonHostBoldFont = new Drw.Font("Segoe UI", 9f, Drw.FontStyle.Bold);
        private static Drw.Color MonFirstColBg => IsBlue ? Drw.Color.FromArgb(0xD3, 0xDC, 0xE8) : Drw.Color.FromArgb(0xE2, 0xDD, 0xC9);
        // v3.9.3W — зебра первой колонки (светлая #ECE9DC) + хедер темнее (#C7C3B1).
        private static readonly Drw.Color MonFirstColAltBg = Drw.Color.FromArgb(0xEC, 0xE9, 0xDC);
        private static readonly Drw.Color MonFirstColHeaderBg = Drw.Color.FromArgb(0xC7, 0xC3, 0xB1);
        // v3.9.3W — область под таблицей #F2EFE0 (была #E8E4D2).
        // v3.9.3AI — #FAF7F4 (над и под таблицей).
        private static Drw.Color MonFillerBg => IsBlue ? Drw.Color.FromArgb(0xF4, 0xF7, 0xFA) : Drw.Color.FromArgb(0xFA, 0xF7, 0xF4);

        private static Drw.Color MixWhite(Drw.Color c, double k) => Drw.Color.FromArgb(
            (int)(c.R + (255 - c.R) * k), (int)(c.G + (255 - c.G) * k), (int)(c.B + (255 - c.B) * k));

        // v3.9.3BM — ховер строки (не выбранной) для формулы фона.
        private static bool HoverIs(DataGridViewRow row, DataGridView grid, int rowIndex)
        {
            try { return !row.Selected && UiGridStyle.HoverRowFor(grid) == rowIndex; }
            catch { return false; }
        }

        private static bool IsWarnRow(ZabbixAgentData? d) =>
            d != null && !d.HasError && (d.CpuLoad >= 100 || d.MemPFree < 20);

        private void OnMonitorCellPainting(object? sender, DataGridViewCellPaintingEventArgs e)
        {
            var grid = sender as DataGridView;
            if (grid == null || e == null) return;
            try
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                // v3.9.3S — текст системным ClearType, как в Событиях
                // (AntiAliasGridFit давал «пиксельный» ч/б вид).
                e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SystemDefault;
                if (e.RowIndex == -1 && e.ColumnIndex >= 0)
                {
                    // v3.9.3BX — все шапки как у ХОСТ: бежевый градиент,
                    // тёмный текст, кромки V2Edge (бывшие светлые V2Card — плоские).
                    string hcol = grid.Columns[e.ColumnIndex].Name;
                    bool hFirst = hcol == "Host";
                    var hr = new Rectangle(e.CellBounds.X, e.CellBounds.Y, e.CellBounds.Width + 1, e.CellBounds.Height);
                    using var hlg = new System.Drawing.Drawing2D.LinearGradientBrush(hr,
                        V2FirstHead1, V2Side, 90f);
                    e.Graphics.FillRectangle(hlg, hr);
                    using var hb = new SolidBrush(V2FirstHead1);
                    e.Graphics.FillRectangle(hb, e.CellBounds.X, e.CellBounds.Y, e.CellBounds.Width, 1);
                    e.Graphics.FillRectangle(hb, e.CellBounds.X, e.CellBounds.Y, 1, e.CellBounds.Height);
                    using var hpen = new Pen(V2Side, 1);
                    e.Graphics.DrawLine(hpen, e.CellBounds.X, e.CellBounds.Bottom - 1, e.CellBounds.Right, e.CellBounds.Bottom - 1);
                    using var hp = new Pen(V2Edge, 1);
                    if (hFirst)
                        e.Graphics.DrawLine(hp, e.CellBounds.X, e.CellBounds.Y, e.CellBounds.X, e.CellBounds.Bottom);
                    e.Graphics.DrawLine(hp, e.CellBounds.Right - 1, e.CellBounds.Y, e.CellBounds.Right - 1, e.CellBounds.Bottom);
                    string ht = (grid.Columns[e.ColumnIndex].HeaderText ?? "").ToUpperInvariant();
                    var cal = grid.Columns[e.ColumnIndex].DefaultCellStyle.Alignment;
                    TextFormatFlags hff = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;
                    Rectangle htr;
                    if (cal == DataGridViewContentAlignment.MiddleRight)
                    {
                        hff |= TextFormatFlags.Right;
                        htr = new Rectangle(e.CellBounds.X, e.CellBounds.Y, Math.Max(0, e.CellBounds.Width - 16), e.CellBounds.Height);
                    }
                    else if (cal == DataGridViewContentAlignment.MiddleCenter)
                    {
                        hff |= TextFormatFlags.HorizontalCenter;
                        htr = new Rectangle(e.CellBounds.X, e.CellBounds.Y, e.CellBounds.Width, e.CellBounds.Height);
                    }
                    else
                    {
                        hff |= TextFormatFlags.Left;
                        htr = new Rectangle(e.CellBounds.X + 12, e.CellBounds.Y, Math.Max(0, e.CellBounds.Width - 16), e.CellBounds.Height);
                    }
                    TextRenderer.DrawText(e.Graphics, ht, V2HeaderFont, htr, V2InkDark, hff);
                    e.Handled = true;
                    return;
                }
                if (e.RowIndex < 0 || e.RowIndex >= grid.Rows.Count) return;
                var row = grid.Rows[e.RowIndex];
                if (row.IsNewRow) return;
                string col = grid.Columns[e.ColumnIndex].Name;
                bool selected = (e.State & DataGridViewElementStates.Selected) != 0;
                var dd = row.Tag as ZabbixAgentData;
                bool isErr = dd != null && dd.HasError;
                // v3.9.3BM — фон v2: зебра; выбор и ховер — подсветка.
                Drw.Color baseBg = (selected || HoverIs(row, grid, e.RowIndex))
                    ? V2HoverRow
                    : (e.RowIndex % 2 == 0 ? V2RowA : V2RowB);

                if (col == "Agent")
                {
                    // v3.9.3BM — бейджи v2: градиент + inset-рамка.
                    string v = e.Value?.ToString() ?? "";
                    Drw.Color b1 = V2OkBg1, b2 = V2OkBg2, fg = V2OkFg;
                    Drw.Color ln = Drw.Color.FromArgb(76, 110, 124, 60);
                    if (v == "ERR") { b1 = V2ErrBg1; b2 = V2ErrBg2; fg = V2ErrFg; ln = Drw.Color.FromArgb(77, 168, 69, 47); }
                    else if (v == "SNMP") { b1 = V2SnmpBg1; b2 = V2SnmpBg2; fg = V2SnmpFg; ln = Drw.Color.FromArgb(64, 72, 97, 76); }
                    UiGridStyle.PaintCellBaseOverlap(e, baseBg);
                    int tw = TextRenderer.MeasureText(e.Graphics, v, V2BadgeFont, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width;
                    int bw = Math.Min(e.CellBounds.Width - 6, tw + 22);
                    int bh = 24;
                    var br2 = new Rectangle(e.CellBounds.X + (e.CellBounds.Width - bw) / 2, e.CellBounds.Y + (e.CellBounds.Height - bh) / 2, bw, bh);
                    using var path = UiButtons.RoundedRect(br2, 9);
                    using var lg = new System.Drawing.Drawing2D.LinearGradientBrush(br2, b1, b2, 90f);
                    e.Graphics.FillPath(lg, path);
                    using var pen = new Pen(ln, 1) { Alignment = System.Drawing.Drawing2D.PenAlignment.Inset };
                    e.Graphics.DrawPath(pen, path);
                    TextRenderer.DrawText(e.Graphics, v, V2BadgeFont, br2, fg,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                    e.Handled = true;
                    return;
                }
                if (col == "Cpu" || col == "RamFree")
                {
                    // v3.9.3BM — метры v2: трек 8px + градиентная заливка.
                    double.TryParse(e.Value?.ToString(), out double val);
                    double pct = Math.Clamp(val, 0, 100);
                    bool warn = col == "Cpu" ? val >= 100 : val < 20;
                    bool errv = isErr || (col == "Cpu" ? val >= 100 : val < 10);
                    Drw.Color f1 = V2Olive2, f2 = V2Olive;
                    if (warn && !errv) { f1 = V2Amber2; f2 = V2Amber; }
                    if (errv) { f1 = Drw.Color.FromArgb(0xC2, 0x65, 0x4C); f2 = V2Brick; }
                    Drw.Color vfg = errv ? V2ErrFg : warn ? V2WarnFg : V2InkDark;
                    UiGridStyle.PaintCellBaseOverlap(e, baseBg);
                    string txt = e.Value?.ToString() ?? "";
                    int barW = 66, barH = 8;
                    int bx = e.CellBounds.X + 6;
                    int by = e.CellBounds.Y + (e.CellBounds.Height - barH) / 2;
                    using var tpath = UiButtons.RoundedRect(new Rectangle(bx, by, barW, barH), 4);
                    using var tbr = new SolidBrush(warn && !errv ? V2AmberBg : V2Track);
                    e.Graphics.FillPath(tbr, tpath);
                    int fw = Math.Max(barH, (int)Math.Round(barW * pct / 100.0));
                    using var fpath = UiButtons.RoundedRect(new Rectangle(bx, by, fw, barH), 4);
                    using var flg = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(bx, by, fw, barH), f1, f2, 0f);
                    e.Graphics.FillPath(flg, fpath);
                    var tr2 = new Rectangle(bx + barW + 9, e.CellBounds.Y, Math.Max(0, e.CellBounds.Right - 6 - bx - barW - 9), e.CellBounds.Height);
                    TextRenderer.DrawText(e.Graphics, txt, V2BoldFont, tr2, vfg,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
                    e.Handled = true;
                    return;
                }
                if (col == "Disks" || col == "Disks2")
                {
                    var disks = grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Tag as System.Collections.Generic.List<Models.ZabbixAgentDisk>;
                    UiGridStyle.PaintCellBaseOverlap(e, baseBg);
                    if (disks != null && disks.Count > 0)
                    {
                        // v3.9.3BM — стек v2: строки 20px, бары 6px с градиентом.
                        int n = Math.Min(2, disks.Count);
                        int lh = 20, y0 = e.CellBounds.Y + (e.CellBounds.Height - n * lh) / 2;
                        for (int i = 0; i < n; i++)
                        {
                            var dk = disks[i];
                            int ly = y0 + i * lh;
                            TextRenderer.DrawText(e.Graphics, dk.Mount + ":", V2BoldFont,
                                new Rectangle(e.CellBounds.X + 6, ly, 26, lh), V2InkMute,
                                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
                            bool dwarn = dk.PFree <= 20;
                            bool derr = dk.PFree <= 10;
                            string pct = $"{dk.PFree:0.0}%";
                            int bx = e.CellBounds.X + 36, bw2 = 52;
                            int bh = 6, by = ly + (lh - bh) / 2;
                            using var tpath = UiButtons.RoundedRect(new Rectangle(bx, by, bw2, bh), 3);
                            using var tbr = new SolidBrush(V2Track);
                            e.Graphics.FillPath(tbr, tpath);
                            int fw2 = Math.Max(bh, (int)Math.Round(bw2 * Math.Clamp(dk.PFree, 0, 100) / 100.0));
                            Drw.Color f1 = V2Olive2, f2 = V2Olive;
                            if (dwarn && !derr) { f1 = V2Amber2; f2 = V2Amber; }
                            if (derr) { f1 = Drw.Color.FromArgb(0xC2, 0x65, 0x4C); f2 = V2Brick; }
                            using var fpath = UiButtons.RoundedRect(new Rectangle(bx, by, fw2, bh), 3);
                            using var flg = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(bx, by, fw2, bh), f1, f2, 0f);
                            e.Graphics.FillPath(flg, fpath);
                            Drw.Color dfg = derr ? V2ErrFg : dwarn ? V2WarnFg : V2Ink;
                            var dfont = (derr || dwarn) ? V2BoldFont : V2BodyFont;
                            TextRenderer.DrawText(e.Graphics, pct, dfont,
                                new Rectangle(bx + bw2 + 8, ly, e.CellBounds.Right - 8 - bx - bw2 - 8, lh), dfg,
                                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                        }
                    }
                    else
                    {
                        TextRenderer.DrawText(e.Graphics, "—", V2BodyFont,
                            new Rectangle(e.CellBounds.X + 6, e.CellBounds.Y, Math.Max(0, e.CellBounds.Width - 12), e.CellBounds.Height), V2InkMute,
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                    }
                    e.Handled = true;
                    return;
                }
                if (col == "Details")
                {
                    // v3.9.3BM — кнопка v2: градиент + рамка.
                    UiGridStyle.PaintCellBaseOverlap(e, baseBg);
                    string txt = "Смотреть детали";
                    int tw = TextRenderer.MeasureText(e.Graphics, txt, V2BadgeFont, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width;
                    int bw = Math.Min(e.CellBounds.Width - 8, tw + 26);
                    int bh = 28;
                    var br2 = new Rectangle(e.CellBounds.X + (e.CellBounds.Width - bw) / 2, e.CellBounds.Y + (e.CellBounds.Height - bh) / 2, bw, bh);
                    using var path = UiButtons.RoundedRect(br2, 10);
                    using var lg = new System.Drawing.Drawing2D.LinearGradientBrush(br2, V2Card, V2BtnBot, 90f);
                    e.Graphics.FillPath(lg, path);
                    using var pen = new Pen(V2Side, 1);
                    e.Graphics.DrawPath(pen, path);
                    TextRenderer.DrawText(e.Graphics, txt, V2BadgeFont, br2, V2Ink,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
                    e.Handled = true;
                    return;
                }
                if (col == "Host")
                {
                    // v3.9.3BM — первая колонка v2: сайд-зона + halo-точка.
                    // Выбор/ховер перекрывают (подсветка + акцент-полоса слева).
                    Drw.Color fbg = e.RowIndex % 2 == 0 ? V2FirstA : V2Side;
                    bool hovH = false;
                    try { hovH = selected || UiGridStyle.HoverRowFor(grid) == e.RowIndex; } catch { }
                    if (hovH) fbg = V2HoverRow;
                    UiGridStyle.PaintCellBaseOverlap(e, fbg);
                    using var edge = new Pen(V2Edge, 1);
                    e.Graphics.DrawLine(edge, e.CellBounds.X, e.CellBounds.Y, e.CellBounds.X, e.CellBounds.Bottom);
                    e.Graphics.DrawLine(edge, e.CellBounds.Right - 1, e.CellBounds.Y, e.CellBounds.Right - 1, e.CellBounds.Bottom);
                    if (hovH)
                    {
                        using var acc = new SolidBrush(V2Ink);
                        e.Graphics.FillRectangle(acc, e.CellBounds.X, e.CellBounds.Y, 3, e.CellBounds.Height);
                    }
                    string hn = e.Value?.ToString() ?? "";
                    Drw.Color dMain = isErr ? V2Brick : V2Olive2;
                    Drw.Color dHalo = isErr ? Drw.Color.FromArgb(46, 168, 69, 47) : Drw.Color.FromArgb(41, 110, 124, 60);
                    using var halo = new SolidBrush(dHalo);
                    e.Graphics.FillEllipse(halo, e.CellBounds.X + 8, e.CellBounds.Y + (e.CellBounds.Height - 11) / 2, 11, 11);
                    using var dot = new SolidBrush(dMain);
                    e.Graphics.FillEllipse(dot, e.CellBounds.X + 8 + 2, e.CellBounds.Y + (e.CellBounds.Height - 7) / 2, 7, 7);
                    Drw.Color hfg = isErr ? V2ErrFg : V2Ink;
                    var htr = new Rectangle(e.CellBounds.X + 26, e.CellBounds.Y, Math.Max(0, e.CellBounds.Width - 32), e.CellBounds.Height);
                    TextRenderer.DrawText(e.Graphics, hn, V2BoldFont, htr, hfg,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
                    e.Handled = true;
                    return;
                }
                if (col == "Ping")
                {
                    // v3.9.3BM — пинг-пилы v2.
                    UiGridStyle.PaintCellBaseOverlap(e, baseBg);
                    string pv = e.Value?.ToString() ?? "";
                    bool pok = pv == "OK";
                    Drw.Color pbg = pok ? V2PingOkBg : V2PingFailBg;
                    Drw.Color pfg = pok ? V2OkFg : V2PingFailFg;
                    int ptw = TextRenderer.MeasureText(e.Graphics, pv, V2BadgeFont, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width;
                    int pbw = Math.Min(e.CellBounds.Width - 4, ptw + 20);
                    int pbh = 24;
                    var pbr = new Rectangle(e.CellBounds.X + (e.CellBounds.Width - pbw) / 2, e.CellBounds.Y + (e.CellBounds.Height - pbh) / 2, pbw, pbh);
                    using var ppath = UiButtons.RoundedRect(pbr, 8);
                    using var pbr2 = new SolidBrush(pbg);
                    e.Graphics.FillPath(pbr2, ppath);
                    TextRenderer.DrawText(e.Graphics, pv, V2BadgeFont, pbr, pfg,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                    e.Handled = true;
                    return;
                }
                {
                    UiGridStyle.PaintCellBaseOverlap(e, baseBg);
                    string txt = e.Value?.ToString() ?? "";
                    Drw.Color tfg = isErr ? V2ErrFg
                        : col == "Uptime" ? V2Ink
                        : (col == "Ip" || col == "Temp" || col == "Ver") ? V2InkMute : V2Ink;
                    var cal = grid.Columns[e.ColumnIndex].DefaultCellStyle.Alignment;
                    TextFormatFlags tff = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;
                    Rectangle tr3;
                    if (cal == DataGridViewContentAlignment.MiddleRight)
                    {
                        tff |= TextFormatFlags.Right;
                        tr3 = new Rectangle(e.CellBounds.X, e.CellBounds.Y, Math.Max(0, e.CellBounds.Width - 16), e.CellBounds.Height);
                    }
                    else if (cal == DataGridViewContentAlignment.MiddleCenter)
                    {
                        tff |= TextFormatFlags.HorizontalCenter;
                        tr3 = new Rectangle(e.CellBounds.X, e.CellBounds.Y, e.CellBounds.Width, e.CellBounds.Height);
                    }
                    else
                    {
                        tff |= TextFormatFlags.Left;
                        tr3 = new Rectangle(e.CellBounds.X + 16, e.CellBounds.Y, Math.Max(0, e.CellBounds.Width - 22), e.CellBounds.Height);
                    }
                    TextRenderer.DrawText(e.Graphics, txt, V2BodyFont, tr3, tfg, tff);
                    e.Handled = true;
                    return;
                }
            }
            catch (Exception ex)
            {
                try
                {
                    if ((DateTime.Now - _paintLogAt).TotalSeconds > 10)
                    {
                        _paintLogAt = DateTime.Now;
                        Logger.Warn("MonitorPaint: " + ex.GetType().Name + ": " + ex.Message);
                    }
                }
                catch { }
            }
        }

        private static DateTime _paintLogAt = DateTime.MinValue;

        private void OpenUrlInBrowser(string url)
        {
            try { Process.Start(new ProcessStartInfo(url.Trim()) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show($"Не удалось открыть ссылку:\n{ex.Message}", "Web", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        private async Task RefreshWebNowAsync()
        {
            SetHeaderProgress("опрос web...");
            await WebMonitorPollerService.PollOnceAsync();
            _allLatest = ZabbixAgentStore.GetLatestPerHost();
            RefreshAllPreservingView();
            SetHeaderProgress(null);
        }

        // v4.7 — текст ячейки «Пинг»: OK/FAIL по последнему опросу, «—» если
        // данных ещё нет (первый часовой опрос не прошёл).
        private static string PingText(string host)
        {
            try
            {
                if (Services.HostPingService.TryGet(host ?? "", out var pt) && pt != null)
                    return pt.Ok ? "OK" : "FAIL";
            }
            catch { }
            return "—";
        }

        private void ApplyFilters()
        {
            var filtered = _allLatest.AsEnumerable();
            if (!string.IsNullOrEmpty(_selectedHost)) filtered = filtered.Where(x => string.Equals(x.Host, _selectedHost, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(_filterText)) filtered = filtered.Where(x => x.Host.Contains(_filterText, StringComparison.OrdinalIgnoreCase));
            var list = filtered.OrderBy(x => x.Host).ToList();

            _grid.SuspendLayout();
            _grid.Rows.Clear();
            var entries = AddressBookStore.Load();
            var manualEntries = ZabbixAgentStore.LoadManualHosts();
            foreach (var d in list)
            {
                string temp = d.TempC.HasValue ? $"{d.TempC.Value:0.0}°C" : "—";
                string uptime = d.UptimeSec > 0 ? $"{d.Uptime.Days}д {d.Uptime.Hours}ч" : "—";
                string agent = d.Source == "snmp" ? (d.AgentPing ? "SNMP" : "ERR") : (d.AgentPing ? "OK" : "ERR");
                var match = entries.FirstOrDefault(e => string.Equals(e.Computer, d.Host, StringComparison.OrdinalIgnoreCase))
                    ?? manualEntries.FirstOrDefault(e => string.Equals(e.Computer, d.Host, StringComparison.OrdinalIgnoreCase));
                string ip = match?.IPs?.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
                // Build row: Host, IP, Agent, Ping, Cpu, RamFree, Disks, Disks2, Temp, Uptime, Ver, Details.
                // v3.9.3J — диски одной ячейкой (paint рисует стек из Tag).
                // v3.9.3AW — ДИСКИ+: 3-й и 4-й диски.
                // v3.9.3AY — ДЕТАЛИ: кнопка рисуется в paint, значение пустое.
                var rowValues = new List<object> { d.Host, ip, agent, PingText(d.Host), d.CpuLoad.ToString("0.0"), d.MemPFree.ToString("0.0"), "", "" };
                rowValues.Add(temp);
                rowValues.Add(uptime);
                rowValues.Add(d.Source == "snmp" ? "—" : d.AgentVersion);
                rowValues.Add("");
                int idx = _grid.Rows.Add(rowValues.ToArray());
                _grid.Rows[idx].Tag = d;
                try { _grid.Rows[idx].Cells["Disks"].Tag = d.Disks.Take(2).ToList(); } catch { }
                try { _grid.Rows[idx].Cells["Disks2"].Tag = d.Disks.Skip(2).Take(2).ToList(); } catch { }
                if (d.HasError)
                {
                    _grid.Rows[idx].DefaultCellStyle.BackColor = Drw.Color.FromArgb(0xF5, 0xDD, 0xD8);
                    _grid.Rows[idx].DefaultCellStyle.ForeColor = Drw.Color.FromArgb(0xC0, 0x30, 0x20);
                }
            }
            _grid.ResumeLayout();
            LoadColumnWidths();
            UpdateStatus(list.Count);
            UpdateStats();
            LayoutTables();
        }

        // Полное обновление с сохранением прокрутки и выбора (фоновые опросы
        // не должны дёргать таблицу под руками).
        private void RefreshAllPreservingView()
        {
            int top = -1;
            try { if (_grid.Rows.Count > 0) top = _grid.FirstDisplayedScrollingRowIndex; } catch { }
            string? selHost = _grid.SelectedRows.Count > 0
                ? (_grid.SelectedRows[0].Tag as ZabbixAgentData)?.Host : null;
            // v3.9.3N — давим churn выбора/деталей на время перестроения.
            _rebuildingGrid = true;
            try
            {
                ApplyFilters();
                RefreshWebGrid();
            }
            finally { _rebuildingGrid = false; }
            try { if (top >= 0 && top < _grid.Rows.Count) _grid.FirstDisplayedScrollingRowIndex = top; } catch { }
            if (selHost == null) { _detailsHost = ""; return; }
            bool restored = false;
            foreach (DataGridViewRow r in _grid.Rows)
            {
                if (string.Equals((r.Tag as ZabbixAgentData)?.Host, selHost, StringComparison.OrdinalIgnoreCase))
                {
                    r.Selected = true;
                    restored = true;
                    break;
                }
            }
            // v3.9.3N — хост исчез из выборки: stale-панель гасим, иначе висит.
            if (!restored) { HideDetails(); return; }
            // v3.9.3BA — нижней панели больше нет: выбор молча восстановлен,
            // пересобирать нечего.
        }

        private void UpdateStatus(int shown = -1)
        {
            try
            {
                // v4.8 — футер контекстный по вьюхе единого дашборда.
                if (_view == DashView.Events)
                {
                    string t = "";
                    try { t = _eventsForm?.GetStatusText() ?? ""; } catch { }
                    _status.Text = t;
                    return;
                }
                int total = _allLatest.Count;
                int alive = _allLatest.Count(x => x.AgentPing);
                int err = total - alive;
                int wTotal = _webLast.Count;
                int wOk = _webLast.Values.Count(p => p != null && p.Ok);
                if (_view == DashView.Web)
                {
                    _status.Text = $"Ресурсов: {wTotal} | OK: {wOk} | DOWN: {wTotal - wOk} | Обновлено: {DateTime.Now:HH:mm}";
                    return;
                }
                _status.Text = $"{(shown >= 0 ? $"Показано: {shown} | " : "")}Всего: {total} | OK: {alive} | ERR: {err} | Обновлено: {DateTime.Now:HH:mm}";
                _hostsTotal = total;
            }
            catch { }
        }

        // Header painting now handled by UiGridStyle.ApplyUnified — white bg, dark text, Segoe UI Semibold 9F, gap 3px.

        // Сохранение/загрузка ширины колонок — как в Даше W
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
                var path = AppPaths.ColumnWidthsPath;
                var data = new Dictionary<string, Dictionary<string, int>>();
                if (System.IO.File.Exists(path))
                {
                    try { data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(System.IO.File.ReadAllText(path)) ?? new(); } catch { }
                }
                var cols = new Dictionary<string, int>();
                foreach (DataGridViewColumn c in _grid.Columns) cols[c.Name] = c.Width;
                data[ColumnWidthsFormKey] = cols;
                var opts = new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
                System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(data, opts));
            }
            catch { }
        }

        private void OnCellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0) return;
            var s = ZabbixAgentStore.LoadSettingsCached();
            var col = _grid.Columns[e.ColumnIndex].Name;
            if (col == "Agent" && e.Value?.ToString() == "ERR") e.CellStyle.ForeColor = Drw.Color.FromArgb(0xDC, 0x26, 0x26);
            if (col == "Agent" && e.Value?.ToString() == "SNMP") e.CellStyle.ForeColor = Drw.Color.FromArgb(0x6E, 0x8A, 0x6E);
// v4.7 — пинг: OK оранжевым (TSX okOrange), FAIL красным.
            // (Cpu/Ram/Disks красит paint; здесь только Ping/Temp.)
            if (col == "Ping" && e.Value?.ToString() == "OK") e.CellStyle.ForeColor = Drw.Color.FromArgb(0xC4, 0x7A, 0x00);
            if (col == "Ping" && e.Value?.ToString() == "FAIL") e.CellStyle.ForeColor = Drw.Color.FromArgb(0xDC, 0x26, 0x26);
            if (col == "Temp" && e.Value?.ToString() is string tv && tv.Contains("°C"))
            {
                if (double.TryParse(tv.Replace("°C", ""), out var tmp))
                {
                    if (tmp >= s.TempCrit) e.CellStyle.ForeColor = Drw.Color.FromArgb(0xDC, 0x26, 0x26);
                    else if (tmp >= s.TempWarn) e.CellStyle.ForeColor = Drw.Color.FromArgb(0xF5, 0x9E, 0x0B);
                }
            }
            // Network columns (if added dynamically)
            if (col.StartsWith("Net_") && e.Value?.ToString() is string nv && nv.EndsWith(" Mbps"))
            {
                if (double.TryParse(nv.Replace(" Mbps", ""), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var nbps))
                {
                    var settings = ZabbixAgentStore.LoadSettingsCached();
                    double pct = 0;
                    var rowData = _grid.Rows[e.RowIndex].Tag as ZabbixAgentData;
                    var iface = rowData?.NetIfs.FirstOrDefault(x => x.Name == col.Substring(4));
                    if (iface != null && iface.Speed > 0) pct = (double)(iface.InBps + iface.OutBps) * 8 / iface.Speed * 100;
                    if (pct >= settings.NetCrit) e.CellStyle.ForeColor = Drw.Color.FromArgb(0xDC, 0x26, 0x26);
                    else if (pct >= settings.NetWarn) e.CellStyle.ForeColor = Drw.Color.FromArgb(0xF5, 0x9E, 0x0B);
                }
            }
            // Disk I/O columns
            if (col.StartsWith("DiskIO_") && e.Value?.ToString() is string div && div.Contains("ms"))
            {
                if (double.TryParse(div.Replace("ms", ""), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var lat))
                {
                    if (lat > 20) e.CellStyle.ForeColor = Drw.Color.FromArgb(0xDC, 0x26, 0x26);
                    else if (lat > 10) e.CellStyle.ForeColor = Drw.Color.FromArgb(0xF5, 0x9E, 0x0B);
                }
            }
        }

        private void OnSelectionChanged(object sender, EventArgs e)
        {
            if (_suppressDetailsOnRightClick || _rebuildingGrid) return;
            if (_grid.SelectedRows.Count == 0)
            {
                _detailsPanel.Visible = false;
                _detailsPanel.Height = 0;
                // v3.9.3AF — детали закрыты: пилы статистики возвращаем.
                try { if (_view == DashView.Monitor) _statsStrip.Visible = true; } catch { }
                // v4.8 — web возвращается только во вьюхе Web.
                if (_view == DashView.Web) _webSection.Visible = true;
                LayoutTables();
                return;
            }
            var data = _grid.SelectedRows[0].Tag as ZabbixAgentData;
            if (data == null) return;
            // v3.9.3BA — нижней панели деталей больше нет (только всплывашка
            // по кнопке): выбор лишь подсвечивает строку и держит её видимой.
            // Строка хоста выбрана — снимаем выбор web-строки.
            if (_webGrid.SelectedRows.Count > 0)
            {
                _suppressSelSync = true;
                try { _webGrid.ClearSelection(); } finally { _suppressSelSync = false; }
            }
            EnsureSelectedRowVisible();
        }

        // v4.6.3 — WEB отдельной таблицей: строки + высота секции по контенту
        // (шапка + до 6 строк, дальше скролл). Выбор взаимоисключающий
        // с главной таблицей. Статус подсвечиваем через CellFormatting.
        private void RefreshWebGrid()
        {
            if (IsDisposed || _webGrid.IsDisposed) return;
            _suppressSelSync = true;
            try
            {
                _webGrid.Rows.Clear();
                var urls = WebMonitorStore.LoadUrls();
                // v4.8 — фильтр Web-вьюхи по имени/URL (бокс шапки).
                if (!string.IsNullOrWhiteSpace(_filterTextWeb))
                {
                    string f = _filterTextWeb.Trim();
                    urls = urls.Where(c => (c.Name ?? "").Contains(f, StringComparison.OrdinalIgnoreCase)
                        || (c.Url ?? "").Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
                }
                var hist = WebMonitorStore.LoadHistory();
                _webLast.Clear();
                int okCount = 0;
                foreach (var c in urls)
                {
                    var last = hist.Where(x => string.Equals(x.Url, c.Url, StringComparison.OrdinalIgnoreCase)).OrderByDescending(x => x.Time).FirstOrDefault();
                    _webLast[c.Url] = last;
                    if (last != null && last.Ok && c.Enabled) okCount++;
                    string label = string.IsNullOrWhiteSpace(c.Name) ? c.Url : c.Name;
                    string status = !c.Enabled ? "выкл" : last == null ? "—" : last.Ok ? "OK" : "DOWN";
                    string check = last == null ? "" : (last.StatusCode > 0 ? $"HTTP {last.StatusCode} · " : "") + $"{last.Ms}мс · {last.Time:HH:mm:ss}";
                    int idx = _webGrid.Rows.Add(label, c.Url, status, check);
                    _webGrid.Rows[idx].Tag = c.Url;
                }
                _webTitle.Text = urls.Count == 0
                    ? "Мониторинг web — нет ресурсов (ПКМ → Добавить URL)"
                    : $"Мониторинг web — {urls.Count}, OK: {okCount}";
                int rowsH = Math.Clamp(urls.Count, 1, 6) * 36;
                _webSection.Height = 24 + 36 + rowsH + 8;
                LayoutTables();
            }
            finally { _suppressSelSync = false; }
        }

        private void OnWebGridCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _webGrid.Rows.Count) return;
            if (_webGrid.Columns[e.ColumnIndex].Name != "Status") return;
            string v = e.Value?.ToString() ?? "";
            if (v == "OK") e.CellStyle.ForeColor = Drw.Color.FromArgb(240, 160, 80);
            else if (v == "DOWN") e.CellStyle.ForeColor = Drw.Color.FromArgb(220, 38, 38);
            else e.CellStyle.ForeColor = UiTheme.MutedForeground;
        }

        private void OnWebGridSelectionChanged(object? sender, EventArgs e)
        {
            if (_suppressSelSync) return;
            if (_webGrid.SelectedRows.Count == 0)
            {
                _detailsPanel.Visible = false;
                _detailsPanel.Height = 0;
                _detailsWebUrl = "";
                // v3.9.3AF — детали закрыты: пилы статистики возвращаем.
                try { if (_view == DashView.Monitor) _statsStrip.Visible = true; } catch { }
                return;
            }
            string url = _webGrid.SelectedRows[0].Tag as string ?? "";
            if (string.IsNullOrEmpty(url)) return;
            _suppressSelSync = true;
            try { _grid.ClearSelection(); } finally { _suppressSelSync = false; }
            // v3.9.3BA — нижней панели больше нет: только выбор + доводка.
            // Дотягиваем выбранную web-строку в видимую зону секции.
            try
            {
                int idx = _webGrid.SelectedRows[0].Index;
                int first = _webGrid.FirstDisplayedScrollingRowIndex;
                int visible = Math.Max(1, _webGrid.DisplayedRowCount(false));
                if (idx < first) _webGrid.FirstDisplayedScrollingRowIndex = idx;
                else if (idx >= first + visible) _webGrid.FirstDisplayedScrollingRowIndex = Math.Max(0, idx - visible + 1);
            }
            catch { }
        }

        // v4.7 — дожиматель выбора по клику (v3.9.3BA: без нижней панели —
        // только выбор). Повторный клик по уже выбранной строке молчит.
        private void OnWebGridCellClick(object? sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _webGrid.Rows.Count) return;
            try
            {
                string url = _webGrid.Rows[e.RowIndex].Tag as string ?? "";
                if (string.IsNullOrEmpty(url)) return;
                _suppressSelSync = true;
                try
                {
                    _webGrid.ClearSelection();
                    _webGrid.Rows[e.RowIndex].Selected = true;
                    _grid.ClearSelection();
                }
                finally { _suppressSelSync = false; }
            }
            catch { }
        }

        private void OnWebGridCellMouseDown(object? sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;
            if (e.RowIndex < 0)
            {
                var menu = new ThemedContextMenu();
                menu.AddItem("Добавить URL...", "add", () => ShowAddWebUrlDialog());
                menu.AddItem("Обновить web сейчас", "info", async () => await RefreshWebNowAsync());
                menu.Show(this, Control.MousePosition);
                return;
            }
            _suppressSelSync = true;
            try
            {
                _webGrid.ClearSelection();
                _webGrid.Rows[e.RowIndex].Selected = true;
            }
            finally { _suppressSelSync = false; }
            string url = _webGrid.Rows[e.RowIndex].Tag as string ?? "";
            if (string.IsNullOrEmpty(url)) return;
            ShowWebRowMenu(url);
        }

        private void ShowWebRowMenu(string url)
        {
            var urls = WebMonitorStore.LoadUrls();
            var check = urls.FirstOrDefault(c => string.Equals(c.Url, url, StringComparison.OrdinalIgnoreCase));
            var menu = new ThemedContextMenu();
            menu.AddHeader("Web-ресурс");
            menu.AddItem("Открыть в браузере", "globe", () => OpenUrlInBrowser(url));
            menu.AddItem("Проверить сейчас", "info", async () => await RefreshWebNowAsync());
            menu.AddSeparator();
            if (check != null)
                menu.AddItem(check.Enabled ? "Выключить" : "Включить", "stop", () =>
                {
                    check.Enabled = !check.Enabled;
                    WebMonitorStore.SaveUrls(urls);
                    ApplyFilters();
                    RefreshWebGrid();
                });
            menu.AddItem("Удалить URL...", "delete", () => DeleteWebUrl(url));
            menu.Show(this, Control.MousePosition);
        }

        private void HideDetails()
        {
            _suppressDetailsOnRightClick = true;
            _detailsPanel.Visible = false;
            _detailsPanel.Height = 0;
            _detailsWebUrl = "";
            _detailsHost = "";
            // v3.9.3AF — детали закрыты: пилы статистики возвращаем.
            try { if (_view == DashView.Monitor) _statsStrip.Visible = true; } catch { }
            // v4.8 — web-секция возвращается только во вьюхе Web
            // (в Monitor она живёт только там, в Events скрыта всегда).
            if (_view == DashView.Web) _webSection.Visible = true;
            _grid.ClearSelection();
            _suppressSelSync = true;
            try { _webGrid.ClearSelection(); } finally { _suppressSelSync = false; }
            _suppressDetailsOnRightClick = false;
            LayoutTables();
        }

        // v4.8 — переключение вьюх единого дашборда (пил в шапке).
        // Матрица видимости:
        //   Events:  всё Z скрыто (фильтр, title/tabs/sep, грид, web, filler), виден _eventsHost;
        //   Monitor: классика Z без web-секции;
        //   Web:     только web-секция (+filler), грид и title/tabs/sep скрыты.
        // v3.9.3A — подсветка активного пункта правой группы hero.
        private void SetViewActive(DashView v)
        {
            try
            {
                if (_viewItems == null) return;
                foreach (var item in _viewItems)
                {
                    if (item == null || item.IsDisposed) continue;
                    var tag = item.Tag as object?[];
                    if (tag == null || tag.Length < 4) continue;
                    bool act = tag[0] is DashView dv && dv == v;
                    tag[1] = act;
                    tag[2] = false;
                    try { item.Font = act ? new Drw.Font("Segoe UI Semibold", 9f) : new Drw.Font("Segoe UI", 9f); } catch { }
                    try { item.Invalidate(); } catch { }
                }
            }
            catch { }
        }

        public void SwitchView(DashView v)
        {
            try { HideDetails(); } catch { }
            _view = v;
            try { SetViewActive(v); } catch { }
            if (v == DashView.Events) EnsureEventsEmbedded();
            ApplyViewVisibility();
            UpdateFilterPlaceholder();
            UpdateHeaderButtons();
            RefreshCurrentViewData();
            UpdateStatus();
            LayoutTables();
        }

        private void ApplyViewVisibility()
        {
            bool isEvents = _view == DashView.Events;
            bool isMonitor = _view == DashView.Monitor;
            bool isWeb = _view == DashView.Web;
            // v4.9.23 — ряд фильтров Событий в hero виден только в Events;
// шапка всегда 48px (всё в один ряд). Позиция ряда — здесь же,
// а не в Load/Resize (порядок тех событий ненадёжен): SwitchView
// всегда идёт по settled-раскладке.
            try { _heroRow.Visible = isEvents; } catch { }
            try
            {
                if (isEvents && _actHostPanel != null)
                {
                    _heroRow.Location = new Point(_actHostPanel.Right + 8, 6);
                    _heroRow.Width = Math.Max(0, _viewGroup.Left - 8 - _heroRow.Left);
                }
            }
            catch { }
            try { if (_header != null && !_header.IsDisposed) _header.Height = 48; } catch { }
            try { _filterPanel.Visible = !isEvents; } catch { }
            // v3.9.3A — статистика видна только в Monitor.
            // v3.9.3AD — полоса переехала под таблицу.
            try { _statsStrip.Visible = isMonitor; } catch { }
            try { if (isMonitor) UpdateStats(); } catch { }
            try { _hostPanel.Visible = isMonitor; } catch { }
            try { _grid.Visible = isMonitor; } catch { }
            try { _webSection.Visible = isWeb; } catch { }
            try { _tablesFiller.Visible = !isEvents; } catch { }
            try { _eventsHost.Visible = isEvents; } catch { }
            // v4.8 — в Events единый футер скрыт: статус показывает встроенный
            // даш W, иначе один и тот же текст виден дважды.
            try { _statusPanel.Visible = !isEvents; } catch { }
            if (isEvents) { try { _eventsHost.BringToFront(); } catch { } }
        }

        private void EnsureEventsEmbedded()
        {
            if (_eventsForm != null && !_eventsForm.IsDisposed) return;
            _eventsForm = new Forms.DashboardForm();
            try { _eventsForm.PrepareForEmbeddedEvents(); } catch { }
            _eventsForm.TopLevel = false;
            _eventsForm.FormBorderStyle = FormBorderStyle.None;
            _eventsForm.Dock = DockStyle.Fill;
            _eventsForm.Visible = true;
            _eventsHost.Controls.Add(_eventsForm);
            _eventsForm.Show();
            Logger.Info("Dash: DashboardForm embedded into _eventsHost");
        }

        private void UpdateFilterPlaceholder()
        {
            try
            {
                var box = Helpers.UiSearchBox.GetBox(_searchWrapper);
                string incoming = _view == DashView.Web ? _filterTextWeb : _filterText;
                if (box != null && box.Text != incoming) Helpers.UiSearchBox.SetText(_searchWrapper, incoming);
                Helpers.UiSearchBox.SetCue(_searchWrapper, _view == DashView.Web ? "Фильтр по URL…" : "Фильтр по хосту…");
            }
            catch { }
        }

        private void UpdateHeaderButtons()
        {
            try
            {
                bool cfg = _view == DashView.Monitor;
                // «Настроить агентов» — кнопка 2, видна только в Monitor.
                // v3.9.3BQ — видимость по явному флагу, НЕ по Control.Visible:
                // в конструкторе всё false и ширина схлопывалась в 0.
                // Перекомпоновка видимых кнопок + ширина хоста.
                try
                {
                    if (_actBtns != null && _actBtns.Length > 2)
                    {
                        int bx = 0;
                        for (int i = 0; i < _actBtns.Length; i++)
                        {
                            bool vis = i == 2 ? cfg : true;
                            _actBtns[i].Visible = vis;
                            if (!vis) continue;
                            _actBtns[i].Location = new Point(bx, 0);
                            bx += _actBtns[i].Width + 8;
                        }
                        if (_actHostPanel != null) _actHostPanel.Width = Math.Max(0, bx - 8);
                    }
                }
                catch { }
            }
            catch { }
        }

        private void RefreshCurrentViewData()
        {
            try
            {
                if (_view == DashView.Events) return; // живые данные своим поллером
                if (_view == DashView.Web) { RefreshWebGrid(); return; }
                ApplyFilters();
            }
            catch { }
        }

        private async Task RefreshCurrentViewAsync()
        {
            try
            {
                if (_view == DashView.Events)
                {
                    if (_eventsForm != null && !_eventsForm.IsDisposed)
                        await _eventsForm.RefreshEventsNowAsync();
                    return;
                }
                if (_view == DashView.Web) { await RefreshWebNowAsync(); return; }
                await RefreshNowAsync();
            }
            catch { }
        }

        private void ShowCurrentSettings()
        {
            try
            {
                if (_view == DashView.Events && _eventsForm != null && !_eventsForm.IsDisposed)
                    _eventsForm.ShowEventsSettings();
                else ShowSettings();
            }
            catch { }
        }

        // Открытая снизу панель перекрывает нижние строки (особенно web-строки
        // в конце таблицы) — дотягиваем выбранную строку в видимую зону.
        // Считаем ТОЛЬКО целиком видимые строки (partial врёт на единицу).
        // Цепочка перепроверок следующими тиками очереди: раскладка оседает
        // не мгновенно, метод идемпотентен — молчит, когда уже видно.
        private void EnsureSelectedRowVisible(int triesLeft = 4)
        {
            try
            {
                if (IsDisposed || _grid.IsDisposed || _grid.SelectedRows.Count == 0 || _grid.Rows.Count == 0) return;
                int idx = _grid.SelectedRows[0].Index;
                int first = _grid.FirstDisplayedScrollingRowIndex;
                int visible = Math.Max(1, _grid.DisplayedRowCount(false));
                bool moved = false;
                if (idx < first) { _grid.FirstDisplayedScrollingRowIndex = idx; moved = true; }
                else if (idx >= first + visible) { _grid.FirstDisplayedScrollingRowIndex = Math.Max(0, idx - visible + 1); moved = true; }
                // v3.9.3N — цепочку перепроверок тянем только если реально
                // двигали скролл; иначе это 4 холостых репейнта на каждый клик.
                if (!moved) return;
            }
            catch { return; }
            if (triesLeft > 0)
            {
                int left = triesLeft - 1;
                try { BeginInvoke(new Action(() => EnsureSelectedRowVisible(left))); } catch { }
            }
        }

        // v4.6 — детали web-ресурса: статус, график времени ответа, последние
        // проверки + действия. Тот же визуальный язык, что у деталей хоста.
        private void BuildWebDetails(string url)
        {
            _detailsWebUrl = url ?? "";
            // v3.9.3AF — детали открыты: пилы статистики прячем.
            try { _statsStrip.Visible = false; } catch { }
            _detailsContent.Controls.Clear();
            var checks = WebMonitorStore.LoadUrls();
            var check = checks.FirstOrDefault(c => string.Equals(c.Url, url, StringComparison.OrdinalIgnoreCase));
            string name = check != null && !string.IsNullOrWhiteSpace(check.Name) ? check.Name : url;
            var last = WebMonitorStore.LatestFor(url);
            bool has = last != null;
            bool ok = has && last.Ok;

            var titleRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, Dock = DockStyle.Top, BackColor = UiTheme.Background, Margin = new Padding(0, 0, 0, 8) };
            titleRow.Controls.Add(new WinForms.Label { Text = name, Font = new Drw.Font("Segoe UI Semibold", 12f), ForeColor = UiTheme.Foreground, AutoSize = true, Margin = new Padding(0, 1, 8, 0) });
            titleRow.Controls.Add(CreatePill("WEB", UiTheme.Accent, UiTheme.AccentSoft));
            titleRow.Controls.Add(CreatePill(!has ? "нет данных" : last.Ok ? $"HTTP {last.StatusCode} · {last.Ms}мс" : (last.StatusCode > 0 ? $"HTTP {last.StatusCode}" : last.Error),
                !has ? UiTheme.MutedForeground : last.Ok ? Drw.Color.FromArgb(110, 138, 110) : Drw.Color.FromArgb(184, 126, 115),
                !has ? UiTheme.Card : last.Ok ? Drw.Color.FromArgb(233, 240, 232) : Drw.Color.FromArgb(249, 232, 229)));
            _detailsContent.Controls.Add(titleRow);

            // Панель действий.
            var actRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, Dock = DockStyle.Top, BackColor = UiTheme.Background, Margin = new Padding(0, 0, 0, 8) };
            var btnOpen = UiButtons.MakeCompactFilled("Открыть в браузере", 160);
            btnOpen.Click += (s, e) => OpenUrlInBrowser(url);
            var btnCheck = UiButtons.MakeCompactFilled("Проверить сейчас", 150);
            btnCheck.Click += async (s, e) => await RefreshWebNowAsync();
            var btnDel = UiButtons.MakeCompactFilled("Удалить URL", 120, bgOverride: UiTheme.MutedForeground);
            btnDel.Click += (s, e) => DeleteWebUrl(url);
            actRow.Controls.Add(btnOpen);
            actRow.Controls.Add(btnCheck);
            actRow.Controls.Add(btnDel);
            foreach (Control b in actRow.Controls) b.Margin = new Padding(0, 0, 8, 0);
            _detailsContent.Controls.Add(actRow);

            var hist = WebMonitorStore.LoadHistory().Where(x => string.Equals(x.Url, url, StringComparison.OrdinalIgnoreCase)).OrderBy(x => x.Time).TakeLast(16).ToList();
            if (hist.Count >= 2)
            {
                _detailsContent.Controls.Add(CreateSectionHeader($"Время ответа — {hist.Count} точек", UiTheme.Accent));
                var pMs = new Panel { Dock = DockStyle.Top, Height = 140, BackColor = Drw.Color.White, Margin = new Padding(4), Padding = new Padding(8, 6, 8, 6) };
                pMs.Paint += (s, e) => { e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias; using var pen = new Pen(UiTheme.BorderSubtle, 1); using var path = UiButtons.RoundedRect(new Rectangle(0, 0, pMs.Width - 1, pMs.Height - 1), 10); e.Graphics.DrawPath(pen, path); };
                RoundCard(pMs, 10);
                var chart = new Panel { Dock = DockStyle.Fill, BackColor = Drw.Color.White, Margin = new Padding(0, 2, 0, 2) };
                chart.Paint += (s, e) =>
                {
                    e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    long mMax = Math.Max(1, hist.Max(x => x.Ms));
                    int n = hist.Count, w = chart.Width, h = chart.Height, plotH = Math.Max(10, h - 14), gap = 2;
                    int bw = Math.Max(3, (w - (n - 1) * gap) / n);
                    for (int i = 0; i < n; i++)
                    {
                        long v = hist[i].Ms;
                        int bh = Math.Max(3, (int)Math.Round((double)v / mMax * (plotH - 2)));
                        int x = i * (bw + gap), y = plotH - bh;
                        var col = hist[i].Ok ? Drw.Color.FromArgb(143, 169, 139) : Drw.Color.FromArgb(216, 175, 163);
                        using var br = new SolidBrush(col);
                        using var bar = RoundedTopBar(x, y, bw, bh, 3);
                        e.Graphics.FillPath(br, bar);
                    }
                    using var basePen = new Pen(UiTheme.BorderSubtle, 1);
                    e.Graphics.DrawLine(basePen, 0, plotH, w, plotH);
                    using var fnt = new Drw.Font("Consolas", 6.5f);
                    TextRenderer.DrawText(e.Graphics, hist.First().Time.ToString("HH:mm"), fnt, new Rectangle(0, plotH + 2, 60, 11), UiTheme.MutedForeground, TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.SingleLine);
                    TextRenderer.DrawText(e.Graphics, hist.Last().Time.ToString("HH:mm"), fnt, new Rectangle(w - 60, plotH + 2, 60, 11), UiTheme.MutedForeground, TextFormatFlags.Right | TextFormatFlags.Top | TextFormatFlags.SingleLine);
                };
                pMs.Controls.Add(chart);
                _detailsContent.Controls.Add(pMs);

                _detailsContent.Controls.Add(CreateSectionHeader("Последние проверки", UiTheme.MutedForeground));
                var flow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Top, BackColor = UiTheme.Background, Margin = new Padding(0, 2, 0, 6) };
                foreach (var p in hist.OrderByDescending(x => x.Time).Take(5))
                {
                    string txt = $"{p.Time:dd.MM HH:mm:ss}   {(p.Ok ? $"HTTP {p.StatusCode}" : (p.StatusCode > 0 ? $"HTTP {p.StatusCode}" : "FAIL"))}   {p.Ms}мс" +
                        (string.IsNullOrEmpty(p.Error) || p.Ok ? "" : $"   {p.Error}");
                    var row = new Panel { Size = new Size(460, 26), BackColor = Drw.Color.White, Margin = new Padding(4, 2, 4, 2) };
                    row.Paint += (s, e) => { e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias; using var pen = new Pen(UiTheme.BorderSubtle, 1); using var path = UiButtons.RoundedRect(new Rectangle(0, 0, row.Width - 1, row.Height - 1), 8); e.Graphics.DrawPath(pen, path); };
                    RoundCard(row, 8);
                    var dotCol = p.Ok ? Drw.Color.FromArgb(240, 160, 80) : Drw.Color.FromArgb(216, 175, 163);
                    var lbl = new WinForms.Label { Text = "●  " + txt, Font = new Drw.Font("Consolas", 7.5f), ForeColor = p.Ok ? dotCol : Drw.Color.FromArgb(184, 126, 115), AutoSize = true, Location = new Point(8, 5) };
                    row.Controls.Add(lbl);
                    flow.Controls.Add(row);
                }
                _detailsContent.Controls.Add(flow);
            }
            else
            {
                _detailsContent.Controls.Add(new WinForms.Label { Text = "Мало данных — дождитесь 2+ опросов.", Font = new Drw.Font("Segoe UI", 8f), ForeColor = UiTheme.MutedForeground, AutoSize = true, Margin = new Padding(4, 6, 0, 0) });
            }
            _detailsPanel.Visible = true;
            _detailsPanel.Height = _manualDetailsHeight > 0
                ? Math.Min(_manualDetailsHeight, MaxDetailsHeight())
                : Math.Min(520, Math.Min(MaxDetailsHeight(), _detailsContent.PreferredSize.Height + 16));
            LayoutTables();
            // Синхронно дожимаем док-раскладку (грид уже ужался) плюс цепочка
            // перепроверок следующими тиками — см. EnsureSelectedRowVisible.
            try { PerformLayout(); } catch { }
            EnsureSelectedRowVisible();
        }

        private void ShowAddWebUrlDialog()
        {
            var owner = this.FindForm() ?? this.TopLevelControl as Form ?? this;
            using var dlg = new Form { Text = "Добавить web-ресурс", Size = new Size(420, 250), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, BackColor = UiTheme.Card };
            dlg.KeyPreview = true;
            dlg.KeyDown += (s2, e2) => { if (e2.KeyCode == Keys.Escape) { e2.Handled = true; dlg.DialogResult = DialogResult.Cancel; dlg.Close(); } };
            int y = 16;
            var lblName = new WinForms.Label { Text = "Название:", Location = new Point(16, y + 2), AutoSize = true, ForeColor = UiTheme.Foreground };
            var txtName = new TextBox { Location = new Point(120, y), Width = 270 };
            y += 28;
            var lblUrl = new WinForms.Label { Text = "URL:", Location = new Point(16, y + 2), AutoSize = true, ForeColor = UiTheme.Foreground };
            var txtUrl = new TextBox { Location = new Point(120, y), Width = 270, PlaceholderText = "https://..." };
            y += 28;
            var lblTimeout = new WinForms.Label { Text = "Таймаут (мс):", Location = new Point(16, y + 2), AutoSize = true, ForeColor = UiTheme.Foreground };
            var numTimeout = new EscNumericUpDown { Minimum = 1000, Maximum = 60000, Increment = 1000, Value = 10000, Location = new Point(120, y), Width = 90 };
            y += 16;
            var btnOk = UiButtons.MakeCompactFilled("Сохранить", 100);
            btnOk.Location = new Point(dlg.ClientSize.Width - 233, dlg.ClientSize.Height - 47);
            btnOk.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnOk.Click += (s2, e2) =>
            {
                string url = txtUrl.Text.Trim();
                if (!Uri.TryCreate(url, UriKind.Absolute, out var u) ||
                    (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))
                {
                    MessageBox.Show("Введите корректный http(s) URL.", "Добавить URL", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                dlg.DialogResult = DialogResult.OK;
                dlg.Close();
            };
            var btnCancel = UiButtons.MakeCompactFilled("Отмена", 100, bgOverride: UiTheme.MutedForeground);
            btnCancel.Location = new Point(dlg.ClientSize.Width - 125, dlg.ClientSize.Height - 47);
            btnCancel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnCancel.Click += (s2, e2) => { dlg.DialogResult = DialogResult.Cancel; dlg.Close(); };
            var hiddenOk = new Button { DialogResult = DialogResult.OK, Visible = false, Location = new Point(-100, -100), Size = new Size(1, 1) };
            dlg.Controls.Add(hiddenOk);
            var hiddenCancel = new Button { DialogResult = DialogResult.Cancel, Visible = false, Location = new Point(-100, -100), Size = new Size(1, 1) };
            dlg.Controls.Add(hiddenCancel);
            dlg.AcceptButton = hiddenOk;
            dlg.CancelButton = hiddenCancel;
            dlg.Controls.AddRange(new Control[] { lblName, txtName, lblUrl, txtUrl, lblTimeout, numTimeout, btnOk, btnCancel });
            if (UiModal.ShowDimmed(owner, dlg) != DialogResult.OK) return;
            string furl = txtUrl.Text.Trim();
            string fname = txtName.Text.Trim();
            if (string.IsNullOrEmpty(fname))
            {
                try { fname = new Uri(furl).Host; } catch { fname = furl; }
            }
            var urls = WebMonitorStore.LoadUrls();
            if (urls.Any(c => string.Equals(c.Url, furl, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show("Такой URL уже есть.", "Добавить URL", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            urls.Add(new Models.WebCheck { Name = fname, Url = furl, TimeoutMs = (int)numTimeout.Value, Enabled = true });
            WebMonitorStore.SaveUrls(urls);
            Logger.Info($"WebMonitor: added {furl}");
            ApplyFilters();
            RefreshWebGrid();
            // Сразу выбираем новую строку — открываются детали.
            foreach (DataGridViewRow r in _webGrid.Rows)
                if (string.Equals(r.Tag as string, furl, StringComparison.OrdinalIgnoreCase)) { r.Selected = true; break; }
            _ = RefreshWebNowAsync();
        }

        private void DeleteWebUrl(string url)
        {
            var r = MessageBox.Show($"Убрать '{url}' из web-мониторинга?\n\nИстория проверок по нему тоже будет удалена.",
                "Удалить URL", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r != DialogResult.Yes) return;
            try
            {
                var urls = WebMonitorStore.LoadUrls();
                urls.RemoveAll(c => string.Equals(c.Url, url, StringComparison.OrdinalIgnoreCase));
                WebMonitorStore.SaveUrls(urls);
                WebMonitorStore.DeleteUrlHistory(url);
                // Перестройка сбросит выбор — детали спрячутся сами.
                ApplyFilters();
                RefreshWebGrid();
                Logger.Info($"WebMonitor: deleted {url}");
            }
            catch (Exception ex) { MessageBox.Show($"Ошибка: {ex.Message}", "Удалить URL", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private void BuildDetailsPanel(ZabbixAgentData d)
        {
            _detailsWebUrl = "";
            _detailsHost = d.Host ?? "";
            // v3.9.3AF — детали открыты: пилы статистики прячем (грид выше).
            // v3.9.3AZ — всплывашке нижняя панель не мешает.
            if (!_popupBuild) { try { _statsStrip.Visible = false; } catch { } }
            // v3.9.2 — детали хоста раскрываются на всё свободное место:
            // web-секцию прячем, возвращаем в HideDetails/снятии выбора.
            // v3.9.3AZ — всплывашке не прячем.
            if (!_popupBuild) _webSection.Visible = false;
            // v3.9.3N — старые контролы dispose'им (иначе утечка GDI-шрифтов
            // и тормозов всё больше с каждым кликом), раскладку замораживаем
            // на время сборки, чтобы не было шторма лэйаутов и тиринга.
            _detailsContent.SuspendLayout();
            try
            {
                while (_detailsContent.Controls.Count > 0)
                {
                    var oldCtl = _detailsContent.Controls[0];
                    _detailsContent.Controls.RemoveAt(0);
                    try { oldCtl.Dispose(); } catch { }
                }
            // Заголовок: крупный хост + мягкие чипы версии/времени
            var titleRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, Dock = DockStyle.Top, BackColor = UiTheme.Background, Margin = new Padding(0, 0, 0, 8) };
            var lblHost = new WinForms.Label { Text = d.Host, Font = new Drw.Font("Segoe UI Semibold", 12f), ForeColor = UiTheme.Foreground, AutoSize = true, Margin = new Padding(0, 1, 8, 0) };
            titleRow.Controls.Add(lblHost);
            titleRow.Controls.Add(CreatePill(d.AgentVersion, UiTheme.MutedForeground, UiTheme.Card));
            titleRow.Controls.Add(CreatePill(d.Time.ToString("HH:mm:ss"), UiTheme.MutedForeground, UiTheme.Card));
            _detailsContent.Controls.Add(titleRow);

            // KPI как в wow2:818 — CPU / RAM FREE карточки
            var kpiRow = new TableLayoutPanel { Dock = DockStyle.Top, Height = 68, ColumnCount = 2, BackColor = UiTheme.Background, Margin = new Padding(0, 4, 0, 8) };
            kpiRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            kpiRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            kpiRow.Controls.Add(CreateKpiCard("CPU", $"{d.CpuLoad:0.0}%", d.CpuLoad, 80, 95, false), 0, 0);
            kpiRow.Controls.Add(CreateKpiCard("RAM FREE", $"{d.MemPFree:0.0}%", d.MemPFree, 15, 5, true), 1, 0);
            _detailsContent.Controls.Add(kpiRow);

            // Диски — карточки как wow2:760
            if (d.Disks.Count > 0)
            {
                _detailsContent.Controls.Add(CreateSectionHeader($"Диски — {d.Disks.Count}", UiTheme.MutedForeground));
                var diskFlow = new FlowLayoutPanel { AutoSize = true, WrapContents = true, FlowDirection = FlowDirection.LeftToRight, Dock = DockStyle.Top, BackColor = UiTheme.Background, Margin = new Padding(0, 2, 0, 6) };
                foreach (var dk in d.Disks.Take(4))
                {
                    double pct = dk.PFree;
                    double used = 100 - pct;
                    var col = pct <= 10 ? Drw.Color.FromArgb(216, 175, 163) : pct <= 20 ? Drw.Color.FromArgb(200, 150, 80) : Drw.Color.FromArgb(143, 169, 139);
                    var card = new Panel { Size = new Size(160, 60), BackColor = Drw.Color.White, Margin = new Padding(4), Padding = new Padding(7, 6, 7, 6) };
                    card.Paint += (s, e) => { e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias; using var pen = new Pen(UiTheme.BorderSubtle, 1); using var path = UiButtons.RoundedRect(new Rectangle(0, 0, card.Width - 1, card.Height - 1), 10); e.Graphics.DrawPath(pen, path); };
                    var lblMount = new WinForms.Label { Text = dk.Mount, Font = new Drw.Font("Segoe UI Semibold", 8f), ForeColor = UiTheme.Foreground, AutoSize = true, Location = new Point(7, 6) };
                    var lblPct = new WinForms.Label { Text = $"{pct:0.0}%", Font = new Drw.Font("Segoe UI Semibold", 8f), ForeColor = pct <= 10 ? Drw.Color.FromArgb(184, 126, 115) : pct <= 20 ? Drw.Color.FromArgb(146, 64, 14) : Drw.Color.FromArgb(110, 138, 110), AutoSize = true, Location = new Point(118, 6) };
                    var barBg = new Panel { Location = new Point(7, 26), Size = new Size(140, 5), BackColor = Drw.Color.FromArgb(246, 241, 231) };
                    var barFill = new Panel { Location = new Point(0, 0), Size = new Size((int)Math.Round(used / 100 * 140), 5), BackColor = col };
                    barBg.Controls.Add(barFill);
                    var lblBytes = new WinForms.Label { Text = $"{FormatBytes(dk.Free)} / {FormatBytes(dk.Total)}", Font = new Drw.Font("Consolas", 6.5f), ForeColor = UiTheme.MutedForeground, AutoSize = true, Location = new Point(7, 36) };
                    card.Controls.Add(lblMount); card.Controls.Add(lblPct); card.Controls.Add(barBg); card.Controls.Add(lblBytes);
                    RoundCard(card, 10);
                    diskFlow.Controls.Add(card);
                }
                _detailsContent.Controls.Add(diskFlow);
            }

            if (d.NetIfs.Count > 0)
            {
                _detailsContent.Controls.Add(CreateSectionHeader("Сеть", UiTheme.MutedForeground));
                var nets = d.NetIfs.Where(n => n.TotalBps > 0).OrderByDescending(n => n.TotalBps).Take(3).ToList();
                if (nets.Count == 0) nets = d.NetIfs.Where(n => !System.Text.RegularExpressions.Regex.IsMatch(n.Name, "Loopback|ISATAP|Kernel|WFP|QoS")).Take(3).ToList();
                double maxNet = Math.Max(1, nets.Max(n => (double)n.TotalBps));
                var netFlow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Top, BackColor = UiTheme.Background, Margin = new Padding(0, 2, 0, 6) };
                foreach (var n in nets)
                {
                    var shortName = System.Text.RegularExpressions.Regex.Replace(n.Name, "Microsoft | Hyper-V | Network Adapter", "").Trim();
                    if (shortName.Length > 28) shortName = shortName.Substring(0, 28);
                    var pct = Math.Max(5, Math.Round(n.TotalBps / maxNet * 100));
                    var row = new Panel { Size = new Size(340, 30), BackColor = Drw.Color.White, Margin = new Padding(4, 2, 4, 2), Padding = new Padding(6, 4, 6, 4) };
                    row.Paint += (s, e) => { e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias; using var pen = new Pen(UiTheme.BorderSubtle, 1); using var path = UiButtons.RoundedRect(new Rectangle(0, 0, row.Width - 1, row.Height - 1), 8); e.Graphics.DrawPath(pen, path); };
                    var lblName = new WinForms.Label { Text = shortName, Font = new Drw.Font("Segoe UI", 7.5f), ForeColor = UiTheme.Foreground, AutoSize = true, Location = new Point(6, 4) };
                    var barBg2 = new Panel { Location = new Point(6, 18), Size = new Size(200, 3), BackColor = Drw.Color.FromArgb(246, 241, 231) };
                    var barFill2 = new Panel { Location = new Point(0, 0), Size = new Size((int)Math.Round(pct / 100 * 200), 3), BackColor = Drw.Color.FromArgb(143, 169, 139) };
                    barBg2.Controls.Add(barFill2);
                    var lblBps = new WinForms.Label { Text = $"{FormatBps(n.TotalBps)}  ↓{FormatBps(n.InBps)} ↑{FormatBps(n.OutBps)}", Font = new Drw.Font("Consolas", 7f), ForeColor = UiTheme.MutedForeground, AutoSize = true, Location = new Point(220, 8) };
                    row.Controls.Add(lblName); row.Controls.Add(barBg2); row.Controls.Add(lblBps);
                    RoundCard(row, 8);
                    netFlow.Controls.Add(row);
                }
                _detailsContent.Controls.Add(netFlow);
            }
            var crit = d.Services.Where(s => s.IsCritical).ToList();
            if (crit.Count > 0)
            {
                _detailsContent.Controls.Add(CreateSectionHeader($"Службы — {crit.Count(s => s.State == "running")}/{crit.Count} OK", crit.Any(s => s.State != "running") ? Drw.Color.FromArgb(176, 122, 114) : UiTheme.MutedForeground));
                var flow = new FlowLayoutPanel { AutoSize = true, WrapContents = true, FlowDirection = FlowDirection.LeftToRight, Dock = DockStyle.Top, BackColor = UiTheme.Background, Margin = new Padding(0, 2, 0, 6) };
                foreach (var s in crit.Take(12))
                {
                    bool ok = s.State == "running" || s.State == "0" || s.State == "4";
                    flow.Controls.Add(CreatePill((ok ? "● " : "■ ") + s.Name,
                        ok ? Drw.Color.FromArgb(110, 138, 110) : Drw.Color.FromArgb(184, 126, 115),
                        ok ? Drw.Color.FromArgb(233, 240, 232) : Drw.Color.FromArgb(249, 232, 229)));
                }
                _detailsContent.Controls.Add(flow);
            }
            if (d.DiskIo.Count > 0)
            {
                _detailsContent.Controls.Add(CreateSectionHeader("Диск I/O", UiTheme.MutedForeground));
                double maxIo = Math.Max(1, d.DiskIo.Max(x => Math.Max(x.ReadBps, x.WriteBps)));
                var ioFlow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Top, BackColor = UiTheme.Background, Margin = new Padding(0, 2, 0, 6) };
                foreach (var io in d.DiskIo.Take(2))
                {
                    var rPct = Math.Max(4, Math.Round(io.ReadBps / maxIo * 100));
                    var wPct = Math.Max(4, Math.Round(io.WriteBps / maxIo * 100));
                    var lat = io.ReadLatencyMs + io.WriteLatencyMs;
                    var cardIo = new Panel { Size = new Size(340, 44), BackColor = Drw.Color.White, Margin = new Padding(4, 2, 4, 2), Padding = new Padding(6, 4, 6, 4) };
                    cardIo.Paint += (s, e) => { e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias; using var pen = new Pen(UiTheme.BorderSubtle, 1); using var path = UiButtons.RoundedRect(new Rectangle(0, 0, cardIo.Width - 1, cardIo.Height - 1), 10); e.Graphics.DrawPath(pen, path); };
                    var lblDev = new WinForms.Label { Text = io.Device, Font = new Drw.Font("Segoe UI Semibold", 8f), ForeColor = UiTheme.Foreground, AutoSize = true, Location = new Point(6, 4) };
                    var lblLat = new WinForms.Label { Text = lat > 0 ? $"{lat:0.0} мс" : "", Font = new Drw.Font("Consolas", 7f), ForeColor = lat > 20 ? Drw.Color.FromArgb(184, 126, 115) : lat > 10 ? Drw.Color.FromArgb(146, 64, 14) : UiTheme.MutedForeground, AutoSize = true, Location = new Point(280, 4) };
                    var rowR = new Panel { Location = new Point(6, 20), Size = new Size(200, 4), BackColor = Drw.Color.FromArgb(246, 241, 231) };
                    var fillR = new Panel { Location = new Point(0, 0), Size = new Size((int)Math.Round(rPct / 100 * 200), 4), BackColor = Drw.Color.FromArgb(143, 169, 139) };
                    rowR.Controls.Add(fillR);
                    var lblR = new WinForms.Label { Text = "Чт " + FormatBytes(io.ReadBps) + "/с", Font = new Drw.Font("Consolas", 7f), ForeColor = UiTheme.MutedForeground, AutoSize = true, Location = new Point(220, 18) };
                    var rowW = new Panel { Location = new Point(6, 30), Size = new Size(200, 4), BackColor = Drw.Color.FromArgb(246, 241, 231) };
                    var fillW = new Panel { Location = new Point(0, 0), Size = new Size((int)Math.Round(wPct / 100 * 200), 4), BackColor = Drw.Color.FromArgb(216, 175, 163) };
                    rowW.Controls.Add(fillW);
                    var lblW = new WinForms.Label { Text = "Зп " + FormatBytes(io.WriteBps) + "/с", Font = new Drw.Font("Consolas", 7f), ForeColor = UiTheme.MutedForeground, AutoSize = true, Location = new Point(220, 28) };
                    cardIo.Controls.Add(lblDev); cardIo.Controls.Add(lblLat); cardIo.Controls.Add(rowR); cardIo.Controls.Add(lblR); cardIo.Controls.Add(rowW); cardIo.Controls.Add(lblW);
                    RoundCard(cardIo, 10);
                    ioFlow.Controls.Add(cardIo);
                }
                _detailsContent.Controls.Add(ioFlow);
            }
            // v3.9.3N — история читается с диска ОДИН раз (было дважды).
            var allHist = ZabbixAgentStore.LoadHistory().Where(x => string.Equals(x.Host, d.Host, StringComparison.OrdinalIgnoreCase)).OrderBy(x => x.Time).ToList();
            var cutoff = DateTime.Now.AddDays(-2);
            var hist = allHist.Where(x => x.Time >= cutoff).ToList();
            if (hist.Count < 2) hist = allHist.TakeLast(16).ToList();
            else hist = hist.TakeLast(16).ToList();
            if (hist.Count >= 2)
            {
                _detailsContent.Controls.Add(CreateSectionHeader($"История 2д — {hist.Count} точек", UiTheme.Accent));
                var gridHist = new TableLayoutPanel { Dock = DockStyle.Top, Height = 140, ColumnCount = 2, BackColor = UiTheme.Background, Margin = new Padding(0, 4, 0, 0) };
                gridHist.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
                gridHist.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
                var pCpu = new Panel { Dock = DockStyle.Fill, BackColor = Drw.Color.White, Margin = new Padding(4), Padding = new Padding(8, 6, 8, 6) };
                pCpu.Paint += (s, e) => { e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias; using var pen = new Pen(UiTheme.BorderSubtle, 1); using var path = UiButtons.RoundedRect(new Rectangle(0, 0, pCpu.Width - 1, pCpu.Height - 1), 10); e.Graphics.DrawPath(pen, path); };
                var lblCpu = new WinForms.Label { Text = "CPU %", Dock = DockStyle.Top, Height = 16, Font = new Drw.Font("Segoe UI Semibold", 8f), ForeColor = UiTheme.MutedForeground, BackColor = Drw.Color.White, TextAlign = ContentAlignment.MiddleLeft };
                var chartCpu = new Panel { Dock = DockStyle.Fill, BackColor = Drw.Color.White, Margin = new Padding(0, 2, 0, 2) };
                chartCpu.Paint += (s, e) => { e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias; double cMax = Math.Max(1, hist.Max(h => h.CpuLoad)); int n = hist.Count; int w = chartCpu.Width; int h = chartCpu.Height; int plotH = Math.Max(10, h - 14); int gap = 2; int bw = Math.Max(3, (w - (n - 1) * gap) / n); for (int i = 0; i < n; i++) { double v = hist[i].CpuLoad; int bh = Math.Max(3, (int)Math.Round(v / cMax * (plotH - 2))); int x = i * (bw + gap); int y = plotH - bh; var col = v >= 80 ? Drw.Color.FromArgb(216, 175, 163) : v >= 50 ? Drw.Color.FromArgb(200, 210, 180) : Drw.Color.FromArgb(143, 169, 139); if (Math.Abs(v - cMax) < 0.01) col = Drw.Color.FromArgb(110, 138, 110); using var br = new SolidBrush(col); using var bar = RoundedTopBar(x, y, bw, bh, 3); e.Graphics.FillPath(br, bar); } using var basePen = new Pen(UiTheme.BorderSubtle, 1); e.Graphics.DrawLine(basePen, 0, plotH, w, plotH); using var fnt = new Drw.Font("Consolas", 6.5f); TextRenderer.DrawText(e.Graphics, hist.First().Time.ToString("HH:mm"), fnt, new Rectangle(0, plotH + 2, 60, 11), UiTheme.MutedForeground, TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.SingleLine); TextRenderer.DrawText(e.Graphics, hist.Last().Time.ToString("HH:mm"), fnt, new Rectangle(w - 60, plotH + 2, 60, 11), UiTheme.MutedForeground, TextFormatFlags.Right | TextFormatFlags.Top | TextFormatFlags.SingleLine); };
                var lastCpu = hist.Last().CpuLoad;
                var lblCpuVal = new WinForms.Label { Text = $"{lastCpu:0.0}%", Dock = DockStyle.Bottom, Height = 14, Font = new Drw.Font("Consolas", 7.5f), ForeColor = UiTheme.MutedForeground, BackColor = Drw.Color.White, TextAlign = ContentAlignment.MiddleRight };
                pCpu.Controls.Add(chartCpu); pCpu.Controls.Add(lblCpuVal); pCpu.Controls.Add(lblCpu); lblCpu.BringToFront(); lblCpuVal.BringToFront();
                RoundCard(pCpu, 10);
                var pRam = new Panel { Dock = DockStyle.Fill, BackColor = Drw.Color.White, Margin = new Padding(4), Padding = new Padding(8, 6, 8, 6) };
                pRam.Paint += (s, e) => { e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias; using var pen = new Pen(UiTheme.BorderSubtle, 1); using var path = UiButtons.RoundedRect(new Rectangle(0, 0, pRam.Width - 1, pRam.Height - 1), 10); e.Graphics.DrawPath(pen, path); };
                var lblRam = new WinForms.Label { Text = "RAM free %", Dock = DockStyle.Top, Height = 16, Font = new Drw.Font("Segoe UI Semibold", 8f), ForeColor = UiTheme.MutedForeground, BackColor = Drw.Color.White, TextAlign = ContentAlignment.MiddleLeft };
                var chartRam = new Panel { Dock = DockStyle.Fill, BackColor = Drw.Color.White, Margin = new Padding(0, 2, 0, 2) };
                chartRam.Paint += (s, e) => { e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias; int w = chartRam.Width; int h = chartRam.Height; int plotH = Math.Max(10, h - 14); int n = hist.Count; if (n < 2) return; var pts = new PointF[n]; for (int i = 0; i < n; i++) { double x = (double)i / (n - 1) * w; double v = hist[i].MemPFree; double y = plotH - 2 - Math.Max(2, Math.Round(v / 100 * (plotH - 4))); pts[i] = new PointF((float)x, (float)y); } var poly = new PointF[n + 2]; Array.Copy(pts, poly, n); poly[n] = new PointF(w, plotH); poly[n + 1] = new PointF(0, plotH); using var lg = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 0, w, plotH), Drw.Color.FromArgb(45, 143, 169, 139), Drw.Color.FromArgb(0, 143, 169, 139), 90f); e.Graphics.FillPolygon(lg, poly); double lastRam = hist.Last().MemPFree; var lineCol = lastRam <= 10 ? Drw.Color.FromArgb(216, 175, 163) : lastRam <= 20 ? Drw.Color.FromArgb(200, 150, 80) : Drw.Color.FromArgb(143, 169, 139); using var pen2 = new Pen(lineCol, 1.8f) { LineJoin = System.Drawing.Drawing2D.LineJoin.Round }; e.Graphics.DrawLines(pen2, pts); using var dotBr = new SolidBrush(lineCol); foreach (var p in pts) e.Graphics.FillEllipse(dotBr, p.X - 1.6f, p.Y - 1.6f, 3.2f, 3.2f); using var basePen2 = new Pen(UiTheme.BorderSubtle, 1); e.Graphics.DrawLine(basePen2, 0, plotH, w, plotH); using var fnt2 = new Drw.Font("Consolas", 6.5f); TextRenderer.DrawText(e.Graphics, hist.First().Time.ToString("HH:mm"), fnt2, new Rectangle(0, plotH + 2, 60, 11), UiTheme.MutedForeground, TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.SingleLine); TextRenderer.DrawText(e.Graphics, hist.Last().Time.ToString("HH:mm"), fnt2, new Rectangle(w - 60, plotH + 2, 60, 11), UiTheme.MutedForeground, TextFormatFlags.Right | TextFormatFlags.Top | TextFormatFlags.SingleLine); };
                var lastRamV = hist.Last().MemPFree;
                var lblRamVal = new WinForms.Label { Text = $"{lastRamV:0.0}%", Dock = DockStyle.Bottom, Height = 14, Font = new Drw.Font("Consolas", 7.5f), ForeColor = lastRamV <= 10 ? Drw.Color.FromArgb(184, 126, 115) : lastRamV <= 20 ? Drw.Color.FromArgb(146, 64, 14) : Drw.Color.FromArgb(110, 138, 110), BackColor = Drw.Color.White, TextAlign = ContentAlignment.MiddleRight };
                pRam.Controls.Add(chartRam); pRam.Controls.Add(lblRamVal); pRam.Controls.Add(lblRam); lblRam.BringToFront(); lblRamVal.BringToFront();
                RoundCard(pRam, 10);
                gridHist.Controls.Add(pCpu, 0, 0); gridHist.Controls.Add(pRam, 1, 0);
                _detailsContent.Controls.Add(gridHist);
            } else { _detailsContent.Controls.Add(new WinForms.Label { Text = "Мало данных для истории — нужно 2+ опроса (15 мин).", Font = new Drw.Font("Segoe UI", 8f), ForeColor = UiTheme.MutedForeground, AutoSize = true, Margin = new Padding(4, 6, 0, 0) }); }
            }
            finally { _detailsContent.ResumeLayout(true); }
            // v3.9.3AZ — всплывашке раскладка нижней панели не нужна.
            if (_popupBuild) return;
            _detailsPanel.Visible = true;
            _detailsPanel.Height = _manualDetailsHeight > 0
                ? Math.Min(_manualDetailsHeight, MaxDetailsHeight())
                : Math.Min(520, Math.Min(MaxDetailsHeight(), _detailsContent.PreferredSize.Height + 16));
            LayoutTables();
            // Синхронно дожимаем док-раскладку (грид уже ужался) плюс цепочка
            // перепроверок следующими тиками — см. EnsureSelectedRowVisible.
            try { PerformLayout(); } catch { }
            EnsureSelectedRowVisible();
        }

        // v3.9.3AZ — детали хоста во всплывающем окне с затемнением:
        // тот же контент, что снизу (графики и тд). Нижняя панель не трогается.
        private void ShowDetailsPopup(ZabbixAgentData d)
        {
            var popContent = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, AutoScroll = true, WrapContents = false, BackColor = UiTheme.Background, Padding = new Padding(16, 12, 16, 12) };
            var savedContent = _detailsContent;
            string savedHost = _detailsHost;
            string savedUrl = _detailsWebUrl;
            _detailsContent = popContent;
            _popupBuild = true;
            try { BuildDetailsPanel(d); }
            finally { _popupBuild = false; _detailsContent = savedContent; _detailsHost = savedHost; _detailsWebUrl = savedUrl; }
            var owner = this.FindForm() ?? this.TopLevelControl as Form ?? this;
            // v3.9.3BA — попап по размеру экрана (графики читаются):
            // до 1400x1000, минимум 700x760.
            int popW = 700, popH = 760;
            try
            {
                var wa = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
                popW = Math.Max(700, Math.Min(1400, (int)(wa.Width * 0.85)));
                popH = Math.Max(760, Math.Min(1000, (int)(wa.Height * 0.9)));
            }
            catch { }
            using var dlg = new Helpers.UiModal.EscModalForm { Text = $"Детали — {d.Host}", Size = new Size(popW, popH), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, BackColor = UiTheme.Background, AutoScroll = false };
            dlg.KeyPreview = true;
            dlg.KeyDown += (s2, e2) => { if (e2.KeyCode == Keys.Escape) { e2.Handled = true; dlg.DialogResult = DialogResult.Cancel; dlg.Close(); } };
            // v3.9.3BB — без второго крестика в hero (остаётся системный + Esc).
            var hero = UiModal.Hero($"Детали — {d.Host}", $"Опрос {d.Time:HH:mm:ss}" + (d.Source == "snmp" ? " · SNMP" : $" · Агент {d.AgentVersion}"), "computer", null);
            var content = new Panel { Dock = DockStyle.Fill, AutoScroll = false, BackColor = UiTheme.DialogBackground };
            content.Controls.Add(popContent);
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = UiTheme.Card };
            footer.Paint += (s2, e2) => { using var pen = new Pen(UiTheme.Border, 1); e2.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };
            var btnClose = UiButtons.MakeCompactFilled("Закрыть", 100);
            btnClose.Click += (s2, e2) => { dlg.DialogResult = DialogResult.Cancel; dlg.Close(); };
            var hiddenCancel = new Button { DialogResult = DialogResult.Cancel, Visible = false, Location = new Point(-100, -100), Size = new Size(1, 1) };
            footer.Controls.Add(btnClose);
            footer.Controls.Add(hiddenCancel);
            dlg.Controls.Add(content);
            dlg.Controls.Add(hero);
            dlg.Controls.Add(footer);
            dlg.CancelButton = hiddenCancel;
            var dpiScale = UiModal.ApplyDpiScale(dlg);
            if (dpiScale > 1.001f)
                dlg.ClientSize = new Size((int)Math.Round(dlg.ClientSize.Width * dpiScale), (int)Math.Round(dlg.ClientSize.Height * dpiScale));
            dlg.Shown += (a, b) =>
            {
                btnClose.Location = new Point(footer.Width - 125, 15);
                try { dlg.BeginInvoke(new Action(() => { try { dlg.Refresh(); } catch { } })); } catch { }
            };
            dlg.AutoScroll = false;
            UiModal.ShowDimmed(owner, dlg);
            DeepRefreshAfterModal();
        }

        private Control CreateSectionHeader(string text, Drw.Color color)
        {
            // Акцентная полоска слева + заголовок — секции читаются легче,
            // чем plain-строка.
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, Dock = DockStyle.Top, BackColor = UiTheme.DialogBackground, Margin = new Padding(0, 10, 0, 6) };
            var bar = new Panel { Size = new Size(3, 14), BackColor = UiTheme.Background, Margin = new Padding(0, 2, 8, 0) };
            bar.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var path = UiButtons.RoundedRect(new Rectangle(0, 0, 2, 13), 1);
                using var br = new SolidBrush(UiTheme.Accent);
                e.Graphics.FillPath(br, path);
            };
            var lbl = new WinForms.Label { Text = text, Font = new Drw.Font("Segoe UI Semibold", 9f), ForeColor = color, AutoSize = true, Margin = new Padding(0) };
            row.Controls.Add(bar);
            row.Controls.Add(lbl);
            return row;
        }

        // Мягкая скруглённая пилюля: текст по центру, тонкий контур.
        // Единый стиль для чипов заголовка и пилюль служб (раньше службы
        // были квадратными FixedSingle-лейблами и выбивались из дизайна).
        private Panel CreatePill(string text, Drw.Color fg, Drw.Color bg)
        {
            var font = new Drw.Font("Segoe UI", 8f);
            var sz = TextRenderer.MeasureText(text, font);
            var pill = new Panel { Size = new Size(sz.Width + 18, 22), BackColor = UiTheme.DialogBackground, Margin = new Padding(0, 2, 6, 0) };
            pill.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var path = UiButtons.RoundedRect(new Rectangle(0, 0, pill.Width - 1, pill.Height - 1), 11);
                using var br = new SolidBrush(bg);
                e.Graphics.FillPath(br, path);
                using var pen = new Pen(UiTheme.BorderSubtle, 1);
                e.Graphics.DrawPath(pen, path);
                TextRenderer.DrawText(e.Graphics, text, font, new Rectangle(0, 0, pill.Width, pill.Height), fg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            };
            return pill;
        }

        private Panel CreateKpiCard(string title, string value, double pctRaw, double warn, double crit, bool inverted)
        {
            double pct = inverted ? 100 - pctRaw : pctRaw;
            // нормализация 0-100
            pct = Math.Clamp(pct, 0, 100);
            bool isCrit = inverted ? pctRaw <= crit : pctRaw >= crit;
            bool isWarn = !isCrit && (inverted ? pctRaw <= warn : pctRaw >= warn);
            var barCol = isCrit ? Drw.Color.FromArgb(216, 175, 163) : isWarn ? Drw.Color.FromArgb(200, 210, 180) : Drw.Color.FromArgb(143, 169, 139);
            var txtCol = isCrit ? Drw.Color.FromArgb(184, 126, 115) : isWarn ? Drw.Color.FromArgb(110, 100, 20) : Drw.Color.FromArgb(43, 36, 30);
            // Лёгкая тонировка фона по состоянию — карточка "дышит", а не
            // просто белая. Тона едва заметные, палитру не ломают.
            var cardBg = isCrit ? Drw.Color.FromArgb(251, 238, 234) : isWarn ? Drw.Color.FromArgb(253, 248, 236) : Drw.Color.White;
            var card = new Panel { BackColor = cardBg, Margin = new Padding(4), Padding = new Padding(10, 8, 10, 8), Dock = DockStyle.Fill };
            card.Paint += (s, e) => { e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias; using var pen = new Pen(UiTheme.BorderSubtle, 1); using var path = UiButtons.RoundedRect(new Rectangle(0, 0, card.Width - 1, card.Height - 1), 10); e.Graphics.DrawPath(pen, path); };
            var lblTitle = new WinForms.Label { Text = title, Font = new Drw.Font("Segoe UI", 7f, Drw.FontStyle.Bold), ForeColor = UiTheme.MutedForeground, AutoSize = true, Location = new Point(10, 8) };
            var lblVal = new WinForms.Label { Text = value, Font = new Drw.Font("Segoe UI", 14f, Drw.FontStyle.Bold), ForeColor = txtCol, AutoSize = true, Location = new Point(10, 20) };
            var barBg = new Panel { Location = new Point(10, 48), Size = new Size(140, 4), BackColor = Drw.Color.FromArgb(246, 241, 231), Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom };
            var barFill = new Panel { Location = new Point(0, 0), Size = new Size((int)Math.Round(pct / 100 * 140), 4), BackColor = barCol, Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Bottom };
            barBg.Controls.Add(barFill);
            card.Controls.Add(lblTitle); card.Controls.Add(lblVal); card.Controls.Add(barBg);
            RoundCard(card, 10);
            return card;
        }

        // Столбик со скруглённой верхушкой для CPU-гистограммы: острые
        // прямоугольники выглядят грубо, радиус 3px смягчает картинку.
        private static System.Drawing.Drawing2D.GraphicsPath RoundedTopBar(int x, int y, int w, int h, int r)
        {
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            r = Math.Max(0, Math.Min(r, Math.Min(w / 2, h)));
            if (r == 0 || h <= 2) { path.AddRectangle(new Rectangle(x, y, w, Math.Max(1, h))); return path; }
            path.AddArc(x, y, r * 2, r * 2, 180, 90);
            path.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
            path.AddLine(x + w, y + r, x + w, y + h);
            path.AddLine(x + w, y + h, x, y + h);
            path.AddLine(x, y + h, x, y + r);
            path.CloseFigure();
            return path;
        }

        // Обрезка прямоугольника панели по скруглению. v4.8.1 — ЧЕСТНАЯ
        // отрисовка вместо Region (лесенка): панель прозрачная, белый
        // скруглённый фил в Paint со сглаживанием. Отдельные outline-хендлеры
        // на местах вызова подписаны РАНЬШЕ и рисуют рамку первыми — фил
        // накрывает только её внутреннюю половину, как раньше резал Region.
        private static void RoundCard(Control c, int radius)
        {
            try { c.BackColor = Drw.Color.Transparent; } catch { }
            try { c.Region?.Dispose(); c.Region = null; } catch { }
            c.Paint += (s, e) =>
            {
                try
                {
                    e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    using var path = UiButtons.RoundedRect(new Rectangle(0, 0, c.Width - 1, c.Height - 1), radius);
                    using var br = new SolidBrush(Drw.Color.White);
                    e.Graphics.FillPath(br, path);
                }
                catch { }
            };
            c.Resize += (s, e) => { try { c.Invalidate(); } catch { } };
        }

        private static string FormatBytes(long b)
        {
            if (b <= 0) return "0 Б";
            string[] u = { "Б", "КБ", "МБ", "ГБ", "ТБ" };
            double v = b; int i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return $"{v:0.0} {u[i]}";
        }

        private void StylePlotModern(ScottPlot.Plot plot)
        {
            var bg = new ScottPlot.Color(UiTheme.Card.R, UiTheme.Card.G, UiTheme.Card.B);
            var grid = new ScottPlot.Color(UiTheme.BorderSubtle.R, UiTheme.BorderSubtle.G, UiTheme.BorderSubtle.B, 45);
            var dm = ControlPaint.Dark(UiTheme.MutedForeground, 0.35f);
            var tc = new ScottPlot.Color(dm.R, dm.G, dm.B);
            plot.FigureBackground.Color = bg; plot.DataBackground.Color = bg;
            plot.Axes.Color(tc); plot.Grid.MajorLineColor = grid; plot.Grid.MinorLineColor = ScottPlot.Colors.Transparent;
            plot.Axes.Bottom.TickLabelStyle.ForeColor = tc; plot.Axes.Left.TickLabelStyle.ForeColor = tc; plot.Axes.Right.TickLabelStyle.ForeColor = tc;
            plot.Axes.Bottom.TickLabelStyle.FontSize = 8; plot.Axes.Left.TickLabelStyle.FontSize = 8; plot.Axes.Right.TickLabelStyle.FontSize = 7;
            plot.Legend.BackgroundColor = bg; plot.Legend.FontColor = tc; plot.Legend.OutlineColor = grid;
        }

        private void StylePlot(ScottPlot.Plot plot)
        {
            var cardColor = new ScottPlot.Color(UiTheme.Card.R, UiTheme.Card.G, UiTheme.Card.B);
            var gridColor = new ScottPlot.Color(UiTheme.BorderSubtle.R, UiTheme.BorderSubtle.G, UiTheme.BorderSubtle.B, 60);
            var dm = ControlPaint.Dark(UiTheme.MutedForeground, 0.4f);
            var textColor = new ScottPlot.Color(dm.R, dm.G, dm.B);

            plot.FigureBackground.Color = cardColor;
            plot.DataBackground.Color = cardColor;
            plot.Axes.Color(textColor);
            plot.Grid.MajorLineColor = gridColor;
            plot.Grid.MinorLineColor = ScottPlot.Colors.Transparent;
            plot.Axes.Bottom.TickLabelStyle.ForeColor = textColor;
            plot.Axes.Left.TickLabelStyle.ForeColor = textColor;
            plot.Axes.Right.TickLabelStyle.ForeColor = textColor;
            plot.Legend.BackgroundColor = cardColor;
            plot.Legend.FontColor = textColor;
            plot.Legend.OutlineColor = gridColor;
        }

        private static ScottPlot.Color ColorFromHex(string hex)
        {
            hex = hex.TrimStart('#');
            int r = int.Parse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber);
            int g = int.Parse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber);
            int b = int.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber);
            return new ScottPlot.Color(r, g, b);
        }

        private static string FormatBps(long bps)
        {
            double v = bps;
            string[] units = { "B/s", "KB/s", "MB/s", "GB/s" };
            int i = 0;
            while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
            return $"{v:0.0} {units[i]}";
        }

        private void OnCellMouseDown(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;
            _suppressDetailsOnRightClick = true;
            try
            {
                if (e.RowIndex < 0)
                {
                    var emptyMenu = new ThemedContextMenu();
                    emptyMenu.AddItem("Добавить запись", "add", () => ShowAddEntryDialog());
                    emptyMenu.Show(this, Cursor.Position);
                    return;
                }
                _grid.ClearSelection();
                _grid.Rows[e.RowIndex].Selected = true;
                _grid.CurrentCell = _grid.Rows[e.RowIndex].Cells[0];
                var data = _grid.Rows[e.RowIndex].Tag as ZabbixAgentData;
                if (data == null) return;
                string ip = _grid.Rows[e.RowIndex].Cells["Ip"].Value?.ToString() ?? "";
                var menu = new ThemedContextMenu();
            menu.AddItem("Добавить запись", "add", () => ShowAddEntryDialog());
            menu.AddSeparator();
            menu.AddItem("Настроить агента", "settings", async () => await ConfigureOneAgentAsync(data.Host, ip));
            menu.AddItem("Копировать хост", "computer", () => Clipboard.SetText(data.Host));
            if (!string.IsNullOrEmpty(ip)) menu.AddItem("Копировать IP", "copy", () => Clipboard.SetText(ip));
            menu.AddItem("Копировать строку", "copy", () =>
            {
                string agent = data.Source == "snmp" ? (data.AgentPing ? "SNMP" : "ERR") : (data.AgentPing ? "OK" : "ERR");
                string row = $"{data.Host}\t{ip}\t{agent}\t{data.CpuLoad:0.0}\t{data.MemPFree:0.0}\t{data.TempC?.ToString("0.0") ?? "—"}\t{data.Uptime.Days}д {data.Uptime.Hours}ч\t{data.AgentVersion}";
                Clipboard.SetText(row);
            });
            menu.AddItem("Открыть C$", "folder", () => { try { Process.Start(new ProcessStartInfo("explorer.exe", $"\\\\{data.Host}\\c$") { UseShellExecute = true }); } catch { } });
            menu.AddItem("Пинг", "info", () => { try { Process.Start(new ProcessStartInfo("cmd.exe", $"/c start ping -t {data.Host}") { UseShellExecute = true }); } catch { } });
            menu.AddItem("Открыть событие", "search", () => { try { Process.Start(new ProcessStartInfo("eventvwr.msc", $"/s {data.Host}") { UseShellExecute = true }); } catch { } });
            menu.AddSubmenu("Web ресурсы", "globe", sub =>
            {
                sub.AddItem("Добавить URL...", "add", () => ShowAddWebUrlDialog());
                sub.AddItem("Обновить web сейчас", "info", async () => { await RefreshWebNowAsync(); });
            });
            menu.AddSeparator();
            menu.AddItem("Удалить", "delete", () =>
            {
                var r = MessageBox.Show($"Убрать '{data.Host}' из мониторинга?\n\nСнимет «Отслеживать» и «SNMP» в книге и удалит всю историю по этому хосту из даша Z.", "Удалить из мониторинга", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r != DialogResult.Yes) return;
                try
                {
                    ZabbixAgentStore.DeleteManualHost(data.Host);
                    var all = AddressBookStore.Load();
                    var ent = all.FirstOrDefault(e => string.Equals(e.Computer, data.Host, StringComparison.OrdinalIgnoreCase));
                    if (ent != null)
                    {
                        ent.Monitored = false;
                        ent.SnmpMonitored = false;
                        AddressBookStore.Save(all);
                        EventDashboardStore.SaveMonitoredComputers(all.Where(e => e.Monitored).Select(e => e.Computer).ToList());
                    }
                    ZabbixAgentStore.DeleteHostHistory(data.Host);
                    _allLatest = ZabbixAgentStore.GetLatestPerHost();
                    RebuildHostBar();
                    ApplyFilters();
                    MessageBox.Show($"Хост {data.Host} удалён из мониторинга.", "Удалено", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex) { MessageBox.Show($"Ошибка: {ex.Message}", "Удалить", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            });
                menu.Show(this, Cursor.Position);
            }
            finally
            {
                _suppressDetailsOnRightClick = false;
                _detailsPanel.Visible = false;
                _detailsPanel.Height = 0;
                // v4.8 — web возвращается только во вьюхе Web.
                if (_view == DashView.Web) _webSection.Visible = true;
                _grid.ClearSelection();
            }
        }

        private async Task RefreshNowAsync()
        {
            _pollStart = DateTime.Now;
            var hosts = EventDashboardStore.GetMonitoredComputers();
            int total = hosts.Count;
            SetHeaderProgress("обновление...");
            _progressBar.Maximum = Math.Max(1, total);
            _progressBar.Value = 0;
            _progressBar.Visible = true;
            _progressLabel.Text = total > 0 ? $"0 / {total} • 0% • старт..." : "Опрос...";
            _progressLabel.Visible = true;
            await ZabbixAgentPollerService.PollOnceAsync();
            await WebMonitorPollerService.PollOnceAsync();
            // v4.7 — ручное обновление дотягивает и пинг (не ждём часовой тик).
            _ = Services.HostPingService.PingNowAsync();
            _progressBar.Visible = false;
            _progressLabel.Visible = false;
            _allLatest = ZabbixAgentStore.GetLatestPerHost();
            RebuildHostBar();
            ApplyFilters();
            RefreshWebGrid();
            SetHeaderProgress(null);
        }

        private async Task ConfigureAgentsAsync()
        {
            try
            {
                var s = ZabbixAgentStore.LoadSettings();
                var hosts = EventDashboardStore.GetMonitoredComputers();
                if (hosts.Count == 0) { MessageBox.Show("Нет хостов для настройки.", "Zabbix", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                string ip = ZabbixAgentConfigurator.GetLocalIp();
                var answer = MessageBox.Show(
                    $"Прописать IP {ip} в Server= на {hosts.Count} хостах?\n\nКонфиг: {s.AgentConfigPath}\nСервис: {s.AgentServiceName}\n\nАгенты будут перезапущены.",
                    "Настроить агентов", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer != DialogResult.Yes) return;

                SetHeaderProgress("настройка агентов...");
                _progressBar.Maximum = hosts.Count;
                _progressBar.Value = 0;
                _progressBar.Visible = true;
                _progressLabel.Visible = true;

                var results = new List<AgentConfigResult>();
                int cur = 0;
                foreach (var host in hosts)
                {
                    cur++;
                    int captured = cur;
                    var r = await ZabbixAgentConfigurator.ConfigureOneAsync(host, s.AgentConfigPath, s.AgentServiceName,
                        (h, msg) =>
                        {
                            try
                            {
                                BeginInvoke(new Action(() =>
                                {
                                    _progressBar.Value = captured;
                                    _progressLabel.Text = $"{captured}/{hosts.Count} • {h}: {msg}";
                                    SetHeaderProgress(h);
                                }));
                            }
                            catch { }
                        });
                    results.Add(r);
                }

                _progressBar.Visible = false;
                _progressLabel.Visible = false;
                SetHeaderProgress(null);

                int ok = results.Count(r => r.Ok);
                int fail = results.Count - ok;
                string summary = $"Настроено: {ok}/{hosts.Count}";
                if (fail > 0) summary += $"\nОшибки:\n" + string.Join("\n", results.Where(r => !r.Ok).Select(r => $"{r.Host}: {r.Error}"));
                MessageBox.Show(summary, "Результат", MessageBoxButtons.OK, fail > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);

                if (ok > 0) await RefreshNowAsync();
            }
            catch (Exception ex)
            {
                _progressBar.Visible = false;
                _progressLabel.Visible = false;
                SetHeaderProgress(null);
                MessageBox.Show($"Ошибка: {ex.Message}", "Zabbix", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async Task ConfigureOneAgentAsync(string host, string ip = "")
        {
            try
            {
                var s = ZabbixAgentStore.LoadSettings();
                string localIp = ZabbixAgentConfigurator.GetLocalIp();
                string target = !string.IsNullOrEmpty(ip) ? $"{host} ({ip})" : host;
                var answer = MessageBox.Show(
                    $"Прописать IP {localIp} в Server= на {target}?\n\nКонфиг: {s.AgentConfigPath}\nСервис: {s.AgentServiceName}\n\nАгент будет перезапущен.",
                    $"Настроить {host}", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer != DialogResult.Yes) return;

                SetHeaderProgress($"настройка {host}...");

                // Пробуем сначала по IP, потом по имени
                AgentConfigResult result = null;
                if (!string.IsNullOrEmpty(ip))
                {
                    result = await ZabbixAgentConfigurator.ConfigureOneAsync(ip, s.AgentConfigPath, s.AgentServiceName,
                        (h, msg) => { try { BeginInvoke(new Action(() => SetHeaderProgress($"{h}: {msg}"))); } catch { } });
                }
                if (result == null || !result.Ok)
                {
                    result = await ZabbixAgentConfigurator.ConfigureOneAsync(host, s.AgentConfigPath, s.AgentServiceName,
                        (h, msg) => { try { BeginInvoke(new Action(() => SetHeaderProgress($"{h}: {msg}"))); } catch { } });
                }

                SetHeaderProgress(null);
                string msg2 = result.Ok ? $"OK: {result.Message}" : $"Ошибка: {result.Error}";
                MessageBox.Show(msg2, host, MessageBoxButtons.OK, result.Ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);

                if (result.Ok) await RefreshNowAsync();
            }
            catch (Exception ex)
            {
                SetHeaderProgress(null);
                MessageBox.Show($"Ошибка: {ex.Message}", "Zabbix", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnNewData(List<ZabbixAgentData> list) => BeginInvoke(new Action(() => { _allLatest = ZabbixAgentStore.GetLatestPerHost(); if (_grid.Visible && !IsDisposed) { RebuildHostBar(); ApplyFilters(); } }));
        // v4.7 — часовой пинг прошёл: перерисовать колонку «Пинг» (вид и выбор сохраняем).
        private void OnPingUpdate()
        {
            try
            {
                BeginInvoke(new Action(() =>
                {
                    RefreshAllPreservingView();
                }));
            }
            catch { }
        }
        private void OnWebChanged(Models.WebPoint pt)
        {
            try
            {
                BeginInvoke(new Action(() =>
                {
                    RefreshAllPreservingView();
                }));
            }
            catch { }
        }
        private void OnProgress(string host, int cur, int total)
        {
            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (string.IsNullOrEmpty(host))
                    {
                        SetHeaderProgress(null);
                        _progressBar.Visible = false;
                        _progressLabel.Visible = false;
                        return;
                    }
                    if (cur == 1) _pollStart = DateTime.Now;
                    int pct = total > 0 ? (int)Math.Round((double)cur / total * 100) : 0;
                    var elapsed = DateTime.Now - _pollStart;
                    string eta = "";
                    if (cur > 0 && cur < total && elapsed.TotalSeconds > 1)
                    {
                        double avg = elapsed.TotalSeconds / cur;
                        double remain = avg * (total - cur);
                        eta = remain < 60 ? $" • осталось ~{Math.Max(1, (int)Math.Round(remain))}с" : $" • осталось ~{Math.Round(remain / 60)}м";
                    }
                    SetHeaderProgress($"{host} ({cur}/{total} • {pct}%)");
                    _progressLabel.Text = $"{cur} / {total} • {pct}%{eta} • {host}";
                    _progressLabel.Visible = true;
                    _progressBar.Visible = true;
                    _progressBar.Maximum = total;
                    _progressBar.Value = Math.Clamp(cur, 0, total);
                }));
            }
            catch { }
        }

        
        private void ShowSettings()
        {
            try { Logger.Info("ZabbixAgent ShowSettings clicked"); } catch { }
            var s = ZabbixAgentStore.LoadSettings();
            var owner = this.FindForm() ?? this.TopLevelControl as Form ?? this;
            // Общий дизайн модалок: hero + карточки + футер (как FTP/календарь).
            using var dlg = new Form
            {
                Text = "Настройки — Дашборд",
                Size = new Size(684, 900),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                BackColor = UiTheme.DialogBackground,
                AutoScroll = false
            };
            dlg.KeyPreview = true;
            dlg.KeyDown += (s2, e2) => { if (e2.KeyCode == Keys.Escape) { e2.Handled = true; dlg.DialogResult = DialogResult.Cancel; dlg.Close(); } };

            // ——— 1. Hero ———
            const int ClientW = 668;
            var hero = UiModal.Hero("Настройки", "Дашборд — опрос, пороги, агент, SNMP", "gear");

            // ——— 2. Footer (Сохранить / Отмена) ———
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = UiTheme.Card };
            footer.Paint += (s2, e2) => { using var pen = new Pen(UiTheme.Border, 1); e2.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };
            var btnOk = UiButtons.MakeCompactFilled("Сохранить", 100);
            btnOk.Location = new Point(ClientW - 233, 15);
            btnOk.Click += (s2, e2) => { dlg.DialogResult = DialogResult.OK; dlg.Close(); };
            var btnCancel = UiButtons.MakeCompactFilled("Отмена", 100, bgOverride: UiTheme.MutedForeground);
            btnCancel.Location = new Point(ClientW - 125, 15);
            btnCancel.Click += (s2, e2) => { dlg.DialogResult = DialogResult.Cancel; dlg.Close(); };
            var hiddenOk = new Button { DialogResult = DialogResult.OK, Visible = false, Location = new Point(-100, -100), Size = new Size(1, 1) };
            footer.Controls.Add(hiddenOk);
            var hiddenCancel = new Button { DialogResult = DialogResult.Cancel, Visible = false, Location = new Point(-100, -100), Size = new Size(1, 1) };
            footer.Controls.Add(hiddenCancel);
            dlg.AcceptButton = hiddenOk;
            dlg.CancelButton = hiddenCancel;
            footer.Controls.Add(btnOk);
            footer.Controls.Add(btnCancel);

            // ——— 3. Контент с карточками ———
            // v3.9.3AB — без прокрутки: компактный двухколоночный layout,
            // всё влезает во вьюпорт (756 из 768).
            var content = new Panel { Dock = DockStyle.Fill, AutoScroll = false, BackColor = UiTheme.DialogBackground };

            // Карточка «Опрос».
            var cardPoll = UiModal.Card(14, 14, 308, 220, "Опрос", "restart");
            var numInterval = new EscNumericUpDown { Minimum = 5, Maximum = 240, Value = s.PollMinutes };
            var lInterval = UiModal.FieldLabel("Интервал (мин):", 16, 56);
            var wInterval = UiModal.Field(numInterval, 76);
            wInterval.Location = new Point(16, 74);
            var numTimeout = new EscNumericUpDown { Minimum = 1000, Maximum = 15000, Increment = 500, Value = s.TimeoutMs };
            var lTimeout = UiModal.FieldLabel("Таймаут (мс):", 16, 110);
            var wTimeout = UiModal.Field(numTimeout, 96);
            wTimeout.Location = new Point(16, 128);
            var numConc = new EscNumericUpDown { Minimum = 1, Maximum = 30, Value = s.Concurrency };
            var lConc = UiModal.FieldLabel("Потоки:", 16, 164);
            var wConc = UiModal.Field(numConc, 76);
            wConc.Location = new Point(16, 182);
            cardPoll.Controls.AddRange(new Control[] { lInterval, wInterval, lTimeout, wTimeout, lConc, wConc });

            // Карточка «Агент».
            var cardAgent = UiModal.Card(14, 246, 308, 170, "Агент", "computer");
            var txtPath = new TextBox { Text = s.AgentConfigPath };
            var lPath = UiModal.FieldLabel("Конфиг агента:", 16, 56);
            var wPath = UiModal.Field(txtPath, 276);
            wPath.Location = new Point(16, 74);
            var txtSvc = new TextBox { Text = s.AgentServiceName };
            var lSvc = UiModal.FieldLabel("Сервис:", 16, 110);
            var wSvc = UiModal.Field(txtSvc, 276);
            wSvc.Location = new Point(16, 128);
            cardAgent.Controls.AddRange(new Control[] { lPath, wPath, lSvc, wSvc });

            // Карточка «Пороги цвета».
            // v3.9.3AC — шаг 40 и высота 396: низ колонки 715 (отступ 14).
            var cardThresh = UiModal.Card(334, 14, 320, 396, "Пороги цвета", "dashboard");
            var threshRows = new (string Label, decimal Min, decimal Max, double Val)[]
            {
                ("CPU жёлтый >%:", 10, 100, s.CpuWarn),
                ("CPU красный >%:", 10, 100, s.CpuCrit),
                ("RAM free жёлтый <%:", 1, 50, s.RamWarn),
                ("RAM free красный <%:", 1, 50, s.RamCrit),
                ("Диск free жёлтый <%:", 1, 50, s.DiskWarn),
                ("Диск free красный <%:", 1, 50, s.DiskCrit),
                ("Темп жёлтый >°C:", 40, 100, s.TempWarn),
                ("Темп красный >°C:", 40, 100, s.TempCrit),
            };
            var threshNums = new List<EscNumericUpDown>();
            for (int i = 0; i < threshRows.Length; i++)
            {
                // v3.9.3AB — шаг 44 (был 50), чтобы влезло без прокрутки.
                // v3.9.3AC — шаг 40.
                int ry = 56 + i * 40;
                var num = new EscNumericUpDown { Minimum = threshRows[i].Min, Maximum = threshRows[i].Max, Value = (decimal)threshRows[i].Val };
                var lb = UiModal.FieldLabel(threshRows[i].Label, 16, ry);
                var w = UiModal.Field(num, 76);
                w.Location = new Point(210, ry + 18);
                cardThresh.Controls.Add(lb);
                cardThresh.Controls.Add(w);
                threshNums.Add(num);
            }
            var numCpuW = threshNums[0]; var numCpuC = threshNums[1];
            var numRamW = threshNums[2]; var numRamC = threshNums[3];
            var numDiskW = threshNums[4]; var numDiskC = threshNums[5];
            var numTempW = threshNums[6]; var numTempC = threshNums[7];

            // Карточка «Уведомления» (v3.9.3 — алерты здоровья: пинг/диски).
            var cardAlerts = UiModal.Card(14, 428, 308, 190, "Уведомления", "info");
            var chkPing = new CheckBox { Text = "Нет пинга (баллун + звук)", Checked = s.PingAlert, Location = new Point(16, 56), AutoSize = true, ForeColor = UiTheme.Foreground, BackColor = Drw.Color.Transparent };
            var chkDisk = new CheckBox { Text = "Мало места на диске", Checked = s.DiskFreeAlert, Location = new Point(16, 82), AutoSize = true, ForeColor = UiTheme.Foreground, BackColor = Drw.Color.Transparent };
            var numGb = new EscNumericUpDown { Minimum = 1, Maximum = 1000, Value = (decimal)Math.Clamp(s.DiskFreeGb, 1, 1000) };
            var lGb = UiModal.FieldLabel("Порог (ГБ):", 16, 108);
            var wGb = UiModal.Field(numGb, 76);
            wGb.Location = new Point(210, 126);
            var chkSound = new CheckBox { Text = "Звук уведомлений", Checked = s.AlertSound, Location = new Point(16, 160), AutoSize = true, ForeColor = UiTheme.Foreground, BackColor = Drw.Color.Transparent };
            cardAlerts.Controls.AddRange(new Control[] { chkPing, chkDisk, lGb, wGb, chkSound });

            // Карточка «SNMP» (v3.9.3AB — правая колонка, поля стопкой
            // вместо двух колонок: всё влезает без прокрутки).
            // v3.9.3AC — низ 715: нижний отступ как верхний (14px).
            var cardSnmp = UiModal.Card(334, 420, 320, 295, "SNMP", "globe");
            var chkSnmp = new CheckBox { Text = "SNMP-мониторинг", Checked = s.SnmpEnabled, Location = new Point(16, 54), AutoSize = true, ForeColor = UiTheme.Foreground, BackColor = Drw.Color.Transparent };
            var chkFallback = new CheckBox { Text = "Автопадаемость: агент → SNMP", Checked = s.SnmpFallback, Location = new Point(16, 78), AutoSize = true, ForeColor = UiTheme.Foreground, BackColor = Drw.Color.Transparent };
            var numPort = new EscNumericUpDown { Minimum = 1, Maximum = 65535, Value = s.SnmpPort };
            var lPort = UiModal.FieldLabel("Порт SNMP:", 16, 104);
            var wPort = UiModal.Field(numPort, 96);
            wPort.Location = new Point(16, 122);
            var txtComm = new TextBox { Text = s.SnmpCommunity };
            var lComm = UiModal.FieldLabel("Community:", 16, 156);
            var wComm = UiModal.Field(txtComm, 288);
            wComm.Location = new Point(16, 174);
            var cmbVer = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            cmbVer.Items.Add("v2c");
            cmbVer.Items.Add("v1");
            cmbVer.SelectedItem = string.Equals(s.SnmpVersion, "v1", StringComparison.OrdinalIgnoreCase) ? "v1" : "v2c";
            var lVer = UiModal.FieldLabel("Версия:", 16, 208);
            var wVer = UiModal.Field(cmbVer, 120);
            wVer.Location = new Point(16, 226);
            var numTime = new EscNumericUpDown { Minimum = 300, Maximum = 5000, Increment = 100, Value = s.SnmpTimeoutMs };
            var lTime = UiModal.FieldLabel("Таймаут (мс):", 152, 208);
            var wTime = UiModal.Field(numTime, 136);
            wTime.Location = new Point(152, 226);
            cardSnmp.Controls.AddRange(new Control[] {
                chkSnmp, chkFallback, lPort, wPort, lComm, wComm, lVer, wVer, lTime, wTime
            });

            var ws = WebMonitorStore.LoadSettings();
            var numWebInterval = new EscNumericUpDown { Minimum = 5, Maximum = 240, Value = ws.IntervalMinutes };
            var numWebTimeout = new EscNumericUpDown { Minimum = 1000, Maximum = 30000, Increment = 1000, Value = ws.TimeoutMs };
            var numWebConc = new EscNumericUpDown { Minimum = 1, Maximum = 30, Value = ws.Concurrency };

            // ——— assemble: hero (top) + footer (bottom) + scrollable content ———
            content.Controls.AddRange(new Control[] { cardPoll, cardAgent, cardAlerts, cardThresh, cardSnmp });
            // v3.9.3Y — UiModal.Card гладкая сама, Region давал лесенку.
            foreach (var cd in new[] { cardPoll, cardAgent, cardAlerts, cardThresh, cardSnmp }) { try { cd.Region?.Dispose(); cd.Region = null; } catch { } }
            // v3.9.3AB — прокрутки нет (всё влезает), кастомные скроллы не нужны.
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
                s.PollMinutes = (int)numInterval.Value;
                s.TimeoutMs = (int)numTimeout.Value;
                s.Concurrency = (int)numConc.Value;
                s.CpuWarn = (double)numCpuW.Value;
                s.CpuCrit = (double)numCpuC.Value;
                s.RamWarn = (double)numRamW.Value;
                s.RamCrit = (double)numRamC.Value;
                s.DiskWarn = (double)numDiskW.Value;
                s.DiskCrit = (double)numDiskC.Value;
                s.TempWarn = (double)numTempW.Value;
                s.TempCrit = (double)numTempC.Value;
                s.AgentConfigPath = txtPath.Text.Trim();
                s.AgentServiceName = txtSvc.Text.Trim();
                s.PingAlert = chkPing.Checked;
                s.DiskFreeAlert = chkDisk.Checked;
                s.DiskFreeGb = (double)numGb.Value;
                s.AlertSound = chkSound.Checked;
                s.SnmpEnabled = chkSnmp.Checked;
                s.SnmpFallback = chkFallback.Checked;
                s.SnmpPort = (int)numPort.Value;
                s.SnmpCommunity = txtComm.Text.Trim();
                s.SnmpVersion = (cmbVer.SelectedItem as string) == "v1" ? "v1" : "v2c";
                s.SnmpTimeoutMs = (int)numTime.Value;
                ZabbixAgentStore.SaveSettings(s);
                ZabbixAgentPollerService.Restart();
                ws.IntervalMinutes = (int)numWebInterval.Value;
                ws.TimeoutMs = (int)numWebTimeout.Value;
                ws.Concurrency = (int)numWebConc.Value;
                WebMonitorStore.SaveSettings(ws);
                WebMonitorPollerService.Restart();
            }
        }

        // v4.7 — даблклик по строке таблицы: редактирование записи мониторинга.
// Ручная запись правится в списке даша, книжная — в книге (тот же диалог,
// предзаполненный). Ключ книги (имя) при правке locked — переименование
// только для ручных записей.
        // v3.9.3AN — ядерный репейнт после модалки дабл-клика: то,
        // что делает свернуть/развернуть (кадр + клиент + дети синхронно).
        // Убивает stale-пиксели, переживающие обычные Invalidate.
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);
        private void DeepRefreshAfterModal()
        {
            try
            {
                const uint RDW_INVALIDATE = 0x1, RDW_ERASE = 0x4, RDW_ALLCHILDREN = 0x80, RDW_UPDATENOW = 0x100, RDW_FRAME = 0x400;
                var top = TopLevelControl as System.Windows.Forms.Form ?? this;
                RedrawWindow(top.Handle, IntPtr.Zero, IntPtr.Zero,
                    RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN | RDW_UPDATENOW | RDW_FRAME);
                // v3.9.3AP — второй проход через 300мс: перекрывает окно
                // DWM-анимации закрытия (остатки в композиторе после первого).
                var t2 = new System.Windows.Forms.Timer { Interval = 300 };
                t2.Tick += (s, e) =>
                {
                    try
                    {
                        t2.Stop();
                        t2.Dispose();
                        if (IsDisposed) return;
                        var top2 = TopLevelControl as System.Windows.Forms.Form ?? this;
                        if (top2.IsDisposed) return;
                        RedrawWindow(top2.Handle, IntPtr.Zero, IntPtr.Zero,
                            RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN | RDW_UPDATENOW | RDW_FRAME);
                    }
                    catch { }
                };
                t2.Start();
            }
            catch { }
        }

        // v3.9.3AY — кнопка ДЕТАЛИ: детали записи во всплывающем окне.
        // v3.9.3AZ — дабл-клик возвращён на Редактировать запись.
        private void OnGridCellClick(object? sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _grid.Rows.Count) return;
            if (e.ColumnIndex < 0 || e.ColumnIndex >= _grid.Columns.Count) return;
            if (_grid.Columns[e.ColumnIndex].Name != "Details") return;
            var data = _grid.Rows[e.RowIndex].Tag as ZabbixAgentData;
            if (data == null) return;
            ShowDetailsPopup(data);
        }

        // Рука-курсор над кнопкой ДЕТАЛИ.
        private void OnGridCellMouseMove(object? sender, DataGridViewCellMouseEventArgs e)
        {
            try
            {
                bool hand = e.RowIndex >= 0 && e.ColumnIndex >= 0
                    && e.ColumnIndex < _grid.Columns.Count
                    && _grid.Columns[e.ColumnIndex].Name == "Details";
                var cur = hand ? Cursors.Hand : Cursors.Default;
                if (_grid.Cursor != cur) _grid.Cursor = cur;
            }
            catch { }
        }

        // v3.9.3AZ — дабл-клик: Редактировать запись.
        private void OnGridCellDoubleClick(object? sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _grid.Rows.Count) return;
            OpenEntryForRow(e.RowIndex);
        }

        private void OpenEntryForRow(int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= _grid.Rows.Count) return;
            try
            {
                var data = _grid.Rows[rowIndex].Tag as ZabbixAgentData;
                string host = data?.Host ?? "";
                if (string.IsNullOrWhiteSpace(host)) return;
                var me = ZabbixAgentStore.LoadManualHosts()
                    .FirstOrDefault(x => string.Equals(x.Computer, host, StringComparison.OrdinalIgnoreCase));
                if (me != null) { ShowEntryDialog(me, true); DeepRefreshAfterModal(); return; }
                var be = AddressBookStore.Load()
                    .FirstOrDefault(x => string.Equals(x.Computer, host, StringComparison.OrdinalIgnoreCase));
                if (be != null) { ShowEntryDialog(be, false); DeepRefreshAfterModal(); return; }
                // Хост только в списке мониторинга (ни в книге, ни в ручных):
                // предложить как новую ручную запись с подставленным именем.
                ShowEntryDialog(new AddressBookEntry { Computer = host }, true);
                DeepRefreshAfterModal();
            }
            catch { }
        }

        private void ShowAddEntryDialog() => ShowEntryDialog(null, true);

        private void ShowEntryDialog(AddressBookEntry? existing, bool isManual)
        {
            bool editing = existing != null;
            var s = ZabbixAgentStore.LoadSettings();
            var owner = this.FindForm() ?? this.TopLevelControl as Form ?? this;
            // v3.9.3AU — общий стандарт модалок: hero + карточка + футер.
            // v3.9.3BD — EscModalForm: выход по Esc не зависит от фокуса.
            using var dlg = new Helpers.UiModal.EscModalForm { Text = editing ? "Редактировать запись" : "Добавить запись в мониторинг", Size = new Size(456, 464), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, BackColor = UiTheme.Background, AutoScroll = false };
            dlg.KeyPreview = true;
            dlg.KeyDown += (s2, e2) => { if (e2.KeyCode == Keys.Escape) { e2.Handled = true; dlg.DialogResult = DialogResult.Cancel; dlg.Close(); } };

            // v3.9.3BB — без второго крестика в hero (остаётся системный + Esc).
            var hero = UiModal.Hero(editing ? "Редактировать запись" : "Добавить запись в мониторинг",
                isManual ? "Ручная запись мониторинга." : "Запись адресной книги (ключ locked).", "computer", null);
            var card = UiModal.Card(14, 14, 412, 260, "Параметры опроса", "gear");

            // v3.9.3AN — редакторы в гладких UiModal.Field (голые стоковые
            // поля с 3D-рамками выглядели как внутренний box-shadow).
            // v3.9.3AU — координаты относительно карточки (шапка до y=44).
            int y = 56;
            var lblHost = new WinForms.Label { Text = "Хост:", Location = new Point(16, y + 7), AutoSize = true, ForeColor = UiTheme.Foreground };
            var txtHost = new TextBox { Text = existing?.Computer ?? "" };
            var wHost = UiModal.Field(txtHost, 258);
            wHost.Location = new Point(111, y);
            // Ключ книги при правке locked — переименование только для ручных.
            if (editing && !isManual) txtHost.Enabled = false;
            y += 32;
            var lblIp = new WinForms.Label { Text = "IP (через ,):", Location = new Point(16, y + 7), AutoSize = true, ForeColor = UiTheme.Foreground };
            var txtIp = new TextBox { Text = existing?.IPs ?? "" };
            var wIp = UiModal.Field(txtIp, 258);
            wIp.Location = new Point(111, y);
            y += 32;
            var lblMethod = new WinForms.Label { Text = "Метод:", Location = new Point(16, y + 7), AutoSize = true, ForeColor = UiTheme.Foreground };
            // v3.9.3AW — пил-селект вместо нативного комбо (двойная рамка).
            var cmbMethod = new Helpers.UiPillSelect();
            cmbMethod.SetItems(new[] { "Агент Zabbix", "SNMP" }, (existing?.SnmpMonitored == true) ? 1 : 0);
            cmbMethod.Size = new Size(198, 30);
            cmbMethod.Location = new Point(111, y);
            y += 32;
            var lblCommunity = new WinForms.Label { Text = "Community:", Location = new Point(16, y + 7), AutoSize = true, ForeColor = UiTheme.Foreground };
            var txtCommunity = new TextBox { Text = existing?.SnmpCommunity ?? s.SnmpCommunity };
            var wCommunity = UiModal.Field(txtCommunity, 138);
            wCommunity.Location = new Point(111, y);
            y += 32;
            var lblPort = new WinForms.Label { Text = "Порт:", Location = new Point(16, y + 7), AutoSize = true, ForeColor = UiTheme.Foreground };
            var numPort = new EscNumericUpDown { Minimum = 1, Maximum = 65535, Value = existing?.SnmpPort ?? s.SnmpPort };
            var wPort = UiModal.Field(numPort, 78);
            wPort.Location = new Point(111, y);
            y += 32;
            var lblVer = new WinForms.Label { Text = "Версия:", Location = new Point(16, y + 7), AutoSize = true, ForeColor = UiTheme.Foreground };
            // v3.9.3AW — пил-селект вместо нативного комбо.
            var cmbVer = new Helpers.UiPillSelect();
            string curVer = existing?.SnmpVersion ?? s.SnmpVersion;
            cmbVer.SetItems(new[] { "v2c", "v1" }, string.Equals(curVer, "v1", StringComparison.OrdinalIgnoreCase) ? 1 : 0);
            cmbVer.Size = new Size(98, 30);
            cmbVer.Location = new Point(111, y);

            void ToggleSnmp(bool on)
            {
                lblCommunity.Enabled = on; txtCommunity.Enabled = on;
                lblPort.Enabled = on; numPort.Enabled = on;
                lblVer.Enabled = on; cmbVer.Enabled = on;
            }
            ToggleSnmp(false);
            cmbMethod.Changed += () => ToggleSnmp(cmbMethod.SelectedIndex == 1);

            y += 16;
            var btnOk = UiButtons.MakeCompactFilled("Сохранить", 100);
            btnOk.Click += (s2, e2) =>
            {
                string host = txtHost.Text.Trim();
                if (string.IsNullOrWhiteSpace(host))
                {
                    MessageBox.Show("Укажите имя хоста.", editing ? "Редактировать запись" : "Добавить запись", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                dlg.DialogResult = DialogResult.OK;
                dlg.Close();
            };
            var btnCancel = UiButtons.MakeCompactFilled("Отмена", 100, bgOverride: UiTheme.MutedForeground);
            btnCancel.Click += (s2, e2) => { dlg.DialogResult = DialogResult.Cancel; dlg.Close(); };
            var hiddenOk = new Button { DialogResult = DialogResult.OK, Visible = false, Location = new Point(-100, -100), Size = new Size(1, 1) };
            var hiddenCancel = new Button { DialogResult = DialogResult.Cancel, Visible = false, Location = new Point(-100, -100), Size = new Size(1, 1) };
            dlg.AcceptButton = hiddenOk;
            dlg.CancelButton = hiddenCancel;

            // ——— assemble: hero + content(card) + footer (общий стандарт) ———
            var content = new Panel { Dock = DockStyle.Fill, AutoScroll = false, BackColor = UiTheme.DialogBackground };
            card.Controls.AddRange(new Control[] {
                lblHost, wHost, lblIp, wIp, lblMethod, cmbMethod,
                lblCommunity, wCommunity, lblPort, wPort, lblVer, cmbVer
            });
            content.Controls.Add(card);
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = UiTheme.Card };
            footer.Paint += (s2, e2) => { using var pen = new Pen(UiTheme.Border, 1); e2.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };
            footer.Controls.Add(btnOk);
            footer.Controls.Add(btnCancel);
            footer.Controls.Add(hiddenOk);
            footer.Controls.Add(hiddenCancel);
            dlg.Controls.Add(content);
            dlg.Controls.Add(hero);
            dlg.Controls.Add(footer);
            // Невысокие редакторы центрируем в 30px-обёртках.
            var dpiScale = UiModal.ApplyDpiScale(dlg);
            if (dpiScale > 1.001f)
                dlg.ClientSize = new Size((int)Math.Round(dlg.ClientSize.Width * dpiScale), (int)Math.Round(dlg.ClientSize.Height * dpiScale));
            dlg.Shown += (a, b) =>
            {
                btnOk.Location = new Point(footer.Width - 233, 15);
                btnCancel.Location = new Point(footer.Width - 125, 15);
                txtHost.Focus();
            };
            dlg.AutoScroll = false;

            if (UiModal.ShowDimmed(owner, dlg) != DialogResult.OK) return;

            string fhost = isManual ? txtHost.Text.Trim() : (existing?.Computer ?? txtHost.Text.Trim());
            if (string.IsNullOrWhiteSpace(fhost))
            {
                MessageBox.Show("Укажите имя хоста.", editing ? "Редактировать запись" : "Добавить запись", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            bool snmp = cmbMethod.SelectedIndex == 1;
            if (isManual)
            {
                string oldKey = editing ? (existing?.Computer ?? "") : "";
                var entry = new AddressBookEntry
                {
                    Computer = fhost,
                    IPs = txtIp.Text.Trim(),
                    SnmpMonitored = snmp,
                    SnmpCommunity = snmp ? txtCommunity.Text.Trim() : null,
                    SnmpPort = snmp ? (int)numPort.Value : (int?)null,
                    SnmpVersion = snmp ? (cmbVer.SelectedItem as string) : null
                };
                var list = ZabbixAgentStore.LoadManualHosts();
                // Переименование ручной записи: удалить старый ключ.
                if (editing && !string.IsNullOrWhiteSpace(oldKey))
                    list.RemoveAll(x => string.Equals(x.Computer, oldKey, StringComparison.OrdinalIgnoreCase));
                list.RemoveAll(x => string.Equals(x.Computer, entry.Computer, StringComparison.OrdinalIgnoreCase));
                list.Add(entry);
                ZabbixAgentStore.SaveManualHosts(list);
                Logger.Info($"ZabbixAgent: manual host {(editing ? "edited" : "added")} '{entry.Computer}' snmp={entry.SnmpMonitored}");
            }
            else
            {
                var book = AddressBookStore.Load();
                var be = book.FirstOrDefault(x => string.Equals(x.Computer, fhost, StringComparison.OrdinalIgnoreCase));
                if (be == null) return;
                be.IPs = txtIp.Text.Trim();
                be.SnmpMonitored = snmp;
                be.SnmpCommunity = snmp ? txtCommunity.Text.Trim() : null;
                be.SnmpPort = snmp ? (int)numPort.Value : (int?)null;
                be.SnmpVersion = snmp ? (cmbVer.SelectedItem as string) : null;
                AddressBookStore.Save(book);
                Logger.Info($"ZabbixAgent: book host edited '{be.Computer}' snmp={be.SnmpMonitored}");
            }
            _ = RefreshNowAsync();
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        private static void SetProgressBarColor(IntPtr hWnd, System.Drawing.Color color)
        {
            const int PBM_SETBARCOLOR = 0x0409;
            SendMessage(hWnd, PBM_SETBARCOLOR, IntPtr.Zero, (IntPtr)ColorTranslator.ToWin32(color));
        }
    }
}
