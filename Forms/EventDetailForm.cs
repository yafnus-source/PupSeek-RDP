using System;
using System.Drawing;
using System.Windows.Forms;
using ShadowRDP.Helpers;

namespace ShadowRDP.Forms
{
    public class EventDetailForm : Form
    {
        public EventDetailForm(int eventId, string source, string level, string computer, string message, string detailedDescription)
        {
            Text = $"EventID {eventId} — {source}";
            Size = new Size(480, 360);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = UiTheme.Card;

            var header = new Panel { Dock = DockStyle.Top, Height = 48, BackColor = UiTheme.HeaderBar };
            var titleLabel = new Label
            {
                Text = $"EventID {eventId} — {source}",
                Font = new Font("Segoe UI Semibold", 11f),
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                AutoSize = true,
                Location = new Point(16, 14)
            };
            header.Controls.Add(titleLabel);
            Controls.Add(header);

            var infoPanel = new Panel { Dock = DockStyle.Top, Height = 60, BackColor = UiTheme.Muted, Padding = new Padding(12, 8, 12, 8) };
            var infoLabel = new Label
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = UiTheme.Foreground,
                Text = $"Уровень: {level}  |  Хост: {computer}"
            };
            infoPanel.Controls.Add(infoLabel);
            Controls.Add(infoPanel);

            var txtBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = UiTheme.Card,
                ForeColor = UiTheme.Foreground,
                Font = new Font("Segoe UI", 10f),
                BorderStyle = BorderStyle.None,
                Padding = new Padding(12, 30, 12, 12),
                Text = string.IsNullOrWhiteSpace(detailedDescription) ? message : detailedDescription
            };
            Controls.Add(txtBox);

            var btnClose = UiButtons.MakeCompactFilled("Закрыть", 80);
            btnClose.Location = new Point(Width - 100, Height - 70);
            btnClose.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };
            Controls.Add(btnClose);

            // Re-layout on resize
            Resize += (s, e) => { btnClose.Location = new Point(Width - 100, Height - 70); };
        }
    }
}
