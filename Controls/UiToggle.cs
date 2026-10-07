using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using ShadowRDP.Helpers;

namespace ShadowRDP.Controls
{
    // v4.3.3 — modern on/off switch built on a plain Panel (no native CheckBox).
    // Looks the same as before (rounded track + knob, accent when checked),
    // but there is no ButtonBase machinery involved at all: no native window
    // painting, no themed background, no transparency tricks, no focus cues —
    // so whole classes of paint artifacts are impossible by construction.
    //
    // Minimal CheckBox-like API used by callers: Text / Checked / Enabled /
    // AutoCheck / CheckedChanged, plus click/space toggling.
    public class UiToggle : Panel
    {
        private bool _hovered;
        private bool _checked;
        // Зелёный ВКЛ — как кнопка «Выгрузить сейчас», а не серый Accent темы.
        private static readonly Color OnGreen = Color.FromArgb(0xF0, 0xA0, 0x50);

        public UiToggle()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Cursor = Cursors.Hand;
            AutoSize = false;
            Height = 22;
            TabStop = true;
            AutoCheck = true;
            Font = new Font("Segoe UI", 9F);
            ForeColor = UiTheme.Foreground;
            BackColor = UiTheme.Card;
        }

        public bool AutoCheck { get; set; }

        public bool Checked
        {
            get => _checked;
            set
            {
                if (_checked != value)
                {
                    _checked = value;
                    Invalidate();
                    CheckedChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public event EventHandler? CheckedChanged;

        protected override void OnClick(EventArgs e)
        {
            if (AutoCheck && Enabled) Checked = !Checked;
            base.OnClick(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if ((e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) && AutoCheck && Enabled)
            {
                Checked = !Checked;
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }
            base.OnKeyDown(e);
        }

        protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hovered = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            var g = pevent.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using (var bgBrush = new SolidBrush(BackColor)) g.FillRectangle(bgBrush, ClientRectangle);

            float d = pevent.Graphics.DpiX / 96f;
            int trackW = (int)Math.Round(40f * d), trackH = (int)Math.Round(22f * d), knob = (int)Math.Round(18f * d);
            int y = (Height - trackH) / 2;
            var track = new Rectangle(0, y, trackW, trackH);
            bool on = Checked && Enabled;

            // Track (ховер слегка осветляет трек; отдельной подсветки нет —
            // старый glow-кружок вылезал за правый край трека).
            Color trackColor = !Enabled ? UiTheme.BorderSubtle
                : on ? (_hovered ? ControlPaint.Light(OnGreen, 0.12f) : OnGreen)
                : _hovered ? UiTheme.Border
                : UiTheme.BorderSubtle;
                using (var path = RoundedRect(track, trackH / 2))
                using (var brush = new SolidBrush(trackColor))
                    g.FillPath(brush, path);

            // Knob
            int kx = on ? trackW - knob - (int)Math.Round(2f * d) : (int)Math.Round(2f * d);
            int ky = y + (trackH - knob) / 2;
            var knobRect = new Rectangle(kx, ky, knob, knob);
            using (var knobPath = RoundedRect(knobRect, knob / 2))
            using (var knobBrush = new SolidBrush(on ? Color.White : Color.FromArgb(0xF5, 0xF5, 0xF5)))
            using (var pen = new Pen(Color.FromArgb(0x00, 0x00, 0x00, on ? 40 : 60), 1))
            {
                g.FillPath(knobBrush, knobPath);
                g.DrawPath(pen, knobPath);
            }

            // Text
            int textX = trackW + (int)Math.Round(8f * d);
            var textRect = new Rectangle(textX, 0, Width - textX, Height);
            TextRenderer.DrawText(g, Text, Font, textRect, Enabled ? ForeColor : UiTheme.MutedForeground,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }

        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            if (radius <= 0) { path.AddRectangle(bounds); return path; }
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
