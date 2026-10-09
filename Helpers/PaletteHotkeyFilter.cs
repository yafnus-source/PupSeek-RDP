using System;
using System.Windows.Forms;

namespace ShadowRDP.Helpers
{
    // v3.9.5 — глобальный Ctrl+K: командная палитра из любого окна.
    // Фильтр ставится в Program.Main через Application.AddMessageFilter;
    // ловит WM_KEYDOWN в очереди сообщений ДО диспатча в control, поэтому
    // работает и там, где у формы нет своего KeyDown (встроенные вкладки,
    // диалоги, WebView2). Владельцем палитры служит активное окно.
    internal sealed class PaletteHotkeyFilter : IMessageFilter
    {
        private const int WM_KEYDOWN = 0x100;
        private bool _busy;

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != WM_KEYDOWN) return false;
            if (_busy) return false;
            if ((Keys)(m.WParam.ToInt32() & 0xFFFF) != Keys.K) return false;
            if (Control.ModifierKeys != Keys.Control) return false;
            try
            {
                // Своя палитра уже открыта — не мешаем её вводу.
                if (Form.ActiveForm is Forms.CommandPaletteForm) return false;
                // Менеджер не открыт — палитра не нужна (холостое нажатие).
                if (!Forms.RdpManagerForm.IsOpen) return false;

                var owner = Form.ActiveForm ?? Forms.RdpManagerForm.Instance;
                var mgr = Forms.RdpManagerForm.Instance;
                if (mgr == null || mgr.IsDisposed) return false;

                _busy = true;
                try { mgr.ShowPalette(owner); }
                finally { _busy = false; }
                return true; // Ctrl+K перехвачен, дальше не раздаём
            }
            catch (Exception ex)
            {
                try { Logger.Warn($"Палитра Ctrl+K: {ex.Message}"); } catch { }
                return false;
            }
        }
    }
}
