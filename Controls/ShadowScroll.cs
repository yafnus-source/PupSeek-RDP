using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ShadowRDP.Controls
{
    // v4.4.0 — ShadowScroll: тонкая (10px) темизированная полоса прокрутки,
    // заменяет системную во всех окнах, кроме терминалов (cmd/ps/ssh —
    // ConsoleSessionControl и SshTerminalControl не трогаем).
    //
    // Устройство: нативный механизм скролла контрола сохраняется
    // (колесо/клавиатура/тачпад работают как раньше), системная полоса
    // прячется, а поверх правого/нижнего края кладётся ShadowScrollBar —
    // overlay-контрол, который лишь показывает позицию и управляет ею.
    // Цвета нейтрально-тёплые (подходят обеим темам), нажатие — акцентом.
    public sealed class ShadowScrollBar : Control
    {
        public enum BarOrientation { Vertical, Horizontal }

        private const int BarW = 10;
        private const int ThumbW = 6;
        private const int MinThumb = 44;

        private static readonly Color ThumbNormal = Color.FromArgb(0xCF, 0xC8, 0xBD);
        private static readonly Color ThumbHover = Color.FromArgb(0xB4, 0xAD, 0xA1);

        private readonly BarOrientation _orientation;
        private readonly Func<int> _getValue;
        private readonly Func<int> _getTotal;
        private readonly Func<int> _getPage;
        private readonly Action<int> _scrollTo;
        private readonly Func<int> _getWheelUnit;

        private int _value;
        private int _total;
        private int _page = 1;

        private enum ThumbState { Normal, Hover, Pressed }
        private ThumbState _state = ThumbState.Normal;
        private bool _dragging;
        private int _dragStartP;
        private int _dragStartValue;
        private int _dragMax;

        public ShadowScrollBar(
            BarOrientation orientation,
            Func<int> getValue,
            Func<int> getTotal,
            Func<int> getPage,
            Action<int> scrollTo,
            Func<int>? getWheelUnit = null)
        {
            _orientation = orientation;
            _getValue = getValue;
            _getTotal = getTotal;
            _getPage = getPage;
            _scrollTo = scrollTo;
            _getWheelUnit = getWheelUnit ?? (() => Math.Max(1, _page / 4));

            SetStyle(ControlStyles.UserPaint |
                     ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
            TabStop = false;
            CausesValidation = false;
            Cursor = Cursors.Hand;
            if (orientation == BarOrientation.Vertical) Width = BarW;
            else Height = BarW;
        }

        // Обновить диапазон из контрола; прячет полосу, если скроллить нечего.
        public void SetRange(int value, int total, int page)
        {
            _value = Math.Max(0, value);
            _total = Math.Max(0, total);
            _page = Math.Max(1, page);
            bool show = _total > _page;
            if (Visible != show) Visible = show;
            Invalidate();
        }

        public void RefreshRange()
        {
            try { SetRange(_getValue(), _getTotal(), _getPage()); } catch { }
        }

        private int Length => _orientation == BarOrientation.Vertical ? Height : Width;
        private int Max() => Math.Max(0, _total - _page);

        private Rectangle ThumbRect()
        {
            int len = Math.Max(1, Length);
            int max = Max();
            int thumbLen = _total <= 0 ? len :
                Math.Max(MinThumb, (int)Math.Round(len * (double)_page / _total));
            thumbLen = Math.Min(thumbLen, len);
            int maxTop = Math.Max(0, len - thumbLen);
            double frac = max == 0 ? 0 : (double)Math.Min(_value, max) / max;
            int top = (int)Math.Round(maxTop * frac);
            if (_orientation == BarOrientation.Vertical)
                return new Rectangle((Width - ThumbW) / 2, top, ThumbW, thumbLen);
            return new Rectangle(top, (Height - ThumbW) / 2, thumbLen, ThumbW);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var track = new SolidBrush(Helpers.UiTheme.Card))
                e.Graphics.FillRectangle(track, 0, 0, Width, Height);
            // Тонкая разделительная линия со стороны контента.
            using (var sep = new Pen(Helpers.UiTheme.BorderSubtle))
            {
                if (_orientation == BarOrientation.Vertical)
                    e.Graphics.DrawLine(sep, 0, 0, 0, Height);
                else
                    e.Graphics.DrawLine(sep, 0, 0, Width, 0);
            }
            var thumb = ThumbRect();
            if (thumb.Width <= 0 || thumb.Height <= 0) return;
            Color c = _state switch
            {
                ThumbState.Pressed => Helpers.UiTheme.AccentHover,
                ThumbState.Hover => ThumbHover,
                _ => ThumbNormal
            };
            using var b = new SolidBrush(c);
            using var path = RoundedRect(thumb, ThumbW / 2);
            e.Graphics.FillPath(b, path);
        }

        private int PosOf(MouseEventArgs e) =>
            _orientation == BarOrientation.Vertical ? e.Y : e.X;

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            var thumb = ThumbRect();
            if (thumb.Contains(e.Location))
            {
                _dragging = true;
                _state = ThumbState.Pressed;
                _dragStartP = PosOf(e);
                _dragStartValue = Math.Min(_value, Max());
                _dragMax = Max();
                Capture = true;
            }
            else
            {
                int p = PosOf(e);
                int t0 = _orientation == BarOrientation.Vertical ? thumb.Y : thumb.X;
                try { _scrollTo(Math.Min(_value, Max()) + (p < t0 ? -_page : _page)); } catch { }
                RefreshRange();
            }
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging)
            {
                var thumb = ThumbRect();
                int thumbLen = _orientation == BarOrientation.Vertical ? thumb.Height : thumb.Width;
                int usable = Math.Max(1, Length - thumbLen);
                int nv = _dragStartValue +
                    (int)Math.Round((PosOf(e) - _dragStartP) * (double)_dragMax / usable);
                nv = Math.Max(0, Math.Min(_dragMax, nv));
                try { _scrollTo(nv); } catch { }
                RefreshRange();
            }
            else
            {
                var st = ThumbRect().Contains(e.Location) ? ThumbState.Hover : ThumbState.Normal;
                if (st != _state) { _state = st; Invalidate(); }
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragging = false;
            _state = ThumbRect().Contains(e.Location) ? ThumbState.Hover : ThumbState.Normal;
            Capture = false;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (!_dragging && _state != ThumbState.Normal)
            {
                _state = ThumbState.Normal;
                Invalidate();
            }
        }

        // Колесо над самой полосой тоже скроллит цель.
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            try
            {
                int unit = Math.Max(1, _getWheelUnit());
                _scrollTo(Math.Min(Max(), Math.Max(0, _value + (e.Delta < 0 ? unit : -unit))));
            }
            catch { }
            RefreshRange();
        }

        private static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            int d = Math.Max(2, radius * 2);
            d = Math.Min(d, Math.Min(r.Width, r.Height));
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    // Роутер колеса мыши: кручение над скроллируемой целью скроллит
    // именно её — независимо от фокуса (WinForms шлёт WM_MOUSEWHEEL
    // сфокусированному окну, а не окну под курсором) и от видимости
    // системных полос (со спрятанными полосами нативный путь мёртв).
    // Тот же приём, что TerminalWheelFilter в терминале.
    //
    // Перехватываем только когда цель реально может скроллиться в эту
    // сторону; иначе сообщение идёт дальше штатным путём.
    public static class ShadowWheel
    {
        private const int WM_MOUSEWHEEL = 0x020A;
        private const int WM_MOUSEHWHEEL = 0x020E;

        private sealed class Entry
        {
            public Func<bool> CanV = () => false;
            public Func<int, bool> ScrollV = _ => false;
            public Func<bool> CanH = () => false;
            public Func<int, bool>? ScrollH;
        }

        private sealed class WheelFilter : IMessageFilter
        {
            public bool PreFilterMessage(ref Message m)
            {
                if (m.Msg != WM_MOUSEWHEEL && m.Msg != WM_MOUSEHWHEEL) return false;
                try
                {
                    // Та же механика, что у рабочего TerminalWheelFilter:
                    // позиция курсора + прямое попадание в прямоугольник.
                    Point pt;
                    try { pt = Cursor.Position; }
                    catch { return false; }
                    List<(Control alias, Entry e)> snapshot;
                    lock (_reg) { snapshot = new List<(Control, Entry)>(_reg); }
                    // С конца (позже зарегистрированные — верхние).
                    var hits = new List<(Control alias, Entry e)>();
                    for (int i = snapshot.Count - 1; i >= 0; i--)
                    {
                        var (alias, e) = snapshot[i];
                        try
                        {
                            if (alias.IsDisposed || !alias.IsHandleCreated || !alias.Visible) continue;
                            if (!alias.RectangleToScreen(alias.ClientRectangle).Contains(pt)) continue;
                            hits.Add((alias, e));
                        }
                        catch { }
                    }
                    if (hits.Count == 0) return false;
                    // Побеждает самая вложенная (не содержащая других хитов).
                    var chosen = hits[0];
                    foreach (var h1 in hits)
                    {
                        bool containsOther = false;
                        foreach (var h2 in hits)
                        {
                            if (ReferenceEquals(h1.alias, h2.alias)) continue;
                            try { if (h1.alias.Contains(h2.alias)) { containsOther = true; break; } }
                            catch { }
                        }
                        if (!containsOther) { chosen = h1; break; }
                    }
                    bool horiz = m.Msg == WM_MOUSEHWHEEL;
                    bool can;
                    try { can = horiz ? chosen.e.CanH() : chosen.e.CanV(); }
                    catch { can = false; }
                    int raw = (short)(m.WParam.ToInt64() >> 16);
                    if (!can || raw == 0) return false;
                    try
                    {
                        if (horiz)
                        {
                            if (chosen.e.ScrollH == null) return false;
                            int px = (int)Math.Round(raw / 120.0 * 60);
                            if (px == 0) px = raw > 0 ? -20 : 20;
                            chosen.e.ScrollH(px);
                        }
                        else
                        {
                            // Вверх (delta>0) = значение меньше.
                            int lines = -(int)Math.Round(raw / 120.0 * 3);
                            if (lines == 0) lines = raw > 0 ? -1 : 1;
                            chosen.e.ScrollV(lines);
                        }
                    }
                    catch { }
                    return true; // съели — сфокусированный контрол не дёргаем
                }
                catch { }
                return false;
            }
        }

        private static readonly List<(Control alias, Entry e)> _reg = new List<(Control, Entry)>();
        private static WheelFilter? _filter;

        private static void EnsureInstalled()
        {
            if (_filter != null) return;
            _filter = new WheelFilter();
            Application.AddMessageFilter(_filter);
        }

        // alias — цель или её overlay-полоса. Хендлы не храним (попадание
        // считается по живому RectangleToScreen), на Dispose снимаем.
        public static void Register(Control alias, Func<bool> canV, Func<int, bool> scrollV,
            Func<bool>? canH = null, Func<int, bool>? scrollH = null)
        {
            EnsureInstalled();
            var e = new Entry
            {
                CanV = canV,
                ScrollV = scrollV,
                CanH = canH ?? (() => false),
                ScrollH = scrollH
            };
            lock (_reg)
            {
                for (int i = _reg.Count - 1; i >= 0; i--)
                    if (ReferenceEquals(_reg[i].alias, alias)) _reg.RemoveAt(i);
                _reg.Add((alias, e));
            }
            alias.Disposed += (s, ev) =>
            {
                try
                {
                    lock (_reg)
                    {
                        for (int i = _reg.Count - 1; i >= 0; i--)
                            if (ReferenceEquals(_reg[i].alias, alias)) _reg.RemoveAt(i);
                    }
                }
                catch { }
            };
        }
    }

    // Точки подключения ShadowScrollBar к контролам. Все методы безопасно
    // вызывать один раз после создания контрола (контрол уже должен лежать
    // в parent — полоса добавляется туда же поверх).
    public static class ShadowScroll
    {
        private const int BarW = 10;

        private static int Safe(Func<int> f, int fallback)
        {
            try { return f(); } catch { return fallback; }
        }

        // === DataGridView: прячем обе нативные полосы (колесо/клавиатура
        // продолжают работать внутри грида), кладём свои поверх. ===
        public static void AttachGrid(DataGridView grid)
        {
            try { grid.ScrollBars = ScrollBars.None; } catch { }
            var v = new ShadowScrollBar(ShadowScrollBar.BarOrientation.Vertical,
                () => GridWheelScroller.FirstRow(grid),
                () => Safe(() => grid.Rows.Count, 0),
                () => GridWheelScroller.PageRows(grid),
                n => GridWheelScroller.ScrollToRow(grid, n),
                () => Math.Max(1, GridWheelScroller.PageRows(grid) / 3));
            var h = new ShadowScrollBar(ShadowScrollBar.BarOrientation.Horizontal,
                () => Safe(() => grid.HorizontalScrollingOffset, 0),
                () => GridWheelScroller.TotalWidth(grid),
                () => Math.Max(1, grid.ClientSize.Width),
                px => { try { grid.HorizontalScrollingOffset = Math.Max(0, px); } catch { } },
                () => 60);
            var parent = grid.Parent;
            if (parent == null) return;
            parent.Controls.Add(v);
            parent.Controls.Add(h);

            void Refresh() => RefreshGrid(grid, v, h);

            // WndProc-перехват колеса самого грида (прямая доставка мимо
            // очереди) — обновляем свои полосы. Только DoubleBuffered-гряды.
            if (grid is DoubleBufferedDataGridView dbg)
                dbg.WheelScrolled += (s, e) => Refresh();
            else
            {
                // Обычный DataGridView (класс менять нельзя): колесо через
                // NativeWindow-хук на хендл грида. Та же логика скролла.
                GridWheelHook? hook = null;
                void EnsureHook()
                {
                    try
                    {
                        if (grid.IsDisposed) return;
                        if (hook == null) hook = new GridWheelHook(grid, Refresh);
                        hook.EnsureAssigned();
                    }
                    catch { }
                }
                void ReleaseHook() { try { hook?.Detach(); } catch { } }
                grid.HandleCreated += (s, e) => EnsureHook();
                grid.HandleDestroyed += (s, e) => ReleaseHook();
                grid.Disposed += (s, e) => ReleaseHook();
                EnsureHook();
            }

            // Колесо над гридом и его полосами — через роутер (см. ShadowWheel).
            bool CanV()
            {
                int rows = Safe(() => grid.Rows.Count, 0);
                return rows > GridWheelScroller.PageRows(grid);
            }
            bool ScrollV(int lines)
            {
                try
                {
                    if (grid.IsDisposed || !CanV()) return false;
                    GridWheelScroller.ScrollToRow(grid, GridWheelScroller.FirstRow(grid) + lines);
                    RefreshGrid(grid, v, h);
                    return true;
                }
                catch { return false; }
            }
            bool CanH() => GridWheelScroller.TotalWidth(grid) > Math.Max(1, grid.ClientSize.Width);
            bool ScrollH(int px)
            {
                try
                {
                    if (grid.IsDisposed || !CanH()) return false;
                    grid.HorizontalScrollingOffset =
                        Math.Max(0, Safe(() => grid.HorizontalScrollingOffset, 0) + px);
                    RefreshGrid(grid, v, h);
                    return true;
                }
                catch { return false; }
            }
            ShadowWheel.Register(grid, CanV, ScrollV, CanH, ScrollH);
            ShadowWheel.Register(v, CanV, ScrollV, CanH, ScrollH);
            ShadowWheel.Register(h, CanV, ScrollV, CanH, ScrollH);
            grid.Scroll += (s, e) => Refresh();
            grid.Resize += (s, e) => Refresh();
            grid.LocationChanged += (s, e) => Refresh();
            grid.RowsAdded += (s, e) => Refresh();
            grid.RowsRemoved += (s, e) => Refresh();
            grid.DataSourceChanged += (s, e) => Refresh();
            grid.DataBindingComplete += (s, e) => Refresh();
            grid.ColumnWidthChanged += (s, e) => Refresh();
            grid.ColumnAdded += (s, e) => Refresh();
            grid.ColumnRemoved += (s, e) => Refresh();
            grid.Sorted += (s, e) => Refresh();
            grid.VisibleChanged += (s, e) => Refresh();
            grid.HandleCreated += (s, e) => Refresh();
            parent.Resize += (s, e) => Refresh();
            parent.Layout += (s, e) => Refresh();
            grid.ParentChanged += (s, e) =>
            {
                try
                {
                    var np = grid.Parent;
                    if (np == null || np.IsDisposed) return;
                    if (v.Parent != np) { np.Controls.Add(v); np.Controls.Add(h); }
                    Refresh();
                }
                catch { }
            };
            Refresh();
        }

        private static void RefreshGrid(DataGridView grid, ShadowScrollBar v, ShadowScrollBar h)
        {
            try
            {
                if (grid.IsDisposed || grid.Parent == null || grid.Parent.IsDisposed) return;
                int rows = Safe(() => grid.Rows.Count, 0);
                int page = GridWheelScroller.PageRows(grid);
                bool showV = rows > page;
                int totalW = GridWheelScroller.TotalWidth(grid);
                int clientW = Math.Max(1, grid.ClientSize.Width);
                bool showH = totalW > clientW;
                v.Bounds = new Rectangle(grid.Right - BarW, grid.Top, BarW,
                    Math.Max(1, grid.Height - (showH ? BarW : 0)));
                h.Bounds = new Rectangle(grid.Left, grid.Bottom - BarW,
                    Math.Max(1, grid.Width - (showV ? BarW : 0)), BarW);
                v.SetRange(GridWheelScroller.FirstRow(grid), rows, page);
                h.SetRange(Math.Max(0, Safe(() => grid.HorizontalScrollingOffset, 0)), totalW, clientW);
                if (v.Visible) v.BringToFront();
                if (h.Visible) h.BringToFront();
            }
            catch { }
        }

        // === Panel с AutoScroll: нативные полосы прячем (колесо панели
        // скроллит дальше), вертикальная своя поверх правого края. ===
        public static void AttachPanel(Panel panel)
        {
            HidePanelBars(panel);
            var v = new ShadowScrollBar(ShadowScrollBar.BarOrientation.Vertical,
                () => PanelVal(panel),
                () => PanelTotal(panel),
                () => PanelPage(panel),
                y => PanelScrollTo(panel, y),
                () => 40);
            var parent = panel.Parent;
            if (parent == null) return;
            parent.Controls.Add(v);

            bool ScrollV(int lines)
            {
                try
                {
                    if (panel.IsDisposed || PanelTotal(panel) <= PanelPage(panel)) return false;
                    // Строка панели ≈ 20px.
                    PanelScrollTo(panel, PanelVal(panel) + lines * 20);
                    v.RefreshRange();
                    return true;
                }
                catch { return false; }
            }
            ShadowWheel.Register(panel, () => PanelTotal(panel) > PanelPage(panel), ScrollV);
            ShadowWheel.Register(v, () => PanelTotal(panel) > PanelPage(panel), ScrollV);

            void Refresh()
            {
                try
                {
                    if (panel.IsDisposed || panel.Parent == null || panel.Parent.IsDisposed) return;
                    HidePanelBars(panel);
                    v.Bounds = new Rectangle(panel.Right - BarW, panel.Top, BarW, Math.Max(1, panel.Height));
                    v.RefreshRange();
                    if (v.Visible) v.BringToFront();
                }
                catch { }
            }
            panel.Scroll += (s, e) => Refresh();
            panel.Resize += (s, e) => Refresh();
            panel.LocationChanged += (s, e) => Refresh();
            panel.Layout += (s, e) => Refresh();
            panel.ControlAdded += (s, e) => Refresh();
            panel.ControlRemoved += (s, e) => Refresh();
            panel.VisibleChanged += (s, e) => Refresh();
            panel.HandleCreated += (s, e) => Refresh();
            parent.Resize += (s, e) => Refresh();
            parent.Layout += (s, e) => Refresh();
            Refresh();
        }

        private const int SB_BOTH = 3;
        [DllImport("user32.dll")]
        private static extern bool ShowScrollBar(IntPtr hWnd, int wBar, bool bShow);

        private static void HidePanelBars(Panel panel)
        {
            try { if (panel.IsHandleCreated) ShowScrollBar(panel.Handle, SB_BOTH, false); }
            catch { }
        }

        private static int PanelVal(Panel p) =>
            Math.Max(0, -Safe(() => p.AutoScrollPosition.Y, 0));
        private static int PanelTotal(Panel p) =>
            Math.Max(0, Safe(() => p.DisplayRectangle.Height, 0));
        private static int PanelPage(Panel p) =>
            Math.Max(1, p.ClientSize.Height);
        private static void PanelScrollTo(Panel p, int y)
        {
            try { p.AutoScrollPosition = new Point(Math.Abs(p.AutoScrollPosition.X), Math.Max(0, y)); }
            catch { }
        }

        // === Однострочный/многострочный TextBox без нативной полосы. ===
        public static void AttachTextBox(ScrollHiddenTextBox t)
        {
            var v = new ShadowScrollBar(ShadowScrollBar.BarOrientation.Vertical,
                () => t.FirstVisibleLine(),
                () => t.LineCount(),
                () => t.VisibleLineCount(),
                line => t.ScrollToLine(line),
                () => 3);
            var parent = t.Parent;
            if (parent == null) return;
            parent.Controls.Add(v);

            bool ScrollV(int lines)
            {
                try
                {
                    if (t.IsDisposed || t.LineCount() <= t.VisibleLineCount()) return false;
                    t.ScrollByLines(lines);
                    v.RefreshRange();
                    return true;
                }
                catch { return false; }
            }
            ShadowWheel.Register(t, () => t.LineCount() > t.VisibleLineCount(), ScrollV);
            ShadowWheel.Register(v, () => t.LineCount() > t.VisibleLineCount(), ScrollV);

            void Refresh()
            {
                try
                {
                    // v4.6.3 — пока текстбокс не готов (хэндл полусобран),
                    // никаких замеров: RefreshRange слал бы EM_* в пустоту (AV).
                    if (t.IsDisposed || !t.IsReady || t.Parent == null || t.Parent.IsDisposed) return;
                    v.Bounds = new Rectangle(t.Right - BarW, t.Top, BarW, Math.Max(1, t.Height));
                    v.RefreshRange();
                    if (v.Visible) v.BringToFront();
                }
                catch { }
            }
            t.ViewScrolled += (s, e) => Refresh();
            t.Resize += (s, e) => Refresh();
            t.LocationChanged += (s, e) => Refresh();
            t.TextChanged += (s, e) => Refresh();
            t.FontChanged += (s, e) => Refresh();
            t.VisibleChanged += (s, e) => Refresh();
            t.HandleCreated += (s, e) => Refresh();
            parent.Resize += (s, e) => Refresh();
            parent.Layout += (s, e) => Refresh();
            Refresh();
        }

        public static void AttachRichTextBox(ScrollHiddenRichTextBox t)
        {
            var v = new ShadowScrollBar(ShadowScrollBar.BarOrientation.Vertical,
                () => t.FirstVisibleLine(),
                () => t.LineCount(),
                () => t.VisibleLineCount(),
                line => t.ScrollToLine(line),
                () => 3);
            var parent = t.Parent;
            if (parent == null) return;
            parent.Controls.Add(v);

            bool ScrollV(int lines)
            {
                try
                {
                    if (t.IsDisposed || t.LineCount() <= t.VisibleLineCount()) return false;
                    t.ScrollByLines(lines);
                    v.RefreshRange();
                    return true;
                }
                catch { return false; }
            }
            ShadowWheel.Register(t, () => t.LineCount() > t.VisibleLineCount(), ScrollV);
            ShadowWheel.Register(v, () => t.LineCount() > t.VisibleLineCount(), ScrollV);

            void Refresh()
            {
                try
                {
                    // v4.6.3 — см. AttachTextBox.Refresh: пропуск до готовности.
                    if (t.IsDisposed || !t.IsReady || t.Parent == null || t.Parent.IsDisposed) return;
                    v.Bounds = new Rectangle(t.Right - BarW, t.Top, BarW, Math.Max(1, t.Height));
                    v.RefreshRange();
                    if (v.Visible) v.BringToFront();
                }
                catch { }
            }
            t.ViewScrolled += (s, e) => Refresh();
            t.Resize += (s, e) => Refresh();
            t.LocationChanged += (s, e) => Refresh();
            t.TextChanged += (s, e) => Refresh();
            t.FontChanged += (s, e) => Refresh();
            t.VisibleChanged += (s, e) => Refresh();
            t.HandleCreated += (s, e) => Refresh();
            parent.Resize += (s, e) => Refresh();
            parent.Layout += (s, e) => Refresh();
            Refresh();
        }
        // NativeWindow-перехват колеса для обычного DataGridView.
        private sealed class GridWheelHook : NativeWindow
        {
            private readonly DataGridView _grid;
            private readonly Action _refreshed;

            internal GridWheelHook(DataGridView grid, Action refreshed)
            {
                _grid = grid;
                _refreshed = refreshed;
            }

            internal void EnsureAssigned()
            {
                try
                {
                    if (_grid.IsDisposed || !_grid.IsHandleCreated) return;
                    if (Handle == _grid.Handle) return;
                    if (Handle != IntPtr.Zero) ReleaseHandle();
                    AssignHandle(_grid.Handle);
                }
                catch { }
            }

            internal void Detach()
            {
                try { if (Handle != IntPtr.Zero) ReleaseHandle(); }
                catch { }
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == 0x020A || m.Msg == 0x020E)
                {
                    bool handled = false;
                    try { handled = GridWheelScroller.TryScroll(_grid, m); }
                    catch { handled = false; }
                    if (handled)
                    {
                        m.Result = IntPtr.Zero;
                        try { _refreshed(); } catch { }
                        return;
                    }
                }
                base.WndProc(ref m);
            }
        }

        // === Списки/деревья: вертикальная своя полоса поверх правого края. ===
        public static void AttachListBox(ScrollHiddenListBox t)
        {
            var v = new ShadowScrollBar(ShadowScrollBar.BarOrientation.Vertical,
                () => t.FirstVisible(), () => t.Total(), () => t.VisibleCount(),
                i => t.ScrollTo(i), () => 3);
            var parent = t.Parent;
            if (parent == null) return;
            parent.Controls.Add(v);

            bool ScrollV(int lines)
            {
                try
                {
                    if (!IsScrollableList(t)) return false;
                    t.ScrollBy(lines);
                    v.RefreshRange();
                    return true;
                }
                catch { return false; }
            }
            ShadowWheel.Register(t, () => IsScrollableList(t), ScrollV);
            ShadowWheel.Register(v, () => IsScrollableList(t), ScrollV);

            void Refresh()
            {
                try
                {
                    if (t.IsDisposed || t.Parent == null || t.Parent.IsDisposed) return;
                    v.Bounds = new Rectangle(t.Right - BarW, t.Top, BarW, Math.Max(1, t.Height));
                    v.RefreshRange();
                    if (v.Visible) v.BringToFront();
                }
                catch { }
            }
            t.ViewScrolled += (s, e) => Refresh();
            t.Resize += (s, e) => Refresh();
            t.LocationChanged += (s, e) => Refresh();
            t.VisibleChanged += (s, e) => Refresh();
            t.HandleCreated += (s, e) => Refresh();
            parent.Resize += (s, e) => Refresh();
            parent.Layout += (s, e) => Refresh();
            Refresh();
        }

        private static bool IsScrollableList(ScrollHiddenListBox t)
        {
            try { return !t.IsDisposed && t.Total() > t.VisibleCount(); }
            catch { return false; }
        }

        public static void AttachCheckedListBox(ScrollHiddenCheckedListBox t)
        {
            var v = new ShadowScrollBar(ShadowScrollBar.BarOrientation.Vertical,
                () => t.FirstVisible(), () => t.Total(), () => t.VisibleCount(),
                i => t.ScrollTo(i), () => 3);
            var parent = t.Parent;
            if (parent == null) return;
            parent.Controls.Add(v);

            bool ScrollV(int lines)
            {
                try
                {
                    if (!IsScrollableList(t)) return false;
                    t.ScrollBy(lines);
                    v.RefreshRange();
                    return true;
                }
                catch { return false; }
            }
            ShadowWheel.Register(t, () => IsScrollableList(t), ScrollV);
            ShadowWheel.Register(v, () => IsScrollableList(t), ScrollV);

            void Refresh()
            {
                try
                {
                    if (t.IsDisposed || t.Parent == null || t.Parent.IsDisposed) return;
                    v.Bounds = new Rectangle(t.Right - BarW, t.Top, BarW, Math.Max(1, t.Height));
                    v.RefreshRange();
                    if (v.Visible) v.BringToFront();
                }
                catch { }
            }
            t.ViewScrolled += (s, e) => Refresh();
            t.Resize += (s, e) => Refresh();
            t.LocationChanged += (s, e) => Refresh();
            t.VisibleChanged += (s, e) => Refresh();
            t.HandleCreated += (s, e) => Refresh();
            parent.Resize += (s, e) => Refresh();
            parent.Layout += (s, e) => Refresh();
            Refresh();
        }

        private static bool IsScrollableList(ScrollHiddenCheckedListBox t)
        {
            try { return !t.IsDisposed && t.Total() > t.VisibleCount(); }
            catch { return false; }
        }

        public static void AttachTreeView(ScrollHiddenTreeView t)
        {
            var v = new ShadowScrollBar(ShadowScrollBar.BarOrientation.Vertical,
                () => t.FirstVisible(), () => t.Total(), () => t.VisibleCount(),
                i => t.ScrollTo(i), () => 3);
            var parent = t.Parent;
            if (parent == null) return;
            parent.Controls.Add(v);

            bool ScrollV(int lines)
            {
                try
                {
                    if (!IsScrollableList(t)) return false;
                    t.ScrollBy(lines);
                    v.RefreshRange();
                    return true;
                }
                catch { return false; }
            }
            ShadowWheel.Register(t, () => IsScrollableList(t), ScrollV);
            ShadowWheel.Register(v, () => IsScrollableList(t), ScrollV);

            void Refresh()
            {
                try
                {
                    if (t.IsDisposed || t.Parent == null || t.Parent.IsDisposed) return;
                    v.Bounds = new Rectangle(t.Right - BarW, t.Top, BarW, Math.Max(1, t.Height));
                    v.RefreshRange();
                    if (v.Visible) v.BringToFront();
                }
                catch { }
            }
            t.ViewScrolled += (s, e) => Refresh();
            t.Resize += (s, e) => Refresh();
            t.LocationChanged += (s, e) => Refresh();
            t.VisibleChanged += (s, e) => Refresh();
            t.HandleCreated += (s, e) => Refresh();
            parent.Resize += (s, e) => Refresh();
            parent.Layout += (s, e) => Refresh();
            Refresh();
        }

        private static bool IsScrollableList(ScrollHiddenTreeView t)
        {
            try { return !t.IsDisposed && t.Total() > t.VisibleCount(); }
            catch { return false; }
        }

        public static void AttachListView(ScrollHiddenListView t)
        {
            var v = new ShadowScrollBar(ShadowScrollBar.BarOrientation.Vertical,
                () => t.FirstVisible(), () => t.Total(), () => t.VisibleCount(),
                i => t.ScrollTo(i), () => 3);
            var parent = t.Parent;
            if (parent == null) return;
            parent.Controls.Add(v);

            bool ScrollV(int lines)
            {
                try
                {
                    if (!IsScrollableList(t)) return false;
                    t.ScrollBy(lines);
                    v.RefreshRange();
                    return true;
                }
                catch { return false; }
            }
            ShadowWheel.Register(t, () => IsScrollableList(t), ScrollV);
            ShadowWheel.Register(v, () => IsScrollableList(t), ScrollV);

            void Refresh()
            {
                try
                {
                    if (t.IsDisposed || t.Parent == null || t.Parent.IsDisposed) return;
                    v.Bounds = new Rectangle(t.Right - BarW, t.Top, BarW, Math.Max(1, t.Height));
                    v.RefreshRange();
                    if (v.Visible) v.BringToFront();
                }
                catch { }
            }
            t.ViewScrolled += (s, e) => Refresh();
            t.Resize += (s, e) => Refresh();
            t.LocationChanged += (s, e) => Refresh();
            t.VisibleChanged += (s, e) => Refresh();
            t.HandleCreated += (s, e) => Refresh();
            parent.Resize += (s, e) => Refresh();
            parent.Layout += (s, e) => Refresh();
            Refresh();
        }

        private static bool IsScrollableList(ScrollHiddenListView t)
        {
            try { return !t.IsDisposed && t.Total() > t.VisibleCount(); }
            catch { return false; }
        }
    }

    // Общие EM_*-вызовы для TextBox/RichTextBox (номера первых видимых
    // строк, общее число строк, скролл). Тот же приём, что ConsoleOutputBox.
    internal static class EditScrollApi
    {
        private const int EM_GETFIRSTVISIBLELINE = 0xCE;
        private const int EM_GETLINECOUNT = 0xBA;
        private const int EM_LINESCROLL = 0xB6;
        private const int EM_LINEFROMCHAR = 0xC9;
        private const int EM_CHARFROMPOS = 0xD7;

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int SendMessage(IntPtr h, int msg, int w, int l);
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int SendMessage(IntPtr h, int msg, int w, ref Point p);

        public static int FirstVisible(IntPtr h)
        {
            try { return Math.Max(0, SendMessage(h, EM_GETFIRSTVISIBLELINE, 0, 0)); }
            catch { return 0; }
        }

        public static int Total(IntPtr h)
        {
            try { return Math.Max(1, SendMessage(h, EM_GETLINECOUNT, 0, 0)); }
            catch { return 1; }
        }

        // Число видимых строк: индекс строки под левым нижним углом клиента.
        public static int Visible(IntPtr h, int clientHeight)
        {
            try
            {
                int first = FirstVisible(h);
                var pt = new Point(2, Math.Max(0, clientHeight - 2));
                int ch = SendMessage(h, EM_CHARFROMPOS, 0, ref pt);
                int line = SendMessage(h, EM_LINEFROMCHAR, ch, 0);
                return Math.Max(1, line - first + 1);
            }
            catch { return 1; }
        }

        public static void ScrollBy(IntPtr h, int lines)
        {
            try { if (lines != 0) SendMessage(h, EM_LINESCROLL, 0, lines); }
            catch { }
        }
    }

    // TextBox, прячущий системную вертикальную полосу (колесо/клавиши
    // скроллят дальше). Механика — как у ConsoleOutputBox из терминала.
    //
    // v4.6.3 — защита от AV-краха (EntryEditor KeePass: AccessViolation в
    // SendMessage(EM_*) во время CreateWindowEx). Корень: IsHandleCreated уже
    // true, а нативный EDIT ещё полусобран — EM_CHARFROMPOS трогает неготовые
    // внутренние структуры. Поэтому _ready ставится только в КОНЦЕ
    // OnHandleCreated, сбрасывается в OnHandleDestroyed, а все замеры и
    // HideBar до готовности — no-op. Плюс анти-реентрантный флаг: SendMessage
    // реентерабельно входит в WndProc, и вложенный HideBar слал сообщения
    // снова (в стеке падения WndProc ×3 вложенных).
    public class ScrollHiddenTextBox : TextBox
    {
        private const int SB_VERT = 1;
        private const int GWL_STYLE = -16;
        private const int WS_VSCROLL = 0x00200000;
        private const int WM_PAINT = 0x000F;
        private const int WM_NCPAINT = 0x0085;
        private const int WM_SIZE = 0x0005;
        private const int WM_MOUSEWHEEL = 0x020A;
        private const int WM_VSCROLL = 0x0115;
        private const int WM_KEYDOWN = 0x0100;

        [DllImport("user32.dll")]
        private static extern bool ShowScrollBar(IntPtr hWnd, int wBar, bool bShow);
        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        public event EventHandler? ViewScrolled;

        private bool _ready;
        private bool _inHideBar;
        internal bool IsReady => _ready && IsHandleCreated && !IsDisposed && !Disposing;

        public int FirstVisibleLine() =>
            !IsReady ? 0 : EditScrollApi.FirstVisible(Handle);
        public int LineCount() =>
            !IsReady ? 1 : EditScrollApi.Total(Handle);
        public int VisibleLineCount() =>
            !IsReady ? 1 : EditScrollApi.Visible(Handle, ClientSize.Height);
        public void ScrollToLine(int line) =>
            EditScrollApi.ScrollBy(IsReady ? Handle : IntPtr.Zero, line - FirstVisibleLine());
        public void ScrollByLines(int lines) =>
            EditScrollApi.ScrollBy(IsReady ? Handle : IntPtr.Zero, lines);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            _ready = true;
            HideBar();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            _ready = false;
            base.OnHandleDestroyed(e);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            HideBar();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_PAINT || m.Msg == WM_NCPAINT || m.Msg == WM_SIZE)
                ClearVScroll();
            // v4.4.0 — колесо ловим здесь же (прямая доставка SendMessage мимо
            // очереди фильтрам не видна). На упоре — base, пусть всплывает.
            if (m.Msg == WM_MOUSEWHEEL && TryWheelScroll(m))
            {
                m.Result = IntPtr.Zero;
                HideBar();
                ViewScrolled?.Invoke(this, EventArgs.Empty);
                return;
            }
            base.WndProc(ref m);
            HideBar();
            if (m.Msg == WM_MOUSEWHEEL || m.Msg == WM_VSCROLL || m.Msg == WM_KEYDOWN)
                ViewScrolled?.Invoke(this, EventArgs.Empty);
        }

        private bool TryWheelScroll(Message m)
        {
            try
            {
                int raw = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
                if (raw == 0) return false;
                int lines = -(int)Math.Round(raw / 120.0 * 3);
                if (lines == 0) lines = raw > 0 ? -1 : 1;
                int first = FirstVisibleLine();
                int total = LineCount();
                int page = VisibleLineCount();
                if (total <= page) return false;
                if (lines < 0 && first <= 0) return false;
                if (lines > 0 && first + page >= total) return false;
                EditScrollApi.ScrollBy(IsHandleCreated ? Handle : IntPtr.Zero, lines);
                return true;
            }
            catch { return false; }
        }

        private void ClearVScroll()
        {
            try
            {
                if (!IsReady) return;
                int style = GetWindowLong(Handle, GWL_STYLE);
                if ((style & WS_VSCROLL) != 0)
                    SetWindowLong(Handle, GWL_STYLE, style & ~WS_VSCROLL);
            }
            catch { }
        }

        private void HideBar()
        {
            // Анти-реентрантность: ShowScrollBar/SendMessage синхронно входят
            // обратно в WndProc — вложенный вызов выходим сразу.
            if (_inHideBar || !IsReady) return;
            _inHideBar = true;
            try
            {
                ShowScrollBar(Handle, SB_VERT, false);
                ClearVScroll();
            }
            catch { }
            finally { _inHideBar = false; }
        }
    }

    // RichTextBox, прячущий системную вертикальную полосу. Для просмотрщиков
    // вроде «Ответ ИИ». Механика та же, что у ScrollHiddenTextBox.
    public class ScrollHiddenRichTextBox : RichTextBox
    {
        private const int SB_VERT = 1;
        private const int GWL_STYLE = -16;
        private const int WS_VSCROLL = 0x00200000;
        private const int WM_PAINT = 0x000F;
        private const int WM_NCPAINT = 0x0085;
        private const int WM_SIZE = 0x0005;
        private const int WM_MOUSEWHEEL = 0x020A;
        private const int WM_VSCROLL = 0x0115;
        private const int WM_KEYDOWN = 0x0100;

        [DllImport("user32.dll")]
        private static extern bool ShowScrollBar(IntPtr hWnd, int wBar, bool bShow);
        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        public event EventHandler? ViewScrolled;

        // v4.6.3 — та же защита, что у ScrollHiddenTextBox (готовность хэндла
        // + анти-реентрантность HideBar): RichTextBox умирает так же.
        private bool _ready;
        private bool _inHideBar;
        internal bool IsReady => _ready && IsHandleCreated && !IsDisposed && !Disposing;

        public int FirstVisibleLine() =>
            !IsReady ? 0 : EditScrollApi.FirstVisible(Handle);
        public int LineCount() =>
            !IsReady ? 1 : EditScrollApi.Total(Handle);
        public int VisibleLineCount() =>
            !IsReady ? 1 : EditScrollApi.Visible(Handle, ClientSize.Height);
        public void ScrollToLine(int line) =>
            EditScrollApi.ScrollBy(IsReady ? Handle : IntPtr.Zero, line - FirstVisibleLine());
        public void ScrollByLines(int lines) =>
            EditScrollApi.ScrollBy(IsReady ? Handle : IntPtr.Zero, lines);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            _ready = true;
            HideBar();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            _ready = false;
            base.OnHandleDestroyed(e);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            HideBar();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_PAINT || m.Msg == WM_NCPAINT || m.Msg == WM_SIZE)
                ClearVScroll();
            // v4.4.0 — колесо ловим здесь же (см. ScrollHiddenTextBox).
            if (m.Msg == WM_MOUSEWHEEL && TryWheelScroll(m))
            {
                m.Result = IntPtr.Zero;
                HideBar();
                ViewScrolled?.Invoke(this, EventArgs.Empty);
                return;
            }
            base.WndProc(ref m);
            HideBar();
            if (m.Msg == WM_MOUSEWHEEL || m.Msg == WM_VSCROLL || m.Msg == WM_KEYDOWN)
                ViewScrolled?.Invoke(this, EventArgs.Empty);
        }

        private bool TryWheelScroll(Message m)
        {
            try
            {
                int raw = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
                if (raw == 0) return false;
                int lines = -(int)Math.Round(raw / 120.0 * 3);
                if (lines == 0) lines = raw > 0 ? -1 : 1;
                int first = FirstVisibleLine();
                int total = LineCount();
                int page = VisibleLineCount();
                if (total <= page) return false;
                if (lines < 0 && first <= 0) return false;
                if (lines > 0 && first + page >= total) return false;
                EditScrollApi.ScrollBy(IsHandleCreated ? Handle : IntPtr.Zero, lines);
                return true;
            }
            catch { return false; }
        }

        private void ClearVScroll()
        {
            try
            {
                if (!IsReady) return;
                int style = GetWindowLong(Handle, GWL_STYLE);
                if ((style & WS_VSCROLL) != 0)
                    SetWindowLong(Handle, GWL_STYLE, style & ~WS_VSCROLL);
            }
            catch { }
        }

        private void HideBar()
        {
            // v4.6.3 — см. ScrollHiddenTextBox.HideBar: готовность + анти-реентрантность.
            if (_inHideBar || !IsReady) return;
            _inHideBar = true;
            try
            {
                ShowScrollBar(Handle, SB_VERT, false);
                ClearVScroll();
            }
            catch { }
            finally { _inHideBar = false; }
        }
    }

    // Общий скролл колеса для DataGridView: им пользуются и WndProc-перехват
    // DoubleBufferedDataGridView, и NativeWindow-хук для обычных гридов
    // (класс которых менять нельзя — напр. AddressBook). Возвращает true,
    // если позиция реально сдвинулась (иначе сообщение всплывает родителю).
    internal static class GridWheelScroller
    {
        private const int WM_MOUSEWHEEL = 0x020A;
        private const int WM_MOUSEHWHEEL = 0x020E;

        public static bool TryScroll(DataGridView grid, Message m)
        {
            try
            {
                int raw = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
                if (raw == 0) return false;
                if (m.Msg == WM_MOUSEHWHEEL)
                {
                    int px = (int)Math.Round(raw / 120.0 * 60);
                    if (px == 0) px = raw > 0 ? -20 : 20;
                    int cur = 0;
                    try { cur = grid.HorizontalScrollingOffset; } catch { return false; }
                    int nv = Math.Max(0, cur + px);
                    if (nv == cur) return false;
                    try { grid.HorizontalScrollingOffset = nv; } catch { return false; }
                    return true;
                }
                int lines = -(int)Math.Round(raw / 120.0 * 3);
                if (lines == 0) lines = raw > 0 ? -1 : 1;
                if (grid.Rows.Count == 0) return false;
                int page = PageRows(grid);
                if (grid.Rows.Count <= page) return false;
                int n = Math.Max(0, Math.Min(grid.Rows.Count - page, FirstRow(grid) + lines));
                if (n == FirstRow(grid)) return false;
                try { grid.FirstDisplayedScrollingRowIndex = n; } catch { return false; }
                return true;
            }
            catch { return false; }
        }

        public static int FirstRow(DataGridView grid)
        {
            try
            {
                if (grid.Rows.Count == 0) return 0;
                return Math.Max(0, grid.FirstDisplayedScrollingRowIndex);
            }
            catch { return 0; }
        }

        public static int PageRows(DataGridView grid)
        {
            try { return Math.Max(1, grid.DisplayedRowCount(false)); }
            catch { return 1; }
        }

        public static void ScrollToRow(DataGridView grid, int n)
        {
            try
            {
                if (grid.IsDisposed || grid.Rows.Count == 0) return;
                n = Math.Max(0, Math.Min(grid.Rows.Count - PageRows(grid), n));
                if (n != FirstRow(grid)) grid.FirstDisplayedScrollingRowIndex = n;
            }
            catch { }
        }

        public static int TotalWidth(DataGridView grid)
        {
            int w = 0;
            try { foreach (DataGridViewColumn c in grid.Columns) if (c.Visible) w += c.Width; }
            catch { }
            return w;
        }
    }

    // ListBox без системной полосы + программное колесо (попапы, списки).
    public class ScrollHiddenListBox : ListBox
    {
        private const int SB_VERT = 1;
        private const int GWL_STYLE = -16;
        private const int WS_VSCROLL = 0x00200000;
        private const int WM_PAINT = 0x000F;
        private const int WM_NCPAINT = 0x0085;
        private const int WM_SIZE = 0x0005;
        private const int WM_MOUSEWHEEL = 0x020A;
        private const int WM_VSCROLL = 0x0115;
        private const int WM_KEYDOWN = 0x0100;

        [DllImport("user32.dll")]
        private static extern bool ShowScrollBar(IntPtr hWnd, int wBar, bool bShow);
        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        public event EventHandler? ViewScrolled;

        public int FirstVisible()
        {
            try { return IsHandleCreated ? Math.Max(0, TopIndex) : 0; }
            catch { return 0; }
        }
        public int Total()
        {
            try { return Items.Count; } catch { return 0; }
        }
        public int VisibleCount()
        {
            try { return Math.Max(1, ClientSize.Height / Math.Max(1, ItemHeight)); }
            catch { return 1; }
        }
        public void ScrollTo(int i)
        {
            try
            {
                if (!IsHandleCreated || Items.Count == 0) return;
                TopIndex = Math.Max(0, Math.Min(Items.Count - 1, i));
            }
            catch { }
        }
        public void ScrollBy(int n) => ScrollTo(FirstVisible() + n);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            HideBar();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            HideBar();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_PAINT || m.Msg == WM_NCPAINT || m.Msg == WM_SIZE)
                ClearVScroll();
            if (m.Msg == WM_MOUSEWHEEL && TryWheelScroll(m))
            {
                m.Result = IntPtr.Zero;
                HideBar();
                ViewScrolled?.Invoke(this, EventArgs.Empty);
                return;
            }
            base.WndProc(ref m);
            HideBar();
            if (m.Msg == WM_MOUSEWHEEL || m.Msg == WM_VSCROLL || m.Msg == WM_KEYDOWN)
                ViewScrolled?.Invoke(this, EventArgs.Empty);
        }

        private bool TryWheelScroll(Message m)
        {
            try
            {
                int raw = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
                if (raw == 0) return false;
                int lines = -(int)Math.Round(raw / 120.0 * 3);
                if (lines == 0) lines = raw > 0 ? -1 : 1;
                int first = FirstVisible();
                int total = Total();
                if (total <= VisibleCount()) return false;
                if (lines < 0 && first <= 0) return false;
                if (lines > 0 && first >= total - 1) return false;
                ScrollBy(lines);
                return true;
            }
            catch { return false; }
        }

        private void ClearVScroll()
        {
            try
            {
                if (!IsHandleCreated) return;
                int style = GetWindowLong(Handle, GWL_STYLE);
                if ((style & WS_VSCROLL) != 0)
                    SetWindowLong(Handle, GWL_STYLE, style & ~WS_VSCROLL);
            }
            catch { }
        }

        private void HideBar()
        {
            try
            {
                if (!IsHandleCreated) return;
                ShowScrollBar(Handle, SB_VERT, false);
                ClearVScroll();
            }
            catch { }
        }
    }

    // CheckedListBox без системной полосы (список колонок экспорта).
    public class ScrollHiddenCheckedListBox : CheckedListBox
    {
        private const int SB_VERT = 1;
        private const int GWL_STYLE = -16;
        private const int WS_VSCROLL = 0x00200000;
        private const int WM_PAINT = 0x000F;
        private const int WM_NCPAINT = 0x0085;
        private const int WM_SIZE = 0x0005;
        private const int WM_MOUSEWHEEL = 0x020A;
        private const int WM_VSCROLL = 0x0115;
        private const int WM_KEYDOWN = 0x0100;

        [DllImport("user32.dll")]
        private static extern bool ShowScrollBar(IntPtr hWnd, int wBar, bool bShow);
        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        public event EventHandler? ViewScrolled;

        public int FirstVisible()
        {
            try { return IsHandleCreated ? Math.Max(0, TopIndex) : 0; }
            catch { return 0; }
        }
        public int Total()
        {
            try { return Items.Count; } catch { return 0; }
        }
        public int VisibleCount()
        {
            try { return Math.Max(1, ClientSize.Height / Math.Max(1, ItemHeight)); }
            catch { return 1; }
        }
        public void ScrollTo(int i)
        {
            try
            {
                if (!IsHandleCreated || Items.Count == 0) return;
                TopIndex = Math.Max(0, Math.Min(Items.Count - 1, i));
            }
            catch { }
        }
        public void ScrollBy(int n) => ScrollTo(FirstVisible() + n);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            HideBar();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            HideBar();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_PAINT || m.Msg == WM_NCPAINT || m.Msg == WM_SIZE)
                ClearVScroll();
            if (m.Msg == WM_MOUSEWHEEL && TryWheelScroll(m))
            {
                m.Result = IntPtr.Zero;
                HideBar();
                ViewScrolled?.Invoke(this, EventArgs.Empty);
                return;
            }
            base.WndProc(ref m);
            HideBar();
            if (m.Msg == WM_MOUSEWHEEL || m.Msg == WM_VSCROLL || m.Msg == WM_KEYDOWN)
                ViewScrolled?.Invoke(this, EventArgs.Empty);
        }

        private bool TryWheelScroll(Message m)
        {
            try
            {
                int raw = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
                if (raw == 0) return false;
                int lines = -(int)Math.Round(raw / 120.0 * 3);
                if (lines == 0) lines = raw > 0 ? -1 : 1;
                int first = FirstVisible();
                int total = Total();
                if (total <= VisibleCount()) return false;
                if (lines < 0 && first <= 0) return false;
                if (lines > 0 && first >= total - 1) return false;
                ScrollBy(lines);
                return true;
            }
            catch { return false; }
        }

        private void ClearVScroll()
        {
            try
            {
                if (!IsHandleCreated) return;
                int style = GetWindowLong(Handle, GWL_STYLE);
                if ((style & WS_VSCROLL) != 0)
                    SetWindowLong(Handle, GWL_STYLE, style & ~WS_VSCROLL);
            }
            catch { }
        }

        private void HideBar()
        {
            try
            {
                if (!IsHandleCreated) return;
                ShowScrollBar(Handle, SB_VERT, false);
                ClearVScroll();
            }
            catch { }
        }
    }

    // TreeView без системных полос (дерево групп KeePass). Позиция считается
    // по видимым узлам (раскрытые ветки), скролл — построчными WM_VSCROLL.
    public class ScrollHiddenTreeView : TreeView
    {
        private const int SB_BOTH = 3;
        private const int WM_PAINT = 0x000F;
        private const int WM_NCPAINT = 0x0085;
        private const int WM_SIZE = 0x0005;
        private const int WM_MOUSEWHEEL = 0x020A;
        private const int WM_VSCROLL = 0x0115;
        private const int WM_KEYDOWN = 0x0100;
        private const int SB_LINEUP = 0;
        private const int SB_LINEDOWN = 1;

        [DllImport("user32.dll")]
        private static extern bool ShowScrollBar(IntPtr hWnd, int wBar, bool bShow);
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        public event EventHandler? ViewScrolled;

        public int FirstVisible()
        {
            try
            {
                if (!IsHandleCreated) return 0;
                return Math.Max(0, VisibleIndexOf(TopNode));
            }
            catch { return 0; }
        }
        public int Total()
        {
            try { return Math.Max(0, GetNodeCount(true)); }
            catch { return 0; }
        }
        public int VisibleCount()
        {
            try { return Math.Max(1, ClientSize.Height / Math.Max(1, ItemHeight)); }
            catch { return 1; }
        }
        public void ScrollTo(int i)
        {
            try
            {
                if (!IsHandleCreated) return;
                var node = NodeAtVisible(Math.Max(0, i));
                if (node != null) TopNode = node;
            }
            catch { }
        }
        public void ScrollBy(int n)
        {
            try
            {
                if (!IsHandleCreated) return;
                n = Math.Max(-200, Math.Min(200, n));
                if (n == 0) return;
                // v4.9.4 — единый TopNode-перход вместо цикла из до 200
                // WM_VSCROLL. Нативный цикл давал рывки и дроп отрисовки
                // на больших ветках; TopNode ходит сразу.
                ScrollTo(FirstVisible() + n);
            }
            catch { }
        }

        private int VisibleIndexOf(TreeNode? target)
        {
            if (target == null) return 0;
            int idx = 0;
            foreach (TreeNode n in Nodes)
            {
                int r = WalkIndex(n, target, ref idx);
                if (r >= 0) return r;
            }
            return 0;
        }

        private static int WalkIndex(TreeNode cur, TreeNode target, ref int idx)
        {
            if (cur == target) return idx;
            idx++;
            if (cur.IsExpanded)
                foreach (TreeNode c in cur.Nodes)
                {
                    int r = WalkIndex(c, target, ref idx);
                    if (r >= 0) return r;
                }
            return -1;
        }

        private TreeNode? NodeAtVisible(int index)
        {
            int idx = 0;
            foreach (TreeNode n in Nodes)
            {
                var r = WalkFind(n, index, ref idx);
                if (r != null) return r;
            }
            return null;
        }

        private static TreeNode? WalkFind(TreeNode cur, int index, ref int idx)
        {
            if (idx == index) return cur;
            idx++;
            if (cur.IsExpanded)
                foreach (TreeNode c in cur.Nodes)
                {
                    var r = WalkFind(c, index, ref idx);
                    if (r != null) return r;
                }
            return null;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            HideBars();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            HideBars();
        }

        protected override void OnAfterExpand(TreeViewEventArgs e)
        {
            base.OnAfterExpand(e);
            ViewScrolled?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnAfterCollapse(TreeViewEventArgs e)
        {
            base.OnAfterCollapse(e);
            ViewScrolled?.Invoke(this, EventArgs.Empty);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_PAINT || m.Msg == WM_NCPAINT || m.Msg == WM_SIZE)
                HideBars();
            if (m.Msg == WM_MOUSEWHEEL && TryWheelScroll(m))
            {
                m.Result = IntPtr.Zero;
                HideBars();
                ViewScrolled?.Invoke(this, EventArgs.Empty);
                return;
            }
            base.WndProc(ref m);
            HideBars();
            if (m.Msg == WM_MOUSEWHEEL || m.Msg == WM_VSCROLL || m.Msg == WM_KEYDOWN)
                ViewScrolled?.Invoke(this, EventArgs.Empty);
        }

        private bool TryWheelScroll(Message m)
        {
            try
            {
                int raw = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
                if (raw == 0) return false;
                int lines = -(int)Math.Round(raw / 120.0 * 3);
                if (lines == 0) lines = raw > 0 ? -1 : 1;
                int first = FirstVisible();
                int total = Total();
                if (total <= VisibleCount()) return false;
                if (lines < 0 && first <= 0) return false;
                if (lines > 0 && first + VisibleCount() >= total) return false;
                ScrollBy(lines);
                return true;
            }
            catch { return false; }
        }

        private void HideBars()
        {
            try { if (IsHandleCreated) ShowScrollBar(Handle, SB_BOTH, false); }
            catch { }
        }
    }

    // ListView без системных полос (панели SFTP). Позиция — по TopItem.
    public class ScrollHiddenListView : ListView
    {
        private const int SB_BOTH = 3;
        private const int WM_PAINT = 0x000F;
        private const int WM_NCPAINT = 0x0085;
        private const int WM_SIZE = 0x0005;
        private const int WM_MOUSEWHEEL = 0x020A;
        private const int WM_VSCROLL = 0x0115;
        private const int WM_KEYDOWN = 0x0100;

        [DllImport("user32.dll")]
        private static extern bool ShowScrollBar(IntPtr hWnd, int wBar, bool bShow);

        public event EventHandler? ViewScrolled;

        public int FirstVisible()
        {
            try
            {
                if (!IsHandleCreated || TopItem == null) return 0;
                return Math.Max(0, TopItem.Index);
            }
            catch { return 0; }
        }
        public int Total()
        {
            try { return Items.Count; } catch { return 0; }
        }
        public int VisibleCount()
        {
            try
            {
                int itemH = Font.Height + 6;
                try
                {
                    if (TopItem != null)
                    {
                        var r = GetItemRect(TopItem.Index);
                        if (r.Height > 0) itemH = r.Height;
                    }
                }
                catch { }
                return Math.Max(1, ClientSize.Height / Math.Max(1, itemH));
            }
            catch { return 1; }
        }
        public void ScrollTo(int i)
        {
            try
            {
                if (!IsHandleCreated || Items.Count == 0) return;
                TopItem = Items[Math.Max(0, Math.Min(Items.Count - 1, i))];
            }
            catch { }
        }
        public void ScrollBy(int n) => ScrollTo(FirstVisible() + n);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            HideBars();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            HideBars();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_PAINT || m.Msg == WM_NCPAINT || m.Msg == WM_SIZE)
                HideBars();
            if (m.Msg == WM_MOUSEWHEEL && TryWheelScroll(m))
            {
                m.Result = IntPtr.Zero;
                HideBars();
                ViewScrolled?.Invoke(this, EventArgs.Empty);
                return;
            }
            base.WndProc(ref m);
            HideBars();
            if (m.Msg == WM_MOUSEWHEEL || m.Msg == WM_VSCROLL || m.Msg == WM_KEYDOWN)
                ViewScrolled?.Invoke(this, EventArgs.Empty);
        }

        private bool TryWheelScroll(Message m)
        {
            try
            {
                int raw = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
                if (raw == 0) return false;
                int lines = -(int)Math.Round(raw / 120.0 * 3);
                if (lines == 0) lines = raw > 0 ? -1 : 1;
                int first = FirstVisible();
                int total = Total();
                if (total <= VisibleCount()) return false;
                if (lines < 0 && first <= 0) return false;
                if (lines > 0 && first + VisibleCount() >= total) return false;
                ScrollBy(lines);
                return true;
            }
            catch { return false; }
        }

        private void HideBars()
        {
            try { if (IsHandleCreated) ShowScrollBar(Handle, SB_BOTH, false); }
            catch { }
        }
    }
}
