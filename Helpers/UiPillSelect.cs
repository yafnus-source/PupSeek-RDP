using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace ShadowRDP.Helpers
{
    // v3.9.3AW — пил-селект в дизайн-системе: замена нативного ComboBox
    // в белых обёртках (квадратный системный комбо с собственной рамкой
    // внутри скругления всегда выглядит «двойным»). Белое скруглённое поле,
    // значение + стрелка, выбор через ThemedContextMenu (с галкой).
    public class UiPillSelect : Panel
    {
        private readonly List<string> _items = new();
        private int _sel = -1;

        public event Action? Changed;

        public IReadOnlyList<string> Items => _items;
        public int SelectedIndex
        {
            get => _sel;
            set
            {
                int v = value < 0 || value >= _items.Count ? (_items.Count > 0 ? 0 : -1) : value;
                if (v == _sel) return;
                _sel = v;
                try { Invalidate(); } catch { }
                try { Changed?.Invoke(); } catch { }
            }
        }
        public string? SelectedItem => _sel >= 0 && _sel < _items.Count ? _items[_sel] : null;

        public void SetItems(IEnumerable<string> items, int selected = 0)
        {
            _items.Clear();
            _items.AddRange(items);
            _sel = -1;
            SelectedIndex = selected;
            try { Invalidate(); } catch { }
        }

        public UiPillSelect()
        {
            Size = new Size(150, 30);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            TabStop = true;
            try { SetStyle(ControlStyles.Selectable, true); } catch { }
            Paint += OnPaint;
            Click += (s, e) => Open();
            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) { Open(); e.Handled = true; }
            };
            GotFocus += (s, e) => { try { Invalidate(); } catch { } };
            LostFocus += (s, e) => { try { Invalidate(); } catch { } };
            MouseEnter += (s, e) => { try { Invalidate(); } catch { } };
            MouseLeave += (s, e) => { try { Invalidate(); } catch { } };
        }

        private void OnPaint(object? sender, PaintEventArgs e)
        {
            try
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                    var rect = new Rectangle(0, 0, Width - 1, Height - 1);
                    using var path = UiButtons.RoundedRect(rect, 7);
                    using (var brush = new SolidBrush(Color.White)) g.FillPath(brush, path);
                bool focused = false;
                try { focused = Focused; } catch { }
                bool hov = false;
                try { hov = ClientRectangle.Contains(PointToClient(Control.MousePosition)); } catch { }
                var border = !Enabled ? UiTheme.Border
                    : focused ? UiTheme.Accent
                    : hov ? ControlPaint.Dark(UiTheme.Border, 0.08f) : UiTheme.Border;
                using (var pen = new Pen(border, focused && Enabled ? 1.5f : 1f)) g.DrawPath(pen, path);
                var fg = !Enabled ? UiTheme.MutedForeground : UiTheme.Foreground;
                string txt = SelectedItem ?? "";
                using var font = new Font("Segoe UI", 9.5f);
                var tr = new Rectangle(10, 0, Math.Max(0, Width - 32), Height);
                TextRenderer.DrawText(g, txt, font, tr, fg,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
                // Стрелка — рисуем сами (без зависимости от шрифтовых глифов).
                int cx = Width - 14, cy = Height / 2;
                using var tri = new SolidBrush(fg);
                var pts = new[] { new Point(cx - 5, cy - 2), new Point(cx + 5, cy - 2), new Point(cx, cy + 3) };
                g.FillPolygon(tri, pts);
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SystemDefault;
            }
            catch { }
        }

        public void Open()
        {
            if (!Enabled || IsDisposed) return;
            try
            {
                var menu = new ThemedContextMenu();
                for (int i = 0; i < _items.Count; i++)
                {
                    int idx = i;
                    menu.AddItem(_items[i], null, () => { SelectedIndex = idx; }, null, idx == _sel);
                }
                menu.Show(this, PointToScreen(new Point(0, Height)));
            }
            catch { }
        }
    }
}
