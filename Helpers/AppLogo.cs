using System;
using System.Drawing;
using System.Reflection;

namespace ShadowRDP.Helpers
{
    // Loads the embedded SessionSpy logo (Assets/logo.png) once and caches
    // it. Used by SplashForm and the RdpManagerForm sidebar branding.
    public static class AppLogo
    {
        private static Image? _cached;
        private static readonly object _lock = new object();

        public static Image? Load()
        {
            lock (_lock)
            {
                if (_cached != null) return _cached;
                try
                {
                    var asm = Assembly.GetExecutingAssembly();
                    var stream = asm.GetManifestResourceStream("SessionSpy.logo.png");
                    if (stream == null)
                    {
                        Logger.Warn("AppLogo: embedded resource 'SessionSpy.logo.png' not found");
                        return null;
                    }
                    _cached = Image.FromStream(stream);
                    return _cached;
                }
                catch (Exception ex)
                {
                    Logger.Warn($"AppLogo.Load failed: {ex.Message}");
                    return null;
                }
            }
        }
    }
}
