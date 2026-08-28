using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ShadowRDP.Helpers
{
    // v2.1.34 — Figma-style theming for ContextMenuStrip, restored after
    // the Win10-style experiment. Indigo accent hover on warm card
    // background. WinForms renders context menus via
    // ToolStripProfessionalRenderer + a ProfessionalColorTable — we
    // override both, plus uniform padding/font on every item (recursive
    // into dropdowns).
    //
    // v2.1.42: submenu position override via P/Invoke SetWindowPos —
    // the Location setter on ToolStripDropDown is silently ignored by
    // WinForms; only the Win32 call actually moves the window.
    //
    // Usage:  FigmaMenuStyle.Apply(menuStrip);
    public static class FigmaMenuStyle
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;

        public static void Apply(ContextMenuStrip menu)
        {
            if (menu == null) return;
            menu.Renderer = new ToolStripProfessionalRenderer(new FigmaColorTable());
            menu.BackColor = UiTheme.MenuBackground;
            menu.ForeColor = UiTheme.Foreground;
            menu.Font = new Font("Segoe UI", 9F);
            // Right padding 10 (v2.1.51: was 0) widens the menu by 10px
            // per user request "context menus everywhere wider by 10px".
            // The Padding-right on the menu itself extends the popup window;
            // item padding (Padding(24,2,0,2) below) stays at 0 on the
            // right so items keep flushing against the inner content edge.
            menu.Padding = new Padding(4, 6, 10, 6);
            menu.ShowImageMargin = true;
            StyleItems(menu.Items);
        }

        private static void StyleItems(ToolStripItemCollection items)
        {
            foreach (ToolStripItem item in items)
            {
                if (item is ToolStripMenuItem mi)
                {
                    // Right padding 0 so the item's right edge IS the
                    // menu's right edge — submenu drops in the tightest
                    // possible spot.
                    mi.Padding = new Padding(24, 2, 0, 2);
                    mi.Font = new Font("Segoe UI", 9F);
                    mi.ForeColor = UiTheme.Foreground;
                    mi.BackColor = UiTheme.MenuBackground;
                    mi.Margin = new Padding(0);
                    // v3.8.2 — darken the icon while the item is hovered/
                    // selected so it stands out against the accent highlight.
                    if (mi.Image != null)
                    {
                        var origImg = mi.Image;
                        var darkImg = MenuIcons.Darken(origImg);
                        mi.MouseEnter += (s, e) => { if (mi.Image != darkImg) mi.Image = darkImg; };
                        mi.MouseLeave += (s, e) => { if (mi.Image != origImg) mi.Image = origImg; };
                    }
                    if (mi.HasDropDownItems)
                    {
                        StyleItems(mi.DropDownItems);
                        mi.DropDownDirection = ToolStripDropDownDirection.Default;
                        // Manual position override via SetWindowPos — the
                        // Location setter on ToolStripDropDown is silently
                        // ignored. Win32 SetWindowPos actually moves the
                        // window. Opened fires AFTER default positioning
                        // completes, so we override here.
                        mi.DropDown.Opened += (s, e) =>
                        {
                            try
                            {
                                if (mi.Owner == null) return;
                                var itemRightScreen = mi.Owner.PointToScreen(
                                    new Point(mi.Bounds.Right, mi.Bounds.Top));
                                int x = itemRightScreen.X - 20;  // v2.1.46: was -15, -5 more per request
                                int y = itemRightScreen.Y;
                                var h = mi.DropDown.Handle;
                                if (h != IntPtr.Zero)
                                {
                                    SetWindowPos(h, IntPtr.Zero, x, y, 0, 0,
                                        SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                                }
                            }
                            catch { /* best-effort */ }
                        };
                    }
                }
                else if (item is ToolStripSeparator sep)
                {
                    sep.BackColor = UiTheme.MenuBackground;
                    sep.Margin = new Padding(8, 3, 8, 3);
                }
            }
        }
    }

    // Figma colour table — indigo accent hover on warm card background.
    // Each property the framework queries is overridden so no system blue
    // bleeds through.
    internal sealed class FigmaColorTable : ProfessionalColorTable
    {
        public override Color MenuBorder => UiTheme.Border;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color ToolStripBorder => UiTheme.Border;

        public override Color MenuItemSelected => UiTheme.AccentSoft;
        public override Color MenuItemSelectedGradientBegin => UiTheme.AccentSoft;
        public override Color MenuItemSelectedGradientEnd => UiTheme.AccentSoft;

        public override Color MenuItemPressedGradientBegin => UiTheme.AccentSoft;
        public override Color MenuItemPressedGradientEnd => UiTheme.AccentSoft;
        public override Color ToolStripDropDownBackground => UiTheme.MenuBackground;

        public override Color MenuStripGradientBegin => UiTheme.MenuBackground;
        public override Color MenuStripGradientEnd => UiTheme.MenuBackground;

        public override Color ToolStripGradientBegin => UiTheme.MenuBackground;
        public override Color ToolStripGradientMiddle => UiTheme.MenuBackground;
        public override Color ToolStripGradientEnd => UiTheme.MenuBackground;

        public override Color ImageMarginGradientBegin => UiTheme.MenuBackground;
        public override Color ImageMarginGradientMiddle => UiTheme.MenuBackground;
        public override Color ImageMarginGradientEnd => UiTheme.MenuBackground;

        public override Color SeparatorDark => UiTheme.BorderSubtle;
        public override Color SeparatorLight => UiTheme.BorderSubtle;

        public override Color CheckBackground => UiTheme.AccentSoft;
        public override Color CheckPressedBackground => UiTheme.AccentSoft;
        public override Color CheckSelectedBackground => UiTheme.AccentSoft;

        public override Color ButtonCheckedGradientBegin => UiTheme.AccentSoft;
        public override Color ButtonCheckedGradientEnd => UiTheme.AccentSoft;
        public override Color ButtonCheckedHighlight => UiTheme.AccentSoft;
        public override Color ButtonPressedGradientBegin => UiTheme.AccentSoft;
        public override Color ButtonPressedGradientEnd => UiTheme.AccentSoft;
        public override Color ButtonSelectedGradientBegin => UiTheme.AccentSoft;
        public override Color ButtonSelectedGradientEnd => UiTheme.AccentSoft;
        public override Color ButtonSelectedHighlight => UiTheme.AccentSoft;
    }
}


