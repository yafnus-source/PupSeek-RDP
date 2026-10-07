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

        // Поисковые поля: при true каретка (|) показывается при фокусе
        // (мигание — нативное), при false — классическое скрытие.
        public bool ShowCaretOnFocus { get; set; } = false;

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
                        try
                        {
                            if (ShowCaretOnFocus) ShowCaret(Handle);
                            else HideCaret(Handle);
                        }
                        catch { }
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
    // Double-buffered grid — enables DoubleBuffered for buttery scrolling.
    // NOTE for fully-custom cell painting (every cell e.Handled=true):
    // the base OnPaintBackground only fills the DataGridView's display area
    // and, with DoubleBuffered + custom cell painting, untouched buffer
    // pixels (scrollbar gutter, clip edges) can blit through as black lines
    // that flicker on/off while scrolling. Forcing the background to fill
    // the ENTIRE ClientRectangle guarantees a light buffer, no artifacts.
    public class DoubleBufferedDataGridView : DataGridView
    {
        public DoubleBufferedDataGridView()
        {
            DoubleBuffered = true;
        }

        protected override void OnPaintBackground(PaintEventArgs pevent)
        {
            using var b = new System.Drawing.SolidBrush(BackgroundColor);
            pevent.Graphics.FillRectangle(b, ClientRectangle);
        }

        // v4.4.0 — колесо в WndProc, а не в фильтре сообщений: на части машин
        // драйверы мыши/тачпада шлют WM_MOUSEWHEEL напрямую SendMessage в окно
        // под курсором, минуя очередь (IMessageFilter такие не видит —
        // доказано пустым wheel_diag.log при живых REG). WndProc видит оба
        // пути доставки. Перехватываем ТОЛЬКО когда системные полосы спрятаны
        // (ScrollBars==None — режим ShadowScroll.AttachGrid): нативный путь
        // там мёртв, скроллим сами построчно/попиксельно. Во всех остальных
        // случаях — штатный base (поведение прочих гридов не меняется).
        // На упоре (дальше некуда) — base, чтобы колесо всплыло родителю.
        public event EventHandler? WheelScrolled;

        private const int WM_MOUSEWHEEL = 0x020A;
        private const int WM_MOUSEHWHEEL = 0x020E;

        protected override void WndProc(ref Message m)
        {
            if ((m.Msg == WM_MOUSEWHEEL || m.Msg == WM_MOUSEHWHEEL) && ScrollBars == ScrollBars.None)
            {
                if (TryWheelScroll(m)) { m.Result = IntPtr.Zero; return; }
            }
            base.WndProc(ref m);
        }

        private bool TryWheelScroll(Message m)
        {
            // Логика — общая (GridWheelScroller), здесь только событие
            // для обновления overlay-полос ShadowScroll.
            bool handled = false;
            try { handled = GridWheelScroller.TryScroll(this, m); }
            catch { handled = false; }
            if (handled)
            {
                try { WheelScrolled?.Invoke(this, EventArgs.Empty); }
                catch { }
            }
            return handled;
        }
    }
}
