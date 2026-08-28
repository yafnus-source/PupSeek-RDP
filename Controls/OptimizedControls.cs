using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ShadowRDP.Controls
{
    // v2.1.62 — TextBox subclass that hides the blinking caret (|)
    // for the lifetime of focus. Calling HideCaret from .NET events
    // (Enter/Click/KeyPress/TextChanged) is unreliable because the
    // base TextBox WndProc re-shows the caret AFTER our handlers run.
    // Intercepting the relevant Windows messages here and calling
    // HideCaret immediately after the base WndProc processes them
    // guarantees the caret stays hidden while the field is focused.
    //
    // Used by AddressBookForm's search field — user wants the "clean"
    // look with no | cursor. Text entry still works.
    public class CaretHidingTextBox : TextBox
    {
        [DllImport("user32.dll")]
        private static extern bool HideCaret(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowCaret(IntPtr hWnd);

        private const int WM_SETFOCUS = 0x0007;
        private const int WM_KILLFOCUS = 0x0008;
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_CHAR = 0x0102;
        private const int WM_PAINT = 0x000F;

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            // v2.1.78: removed WM_PAINT intercept — was causing repaint
            // re-entrancy that shifted text alignment. Only intercept
            // focus + key + click messages now (caret re-appears only on
            // these, not on plain paint).
            switch (m.Msg)
            {
                case WM_SETFOCUS:
                case WM_LBUTTONUP:
                case WM_KEYDOWN:
                case WM_CHAR:
                    if (IsHandleCreated && Focused)
                    {
                        try { HideCaret(Handle); } catch { }
                    }
                    break;
                case WM_KILLFOCUS:
                    try { ShowCaret(Handle); } catch { }
                    break;
            }
        }
    }

    // v2.1.63 — DataGridView subclass with DoubleBuffered enabled.
    // The base class disables double-buffering because the protected
    // DoubleBuffered property defaults to false. Without it, every
    // scroll tick triggers a full repaint that visibly flickers and
    // feels slow even with few rows. Enabling it via subclass (the
    // cleanest path — reflection on the protected setter works but is
    // brittle) gives buttery 60fps scrolling for the address-book grid.
    public class DoubleBufferedDataGridView : DataGridView
    {
        public DoubleBufferedDataGridView()
        {
            DoubleBuffered = true;
        }
    }
}
