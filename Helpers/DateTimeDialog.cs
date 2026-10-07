using System;
using System.Drawing;
using System.Windows.Forms;

namespace ShadowRDP.Helpers
{
    // "Выбрать дату..." в TODO.шная — сразу видимые календарь и часы.
    // Возвращает выбранные дату+время или null при отмене (Esc/Отмена).
    public static class DateTimeDialog
    {
        private static readonly Color Brand = Color.FromArgb(0x62, 0x6F, 0x5E);

        public static DateTime? Show(IWin32Window owner, DateTime initial)
        {
            using var dlg = new EscCloseForm
            {
                Text = "Срок",
                ClientSize = new Size(476, 364),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                BackColor = UiTheme.Card,
                Font = new Font("Segoe UI", 9F)
            };
            dlg.Paint += (se, e) =>
            {
                using var pen = new Pen(UiTheme.Border, 1);
                e.Graphics.DrawRectangle(pen, 0, 0, dlg.Width - 1, dlg.Height - 1);
            };

            // Header
            var header = new Panel { Location = new Point(0, 0), Size = new Size(476, 64), BackColor = Brand };
            header.Controls.Add(new Label
            {
                Text = "Выбрать дату и время", Location = new Point(20, 12), AutoSize = true,
                Font = new Font("Segoe UI Semibold", 12F), ForeColor = Color.White, BackColor = Color.Transparent
            });
            header.Controls.Add(new Label
            {
                Text = "Сразу календарь и часы", Location = new Point(20, 36), AutoSize = true,
                Font = new Font("Segoe UI", 8.5F), ForeColor = Color.FromArgb(0xE4, 0xE0, 0xCE), BackColor = Color.Transparent
            });
            var btnCloseX = new Label
            {
                Text = "✕", Location = new Point(436, 18), Size = new Size(24, 24),
                TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 11F),
                ForeColor = Color.FromArgb(0xD4, 0xCF, 0xBB), Cursor = Cursors.Hand, BackColor = Color.Transparent
            };
            btnCloseX.Click += (se, ev) => dlg.DialogResult = DialogResult.Cancel;
            btnCloseX.MouseEnter += (se, ev) => btnCloseX.ForeColor = Color.White;
            btnCloseX.MouseLeave += (se, ev) => btnCloseX.ForeColor = Color.FromArgb(0xD4, 0xCF, 0xBB);
            header.Controls.Add(btnCloseX);
            dlg.Controls.Add(header);

            // Calendar — всегда видимый
            var cal = new MonthCalendar { Location = new Point(18, 78), MaxSelectionCount = 1, ShowTodayCircle = true };
            try
            {
                cal.BackColor = Color.White;
                cal.TitleBackColor = Brand;
                cal.TitleForeColor = Color.White;
                cal.TrailingForeColor = Color.FromArgb(0xB0, 0xAC, 0xA0);
                cal.ForeColor = Color.FromArgb(0x33, 0x2E, 0x24);
            }
            catch { }
            try { cal.SetSelectionRange(initial.Date, initial.Date); } catch { }
            dlg.Controls.Add(cal);

            // Часы — спиннер времени
            dlg.Controls.Add(new Label
            {
                Text = "Время", Location = new Point(272, 84), AutoSize = true,
                Font = new Font("Segoe UI Semibold", 8F, FontStyle.Bold), ForeColor = UiTheme.MutedForeground, BackColor = Color.Transparent
            });
            var clock = new EscDateTimePicker
            {
                Location = new Point(272, 104), Width = 120,
                Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true,
                Value = initial
            };
            dlg.Controls.Add(clock);

            // Блок с быстрым выбором времени
            var lblQuick = new Label
            {
                Text = "Быстро:", Location = new Point(272, 150), AutoSize = true,
                Font = new Font("Segoe UI", 8F), ForeColor = UiTheme.MutedForeground, BackColor = Color.Transparent
            };
            dlg.Controls.Add(lblQuick);
            var quickFlow = new FlowLayoutPanel { Location = new Point(272, 172), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.Transparent };
            foreach (var h in new[] { "9:00", "12:00", "15:00", "18:00", "21:00" })
            {
                var chip = UiPills.Create(h, false);
                chip.Tag = h;
                chip.Click += (se, ev) =>
                {
                    try
                    {
                        var t = TimeSpan.Parse(h);
                        clock.Value = cal.SelectionStart.Date.Add(t);
                    }
                    catch { }
                };
                quickFlow.Controls.Add(chip);
            }
            dlg.Controls.Add(quickFlow);

            // Preview
            var lblPreview = new Label
            {
                Location = new Point(18, 268), Size = new Size(440, 24),
                Font = new Font("Segoe UI Semibold", 9F), ForeColor = UiTheme.Foreground,
                TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true
            };
            dlg.Controls.Add(lblPreview);

            void UpdatePreview()
            {
                try
                {
                    var val = cal.SelectionStart.Date.Add(clock.Value.TimeOfDay);
                    lblPreview.Text = "Срок: " + val.ToString("ddd, dd.MM.yyyy  HH:mm");
                }
                catch { }
            }

            cal.DateChanged += (se, ev) => UpdatePreview();
            clock.ValueChanged += (se, ev) => UpdatePreview();

            // Buttons
            var btnOk = UiButtons.MakeCompactFilled("OK", 100);
            btnOk.Location = new Point(476 - 233, 317);
            btnOk.Click += (se, ev) => dlg.DialogResult = DialogResult.OK;
            dlg.Controls.Add(btnOk);

            var btnCancel = UiButtons.MakeCompactFilled("Отмена", 100, bgOverride: UiTheme.MutedForeground);
            btnCancel.Location = new Point(476 - 125, 317);
            btnCancel.Click += (se, ev) => dlg.DialogResult = DialogResult.Cancel;
            dlg.Controls.Add(btnCancel);

            var hiddenOk = new Button { DialogResult = DialogResult.OK, Visible = false, Location = new Point(-100, -100), Size = new Size(1, 1) };
            dlg.Controls.Add(hiddenOk);
            var hiddenCancel = new Button { DialogResult = DialogResult.Cancel, Visible = false, Location = new Point(-100, -100), Size = new Size(1, 1) };
            dlg.Controls.Add(hiddenCancel);
            dlg.AcceptButton = hiddenOk;
            dlg.CancelButton = hiddenCancel;
            dlg.KeyPreview = true;
            dlg.KeyDown += (se, ev) => { if (ev.KeyCode == Keys.Escape) { ev.Handled = true; dlg.DialogResult = DialogResult.Cancel; } };

            // Drag via header
            bool drag = false; Point off = Point.Empty;
            header.MouseDown += (se, ev) => { drag = true; off = ev.Location; };
            header.MouseMove += (se, ev) => { if (drag) dlg.Location = new Point(dlg.Location.X + ev.X - off.X, dlg.Location.Y + ev.Y - off.Y); };
            header.MouseUp += (se, ev) => drag = false;

            UpdatePreview();

            if (UiModal.ShowDimmed(owner, dlg) != DialogResult.OK) return null;
            return cal.SelectionStart.Date.Add(clock.Value.TimeOfDay);
        }
    }
}