using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace ShadowRDP.Services
{
    // Branded app icon for the title bar / taskbar / tray. Uses the embedded
    // logo (Assets/logo.png → SessionSpy.logo.png resource). The `admin`
    // param is kept for signature compat with ShadowRdpAppContext but no
    // longer changes the visual — the form title already shows
    // "Администратор" / "без прав".
    //
    // v2.1.45 — caches the icon and keeps the source bitmap alive via a
    // static field. Without this, Icon.FromHandle creates a wrapper that
    // doesn't own the HICON, and the GC can drop the source bitmap, which
    // causes the tray icon to vanish on right-click (Windows recreates
    // the icon and puts it at the end of the tray list).
    public static class StatusIcon
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr handle);

        private static Bitmap? _keepAliveBitmap;
        private static Icon? _cached;

        public static Icon Create(bool admin)
        {
            if (_cached != null) return _cached;

            try
            {
                var logo = Helpers.AppLogo.Load();
                if (logo != null)
                {
                    // Render at 32×32 for taskbar/title bar (WinForms scales
                    // down for the smaller title-bar size automatically).
                    // IMPORTANT: don't dispose the bitmap — the HICON we get
                    // from GetHicon references pixel data that the bitmap
                    // owns. We keep _keepAliveBitmap alive for the process
                    // lifetime to prevent the GC from collecting it.
                    _keepAliveBitmap = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    using (var g = Graphics.FromImage(_keepAliveBitmap))
                    {
                        g.SmoothingMode = SmoothingMode.HighQuality;
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.Clear(Color.Transparent);
                        g.DrawImage(logo, 0, 0, 32, 32);
                    }
                    var hicon = _keepAliveBitmap.GetHicon();
                    _cached = Icon.FromHandle(hicon);
                    return _cached;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"StatusIcon logo fallback: {ex.Message}");
            }

            // Fallback: solid blue square with white "S" (matches the
            // original PowerShell version's tray icon).
            _keepAliveBitmap = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(_keepAliveBitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.FromArgb(255, 0, 120, 215));  // #0078D7
                using var font = new Font("Segoe UI", 18F, FontStyle.Bold);
                var sf = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center
                };
                g.DrawString("S", font, Brushes.White, new RectangleF(0, 0, 32, 32), sf);
            }
            var fallbackHicon = _keepAliveBitmap.GetHicon();
            _cached = Icon.FromHandle(fallbackHicon);
            return _cached;
        }
    }
}
