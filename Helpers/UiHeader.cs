using System.Drawing;
using System.Windows.Forms;

namespace ShadowRDP.Helpers
{
    // v2.2.15 — Shared blue header bar for all report/dialog forms.
    // Creates a Panel with:
    //   - bg #296AA0
    //   - height 66px (Dock=Top)
    //   - white-tinted icon (16×16 scaled to 28×28)
    //   - title text (Segoe UI Semibold 14F, white)
    //   - optional subtitle (Segoe UI 9F, white 80% opacity)
    //
    // Usage:
    //   var header = UiHeader.Create("Информация о компьютере", "srv01.domain.local", "info");
    //   Controls.Add(header);
    //   // header must be added BEFORE any Dock=Fill control so it docks Top first.
    public static class UiHeader
    {
        public static Panel Create(string title, string? subtitle = null, string? iconKey = null,
            Color? headerBg = null, Color? subtitleColor = null)
        {
            var bg = headerBg ?? ShadowRDP.Helpers.UiTheme.HeaderBar;
            var panel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 76,  // v2.2.16: 66→76 — long computer names overlapped subtitle
                BackColor = bg
            };

            int iconX = 20;
            int textX = iconKey != null ? 56 : 20;

            if (iconKey != null)
            {
                var rawIcon = MenuIcons.Get(iconKey);
                var whiteIcon = UiButtons.TintWhite(rawIcon);
                var icon = new PictureBox
                {
                    Image = whiteIcon,
                    SizeMode = PictureBoxSizeMode.Zoom,
                    Size = new Size(28, 28),
                    Location = new Point(iconX, (76 - 28) / 2),
                    BackColor = Color.Transparent
                };
                panel.Controls.Add(icon);
            }

            if (subtitle != null)
            {
                var sub = new Label
                {
                    Text = subtitle,
                    Location = new Point(textX, 44),  // v2.2.16: shifted down for taller header
                    AutoSize = true,
                    Font = new Font("Segoe UI", 9F),
                    ForeColor = subtitleColor ?? Color.FromArgb(204, 224, 235),
                    BackColor = bg
                };
                panel.Controls.Add(sub);
            }

            var lbl = new Label
            {
                Text = title,
                Location = new Point(textX, subtitle != null ? 14 : 28),  // v2.2.16: adjusted Y for 76px
                AutoSize = true,
                Font = new Font("Segoe UI Semibold", 14F),
                ForeColor = Color.White,
                BackColor = bg
            };
            panel.Controls.Add(lbl);

            return panel;
        }
    }
}
