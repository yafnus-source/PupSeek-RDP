using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using ShadowRDP.Helpers;

namespace ShadowRDP.Controls
{
    // GroupBox with a refined flat look. Replaces the default WinForms
    // GroupBox which has a heavy grey border + tiny blue title text.
    //
    // v2.0 styled to match the Figma redesign — card-coloured background,
    // indigo accent title, subtle warm border.
    public class NeoGroupBox : GroupBox
    {
        public string Title
        {
            get => Text;
            set => Text = value;
        }

        public NeoGroupBox()
        {
            BackColor = UiTheme.Card;
            Font = new Font("Segoe UI Semibold", 10F);
            DoubleBuffered = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // Card-style rounded border in the Figma warm border colour.
            var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
            var radius = UiTheme.RadiusLg;
            using var path = RoundedRect(bounds, radius);
            using var pen = new Pen(UiTheme.Border, 1);
            g.DrawPath(pen, path);

            // Title text sits on top of the border (drawn after) so the border
            // appears "cut" by the label — gives a card-like look.
            if (!string.IsNullOrEmpty(Text))
            {
                var titleFont = new Font("Segoe UI Semibold", 10F);
                var titleSize = g.MeasureString(Text, titleFont);
                var padLeft = 8;
                var padRight = 8;
                var titleY = 0;
                var titleRect = new Rectangle(
                    x: padLeft,
                    y: titleY,
                    width: (int)(titleSize.Width + padRight),
                    height: (int)Math.Max(titleSize.Height, 14));

                using var bgBrush = new SolidBrush(UiTheme.Card);
                g.FillRectangle(bgBrush, titleRect);

                // v2.0 title colour — indigo accent.
                using var titleBrush = new SolidBrush(UiTheme.Accent);
                var sf = new StringFormat
                {
                    LineAlignment = StringAlignment.Center,
                    Alignment = StringAlignment.Near
                };
                g.DrawString(Text, titleFont, titleBrush,
                    new RectangleF(padLeft + 2, titleY, titleRect.Width - 2, titleRect.Height),
                    sf);
            }
        }

        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            if (radius <= 0)
            {
                path.AddRectangle(bounds);
                return path;
            }
            var d = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}

