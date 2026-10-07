using System.Drawing;
using System.Drawing.Drawing2D;
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
            // v3.9.3CY — шапка диалогов: DialogHeaderBg (независим от hero).
            var bg = headerBg ?? ShadowRDP.Helpers.UiTheme.DialogHeaderBg;
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
                // v3.9.3 — нативный рендер 28 (было растягивание 16→28 = мыло).
                var rawIcon = MenuIcons.GetScaled(iconKey, 28);
                var whiteIcon = UiButtons.TintWhite(rawIcon);
                // v3.9.3 — Panel+Paint вместо PictureBox.Zoom (16→28): Zoom
                // масштабирует без интерполяции, иконка пиксельная.
                var iconPanel = new Panel
                {
                    Size = new Size(28, 28),
                    Location = new Point(iconX, (76 - 28) / 2),
                    BackColor = Color.Transparent
                };
                iconPanel.Paint += (s, e) =>
                {
                    if (whiteIcon == null) return;
                    var g = e.Graphics;
                    var imPrev = g.InterpolationMode;
                    var poPrev = g.PixelOffsetMode;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(whiteIcon, new Rectangle(0, 0, 28, 28));
                    g.InterpolationMode = imPrev;
                    g.PixelOffsetMode = poPrev;
                };
                panel.Controls.Add(iconPanel);
            }

            if (subtitle != null)
            {
                var sub = new Label
                {
                    Text = subtitle,
                    Location = new Point(textX, 44),  // v2.2.16: shifted down for taller header
                    AutoSize = true,
                    Font = new Font("Segoe UI", 9F),
                    ForeColor = subtitleColor ?? UiTheme.DialogHeaderFg,
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
                ForeColor = UiTheme.DialogHeaderFg,
                BackColor = bg
            };
            panel.Controls.Add(lbl);

            return panel;
        }
    }
}
