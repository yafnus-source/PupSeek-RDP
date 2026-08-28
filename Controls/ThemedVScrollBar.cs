using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ShadowRDP.Controls
{
    /// <summary>
    /// Minimal dark-themed vertical scrollbar for the embedded terminal.
    /// Replaces the default Windows RichTextBox scrollbar. Position/metrics
    /// are pulled live from callbacks, so it always reflects the real state
    /// of the output control (native wheel scrolling included).
    /// </summary>
    internal sealed class ThemedVScrollBar : Control
    {
        private enum ThumbState { Normal, Hover, Pressed }

        private const int BarWidth = 12;
        private const int MinThumbHeight = 26;
        private const int ThumbWidth = 6;
        private const int ThumbCorner = 3;

        private readonly Func<int> _getFirstLine;
        private readonly Func<int> _getTotalLines;
        private readonly Func<int> _getVisibleLines;
        private readonly Action<int> _scrollToFirstLine;

        private ThumbState _state = ThumbState.Normal;
        private bool _dragging;
        private int _dragStartY;
        private int _dragStartValue;
        private int _dragMaxScroll;

        public ThemedVScrollBar(
            Func<int> getFirstLine,
            Func<int> getTotalLines,
            Func<int> getVisibleLines,
            Action<int> scrollToFirstLine)
        {
            _getFirstLine = getFirstLine;
            _getTotalLines = getTotalLines;
            _getVisibleLines = getVisibleLines;
            _scrollToFirstLine = scrollToFirstLine;

            SetStyle(ControlStyles.UserPaint |
                     ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
            Width = BarWidth;
            Dock = DockStyle.Right;
            Cursor = Cursors.Hand;
        }

        private int TotalLines()
        {
            try { return _getTotalLines(); } catch { return 1; }
        }

        private int CurrentValue()
        {
            try { return _getFirstLine(); } catch { return 0; }
        }

        private int VisibleLines()
        {
            try { return _getVisibleLines(); } catch { return 1; }
        }

        private bool HasThumb => TotalLines() > VisibleLines();

        private int MaxScroll() => Math.Max(0, TotalLines() - VisibleLines());

        private Rectangle ThumbRect()
        {
            int total = TotalLines();
            int visible = VisibleLines();
            int maxScroll = MaxScroll();
            int thumbH = Math.Max(MinThumbHeight,
                (int)Math.Round(Height * (double)visible / total));
            int maxTop = Math.Max(0, Height - thumbH);
            double frac = maxScroll == 0 ? 0 : (double)Math.Max(0, CurrentValue()) / maxScroll;
            int top = (int)Math.Round(maxTop * frac);
            int x = (Width - ThumbWidth) / 2;
            return new Rectangle(x, top, ThumbWidth, thumbH);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.Clear(Color.FromArgb(0x0A, 0x0A, 0x0A));

            using (var track = new SolidBrush(Color.FromArgb(0x11, 0x11, 0x11)))
                e.Graphics.FillRectangle(track, 0, 0, Width, Height);
            using (var sep = new Pen(Color.FromArgb(0x2A, 0x2A, 0x2A)))
                e.Graphics.DrawLine(sep, 0, 0, 0, Height);

            if (!HasThumb) return;

            var thumb = ThumbRect();
            Color c = _state switch
            {
                ThumbState.Pressed => Color.FromArgb(0x7A, 0x7A, 0x7A),
                ThumbState.Hover => Color.FromArgb(0x56, 0x56, 0x56),
                _ => Color.FromArgb(0x3A, 0x3A, 0x3A)
            };
            using (var b = new SolidBrush(c))
            using (var path = RoundedRect(thumb, ThumbCorner))
                e.Graphics.FillPath(b, path);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || !HasThumb) return;

            var thumb = ThumbRect();
            if (thumb.Contains(e.Location))
            {
                _dragging = true;
                _state = ThumbState.Pressed;
                _dragStartY = e.Y;
                _dragStartValue = CurrentValue();
                _dragMaxScroll = MaxScroll();
                Capture = true;
            }
            else
            {
                _scrollToFirstLine(CurrentValue() + (e.Y < thumb.Y ? -VisibleLines() : VisibleLines()));
            }
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging)
            {
                if (!HasThumb) return;
                int maxScroll = _dragMaxScroll;
                int thumbH = ThumbRect().Height;
                int usable = Math.Max(1, Height - thumbH);
                int nv = _dragStartValue + (int)Math.Round((e.Y - _dragStartY) * (double)maxScroll / usable);
                nv = Math.Max(0, Math.Min(maxScroll, nv));
                if (nv != CurrentValue())
                    _scrollToFirstLine(nv);
                Invalidate();
            }
            else
            {
                var st = ThumbRect().Contains(e.Location) ? ThumbState.Hover : ThumbState.Normal;
                if (st != _state)
                {
                    _state = st;
                    Invalidate();
                }
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

        private static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            if (radius <= 0)
            {
                path.AddRectangle(r);
                return path;
            }
            int d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
