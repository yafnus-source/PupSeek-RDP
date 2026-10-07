using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace ShadowRDP.Helpers
{
    // v3.9.0 — themed context menu built on the native ContextMenuStrip.
    // Using the native control gives correct cursor positioning and the
    // standard "no need to hold the button" behaviour for free; the visual
    // style is applied through a custom renderer + colour table so it matches
    // the rest of the app's design (header band, icons, hover, checks).
    public sealed class ThemedContextMenu : ContextMenuStrip
    {
        private sealed class Spec
        {
            public enum Kind { Header, Item, Separator, Submenu, DynamicSubmenu }
            public Kind Type;
            public string? Title;
            public string? Subtitle;
            public string? Text;
            public string? IconKey;
            public string? RightMeta;
            public bool IsChecked;
            public Action? OnClick;
            public Action<ThemedContextMenu>? BuildSub;
        }

        private readonly List<Spec> _specs = new();
        private bool _built;

        private const int HEADER_H = 54;

        public ThemedContextMenu()
        {
            Renderer = new ThemedRenderer();
            ShowImageMargin = true;
            ShowCheckMargin = false;
            Font = new Font("Segoe UI", 9f);
            Padding = Padding.Empty;
        }

        // === builder API (unchanged surface) ===
        public ThemedContextMenu AddHeader(string title, string? subtitle = null)
        {
            _specs.Add(new Spec { Type = Spec.Kind.Header, Title = title, Subtitle = subtitle });
            return this;
        }

        public ThemedContextMenu AddItem(string text, string? iconKey, Action onClick,
            string? rightMeta = null, bool isChecked = false)
        {
            _specs.Add(new Spec
            {
                Type = Spec.Kind.Item,
                Text = text,
                IconKey = iconKey,
                OnClick = onClick,
                RightMeta = rightMeta,
                IsChecked = isChecked
            });
            return this;
        }

        public ThemedContextMenu AddSeparator()
        {
            _specs.Add(new Spec { Type = Spec.Kind.Separator });
            return this;
        }

        public ThemedContextMenu AddSubmenu(string text, string? iconKey, Action<ThemedContextMenu> buildSub)
        {
            _specs.Add(new Spec
            {
                Type = Spec.Kind.Submenu,
                Text = text,
                IconKey = iconKey,
                BuildSub = buildSub
            });
            return this;
        }

        public ThemedContextMenu AddDynamicSubmenu(string text, string? iconKey, Action<ThemedContextMenu> populate)
        {
            _specs.Add(new Spec
            {
                Type = Spec.Kind.DynamicSubmenu,
                Text = text,
                IconKey = iconKey,
                BuildSub = populate
            });
            return this;
        }

        public ThemedContextMenu InsertItem(int index, string text, string? iconKey, Action onClick,
            string? rightMeta = null, bool isChecked = false)
        {
            _specs.Insert(index, new Spec
            {
                Type = Spec.Kind.Item,
                Text = text,
                IconKey = iconKey,
                OnClick = onClick,
                RightMeta = rightMeta,
                IsChecked = isChecked
            });
            return this;
        }

        public ThemedContextMenu InsertSeparator(int index)
        {
            _specs.Insert(index, new Spec { Type = Spec.Kind.Separator });
            return this;
        }

        public ThemedContextMenu InsertSubmenu(int index, string text, string? iconKey, Action<ThemedContextMenu> buildSub)
        {
            _specs.Insert(index, new Spec
            {
                Type = Spec.Kind.Submenu,
                Text = text,
                IconKey = iconKey,
                BuildSub = buildSub
            });
            return this;
        }

        // For dynamic submenu the child is rebuilt on every click, so it never goes stale.
        private void RebuildDynamicChild(ThemedContextMenu child, Action<ThemedContextMenu> populate)
        {
            child.Items.Clear();
            // Prevent the child's own Show() from clearing what we just added via BuildItems.
            child._built = true;
            populate(child);
        }

        public new void Show(IWin32Window? owner, Point screenLocation)
        {
            if (!_built) BuildItems();
            if (owner is Control ctl) base.Show(ctl, ctl.PointToClient(screenLocation));
            else base.Show(screenLocation);
        }

        private const int CornerRadius = 4; // v4.8.1: окна скругляет DWM (см. выше), константа — для истории

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // v4.8.1 — скругление ОКНА меню отдаём DWM (гладко на Win11, как в
            // вебе): Region режет без сглаживания. На Win10 игнорируется.
            try
            {
                int pref = 2; // DWMWCP_ROUND; attr 33 = DWMWA_WINDOW_CORNER_PREFERENCE
                DwmSetWindowAttribute(Handle, 33, ref pref, sizeof(int));
            }
            catch { }
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
        }

        // === layout ===
        private void BuildItems()
        {
            _built = true;
            Items.Clear();
            var th = UiTheme.Current;

            foreach (var spec in _specs)
            {
                ToolStripItem item;
                switch (spec.Type)
                {
                    case Spec.Kind.Header:
                    {
                        var hdr = new ToolStripMenuItem(spec.Title ?? "")
                        {
                            Enabled = true,
                            Tag = new HeaderData
                            {
                                Title = (spec.Title ?? "").ToUpper(),
                                Subtitle = spec.Subtitle ?? ""
                            }
                        };
                        hdr.Padding = new Padding(0, 11, 0, 11);
                        hdr.Margin = new Padding(0, 0, 0, 10);
                        item = hdr;
                        break;
                    }
                    case Spec.Kind.Separator:
                        item = new ToolStripSeparator();
                        break;
                    case Spec.Kind.Submenu:
                    {
                        var tsi = new ToolStripMenuItem(spec.Text ?? "")
                        {
                            Image = IconFor(spec.IconKey, th),
                            ForeColor = th.Foreground,
                            ImageScaling = ToolStripItemImageScaling.None,
                            Padding = new Padding(0, 3, 0, 4)
                        };
                        var child = new ThemedContextMenu();
                        spec.BuildSub!(child);
                        child.BuildItems();           // build eagerly so the dropdown isn't empty
                        tsi.DropDown = child;
                        // #3 global hover: open on hover, click toggles (no Opening cancel).
                        tsi.Click += (s, e) =>
                        {
                            if (tsi.DropDown.Visible) { child.Close(); return; }
                            tsi.ShowDropDown();
                        };
                        tsi.MouseEnter += (s, e) =>
                        {
                            if (!tsi.DropDown.Visible) tsi.ShowDropDown();
                        };
                        item = tsi;
                        break;
                    }
                    case Spec.Kind.DynamicSubmenu:
                    {
                        var tsi = new ToolStripMenuItem(spec.Text ?? "")
                        {
                            Image = IconFor(spec.IconKey, th),
                            ForeColor = th.Foreground,
                            ImageScaling = ToolStripItemImageScaling.None,
                            Padding = new Padding(0, 3, 0, 4),
                            DropDownDirection = ToolStripDropDownDirection.Right
                        };
                        var child = new ThemedContextMenu();
                        child._built = true; // prevent Show() from clearing dynamic items via BuildItems()
                        tsi.DropDown = child;
                        var populate = spec.BuildSub;
                        void OpenDynamic()
                        {
                            RebuildDynamicChild(child, populate!);
                            child.PerformLayout();
                            if (!tsi.DropDown.Visible) tsi.ShowDropDown();
                        }
                        tsi.Click += (s, e) =>
                        {
                            if (tsi.DropDown.Visible) { child.Close(); return; }
                            OpenDynamic();
                        };
                        // #3 global hover: rebuild + open on hover (no Opening cancel).
                        tsi.MouseEnter += (s, e) => OpenDynamic();
                        item = tsi;
                        break;
                    }
                    default:
                    {
                        var tsi = new ToolStripMenuItem(spec.Text ?? "")
                        {
                            Image = IconFor(spec.IconKey, th),
                            ForeColor = th.Foreground,
                            ImageScaling = ToolStripItemImageScaling.None,
                            ShortcutKeyDisplayString = spec.RightMeta,
                            Checked = spec.IsChecked,
                            Padding =             new Padding(0, 3, 0, 4)
                        };
                        var captured = spec.OnClick;
                        tsi.Click += (s, e) => captured?.Invoke();
                        item = tsi;
                        break;
                    }
                }
                Items.Add(item);

                // Darken icon on hover for all items with images
                if (item is ToolStripMenuItem tsiHover && tsiHover.Image != null)
                {
                    var origImg = tsiHover.Image;
                    var darkImg = MenuIcons.Darken(origImg);
                    tsiHover.MouseEnter += (s, e) => { tsiHover.Image = darkImg; };
                    tsiHover.MouseLeave += (s, e) => { tsiHover.Image = origImg; };
                }
            }
        }

        private static Image? IconFor(string? iconKey, ThemePalette th)
        {
            if (iconKey == "folder") return MakeFolderIcon(th.MenuIcon);
            if (string.IsNullOrEmpty(iconKey)) return null;
            return MenuIcons.Get(iconKey);
        }

        private static Image MakeFolderIcon(Color c)
        {
            var bmp = new Bitmap(16, 16);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(c);
            g.FillRectangle(brush, 1, 3, 7, 2);
            g.FillRectangle(brush, 1, 5, 14, 9);
            return bmp;
        }

        private sealed class ThemedColorTable : ProfessionalColorTable
        {
            private readonly ThemePalette th = UiTheme.Current;
            public override Color ToolStripDropDownBackground => th.MenuBackground;
            public override Color MenuStripGradientBegin => th.MenuBackground;
            public override Color MenuStripGradientEnd => th.MenuBackground;
            public override Color MenuItemSelected => th.AccentSoft;
            public override Color MenuItemSelectedGradientBegin => th.AccentSoft;
            public override Color MenuItemSelectedGradientEnd => th.AccentSoft;
            public override Color MenuItemBorder => th.AccentSoftBorder;
            public override Color MenuBorder => th.BorderSubtle;
            public override Color ImageMarginGradientBegin => th.MenuBackground;
            public override Color ImageMarginGradientEnd => th.MenuBackground;
            public override Color ImageMarginGradientMiddle => th.MenuBackground;
            public override Color ImageMarginRevealedGradientBegin => th.MenuBackground;
            public override Color ImageMarginRevealedGradientEnd => th.MenuBackground;
            public override Color ImageMarginRevealedGradientMiddle => th.MenuBackground;
            public override Color SeparatorDark => th.BorderSubtle;
            public override Color SeparatorLight => th.MenuBackground;
            public override Color CheckSelectedBackground => th.Accent;
            public override Color CheckBackground => th.MenuBackground;
            public override Color CheckPressedBackground => th.Accent;
            public override Color ButtonSelectedBorder => th.Accent;
        }

        private sealed class HeaderData
        {
            public string Title = "";
            public string Subtitle = "";
        }

        private sealed class ThemedRenderer : ToolStripProfessionalRenderer
        {
            private static readonly Font TitleFont = new Font("Segoe UI", 8f, FontStyle.Bold);
            private static readonly Font SubFont = new Font("Segoe UI", 10.5f);

            public ThemedRenderer() : base(new ThemedColorTable()) { }

            protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
            {
                var th = UiTheme.Current;
                using (var b = new SolidBrush(th.MenuBackground))
                    e.Graphics.FillRectangle(b, e.AffectedBounds);
                foreach (ToolStripItem it in e.ToolStrip.Items)
                {
                    if (it.Tag is HeaderData)
                    {
                        using var hb = new SolidBrush(th.HeaderBar);
                        e.Graphics.FillRectangle(hb, new Rectangle(e.AffectedBounds.X, 0, e.AffectedBounds.Width, it.Bounds.Y + it.Bounds.Height));
                        break;
                    }
                }
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                // no border -> header touches the top edge without a white strip
            }

            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                base.OnRenderToolStripBackground(e);
                foreach (ToolStripItem it in e.ToolStrip.Items)
                {
                    if (it.Tag is HeaderData)
                    {
                        using var b = new SolidBrush(UiTheme.Current.HeaderBar);
                        e.Graphics.FillRectangle(b, new Rectangle(0, 0, e.ToolStrip.Width, it.Bounds.Y + it.Bounds.Height));
                        break;
                    }
                }
            }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                if (e.Item.Tag is HeaderData)
                {
                    using var b = new SolidBrush(UiTheme.Current.HeaderBar);
                    e.Graphics.FillRectangle(b, e.Item.Bounds);
                    return;
                }
                base.OnRenderMenuItemBackground(e);
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                if (e.Item.Tag is HeaderData hd)
                {
                    var r = new Rectangle(e.TextRectangle.X - 20, e.Item.Bounds.Y, e.TextRectangle.Width + 20, e.Item.Bounds.Height);
                    TextRenderer.DrawText(e.Graphics, hd.Title, TitleFont, r,
                        Color.White, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                    if (hd.Subtitle.Length > 0)
                    {
                        var sr = new Rectangle(r.X, r.Y + 27, r.Width, r.Height - 27);
                        TextRenderer.DrawText(e.Graphics, hd.Subtitle, SubFont, sr,
                            Color.White, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                    }
                    return;
                }
                base.OnRenderItemText(e);
            }
        }
    }
}
