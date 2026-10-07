using System;
using System.Drawing;
using System.Windows.Forms;
using ShadowRDP.Models;

namespace ShadowRDP.Helpers
{
    // v3.6.8 — a row in the "Встроить ММС" recent-console menu.
    // Layout: console icon + name on the left (click → open the console),
    // a ✕ button on the right (click → remove the entry from history,
    // confirmation handled by the caller via OnDelete).
    public sealed class EmbedRecentItem : ToolStripItem
    {
        private const int IconSize = 16;
        private const int CloseSize = 20;
        private bool _hoverClose;

        public EmbedRecentItem(EmbeddedAppEntry entry)
        {
            Entry = entry;
            Text = entry.Name;
            Padding = Padding.Empty;
            Margin = Padding.Empty;
        }

        public EmbeddedAppEntry Entry { get; }
        public Image? ProgramIcon { get; set; }
        public Action<EmbeddedAppEntry>? OnOpen { get; set; }
        public Action<EmbeddedAppEntry>? OnDelete { get; set; }

        // The row stretches to the menu's content width (see PopulateEmbedMenu
        // in RdpManagerForm) so the ✕ always sits at the menu's right edge.
        // DrawWidth clamps the drawing area to the row's own bounds so the ✕
        // can never render outside (and get clipped by) the row rectangle.
        private int RowWidth => Owner is ToolStripDropDown dd ? dd.DisplayRectangle.Width : Width;
        private int DrawWidth => Math.Min(RowWidth, Width);

        private Rectangle CloseRect =>
            new Rectangle(DrawWidth - CloseSize - 4, (Height - CloseSize) / 2, CloseSize, CloseSize);

        public override Size GetPreferredSize(Size proposedSize)
        {
            var font = Font ?? SystemFonts.MenuFont;
            var textSize = TextRenderer.MeasureText(Text, font);
            return new Size(4 + IconSize + 6 + textSize.Width + 8 + CloseSize + 4, 26);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            var font = Font ?? SystemFonts.MenuFont;
            var midY = (Height - IconSize) / 2;

            // v3.6.8 — small hover highlight on the whole row (#EDE8E2) on top
            // of the default ToolStripItem selection colour so the menu matches
            // the rest of the app's cream theme.
            if (Selected)
            {
                using var hoverBrush = new SolidBrush(Color.FromArgb(0xED, 0xE8, 0xE2));
                g.FillRectangle(hoverBrush, 0, 0, Width, Height);
            }

            // Program icon on the left.
            // v3.9.3 — bicubic-масштаб иконки (был Default → пиксели).
            if (ProgramIcon != null)
            {
                var imPrev = g.InterpolationMode;
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(ProgramIcon, new Rectangle(4, midY, IconSize, IconSize));
                g.InterpolationMode = imPrev;
            }
            else
            {
                TextRenderer.DrawText(g, "⚙", font, new Rectangle(4, midY, IconSize, IconSize),
                    ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            // Program name.
            var txtRect = new Rectangle(4 + IconSize + 6, 0,
                DrawWidth - (4 + IconSize + 6 + 8 + CloseSize + 4), Height);
            TextRenderer.DrawText(g, Text, font, txtRect, ForeColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            // ✕ close button on the right.
            var close = CloseRect;
            if (_hoverClose)
            {
                using var hoverBrush = new SolidBrush(Color.FromArgb(0x2A, 0x4F, 0x7A));
                g.FillRectangle(hoverBrush, close);
            }
            TextRenderer.DrawText(g, "✕", font, close,
                _hoverClose ? Color.White : Color.Gray,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var over = CloseRect.Contains(e.Location);
            if (over != _hoverClose)
            {
                _hoverClose = over;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverClose)
            {
                _hoverClose = false;
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            if (CloseRect.Contains(e.Location))
                OnDelete?.Invoke(Entry);
            else
                OnOpen?.Invoke(Entry);
        }
    }
}
