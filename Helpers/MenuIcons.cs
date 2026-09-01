using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ShadowRDP.Helpers
{
    // Programmatically-drawn 16x16 menu icons. Mirror of New-InfoIcon /
    // New-GearIcon / etc. from the .ps1 version.
    //
    // Same approach: draw simple shapes (circle, triangle, rect, X) on a
    // transparent bitmap with a small palette:
    //   accent = #0078D7 (NeoAccent blue)
    //   dark   = #6E747A (mid grey)
    //   danger = #D32F2F (red, for delete / stop)
    //
    // All bitmaps are cached after first use — a context menu shows many of
    // these simultaneously and there's no point redrawing.
    public static class MenuIcons
    {
        private static readonly Dictionary<string, Bitmap> _cache = new();
        // v2.1.3 — icons recoloured to match the warm Figma palette.
        //   accent → warm brown (was saturated blue)
        //   dark   → muted stone grey (unchanged conceptually, slightly warmer)
        //   danger → red (unchanged)
        private static readonly Color _accent = UiTheme.MenuIconBrown;
        private static readonly Color _dark = UiTheme.MenuIconNeutral;
        private static readonly Color _danger = Color.FromArgb(211, 47, 47);

        public static Image Get(string key)
        {
            // v2.1.46: tint most icons to the theme accent (UiTheme.MenuIcon)
            // so the context menu reads as a unified accent set.
            // play_green stays green for "ПОДКЛЮЧИТЬСЯ (RDP)".
            // v3.0.8: on_green / off_red stay as-drawn for "Автозагрузка"
            // radio icons in the session tab context menu.
            // v3.8.2: in the Olive theme UiTheme.MenuIcon = #A2AD9E, so every
            // monochrome menu icon becomes sage; green/red status icons keep
            // their semantic colour.
            var noTintKeys = new HashSet<string> { "play_green", "on_green", "off_red" };
            if (noTintKeys.Contains(key))
            {
                if (_cache.TryGetValue(key, out var cached)) return cached;
                var raw = Draw(key);
                _cache[key] = raw;
                return raw;
            }

            // For non-green: draw raw, then tint pixels to the theme icon colour.
            var target = ShadowRDP.Helpers.UiTheme.MenuIcon;
            var tintedKey = key + "_tint" + target.ToArgb();
            if (_cache.TryGetValue(tintedKey, out var tinted)) return tinted;
            var src = Draw(key);
            var tintedBmp = TintColor(src, target);
            _cache[tintedKey] = tintedBmp;
            return tintedBmp;
        }

        // Recolor every non-transparent pixel of an Image to `target`.
        // Preserves alpha. Used to unify menu icons under a single accent
        // colour regardless of their original palette slot.
        private static Bitmap TintColor(Bitmap src, Color target)
        {
            var bmp = new Bitmap(src.Width, src.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.Transparent);
            using var attrs = new System.Drawing.Imaging.ImageAttributes();
            var matrix = new System.Drawing.Imaging.ColorMatrix(new[]
            {
                new[] { 0f, 0f, 0f, 0f, 0f },
                new[] { 0f, 0f, 0f, 0f, 0f },
                new[] { 0f, 0f, 0f, 0f, 0f },
                new[] { 0f, 0f, 0f, 1f, 0f },
                new[] {
                    target.R / 255f,
                    target.G / 255f,
                    target.B / 255f,
                    0f, 1f
                }
            });
            attrs.SetColorMatrix(matrix);
            var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            g.DrawImage(src, rect, 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
            return bmp;
        }

        private static Bitmap Draw(string key) => key switch
        {
            "info"     => DrawInfo(),
            "play"     => DrawTriangle(_accent),
            "play_green" => DrawTriangle(Color.FromArgb(34, 139, 34)),
            // v3.0.8 — Автозагрузка radio icons. Filled circle + white glyph.
            "on_green"  => DrawStatusDot(Color.FromArgb(46, 160, 67), true),
            "off_red"   => DrawStatusDot(Color.FromArgb(208, 64, 64), false),
            "stop"     => DrawSquare(_danger),
            "msg"      => DrawMessage(),
            "copy"     => DrawCopy(),
            "computer" => DrawComputer(),
            "globe"    => DrawGlobe(),
            "gear"     => DrawGear(),
            "list"     => DrawList(),
            "tasks"    => DrawTasks(),
            "restart"  => DrawRestart(),
            "ping"     => DrawWave(_accent),
            "delete"   => DrawX(_danger),
            "add"      => DrawPlus(_accent),
            "shield"   => DrawShield(),
            "person"   => DrawPerson(),
            "search"   => DrawSearch(),
            "terminal" => DrawTerminal(),
            "powershell" => DrawPowerShell(),
            "exchange" => DrawExchange(),
            "sun"      => DrawSun(),
            "key"      => DrawKey(),
            "book"     => DrawBook(),
            "dashboard" => DrawDashboard(),
            _          => DrawTriangle(_dark)
        };

        // === Helpers ===

        // v3.8.2 — darker variant of an icon for when its menu item is
        // hovered/selected, so the glyph reads stronger against the accent
        // highlight. Cached per source bitmap.
        private static readonly Dictionary<Image, Image> _darkCache = new();
        public static Image Darken(Image src)
        {
            if (src == null) return null;
            if (_darkCache.TryGetValue(src, out var d)) return d;
            var bmp = new Bitmap(src.Width, src.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.Transparent);
            var m = new System.Drawing.Imaging.ColorMatrix(new[]
            {
                new[] { 0.6f, 0f, 0f, 0f, 0f },
                new[] { 0f, 0.6f, 0f, 0f, 0f },
                new[] { 0f, 0f, 0.6f, 0f, 0f },
                new[] { 0f, 0f, 0f, 1f, 0f },
                new[] { 0f, 0f, 0f, 0f, 1f }
            });
            using var attrs = new System.Drawing.Imaging.ImageAttributes();
            attrs.SetColorMatrix(m);
            g.DrawImage(src, new Rectangle(0, 0, bmp.Width, bmp.Height), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
            _darkCache[src] = bmp;
            return bmp;
        }

        private static Bitmap NewBitmap()
        {
            var bmp = new Bitmap(16, 16, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            return bmp;
        }

        private static Bitmap DrawInfo()
        {
            var bmp = NewBitmap();
            using var g = Graphics.FromImage(bmp);
            using var pen = new Pen(_accent, 1.4f);
            using var brush = new SolidBrush(_accent);
            g.DrawEllipse(pen, 2, 2, 12, 12);
            g.FillRectangle(brush, 7, 7, 2, 5);
            g.FillEllipse(brush, 7, 4, 2, 2);
            return bmp;
        }

        private static Bitmap DrawTriangle(Color color)
        {
            var bmp = NewBitmap();
            using var g = Graphics.FromImage(bmp);
            using var brush = new SolidBrush(color);
            var points = new[] { new Point(4, 3), new Point(13, 8), new Point(4, 13) };
            g.FillPolygon(brush, points);
            return bmp;
        }

        private static Bitmap DrawSquare(Color color)
        {
            var bmp = NewBitmap();
            using var g = Graphics.FromImage(bmp);
            using var brush = new SolidBrush(color);
            g.FillRectangle(brush, 4, 4, 8, 8);
            return bmp;
        }

        private static Bitmap DrawMessage()
        {
            var bmp = NewBitmap();
            using var g = Graphics.FromImage(bmp);
            using var pen = new Pen(_dark, 1.4f);
            g.DrawRectangle(pen, 2, 3, 12, 7);
            // Tail (triangle pointing down-left)
            using var brush = new SolidBrush(_dark);
            var tail = new[] { new Point(5, 10), new Point(5, 13), new Point(8, 10) };
            g.FillPolygon(brush, tail);
            return bmp;
        }

        private static Bitmap DrawCopy()
        {
            var bmp = NewBitmap();
            using var g = Graphics.FromImage(bmp);
            using var pen = new Pen(_dark, 1.2f);
            g.DrawRectangle(pen, 2, 4, 8, 10);   // back rect
            g.DrawRectangle(pen, 5, 2, 9, 10);  // front rect (overlap)
            return bmp;
        }

        private static Bitmap DrawComputer()
        {
            var bmp = NewBitmap();
            using var g = Graphics.FromImage(bmp);
            using var pen = new Pen(_dark, 1.4f);
            g.DrawRectangle(pen, 2, 3, 12, 8);   // monitor
            g.FillRectangle(new SolidBrush(_dark), 7, 12, 2, 2); // stand
            g.DrawLine(pen, 5, 14, 11, 14);     // base
            return bmp;
        }

        // v3.9 — flat icons matching the design preview.
        private static Bitmap DrawTerminal()
        {
            var bmp = NewBitmap();
            using var g = Graphics.FromImage(bmp);
            using var pen = new Pen(_dark, 1.4f);
            using var brush = new SolidBrush(_dark);
            g.DrawRectangle(pen, 2, 3, 12, 10);
            g.FillPolygon(brush, new[] { new Point(4, 6), new Point(7, 9), new Point(4, 12) });
            g.DrawLine(pen, 7, 12, 11, 12);
            return bmp;
        }

        private static Bitmap DrawPowerShell()
        {
            var bmp = NewBitmap();
            using var g = Graphics.FromImage(bmp);
            using var pen = new Pen(_dark, 1.4f);
            using var brush = new SolidBrush(_dark);
            g.DrawRectangle(pen, 2, 3, 12, 10);
            g.FillRectangle(brush, 2, 3, 12, 2);
            g.FillPolygon(brush, new[] { new Point(4, 8), new Point(7, 11), new Point(4, 14) });
            g.DrawLine(pen, 7, 14, 11, 14);
            return bmp;
        }

        private static Bitmap DrawExchange()
        {
            var bmp = NewBitmap();
            using var g = Graphics.FromImage(bmp);
            using var pen = new Pen(_dark, 1.4f);
            g.DrawLine(pen, 8, 3, 8, 13);
            g.DrawLine(pen, 5, 6, 8, 3);
            g.DrawLine(pen, 8, 3, 11, 6);
            g.DrawLine(pen, 5, 10, 8, 13);
            g.DrawLine(pen, 8, 13, 11, 10);
            return bmp;
        }

        private static Bitmap DrawSun()
        {
            var bmp = NewBitmap();
            using var g = Graphics.FromImage(bmp);
            using var pen = new Pen(_dark, 1.4f);
            g.DrawEllipse(pen, 5, 5, 6, 6);
            for (int a = 0; a < 8; a++)
            {
                double ang = a * Math.PI / 4;
                int x1 = 8 + (int)(Math.Cos(ang) * 5), y1 = 8 + (int)(Math.Sin(ang) * 5);
                int x2 = 8 + (int)(Math.Cos(ang) * 7), y2 = 8 + (int)(Math.Sin(ang) * 7);
                g.DrawLine(pen, x1, y1, x2, y2);
            }
            return bmp;
        }

        private static Bitmap DrawKey()
        {
            var bmp = NewBitmap();
            using var g = Graphics.FromImage(bmp);
            using var pen = new Pen(_dark, 1.4f);
            g.DrawEllipse(pen, 3, 4, 6, 6);
            g.DrawLine(pen, 8, 9, 13, 14);
            g.DrawLine(pen, 11, 12, 13, 10);
            return bmp;
        }

        private static Bitmap DrawBook()
        {
            var bmp = NewBitmap();
            using var g = Graphics.FromImage(bmp);
            using var pen = new Pen(_dark, 1.4f);
            g.DrawRectangle(pen, 3, 3, 10, 10);
            g.DrawLine(pen, 8, 3, 8, 13);
            return bmp;
        }

        private static Bitmap DrawDashboard()
        {
            var bmp = NewBitmap();
            using var g = Graphics.FromImage(bmp);
            using var pen = new Pen(_dark, 1.2f);
            using var brush = new SolidBrush(_dark);
            // 3 bars of increasing height
            g.FillRectangle(brush, 2, 9, 3, 5);
            g.FillRectangle(brush, 6, 6, 3, 8);
            g.FillRectangle(brush, 10, 3, 3, 11);
            // small border
            g.DrawRectangle(pen, 2, 2, 12, 12);
            return bmp;
        }

        private static Bitmap DrawGlobe()
        {
            var bmp = NewBitmap();
            using var g = Graphics.FromImage(bmp);
            using var pen = new Pen(_accent, 1.2f);
            g.DrawEllipse(pen, 2, 2, 12, 12);
            g.DrawEllipse(pen, 5, 2, 6, 12);     // vertical line
            g.DrawLine(pen, 2, 8, 14, 8);        // horizontal line
            return bmp;
        }

        private static Bitmap DrawGear()
        {
            var bmp = NewBitmap();
            using var g = Graphics.FromImage(bmp);
            using var pen = new Pen(_dark, 1.2f);
            g.DrawEllipse(pen, 5, 5, 6, 6);      // body
            // 6 teeth around the body
            var teeth = new[] { (7, 2), (7, 13), (2, 7), (13, 7), (4, 4), (11, 4), (4, 11), (11, 11) };
            using var brush = new SolidBrush(_dark);
            foreach (var (x, y) in teeth) g.FillRectangle(brush, x, y, 2, 2);
            return bmp;
        }

        private static Bitmap DrawList()
        {
            var bmp = NewBitmap();
            using var g = Graphics.FromImage(bmp);
            using var pen = new Pen(_dark, 1.4f);
            for (int i = 0; i < 3; i++)
            {
                int y = 4 + i * 4;
                g.FillEllipse(new SolidBrush(_dark), 2, y, 2, 2);  // bullet
                g.DrawLine(pen, 6, y + 1, 14, y + 1);               // line
            }
            return bmp;
        }

        private static Bitmap DrawTasks()
        {
            var bmp = NewBitmap();
            using var g = Graphics.FromImage(bmp);
            using var pen = new Pen(_dark, 1.2f);
            // Clipboard outline
            g.DrawRectangle(pen, 3, 2, 10, 12);
            g.FillRectangle(new SolidBrush(_dark), 6, 1, 4, 2); // clip
            // 3 lines
            for (int i = 0; i < 3; i++)
            {
                int y = 5 + i * 3;
                g.DrawLine(pen, 5, y, 11, y);
            }
            return bmp;
        }

        private static Bitmap DrawRestart()
        {
            var bmp = NewBitmap();
            using var g = Graphics.FromImage(bmp);
            using var pen = new Pen(_accent, 1.4f);
            var arcRect = new Rectangle(2, 2, 12, 12);
            // 3/4 arc
            g.DrawArc(pen, arcRect, -45, 270);
            // Arrow head
            using var brush = new SolidBrush(_accent);
            g.FillPolygon(brush, new[] { new Point(13, 2), new Point(15, 6), new Point(11, 6) });
            return bmp;
        }

        private static Bitmap DrawWave(Color color)
        {
            var bmp = NewBitmap();
            using var g = Graphics.FromImage(bmp);
            using var brush = new SolidBrush(color);
            // 3 ascending bars
            g.FillRectangle(brush, 2, 10, 3, 4);
            g.FillRectangle(brush, 6, 7, 3, 7);
            g.FillRectangle(brush, 10, 4, 3, 10);
            return bmp;
        }

        private static Bitmap DrawX(Color color)
        {
            var bmp = NewBitmap();
            using var g = Graphics.FromImage(bmp);
            using var pen = new Pen(color, 2f);
            g.DrawLine(pen, 3, 3, 13, 13);
            g.DrawLine(pen, 13, 3, 3, 13);
            return bmp;
        }

        private static Bitmap DrawPlus(Color color)
        {
            var bmp = NewBitmap();
            using var g = Graphics.FromImage(bmp);
            using var brush = new SolidBrush(color);
            g.FillRectangle(brush, 7, 2, 2, 12);
            g.FillRectangle(brush, 2, 7, 12, 2);
            return bmp;
        }

        private static Bitmap DrawShield()
        {
            var bmp = NewBitmap();
            using var g = Graphics.FromImage(bmp);
            using var brush = new SolidBrush(_accent);
            // Shield outline: pentagon-like
            var points = new[] {
                new Point(8, 1), new Point(14, 4),
                new Point(14, 9), new Point(8, 15),
                new Point(2, 9), new Point(2, 4)
            };
            g.FillPolygon(brush, points);
            // Inner check mark
            using var whitePen = new Pen(Color.White, 1.5f);
            g.DrawLine(whitePen, 5, 8, 7, 10);
            g.DrawLine(whitePen, 7, 10, 11, 6);
            return bmp;
        }

        private static Bitmap DrawPerson()
        {
            var bmp = NewBitmap();
            using var g = Graphics.FromImage(bmp);
            using var brush = new SolidBrush(_dark);
            g.FillEllipse(brush, 5, 2, 6, 6);          // head
            g.FillEllipse(brush, 2, 9, 12, 10);        // shoulders (lower half visible)
            return bmp;
        }

        private static Bitmap DrawSearch()
        {
            var bmp = NewBitmap();
            using var g = Graphics.FromImage(bmp);
            using var pen = new Pen(_dark, 1.5f);
            g.DrawEllipse(pen, 2, 2, 8, 8);            // lens
            g.DrawLine(pen, 9, 9, 14, 14);             // handle
            return bmp;
        }

        // v3.0.8 — filled circle with a white check (when withCheck=true) or
        // white X. Used by the "Автозагрузка" submenu in RdpManagerForm.
        private static Bitmap DrawStatusDot(Color fill, bool withCheck)
        {
            var bmp = NewBitmap();
            using var g = Graphics.FromImage(bmp);
            using var brush = new SolidBrush(fill);
            g.FillEllipse(brush, 2, 2, 12, 12);
            using var whitePen = new Pen(Color.White, 1.8f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            if (withCheck)
            {
                g.DrawLine(whitePen, 5, 8, 7, 10);
                g.DrawLine(whitePen, 7, 10, 11, 5);
            }
            else
            {
                g.DrawLine(whitePen, 5, 5, 11, 11);
                g.DrawLine(whitePen, 11, 5, 5, 11);
            }
            return bmp;
        }
    }
}
