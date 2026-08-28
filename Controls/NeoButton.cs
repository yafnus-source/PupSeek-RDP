using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using ShadowRDP.Helpers;

namespace ShadowRDP.Controls
{
    // Button with the NeoUI flat look + rounded corners. Mirror of the
    // NeoButton class embedded as Add-Type in ShadowRDP-GUI.ps1, modernised
    // in v2.0 to match the Figma redesign (indigo accent, configurable
    // corner radius, pill mode for filter tabs).
    //
    // Modes:
    //   Accent = true  → solid indigo background (UiTheme.Accent), white text.
    //                    Used for primary actions (Shadow, Поиск, Удалить, Закрыть).
    //   Accent = false → transparent background with thin border.
    //                    Used for secondary actions (Обновить ФИО, Книга, etc).
    //   Pill = true    → pill-style toggle (filter tabs). Caller toggles
    //                    Accent to indicate selection. Larger corner radius.
    //
    // WinForms doesn't support border-radius natively — we override OnPaint
    // and draw everything ourselves: rounded background, hover/pressed tint,
    // and centered text. Region is also set to the rounded path so click
    // detection follows the same shape (no clicks on the "outside corners").
    public class NeoButton : Button
    {
        // Default corner radius — barely-rounded for the classic NeoUI look.
        // Pills override via the CornerRadius property.
        private int _cornerRadius = 1;

        private bool _accent;
        private bool _pill;
        private bool _hovered;
        private bool _pressed;

        public bool Accent
        {
            get => _accent;
            set { _accent = value; ApplyStyle(); Invalidate(); }
        }

        // Pill mode — larger corner radius, transparent base, draws a thin
        // border even when not Accent. Designed for the filter-tab row.
        public bool Pill
        {
            get => _pill;
            set { _pill = value; if (value && _cornerRadius == 1) _cornerRadius = UiTheme.RadiusMd; ApplyStyle(); Invalidate(); }
        }

        public int CornerRadius
        {
            get => _cornerRadius;
            set { _cornerRadius = value; OnResize(EventArgs.Empty); Invalidate(); }
        }

        public bool Inset { get; set; }

        public NeoButton()
        {
            FlatStyle = FlatStyle.Flat;
            Cursor = Cursors.Hand;
            Font = new Font("Segoe UI", 10F);
            // Turn off the default focus-rectangle / border drawing — our
            // OnPaint does all of it.
            FlatAppearance.BorderSize = 0;
            ApplyStyle();
        }

        private void ApplyStyle()
        {
            if (_pill)
            {
                // Pill style is driven by caller setting BackColor/ForeColor
                // directly (typically AccentSoft + Accent for active,
                // transparent + MutedForeground for inactive). We just clear
                // any inherited colours so what the caller sets is what shows.
                return;
            }
            if (_accent)
            {
                BackColor = UiTheme.Accent;
                ForeColor = UiTheme.AccentFg;
            }
            else
            {
                BackColor = UiTheme.Card;
                ForeColor = UiTheme.Foreground;
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            // Region follows the rounded path so hit-testing, hover detection
            // and accessibility all match what's painted.
            try
            {
                using var path = BuildRoundedPath(ClientRectangle, _cornerRadius);
                Region?.Dispose();
                Region = new Region(path);
            }
            catch { }
        }

        // Suppress the default Button background painting. Without this,
        // WinForms fills the button's parent-visible area with the BackColor
        // AND draws a thin border via FlatAppearance even when BorderSize=0.
        // For pills (inactive = transparent) we want NEITHER — just text on
        // the parent's surface. Returning without calling base skips both.
        protected override void OnPaintBackground(PaintEventArgs pevent)
        {
            if (_pill) return;
            base.OnPaintBackground(pevent);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hovered = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hovered = false;
            _pressed = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs mevent)
        {
            _pressed = true;
            Invalidate();
            base.OnMouseDown(mevent);
        }

        protected override void OnMouseUp(MouseEventArgs mevent)
        {
            _pressed = false;
            Invalidate();
            base.OnMouseUp(mevent);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using var path = BuildRoundedPath(rect, _cornerRadius);

            // Background fill — pills handle their own bg below (skip when
            // transparent), accent/outline buttons always fill with BackColor.
            if (!_pill || BackColor.A > 0)
            {
                Color bg;
                if (!Enabled)
                {
                    bg = _accent ? UiTheme.Accent : UiTheme.Card;
                }
                else
                {
                    bg = BackColor;
                }
                using var bgBrush = new SolidBrush(bg);
                g.FillPath(bgBrush, path);
            }

            // Border + background logic.
            // Pills: when BackColor is transparent the pill is INACTIVE —
            // skip both background and border, draw text only (matches the
            // Figma redesign where inactive tabs are just words). When the
            // caller sets a non-transparent BackColor (typically AccentSoft)
            // the pill is ACTIVE — fill with that colour + matching border.
            if (_pill)
            {
                bool isActive = BackColor.A > 0;
                if (isActive)
                {
                    using var bgBrush = new SolidBrush(BackColor);
                    g.FillPath(bgBrush, path);
                    using var borderPen = new Pen(UiTheme.AccentSoftBorder, 1);
                    g.DrawPath(borderPen, path);
                }
                // else: nothing — parent's background shows through.
            }
            else if (_accent && Enabled)
            {
                // Solid accent button — already filled above, add accent border.
                using var accentPen = new Pen(UiTheme.Accent, 1);
                g.DrawPath(accentPen, path);
            }
            else if (!_accent)
            {
                var border = Enabled ? UiTheme.Border : UiTheme.BorderSubtle;
                using var pen = new Pen(border, 1);
                g.DrawPath(pen, path);
            }

            // Hover / pressed overlay — clipped to the rounded shape so the
            // tint doesn't bleed into the corners.
            if (Enabled && _hovered && !_pressed)
            {
                using var tint = new SolidBrush(Color.FromArgb(20, 255, 255, 255));
                g.FillPath(tint, path);
            }
            else if (Enabled && _pressed)
            {
                using var tint = new SolidBrush(Color.FromArgb(40, 0, 0, 0));
                g.FillPath(tint, path);
            }

            // Foreground text. When disabled, keep accent text near-white —
            // a near-white shade reads cleanly on the indigo background and
            // matches the enabled accent buttons (user feedback: disabled
            // accent buttons looked too washed-out vs enabled ones). The
            // disabled state is still conveyed by the absence of hover tint.
            Color fg;
            if (!Enabled)
                fg = _accent ? Color.FromArgb(245, 250, 255) : UiTheme.MutedForeground;
            else
                fg = ForeColor;
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            // Focus indicator — thin inner outline when this button has focus.
            // Replaces the default WinForms dotted focus rect which looks
            // dated. Subtle so it doesn't fight with the accent colour.
            if (Focused && ShowFocusCues)
            {
                var focusRect = new Rectangle(2, 2, Width - 5, Height - 5);
                var focusPath = BuildRoundedPath(focusRect, Math.Max(1, _cornerRadius - 2));
                using var focusPen = new Pen(Color.FromArgb(255, 255, 255), 1.5f);
                g.DrawPath(focusPen, focusPath);
            }
        }

        // Build a rounded-rectangle GraphicsPath. Corner radius is the same
        // for all four corners.
        private static GraphicsPath BuildRoundedPath(Rectangle rect, int radius)
        {
            var path = new GraphicsPath();
            if (radius <= 0)
            {
                path.AddRectangle(rect);
                return path;
            }
            // Clamp radius to half the smaller dimension so the corners don't
            // overlap when the button is very small.
            var d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
            var r = d / 2;
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}

