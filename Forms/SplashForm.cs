using System;
using System.Drawing;
using System.Windows.Forms;
using ShadowRDP.Helpers;

namespace ShadowRDP.Forms
{
    // Borderless splash shown for ~1 second at app startup, before the
    // MainForm appears. Centred on screen, shows the SessionSpy logo
    // scaled to ~256px + a small caption underneath. Closes via a Timer
    // or explicitly via Hide().
    public class SplashForm : Form
    {
        public SplashForm()
        {
            // Borderless, no caption, stays on top until main form shows.
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = UiTheme.Background;

            const int splashSize = 360;
            ClientSize = new Size(splashSize, splashSize + 80);

            var logo = AppLogo.Load();
            if (logo != null)
            {
                var pb = new PictureBox
                {
                    Image = logo,
                    SizeMode = PictureBoxSizeMode.Zoom,
                    Bounds = new Rectangle((splashSize - 256) / 2, 30, 256, 256),
                    BackColor = Color.Transparent
                };
                Controls.Add(pb);
            }

            var title = new Label
            {
                Text = "Pup Seek RDP",
                Font = new Font("Segoe UI Semibold", 20F),
                ForeColor = UiTheme.Foreground,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleCenter,
                Bounds = new Rectangle(0, 300, splashSize, 36)
            };
            Controls.Add(title);

            var sub = new Label
            {
                Text = "управление RDP shadow-сессиями\nи все такое",
                Font = new Font("Segoe UI", 9F),
                ForeColor = UiTheme.MutedForeground,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleCenter,
                Bounds = new Rectangle(0, 340, splashSize, 36)
            };
            Controls.Add(sub);

            // v3.7.1 — version pulled from the assembly (was hardcoded "v3.5.8"
            // since ~v3.5.8 — every release forgot to bump it). Format
            // matches the sidebar copyright: v{Major}.{Minor}.{Build}.
            var asmVer = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            var verText = asmVer != null ? $"v{asmVer.Major}.{asmVer.Minor}.{asmVer.Build}" : "v3.8.2";

            var ver = new Label
            {
                Text = verText,
                Font = new Font("Segoe UI", 8F),
                ForeColor = UiTheme.MutedForeground,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleCenter,
                Bounds = new Rectangle(0, 380, splashSize, 18)
            };
            Controls.Add(ver);

            // Rounded corners via Region — clip to a 16px rounded rect.
            using var path = UiButtons.RoundedRect(new Rectangle(0, 0, splashSize, splashSize + 80), 16);
            Region = new Region(path);
        }

        // Auto-close after `ms` milliseconds. Optional — MainForm can also
        // call Close() explicitly when it's ready.
        public void AutoCloseAfter(int ms)
        {
            var t = new Timer { Interval = ms };
            t.Tick += (s, e) =>
            {
                t.Stop();
                t.Dispose();
                try { Close(); } catch { }
            };
            t.Start();
        }
    }
}
