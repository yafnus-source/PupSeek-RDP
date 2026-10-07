using System;
using System.Drawing;
using System.Windows.Forms;

namespace ShadowRDP.Helpers
{
    public static class UiPills
    {
        // v4.9.4 — активность пилы хранится явно (Tag), а не выводится из
        // сравнения ForeColor с PillActiveFg: при кастомных цветах пил
        // (colors.json) PillActiveFg может совпадать с Foreground темы,
        // и тогда все неактивные пилы «выглядят активными» (фон PillActiveBg).
        private sealed class PillState
        {
            public int Width;
            public bool Active;
            public bool Hover;
        }

        public static Label Create(string text, bool active)
        {
            int w = MeasureWidth(text);
            var state = new PillState { Width = w, Active = active };
            var lbl = new UiButtons.CustomPaintLabel
            {
                Text = text,
                Size = new Size(w, 26),
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand,
                Margin = new Padding(14, 0, 14, 0),
                Tag = state
            };
            Apply(lbl, active);
            // v4.8.1 — фон гладкой отрисовкой по состоянию (была Region-обрезка).
            // v4.9.5 — ховер-заливку рисуем по явному флагу Hover, а не по
            // ForeColor: цвет шрифта пила на ховере меняться не должен.
            // v4.9.32 — радиус как у рамки (10): раньше заливка была r=1
            // (почти квадрат) и её углы торчали из-под скругления — «след».
            UiButtons.EnableSmoothFill(lbl, () =>
            {
                bool isActive = TryActive(lbl);
                if (isActive) return UiTheme.PillActiveBg;
                if (TryHover(lbl)) return UiTheme.AccentSoft;
                return Color.Transparent;
            }, 10);
            lbl.Paint += (s, e) =>
            {
                if (TryActive(lbl))
                {
                    e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    using var path = UiButtons.RoundedRect(new Rectangle(0, 0, lbl.Width - 1, lbl.Height - 1), 10);
                    using var pen = new Pen(UiTheme.PillActiveBorder, 1) { Alignment = System.Drawing.Drawing2D.PenAlignment.Inset };
                    e.Graphics.DrawPath(pen, path);
                }
            };
            // hover only for inactive (заливку рисует гладкий фил по флагу Hover;
            // цвет шрифта на ховере НЕ меняем — v4.9.5)
            lbl.MouseEnter += (s, e) =>
            {
                if (TryActive(lbl)) return;
                if (lbl.Tag is PillState st) st.Hover = true;
                lbl.Invalidate();
            };
            lbl.MouseLeave += (s, e) =>
            {
                if (TryActive(lbl)) return;
                if (lbl.Tag is PillState st) st.Hover = false;
                lbl.Invalidate();
            };
            return lbl;
        }

        private static bool TryActive(Label lbl)
        {
            try { return lbl.Tag is PillState st && st.Active; }
            catch { return false; }
        }

        private static bool TryHover(Label lbl)
        {
            try { return lbl.Tag is PillState st && st.Hover; }
            catch { return false; }
        }

        public static void Apply(Label lbl, bool active)
        {
            // v4.8.1 — BackColor всегда Transparent (фон рисует гладкая заливка
            // из Create по ForeColor); иначе квадратные углы торчат из-под пила.
            lbl.BackColor = Color.Transparent;
            if (lbl.Tag is PillState st) st.Active = active;
            if (active)
            {
                lbl.ForeColor = UiTheme.PillActiveFg;
                lbl.Font = new Font("Segoe UI Semibold", 9F);
            }
            else
            {
                lbl.ForeColor = UiTheme.Foreground;
                lbl.Font = new Font("Segoe UI", 9F);
            }
            lbl.Invalidate();
        }

        private static int MeasureWidth(string text)
        {
            using var g = Graphics.FromHwnd(IntPtr.Zero);
            var sz = TextRenderer.MeasureText(g, text, new Font("Segoe UI", 9F), new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
            return sz.Width + 28;
        }
    }
}