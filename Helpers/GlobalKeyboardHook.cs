using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ShadowRDP.Helpers
{
    // v3.9.5 — глобальный низкоуровневый хук Ctrl+K (командная палитра).
    // Зачем, если уже есть PaletteHotkeyFilter (IMessageFilter): фильтр видит
    // клавиши только когда фокус в обычных WinForms-контролах. Если курсор в
    // RDP-сессии (AxMsRdpClient) или окне без активации — клавиши уходят мимо
    // (в удалёнку / другому приложению) и палитра не открывается.
    // WH_KEYBOARD_LL срабатывает раньше всех очередей, поэтому Ctrl+K ловится
    // из любого места, пока foreground-окно — наше. Чужие приложения не
    // трогаем: если активное окно не нашего процесса — пропускаем как есть
    // (иначе украли бы Ctrl+K у Chrome/VS Code).
    //
    // Правила колбэка: возвращать БЫСТРО (блок > ~100мс — ОС молча снимет
    // хук), поэтому здесь только проверки + событие; палитру открывает
    // подписчик через BeginInvoke. Съедаем только первое нажатие K
    // (_kDown-защёлка против автоповтора удержания).
    public sealed class GlobalKeyboardHook : IDisposable
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern short GetAsyncKeyState(int nVirtKey);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x100;
        private const int WM_KEYUP = 0x101;
        private const int VK_K = 0x4B;
        private const int VK_CONTROL = 0x11;
        private const int VK_SHIFT = 0x10;

        private readonly HookProc _proc;
        private IntPtr _hook = IntPtr.Zero;
        private uint _ownPid;
        private bool _kDown;

        // Срабатывает на UI-потоке (хук ставится с него же).
        public event EventHandler? CtrlKDown;

        public GlobalKeyboardHook()
        {
            // Делегат держим полем: SetWindowsHookEx его не пинит, transient
            // лямбда была бы собрана GC с крашем колбэка (см. GlobalMouseHook).
            _proc = HookCallback;
        }

        public void Start()
        {
            if (_hook != IntPtr.Zero) return;
            try { _ownPid = (uint)Process.GetCurrentProcess().Id; } catch { _ownPid = 0; }
            string moduleName;
            try
            {
                using var process = Process.GetCurrentProcess();
                using var module = process.MainModule;
                moduleName = module?.ModuleName ?? (process.ProcessName + ".exe");
            }
            catch
            {
                moduleName = "PupSeekRDP.exe";
            }
            var hMod = GetModuleHandle(moduleName);
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, hMod, 0);
            Logger.Info($"GlobalKeyboardHook installed: handle=0x{_hook.ToInt64():X}, module={moduleName}");
        }

        public void Stop()
        {
            if (_hook == IntPtr.Zero) return;
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }

        public void Dispose() => Stop();

        private bool IsOurForegroundWindow()
        {
            try
            {
                var fg = GetForegroundWindow();
                if (fg == IntPtr.Zero || _ownPid == 0) return false;
                GetWindowThreadProcessId(fg, out uint pid);
                return pid == _ownPid;
            }
            catch { return false; }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = (int)wParam;
                uint vk = 0;
                try
                {
                    vk = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam).vkCode;
                }
                catch { }
                if (vk == VK_K)
                {
                    if (msg == WM_KEYUP)
                    {
                        _kDown = false;
                    }
                    else if (msg == WM_KEYDOWN && !_kDown)
                    {
                        // Только голый Ctrl+K: Alt давал бы SYSKEYDOWN
                        // (сюда не доходит), Shift отсекаем явно.
                        bool ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
                        bool shift = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
                        if (ctrl && !shift
                            && IsOurForegroundWindow()
                            && !(Form.ActiveForm is Forms.CommandPaletteForm))
                        {
                            _kDown = true;
                            try { CtrlKDown?.Invoke(this, EventArgs.Empty); }
                            catch { }
                            return (IntPtr)1; // съели — дальше не раздаём
                        }
                    }
                }
            }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }
    }
}
