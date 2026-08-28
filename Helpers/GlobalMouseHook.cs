using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;

namespace ShadowRDP.Helpers
{
    // v2.1.53 — Global low-level mouse hook for the "Ctrl + middle-click →
    // active sessions menu" feature. Plain middle-click is used by browsers
    // (open link in new tab, close tab, auto-scroll) so we ONLY trigger when
    // Ctrl is held — that combo has no common system-wide meaning and is
    // memorable enough for "quick switch to an RDP session".
    //
    // WH_MOUSE_LL runs the callback on the thread that installed the hook
    // (the UI thread, via its message loop) — no marshaling required.
    public sealed class GlobalMouseHook : IDisposable
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
        private static extern short GetKeyState(int nVirtKey);

        // v2.2.1: GetAsyncKeyState returns the CURRENT keyboard state at
        // the moment of the call, not the state when the message was
        // queued. GetKeyState reads from the thread's local keyboard
        // state which is updated only on certain window messages — for a
        // low-level mouse hook running on the UI thread, this lag can
        // cause Ctrl+MButton misses (Ctrl was down at click time but
        // GetKeyState returns 0 because the local state hasn't been
        // refreshed since the last message). GetAsyncKeyState is more
        // reliable here.
        [DllImport("user32.dll", SetLastError = true)]
        private static extern short GetAsyncKeyState(int nVirtKey);

        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        private const int WH_MOUSE_LL = 14;
        private const int WM_MBUTTONDOWN = 0x0207;
        private const int VK_CONTROL = 0x11;

        private readonly HookProc _proc;
        private IntPtr _hook = IntPtr.Zero;
        // v2.2.1: debounce — WM_MBUTTONDOWN can fire multiple times for a
        // single physical click (some mice send down+up+down for fast
        // clicks). 350ms window suppresses repeat fires so the menu only
        // opens once per click.
        private DateTime _lastFire = DateTime.MinValue;

        // Fires when the user Ctrl+middle-clicks anywhere. Point is in
        // screen coordinates so we can show the menu under the cursor.
        public event EventHandler<Point>? CtrlMiddleButtonDown;

        public GlobalMouseHook()
        {
            // Keep a delegate reference alive — SetWindowsHookEx does NOT
            // pin the delegate, so a transient lambda would be GC'd and
            // crash the hook callback later.
            _proc = HookCallback;
        }

        public void Start()
        {
            if (_hook != IntPtr.Zero) return;
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
            _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, hMod, 0);
            Logger.Info($"GlobalMouseHook installed: handle=0x{_hook.ToInt64():X}, module={moduleName}");
        }

        public void Stop()
        {
            if (_hook == IntPtr.Zero) return;
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && (int)wParam == WM_MBUTTONDOWN)
            {
                // v2.2.1: GetAsyncKeyState reads CURRENT keyboard state.
                bool ctrlDown = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
                if (ctrlDown)
                {
                    // Debounce — minimum 350ms between fires.
                    var now = DateTime.UtcNow;
                    if ((now - _lastFire).TotalMilliseconds > 350)
                    {
                        _lastFire = now;
                        var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                        CtrlMiddleButtonDown?.Invoke(this, new Point(info.pt.x, info.pt.y));
                    }
                }
            }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        public void Dispose() => Stop();

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x; public int y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }
    }
}
