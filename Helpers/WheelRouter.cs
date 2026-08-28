using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ShadowRDP.Helpers
{
    // Routes WM_MOUSEWHEEL to the scrollable control under the cursor,
    // regardless of which control owns keyboard focus.
    //
    // WinForms delivers WM_MOUSEWHEEL to the FOCUSED window; a scrollable
    // control only scrolls when it (or a descendant) has focus. The sidebar
    // session items (non-focusable Panels) and the report grids never get
    // focus, so the wheel looked dead unless the user clicked the control
    // first.
    //
    // On every wheel message this filter inspects the control under the
    // cursor (Control.FromChildHandle + WindowFromPoint) and walks its
    // parent chain:
    //   - inside the sidebar scroll host → manual Y-shift (ScrollSidebar)
    //   - over any DataGridView          → re-send the wheel to that grid
    //                                      (it scrolls natively)
    // Everything else (terminals, SFTP, RDP clients) is left untouched —
    // the same IMessageFilter pattern HintWheelFilter/TerminalWheelFilter
    // already use in ConsoleSessionControl. Filter order is irrelevant
    // because the cursor-over targets are disjoint.
    internal sealed class WheelRouter : IMessageFilter
    {
        private const int WM_MOUSEWHEEL = 0x020A;

        private readonly Control _sidebarHost;
        // delta → true if the sidebar actually scrolled.
        private readonly Func<int, bool> _sidebarScroll;

        public WheelRouter(Control sidebarHost, Func<int, bool> sidebarScroll)
        {
            _sidebarHost = sidebarHost;
            _sidebarScroll = sidebarScroll;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(Point p);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != WM_MOUSEWHEEL) return false;
            try
            {
                short delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
                Control? c = Control.FromChildHandle(WindowFromPoint(Cursor.Position));
                while (c != null)
                {
                    if (ReferenceEquals(c, _sidebarHost))
                        return _sidebarScroll(delta);

                    if (c is DataGridView dgv && dgv.IsHandleCreated && dgv.Visible)
                    {
                        SendMessage(dgv.Handle, WM_MOUSEWHEEL, m.WParam, m.LParam);
                        return true;
                    }

                    c = c.Parent;
                }
            }
            catch { }
            return false;
        }
    }
}
