using System;
using System.Drawing;
using System.Windows.Forms;
using ShadowRDP.Helpers;

namespace ShadowRDP.Helpers
{
    // Modal dialog for creating / renaming a group and picking its accent
    // colour. Returns null if cancelled.
    public sealed class GroupEditResult
    {
        public string Name { get; }
        public Color Accent { get; }
        public GroupEditResult(string name, Color accent) { Name = name; Accent = accent; }
    }

    public sealed class GroupEditDialog : Form
    {
        private readonly TextBox _nameBox;
        private Color _accent;
        private readonly FlowLayoutPanel _swatches;

        private static readonly Color[] _presets = new[]
        {
            Color.FromArgb(180, 170, 60),   // Users — warm yellow
            Color.FromArgb(222, 139, 139),  // Servers — rose
            Color.FromArgb(139, 186, 139),  // Network — green
            Color.FromArgb(210, 180, 103),  // Mfu — amber
            Color.FromArgb(124, 120, 200),  // violet
            Color.FromArgb(90, 150, 210),   // blue
            Color.FromArgb(200, 110, 150),  // pink
            Color.FromArgb(150, 110, 200),  // purple
            Color.FromArgb(210, 140, 90),   // orange
            Color.FromArgb(120, 130, 140),  // slate
        };

        public static GroupEditResult? Show(IWin32Window? owner, ComputerGroup.Info? existing)
        {
            using var dlg = new GroupEditDialog(existing);
            return UiModal.ShowDimmed(owner, dlg) == DialogResult.OK
                ? new GroupEditResult(dlg._nameBox.Text.Trim(), dlg._accent)
                : null;
        }

        private GroupEditDialog(ComputerGroup.Info? existing)
        {
            Text = existing == null ? "Новая группа" : "Редактировать группу";
            ClientSize = new Size(440, 222);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9F);
            BackColor = UiTheme.Background;

            var lblName = new Label
            {
                Text = "Название:",
                Location = new Point(16, 18),
                Size = new Size(88, 22),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = UiTheme.Foreground
            };
            Controls.Add(lblName);

            _nameBox = new TextBox
            {
                Location = new Point(108, 16),
                Size = new Size(300, 24),
                Font = new Font("Segoe UI", 10F)
            };
            if (existing != null) _nameBox.Text = existing.Label;
            Controls.Add(_nameBox);

            var lblColor = new Label
            {
                Text = "Цвет:",
                Location = new Point(16, 56),
                Size = new Size(88, 22),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = UiTheme.Foreground
            };
            Controls.Add(lblColor);

            _swatches = new FlowLayoutPanel
            {
                Location = new Point(108, 50),
                Size = new Size(312, 70),
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                BackColor = Color.Transparent
            };
            Controls.Add(_swatches);

            _accent = existing?.Accent ?? _presets[0];
            foreach (var c in _presets)
            {
                var b = new Button
                {
                    Size = new Size(28, 28),
                    BackColor = c,
                    FlatStyle = FlatStyle.Flat,
                    Margin = new Padding(2),
                    Tag = c
                };
                b.FlatAppearance.BorderSize = ColorEquals(c, _accent) ? 3 : 1;
                b.FlatAppearance.BorderColor = ColorEquals(c, _accent) ? UiTheme.Accent : Color.Gray;
                b.Click += (s, e) => SelectSwatch(c);
                _swatches.Controls.Add(b);
            }
            var otherBtn = new Button
            {
                Text = "…",
                Size = new Size(28, 28),
                FlatStyle = FlatStyle.Flat,
                Margin = new Padding(2)
            };
            otherBtn.Click += (s, e) =>
            {
                using var cd = new ColorDialog { Color = _accent, FullOpen = true };
                if (cd.ShowDialog(this) == DialogResult.OK) SelectSwatch(cd.Color, true);
            };
            _swatches.Controls.Add(otherBtn);

            var okBtn = new Button
            {
                Text = "OK",
                Location = new Point(247, 169),
                Size = new Size(80, 28),
                DialogResult = DialogResult.OK
            };
            var cancelBtn = new Button
            {
                Text = "Отмена",
                Location = new Point(335, 169),
                Size = new Size(80, 28),
                DialogResult = DialogResult.Cancel
            };
            Controls.Add(okBtn);
            Controls.Add(cancelBtn);
            AcceptButton = okBtn;
            CancelButton = cancelBtn;
            okBtn.Click += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(_nameBox.Text))
                {
                    MessageBox.Show(this, "Введите название группы.", "Группа",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    _nameBox.Focus();
                    DialogResult = DialogResult.None;
                }
            };
        }

        private void SelectSwatch(Color c, bool isOther = false)
        {
            _accent = c;
            foreach (Control ctrl in _swatches.Controls)
            {
                if (ctrl is Button b && b.Tag is Color bc)
                {
                    bool sel = !isOther && ColorEquals(bc, c);
                    b.FlatAppearance.BorderSize = sel ? 3 : 1;
                    b.FlatAppearance.BorderColor = sel ? UiTheme.Accent : Color.Gray;
                }
            }
        }

        private static bool ColorEquals(Color a, Color b) => a.R == b.R && a.G == b.G && a.B == b.B;
    }
}
