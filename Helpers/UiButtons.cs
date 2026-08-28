using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ShadowRDP.Helpers
{
    // Flat-label action button matching the Figma redesign's group-button /
    // bottom-button look. Used by all dialog forms (Computer Info, Event Log,
    // Local Admins, Task Manager, etc.) so they share one consistent style:
    //
    //   - Bold Segoe UI text
    //   - Transparent background, MutedForeground text by default
    //   - On hover: AccentSoft background + Accent (indigo) text
    //   - 2px rounded Region so the hover background corners are soft
    //
    // The hovered-vs-default look matches the active-vs-inactive filter pill
    // in AddressBookForm — same visual language across the whole app.
    public static class UiButtons
    {
        // Variants mirror AddressBookForm's ActionRole enum. We keep them
        // here too so dialog forms don't need to import AddressBookForm.
        public enum Role { Default, Danger }

        public static Label MakeFlat(string text, Role role = Role.Default)
        {            var lbl = new Label
            {
                Text = text,
                AutoSize = true,
                Font = new Font("Segoe UI Semibold", 10F),
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand,
                Padding = new Padding(12, 8, 12, 8),
                BackColor = Color.Transparent,
                ForeColor = role == Role.Danger ? UiTheme.Danger : UiTheme.MutedForeground
            };
            // v2.3.6: 2 → 1 (user: "скругление 1px во всех кнопках кроме сайдбара").
            ApplyRoundedRegion(lbl, 1);
            lbl.MouseEnter += (s, e) =>
            {
                if (!lbl.Enabled) return;
                lbl.BackColor = UiTheme.HoverBg;
                lbl.ForeColor = UiTheme.HoverText;
            };
            lbl.MouseLeave += (s, e) =>
            {
                if (!lbl.Enabled) return;
                lbl.BackColor = Color.Transparent;
                lbl.ForeColor = role == Role.Danger ? UiTheme.Danger : UiTheme.MutedForeground;
            };
            return lbl;
        }

        // Compact 22px filled button — byte-for-byte the same look as the
        // "+ Сессия" / "+ В книгу" buttons in RdpManagerForm's header:
        //   - Solid #296AA0 teal fill, 1px rounding, white Segoe UI Semibold
        //     9pt text (not the 10pt used by MakeFlat/MakeFilled).
        //   - Hover darkens the fill slightly.
        //   - Disabled state: light-gray fill + gray text (no hover) — used
        //     by dialog toolbar buttons that start disabled ("Прервать").
        // v3.7.1: optional bgOverride for non-default backgrounds (e.g. gray
        // "Отмена" in the KeePass unlock dialog).
        public static Label MakeCompactFilled(string text, int width, bool danger = false, Color? bgOverride = null)
        {
            Color bg = bgOverride ?? (danger ? UiTheme.Danger : ShadowRDP.Helpers.UiTheme.HeaderBar);
            Color hover = ControlPaint.Dark(bg, 0.08f);
            var fill = new SolidBrush(bg);

            var lbl = new Label
            {
                Text = "",
                AutoSize = false,
                Size = new Size(width, 22),
                Font = new Font("Segoe UI Semibold", 9F),
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                BackColor = Color.Transparent
            };

            lbl.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                var r = new Rectangle(0, 0, lbl.Width, lbl.Height);
                using var path = RoundedRect(r, 1);
                if (lbl.Enabled)
                {
                    e.Graphics.FillPath(fill, path);
                }
                else
                {
                    using var dis = new SolidBrush(Color.FromArgb(0xE2, 0xE4, 0xE8));
                    e.Graphics.FillPath(dis, path);
                }
                var fg = lbl.Enabled ? Color.White : Color.FromArgb(0x8A, 0x8E, 0x94);
                TextRenderer.DrawText(e.Graphics, text, lbl.Font, r, fg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            };
            lbl.MouseEnter += (s, e) => { if (lbl.Enabled) { fill.Color = hover; lbl.Invalidate(); } };
            lbl.MouseLeave += (s, e) => { if (lbl.Enabled) { fill.Color = bg; lbl.Invalidate(); } };
            lbl.EnabledChanged += (s, e) => lbl.Invalidate();
            return lbl;
        }

        // v3.7.1 — compact (22px) flat link-button: transparent background,
        // text colour shifts on hover (no fill). Mirrors the "🔑 Сменить
        // пароль" header button in RdpManagerForm. Use for secondary actions
        // that should NOT compete visually with a primary filled button
        // (e.g. "Обзор…" next to a text field in the KeePass unlock dialog).
        public static Label MakeCompactFlat(string text, int width, Color? fg = null, Color? fgHover = null)
        {
            var baseFg = fg ?? ShadowRDP.Helpers.UiTheme.Sidebar;
            var hoverFg = fgHover ?? Color.FromArgb(0x2A, 0x5A, 0x8E);
            var lbl = new Label
            {
                Text = text,
                AutoSize = false,
                Size = new Size(width, 22),
                Font = new Font("Segoe UI Semibold", 9F),
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                BackColor = Color.Transparent,
                ForeColor = baseFg
            };
            ApplyRoundedRegion(lbl, 1);
            lbl.MouseEnter += (s, e) => { if (lbl.Enabled) lbl.ForeColor = hoverFg; };
            lbl.MouseLeave += (s, e) => { if (lbl.Enabled) lbl.ForeColor = baseFg; };
            return lbl;
        }

        // v3.8.x — compact outlined button: white background, 1px border,
        // pale-gray semibold text (per user request for the "Обзор…" buttons
        // in the KeePass unlock dialog). Border #BFBFBF, text #686868 (1.5x
        // darker than the previous #9C9C9C). Height is caller-supplied so it
        // can match the neighbouring text field. Hover tints the bg gray.
        public static Label MakeCompactOutlined(string text, int width, int height = 22)
        {
            var border = Color.FromArgb(0xBF, 0xBF, 0xBF);
            var fg = Color.FromArgb(0x68, 0x68, 0x68);
            var hoverBg = Color.FromArgb(0xF0, 0xF0, 0xF0);
            var lbl = new Label
            {
                Text = "",
                AutoSize = false,
                Size = new Size(width, height),
                Font = new Font("Segoe UI Semibold", 9F),
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                BackColor = Color.White
            };
            lbl.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                var r = new Rectangle(0, 0, lbl.Width - 1, lbl.Height - 1);
                using var path = RoundedRect(r, 1);
                using var bgBrush = new SolidBrush(lbl.BackColor);
                using var pen = new Pen(border, 1);
                e.Graphics.FillPath(bgBrush, path);
                e.Graphics.DrawPath(pen, path);
                TextRenderer.DrawText(e.Graphics, text, lbl.Font, r, fg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            };
            lbl.MouseEnter += (s, e) => { lbl.BackColor = hoverBg; lbl.Invalidate(); };
            lbl.MouseLeave += (s, e) => { lbl.BackColor = Color.White; lbl.Invalidate(); };
            return lbl;
        }

        // Hook a Label (or any Control) so its visible Region tracks a
        // rounded rectangle. Updated on Resize so AutoSize labels that
        // change size after layout still get a correctly-shaped Region.
        private static void ApplyRoundedRegion(Control c, int radius)
        {
            EventHandler update = (_, _) =>
            {
                if (c.IsDisposed || c.Width <= 0 || c.Height <= 0) return;
                c.Region?.Dispose();
                using var path = RoundedRect(new Rectangle(0, 0, c.Width, c.Height), radius);
                c.Region = new System.Drawing.Region(path);
            };
            c.Resize += update;
            if (c.Width > 0 && c.Height > 0) update(c, EventArgs.Empty);
        }

        public static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            if (radius <= 0) { path.AddRectangle(r); return path; }
            var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // v3.6.8 — a tab shape (rounded TOP corners, square bottom) for the
        // running-program tabs in the RDP Manager header.
        public static GraphicsPath TabRect(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            if (radius <= 0) { path.AddRectangle(r); return path; }
            var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            path.AddArc(r.X, r.Y, d, d, 180, 90);              // top-left
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);      // top-right
            path.AddLine(r.Right, r.Y + d, r.Right, r.Bottom); // right edge down
            path.AddLine(r.Right, r.Bottom, r.X, r.Bottom);    // bottom edge
            path.AddLine(r.X, r.Bottom, r.X, r.Y + d);         // left edge up
            path.CloseFigure();
            return path;
        }

        // === Filled colored button (RDP Manager / Книга style) ===
        //
        // Same shape as MakeFlat, but with a solid bg color, white text,
        // and a white-tinted icon. Default bg #296AA0 (teal-blue). Used
        // by:
        //   - MainForm ("RDP Manager" on the search row)
        //   - AddressBookForm ("RDP Manager" after the title)
        //   - RdpManagerForm ("Книга" in the header)
        public static Label MakeFilled(string text, string? iconKey, Color bgColor, int width = 140, int radius = 1)
        {
            var btnH = 36;  // v2.2.29: reverted to snapshot height
            var lbl = new Label
            {
                Text = "",
                AutoSize = false,
                Size = new Size(width, btnH),
                Font = new Font("Segoe UI Semibold", 10F),
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand,
                BackColor = Color.Transparent,
                ForeColor = Color.White
            };
            var bgBrush = new SolidBrush(bgColor);
            var borderPen = new Pen(bgColor, 1);
            var hoverBg = ControlPaint.Dark(bgColor, 0.08f);

            // iconKey may be null (OK button without icon). Pre-resolve so
            // Paint doesn't repeat the work each call.
            Image? whiteIcon = null;
            int iconY = 0;
            if (!string.IsNullOrEmpty(iconKey))
            {
                var icon = MenuIcons.Get(iconKey);
                iconY = (btnH - icon.Height) / 2;
                whiteIcon = TintWhite(icon);
            }
            bool hasIcon = whiteIcon != null;

            lbl.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                var r = new Rectangle(0, 0, lbl.Width, lbl.Height);
                using var path = RoundedRect(r, radius);
                e.Graphics.FillPath(bgBrush, path);
                e.Graphics.DrawPath(borderPen, path);
                if (hasIcon)
                {
                    // v2.1.85: byte-for-byte match to the v2.1.62 snapshot
                    // (user reports "кнопка испорчена" — reverting all
                    // NoPadding/EndEllipsis experiments).
                    e.Graphics.DrawImage(whiteIcon!, 10, iconY);
                    var textRect = new Rectangle(32, 0, lbl.Width - 36, lbl.Height);
                    TextRenderer.DrawText(e.Graphics, text, lbl.Font, textRect,
                        lbl.ForeColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                }
                else
                {
                    var textRect = new Rectangle(0, 0, lbl.Width, lbl.Height);
                    TextRenderer.DrawText(e.Graphics, text, lbl.Font, textRect,
                        lbl.ForeColor,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
            };

            lbl.MouseEnter += (s, e) =>
            {
                bgBrush.Color = hoverBg;
                borderPen.Color = hoverBg;
                lbl.Invalidate();
            };
            lbl.MouseLeave += (s, e) =>
            {
                bgBrush.Color = bgColor;
                borderPen.Color = bgColor;
                lbl.Invalidate();
            };
            return lbl;
        }

        // Remaps every non-transparent pixel of an Image to white — used to
        // recolor the brown/accent MenuIcons into a white silhouette for
        // use on the colored filled button.
        public static Bitmap TintWhite(Image src)
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
                new[] { 1f, 1f, 1f, 0f, 1f }
            });
            attrs.SetColorMatrix(matrix);
            var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            g.DrawImage(src, rect, 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
            return bmp;
        }

        // Reliably brings a top-level Form to the foreground, even when
        // the caller isn't the current foreground process. Bare
        // Form.BringToFront() fails silently in that case because
        // Windows restricts SetForegroundWindow to the foreground
        // process (or a process that was recently activated). The
        // TopMost toggle works around the restriction by flashing the
        // window to always-on-top, then dropping back.
        public static void BringToForeground(Form form)
        {
            if (form == null || form.IsDisposed) return;
            if (!form.Visible) form.Show();
            if (form.WindowState == FormWindowState.Minimized)
                form.WindowState = FormWindowState.Normal;
            form.TopMost = true;
            form.TopMost = false;
            form.Activate();
        }
    }
}
