using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using ShadowRDP.Models;
using ShadowRDP.Services;

namespace ShadowRDP.Helpers
{
    // Диалог "Расписание": выбор режима повтора, времени и дней/числа.
    // Возвращает true, если пользователь сохранил расписание (TodoStore уже обновлён).
    public static class TodoScheduleDialog
    {
        private static readonly string[] DayNames = { "Пн", "Вт", "Ср", "Чт", "Пт", "Сб", "Вс" };
        // DayNames[i] -> DayOfWeek int (0=Вс..6=Сб): Пн=1..Сб=6, Вс=0
        private static readonly int[] DayIdx = { 1, 2, 3, 4, 5, 6, 0 };

        public static bool Show(IWin32Window owner, TodoItem item, TodoRepeat preset)
        {
            if (item == null) return false;
            var repeat = preset == TodoRepeat.None
                ? (item.Repeat != TodoRepeat.None ? item.Repeat : TodoRepeat.Daily)
                : preset;
            var selectedDays = new List<int>();
            if (item.Repeat == TodoRepeat.Weekly) selectedDays.AddRange(item.RepeatDays);
            int dayOfMonth = item.Repeat == TodoRepeat.Monthly && item.RepeatDayOfMonth >= 1
                ? item.RepeatDayOfMonth
                : Math.Clamp(DateTime.Today.Day, 1, 28);
            var pickedTime = item.DueAt ?? DateTime.Now.AddHours(1);

            var dlg = new EscCloseForm
            {
                Text = "Расписание",
                ClientSize = new Size(560, 352),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                BackColor = UiTheme.Card,
                Font = new Font("Segoe UI", 9F)
            };

            // v3.9.3CY — шапка диалога: DialogHeaderBg.
            var header = new Panel { Location = new Point(0, 0), Size = new Size(560, 64), BackColor = UiTheme.DialogHeaderBg };
            dlg.Controls.Add(header);
            var lblTitle = new Label { Text = "Расписание", Location = new Point(20, 12), AutoSize = true, Font = new Font("Segoe UI Semibold", 12F), ForeColor = UiTheme.DialogHeaderFg, BackColor = Color.Transparent };
            header.Controls.Add(lblTitle);
            var taskBrief = item.Text.Length > 44 ? item.Text.Substring(0, 44) + "..." : item.Text;
            var lblSub = new Label { Text = taskBrief, Location = new Point(20, 36), AutoSize = true, Font = new Font("Segoe UI", 8.5F), ForeColor = Color.FromArgb(0xC8, 0xC4, 0xC0), BackColor = Color.Transparent };
            header.Controls.Add(lblSub);
            var btnCloseX = new Label { Text = "✕", Location = new Point(520, 18), Size = new Size(24, 24), TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 11F), ForeColor = Color.FromArgb(0xC8, 0xC4, 0xC0), Cursor = Cursors.Hand, BackColor = Color.Transparent };
            header.Controls.Add(btnCloseX);

            var lblMode = new Label { Text = "Повтор", Location = new Point(20, 80), AutoSize = true, Font = new Font("Segoe UI Semibold", 8F, FontStyle.Bold), ForeColor = UiTheme.MutedForeground, BackColor = Color.Transparent };
            dlg.Controls.Add(lblMode);

            var modePanel = new FlowLayoutPanel { Location = new Point(16, 100), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.Transparent };
            dlg.Controls.Add(modePanel);

            var lblTime = new Label { Text = "Время", Location = new Point(20, 144), AutoSize = true, Font = new Font("Segoe UI Semibold", 8F, FontStyle.Bold), ForeColor = UiTheme.MutedForeground, BackColor = Color.Transparent };
            dlg.Controls.Add(lblTime);

            var dt = new EscDateTimePicker
            {
                Location = new Point(20, 162), Size = new Size(140, 26), Format = DateTimePickerFormat.Custom,
                CustomFormat = "HH:mm", ShowUpDown = true, Value = pickedTime
            };
            dlg.Controls.Add(dt);

            var dayLabel = new Label { Text = "Дни недели", Location = new Point(180, 144), AutoSize = true, Font = new Font("Segoe UI Semibold", 8F, FontStyle.Bold), ForeColor = UiTheme.MutedForeground, BackColor = Color.Transparent };
            dlg.Controls.Add(dayLabel);
            var dayPanel = new FlowLayoutPanel { Location = new Point(176, 160), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.Transparent };
            dlg.Controls.Add(dayPanel);

            var monthLabel = new Label { Text = "Число месяца", Location = new Point(360, 144), AutoSize = true, Font = new Font("Segoe UI Semibold", 8F, FontStyle.Bold), ForeColor = UiTheme.MutedForeground, BackColor = Color.Transparent };
            dlg.Controls.Add(monthLabel);
            var nud = new EscNumericUpDown { Location = new Point(364, 160), Size = new Size(70, 26), Minimum = 1, Maximum = 31, Value = Math.Clamp(dayOfMonth, 1, 31) };
            dlg.Controls.Add(nud);
            var lblHint = new Label { Text = "Раз в месяц (31) = последний день месяца", Location = new Point(360, 188), AutoSize = true, Font = new Font("Segoe UI", 7.5F), ForeColor = UiTheme.MutedForeground, BackColor = Color.Transparent };
            dlg.Controls.Add(lblHint);

            var lblPreview = new Label { Location = new Point(20, 236), Size = new Size(520, 24), Font = new Font("Segoe UI Semibold", 9F), ForeColor = UiTheme.Foreground, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            dlg.Controls.Add(lblPreview);

            var btnSave = UiButtons.MakeCompactFilled("Сохранить", 120);
            btnSave.Location = new Point(560 - 273, 305);
            dlg.Controls.Add(btnSave);
            var btnCancel = UiButtons.MakeCompactFilled("Отмена", 120, bgOverride: UiTheme.MutedForeground);
            btnCancel.Location = new Point(560 - 145, 305);
            dlg.Controls.Add(btnCancel);

            var hiddenOk = new Button { DialogResult = DialogResult.OK, Visible = false, Location = new Point(-100, -100), Size = new Size(1, 1) };
            dlg.Controls.Add(hiddenOk);
            var hiddenCancel = new Button { DialogResult = DialogResult.Cancel, Visible = false, Location = new Point(-100, -100), Size = new Size(1, 1) };
            dlg.Controls.Add(hiddenCancel);
            dlg.AcceptButton = hiddenOk;
            dlg.CancelButton = hiddenCancel;
            // явный Esc — гарантия закрытия по любой раскладке клавиатуры
            dlg.KeyPreview = true;
            dlg.KeyDown += (se, ev) => { if (ev.KeyCode == Keys.Escape) { ev.Handled = true; dlg.DialogResult = DialogResult.Cancel; } };

            List<Label> modePills = new();
            List<Label> dayPills = new();

            int ModeIndex(TodoRepeat r) => r switch
            {
                TodoRepeat.Daily => 0,
                TodoRepeat.Weekly => 1,
                _ => 2
            };

            DateTime NextAt()
            {
                var now = DateTime.Now;
                int h = dt.Value.Hour, m = dt.Value.Minute;
                switch (repeat)
                {
                    case TodoRepeat.Daily:
                        var d = now.Date.AddHours(h).AddMinutes(m);
                        return d <= now ? d.AddDays(1) : d;
                    case TodoRepeat.Weekly:
                        for (int i = 0; i < 8; i++)
                        {
                            var c = now.Date.AddDays(i).AddHours(h).AddMinutes(m);
                            if (c > now && selectedDays.Contains((int)c.DayOfWeek)) return c;
                        }
                        return now.Date.AddDays(7).AddHours(h).AddMinutes(m);
                    case TodoRepeat.Monthly:
                        int target = Math.Clamp((int)nud.Value, 1, 31);
                        var cand = new DateTime(now.Year, now.Month, Math.Min(target, DateTime.DaysInMonth(now.Year, now.Month)), h, m, 0);
                        if (cand <= now)
                        {
                            var nxt = now.AddMonths(1);
                            cand = new DateTime(nxt.Year, nxt.Month, Math.Min(target, DateTime.DaysInMonth(nxt.Year, nxt.Month)), h, m, 0);
                        }
                        return cand;
                    default:
                        var t = now.Date.AddHours(h).AddMinutes(m);
                        return t <= now ? t.AddDays(1) : t;
                }
            }

            void RefreshModeButtons()
            {
                for (int i = 0; i < modePills.Count; i++) UiPills.Apply(modePills[i], i == ModeIndex(repeat));
                bool weekly = repeat == TodoRepeat.Weekly;
                bool monthly = repeat == TodoRepeat.Monthly;
                dayLabel.Visible = dayPanel.Visible = weekly;
                monthLabel.Visible = nud.Visible = lblHint.Visible = monthly;
            }

            void RefreshDayPills()
            {
                for (int i = 0; i < dayPills.Count; i++) UiPills.Apply(dayPills[i], selectedDays.Contains(DayIdx[i]));
            }

            void UpdatePreview()
            {
                try { lblPreview.Text = "Следующий раз: " + NextAt().ToString("dd.MM.yyyy HH:mm"); } catch { }
            }

            foreach (var (name, mode) in new[] { ("Каждый день", TodoRepeat.Daily), ("Каждую неделю", TodoRepeat.Weekly), ("Раз в месяц", TodoRepeat.Monthly) })
            {
                var pill = UiPills.Create(name, mode == repeat);
                var md = mode;
                pill.Click += (se, ev) => { repeat = md; RefreshModeButtons(); UpdatePreview(); };
                modePills.Add(pill);
                modePanel.Controls.Add(pill);
            }

            for (int i = 0; i < DayNames.Length; i++)
            {
                int dint = DayIdx[i];
                var pill = UiPills.Create(DayNames[i], selectedDays.Contains(dint));
                pill.Click += (se, ev) =>
                {
                    if (selectedDays.Contains(dint)) selectedDays.Remove(dint);
                    else selectedDays.Add(dint);
                    RefreshDayPills();
                    UpdatePreview();
                };
                dayPills.Add(pill);
                dayPanel.Controls.Add(pill);
            }

            dt.ValueChanged += (se, ev) => UpdatePreview();
            nud.ValueChanged += (se, ev) => UpdatePreview();
            btnSave.Click += (se, ev) => dlg.DialogResult = DialogResult.OK;
            btnCancel.Click += (se, ev) => dlg.DialogResult = DialogResult.Cancel;
            btnCloseX.Click += (se, ev) => dlg.DialogResult = DialogResult.Cancel;
            btnCloseX.MouseEnter += (se, ev) => btnCloseX.ForeColor = Color.White;
            btnCloseX.MouseLeave += (se, ev) => btnCloseX.ForeColor = Color.FromArgb(0xC8, 0xC4, 0xC0);

            bool drag = false; Point off = Point.Empty;
            header.MouseDown += (se, ev) => { drag = true; off = ev.Location; };
            header.MouseMove += (se, ev) => { if (drag) dlg.Location = new Point(dlg.Location.X + ev.X - off.X, dlg.Location.Y + ev.Y - off.Y); };
            header.MouseUp += (se, ev) => drag = false;

            RefreshModeButtons();
            UpdatePreview();

            bool ok;
            using (dlg) ok = UiModal.ShowDimmed(owner, dlg) == DialogResult.OK;
            if (!ok) return false;

            if (repeat == TodoRepeat.Weekly && selectedDays.Count == 0)
            {
                MessageBox.Show(owner, "Выбери хотя бы один день недели.", "Расписание", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }

            var due = NextAt();
            TodoStore.ApplySchedule(item.Id, repeat, selectedDays, (int)nud.Value, due);
            return true;
        }
    }
}