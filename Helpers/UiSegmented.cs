using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ShadowRDP.Helpers
{
    // v4.8 — сегмент-контрол («герой-пил»): единый скруглённый контейнер
    // с N кликабельными сегментами. Активный сегмент — тёмная заливка +
    // белый текст, остальные — прозрачные. Использование: пил-переключатель
    // вьюх единого дашборда (Events / Monitor / Web) справа в hero-шапке.
    //
    // v4.8.1 — скругление ЧЕСТНОЙ отрисовкой (Paint + AntiAlias), а не
    // Control.Region: регионы не сглаживаются и дают «покусанную» лесенку
    // по краям. Фон контролов — Transparent, заливка скруглённого пути —
    // в Paint (как UiButtons.MakeFilled).
    public static class UiSegmented
    {
        public sealed class SegmentedControl
        {
            public Panel Host { get; }
            private readonly Label[] _segs;
            private readonly Color _activeBg;
            // Явные флаги скрытия: Control.Visible parent-aware (до Show()
            // все false), для раскладки на него полагаться нельзя.
            private readonly bool[] _hidden;
            private int _active;

            public int Active => _active;

            internal SegmentedControl(Panel host, Label[] segs, int active, Color activeBg)
            {
                Host = host;
                _segs = segs;
                _active = active;
                _activeBg = activeBg;
                _hidden = new bool[segs.Length];
            }

            public void SetActive(int index)
            {
                if (index < 0 || index >= _segs.Length) return;
                _active = index;
                for (int i = 0; i < _segs.Length; i++)
                    Apply(_segs[i], i == index);
            }

            public void SetSegmentVisible(int index, bool visible)
            {
                if (index < 0 || index >= _segs.Length) return;
                _hidden[index] = !visible;
                _segs[index].Visible = visible;
                // Перекомпоновка видимых сегментов + ширина хоста.
                int x = 3;
                for (int i = 0; i < _segs.Length; i++)
                {
                    if (_hidden[i]) continue;
                    _segs[i].Location = new Point(x, 3);
                    x += _segs[i].Width + 2;
                }
                Host.Width = x + 1;
                try { Host.Invalidate(); } catch { }
            }
        }

        public static SegmentedControl Create(string[] items, int active, Action<int> onSelect, int height = 28, Color? activeBg = null, float fontSize = 9f, int pad = 30, string[]? icons = null)
        {
            Color abg = activeBg ?? Color.FromArgb(0x3A, 0x3A, 0x38);
            var font = new Font("Segoe UI", fontSize);
            var host = new Panel
            {
                Height = height,
                BackColor = Color.Transparent,
                Margin = Padding.Empty,
                Cursor = Cursors.Hand
            };
            host.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using var path = UiButtons.RoundedRect(new Rectangle(0, 0, host.Width - 1, host.Height - 1), 10);
                using var br = new SolidBrush(UiTheme.Card);
                e.Graphics.FillPath(br, path);
            };
            host.Resize += (s, e) => { try { host.Invalidate(); } catch { } };
            var segs = new Label[items.Length];
            int x = 3;
            for (int i = 0; i < items.Length; i++)
            {
                // v3.9.3A — опциональная иконка слева от текста (мокап hero).
                Image? glyph = null;
                Image? glyphWhite = null;
                try
                {
                    if (icons != null && i < icons.Length && !string.IsNullOrEmpty(icons[i]))
                    {
                        var g = MenuIcons.Get(icons[i]);
                        if (g != null) { glyph = g; glyphWhite = UiButtons.TintWhite(g); }
                    }
                }
                catch { }
                int w = MeasureWidth(items[i], font, pad) + (glyph != null ? 22 : 0);
                var lbl = new UiButtons.CustomPaintLabel
                {
                    Text = items[i],
                    Size = new Size(w, height - 6),
                    Location = new Point(x, 3),
                    AutoSize = false,
                    Font = font,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Cursor = Cursors.Hand,
                    Margin = Padding.Empty,
                    BackColor = Color.Transparent,
                    Tag = 0
                };
                int idx = i;
                Color segActiveBg = abg;
                // v4.8.2 — кнопки строже (r=6), контейнер 10px.
                lbl.Paint += (s, e) =>
                {
                    bool isActive = (int)(lbl.Tag ?? 0) == 1;
                    bool isHover = (int)(lbl.Tag ?? 0) == 2;
e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    // v4.8.2 — grayscale для текста (ClearType на прозрачном
                    // даёт радугу на тёмном тексте).
                    e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    // Фон уже залит (Transparent → фон родителя), рисуем поверх.
                    if (isActive || isHover)
                    {
                        Color fill = isActive ? segActiveBg : UiTheme.AccentSoft;
                        using var path = UiButtons.RoundedRect(new Rectangle(0, 0, lbl.Width - 1, lbl.Height - 1), 6);
                        using var brush = new SolidBrush(fill);
                        e.Graphics.FillPath(brush, path);
                    }
                    // v4.9.5 — цвет шрифта на ховере НЕ меняем (был Accent),
                    // только заливка сегмента (AccentSoft) остаётся подсказкой.
                    if (glyph != null)
                    {
                        var gi = isActive && glyphWhite != null ? glyphWhite : glyph;
                        e.Graphics.DrawImage(gi, new Rectangle(8, (lbl.Height - 16) / 2, 16, 16));
                    }
                    Color fg = isActive ? Color.White : UiTheme.Foreground;
                    var textRect = glyph != null
                        ? new Rectangle(24, 0, Math.Max(0, lbl.Width - 24), lbl.Height)
                        : new Rectangle(0, 0, lbl.Width, lbl.Height);
                    TextRenderer.DrawText(e.Graphics, lbl.Text, lbl.Font,
                        textRect, fg,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                };
                lbl.Click += (s, e) => { try { onSelect(idx); } catch { } };
                lbl.MouseEnter += (s, e) =>
                {
                    if ((int)(lbl.Tag ?? 0) == 1) return;
                    lbl.Tag = 2;
                    try { lbl.Invalidate(); } catch { }
                };
                lbl.MouseLeave += (s, e) =>
                {
                    if ((int)(lbl.Tag ?? 0) == 1) return;
                    lbl.Tag = 0;
                    try { lbl.Invalidate(); } catch { }
                };
                host.Controls.Add(lbl);
                segs[i] = lbl;
                x += w + 2;
            }
            host.Width = x + 1;
            var ctl = new SegmentedControl(host, segs, -1, abg);
            ctl.SetActive(active);
            return ctl;
        }

        private static void Apply(Label lbl, bool active)
        {
            lbl.Tag = active ? 1 : 0;
            try { lbl.Invalidate(); } catch { }
        }

        private static int MeasureWidth(string text, Font font, int pad)
        {
            using var g = Graphics.FromHwnd(IntPtr.Zero);
            var sz = TextRenderer.MeasureText(g, text, font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
            return sz.Width + pad;
        }
    }
}
