using System;
using System.Windows.Forms;

namespace ShadowRDP.Helpers
{
    // v3.9.5 — ядерный Esc: закрывает ЛЮБОЕ всплывающее окно программы
    // (модальное и немодальное), кроме главного менеджера. Раньше формы
    // закрывались по-разному (свой KeyPreview, скрытый CancelButton,
    // централизованный ShowDimmed, никак) — теперь поведение единое.
    // Пропускаем: нет активного окна; фокус не внутри активного окна
    // (открыто меню, выпавший список комбо или системный диалог — у них
    // свой Esc); главное окно (его закрытие по Esc запрещено).
    public sealed class EscGlobalFilter : IMessageFilter
    {
        private const int WM_KEYDOWN = 0x0100;
        private const int VK_ESCAPE = 0x1B;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetFocus();

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != WM_KEYDOWN || m.WParam.ToInt32() != VK_ESCAPE) return false;
            try
            {
                var active = Form.ActiveForm;
                if (active == null || active.IsDisposed) return false;
                if (active is Forms.RdpManagerForm) return false;
                IntPtr focus = IntPtr.Zero;
                try { focus = GetFocus(); } catch { }
                if (focus == IntPtr.Zero) return false;
                Control? focused = null;
                try { focused = Control.FromHandle(focus); } catch { }
                if (focused == null || focused.IsDisposed) return false;
                Control? c = focused;
                while (c != null && !(c is Form)) c = c.Parent;
                if (!ReferenceEquals(c, active)) return false;
                var target = active;
                try
                {
                    target.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            if (target.IsDisposed) return;
                            if (target.Modal) { try { target.DialogResult = DialogResult.Cancel; } catch { } }
                            target.Close();
                        }
                        catch { }
                    }));
                }
                catch { return false; }
                return true;
            }
            catch { return false; }
        }
    }
}
