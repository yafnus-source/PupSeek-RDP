using System;
using Microsoft.Win32;

namespace ShadowRDP.Helpers
{
    // v2.1.88 — WinForms WebBrowser control defaults to IE7 rendering mode
    // regardless of what IE is installed on the system. That breaks
    // modern sites: React/Vue components don't mount, ES6 fails, CSS3
    // features ignored, console shows red errors.
    //
    // The fix is to opt the host process into IE11 Edge mode via the
    // FEATURE_BROWSER_EMULATION registry key under HKCU (no admin needed).
    // Once set, WebBrowser uses the installed IE11 engine for the NEXT
    // navigation in this process.
    //
    // Value 11001 = IE11 Edge mode (uses latest installed IE, ignores
    // the page's X-UA-Compatible meta).
    // Value 11000 = IE11 standards mode (respects X-UA-Compatible meta).
    //
    // Call Enable() at app startup, BEFORE any WebBrowser control is
    // instantiated. See Program.cs.
    public static class IeEmulation
    {
        private const int IE11_EDGE_MODE = 11001;

        public static void Enable()
        {
            try
            {
                var exeName = System.Diagnostics.Process.GetCurrentProcess().ProcessName + ".exe";
                using var key = Registry.CurrentUser.CreateSubKey(
                    @"SOFTWARE\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION");
                key?.SetValue(exeName, IE11_EDGE_MODE, RegistryValueKind.DWord);
                Logger.Info($"IeEmulation: FEATURE_BROWSER_EMULATION[{exeName}] = {IE11_EDGE_MODE}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"IeEmulation failed: {ex.Message}");
            }
        }
    }
}
