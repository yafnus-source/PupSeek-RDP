using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using ShadowRDP.Helpers;
using ShadowRDP.Models;
using ShadowRDP.Services;

namespace ShadowRDP.Forms
{
    // v3.9.3CZ — окно «Новая задача»: текст, дата/время (календарь),
    // повтор (не/день/неделя/месяц + дни/число), оповещение за
    // 10/15/20/30 минут + повтор оповещений.
    // v3.9.3DA — тот же диалог для редактирования (Edit): поля
    // предзаполняются из задачи, сохранение — через TodoStore.Update.
    public class TodoTaskDialog : Form
    {
        private static readonly string[] DayNames = { "Пн", "Вт", "Ср", "Чт", "Пт", "Сб", "Вс" };
        private static readonly int[] DayIdx = { 1, 2, 3, 4, 5, 6, 0 };
        private static readonly int[] RemindMinutes = { 0, 10, 15, 20, 30 };

        private readonly TextBox _tbText;
        private readonly DateTimePicker _dtDate;
        private readonly DateTimePicker _dtTime;
        private readonly NumericUpDown _nud;
        private readonly ComboBox _cbRemind;
        private readonly CheckBox _chkRepeatRem;
        private readonly Label _lblPreview;
        private readonly Label _dayLabel;
        private readonly FlowLayoutPanel _dayPanel;
        private readonly Label _monthLabel;
        private readonly List<Label> _modePills = new();
        private readonly List<Label> _dayPills = new();
        private TodoRepeat _repeat = TodoRepeat.None;
        private readonly List<int> _selectedDays = new();
        private readonly TodoItem? _editItem;

        public static bool Show(IWin32Window owner)
        {
            using var dlg = new TodoTaskDialog(null);
            return UiModal.ShowDimmed(owner, dlg) == DialogResult.OK;
        }

        public static bool Edit(IWin32Window owner, TodoItem item)
        {
            if (item == null) return false;
            using var dlg = new TodoTaskDialog(item);
            return UiModal.ShowDimmed(owner, dlg) == DialogResult.OK;
        }

        private TodoTaskDialog(TodoItem? editItem)
        {
            _editItem = editItem;
            bool isEdit = editItem != null;
            Text = isEdit ? "Редактировать задачу" : "Новая задача";
            ClientSize = new Size(560, 604);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Font = new Font("Segoe UI", 9F);
            BackColor = UiTheme.DialogBackground;

            var hero = UiModal.Hero(
                isEdit ? "Редактировать задачу" : "Новая задача",
                "Текст, срок, повторы и напоминания", "tasks");
            Controls.Add(hero);

            // ——— Карточка «Задача» ———
            var cardTask = UiModal.Card(12, 90, 536, 104, "Задача", "tasks", withHeader: false);
            Controls.Add(cardTask);
            _tbText = new TextBox
            {
                Multiline = true,
                Font = new Font("Segoe UI", 10F),
                ScrollBars = ScrollBars.Vertical
            };
            var wText = UiModal.Field(_tbText, 504, 56);
            wText.Location = new Point(16, 32);
            cardTask.Controls.Add(wText);
            cardTask.Controls.Add(UiModal.FieldLabel("Текст задачи", 16, 12));

            // ——— Карточка «Срок» ———
            var cardDue = UiModal.Card(12, 202, 536, 248, "Срок", "calendar", withHeader: false);
            Controls.Add(cardDue);
            cardDue.Controls.Add(UiModal.FieldLabel("Дата и время", 16, 12));
            _dtDate = new DateTimePicker
            {
                Location = new Point(16, 32),
                Size = new Size(150, 26),
                Format = DateTimePickerFormat.Short,
                Value = DateTime.Today.AddDays(1)
            };
            cardDue.Controls.Add(_dtDate);
            _dtTime = new DateTimePicker
            {
                Location = new Point(176, 32),
                Size = new Size(90, 26),
                Format = DateTimePickerFormat.Custom,
                CustomFormat = "HH:mm",
                ShowUpDown = true,
                Value = DateTime.Today.AddHours(9)
            };
            cardDue.Controls.Add(_dtTime);

            var lblMode = new Label { Text = "Повтор", Location = new Point(16, 68), AutoSize = true, Font = new Font("Segoe UI Semibold", 8F, FontStyle.Bold), ForeColor = UiTheme.MutedForeground, BackColor = Color.Transparent };
            cardDue.Controls.Add(lblMode);
            var modePanel = new FlowLayoutPanel { Location = new Point(16, 88), Size = new Size(504, 30), FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.Transparent };
            cardDue.Controls.Add(modePanel);

            _dayLabel = new Label { Text = "Дни недели", Location = new Point(16, 126), AutoSize = true, Font = new Font("Segoe UI Semibold", 8F, FontStyle.Bold), ForeColor = UiTheme.MutedForeground, BackColor = Color.Transparent };
            cardDue.Controls.Add(_dayLabel);
            _dayPanel = new FlowLayoutPanel { Location = new Point(16, 146), Size = new Size(504, 30), FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.Transparent };
            cardDue.Controls.Add(_dayPanel);

            _monthLabel = new Label { Text = "Число месяца (31 = последний день)", Location = new Point(16, 126), AutoSize = true, Font = new Font("Segoe UI Semibold", 8F, FontStyle.Bold), ForeColor = UiTheme.MutedForeground, BackColor = Color.Transparent };
            cardDue.Controls.Add(_monthLabel);
            _nud = new NumericUpDown { Location = new Point(16, 148), Size = new Size(70, 26), Minimum = 1, Maximum = 31, Value = Math.Clamp(DateTime.Today.Day, 1, 28) };
            cardDue.Controls.Add(_nud);

            _lblPreview = new Label { Location = new Point(16, 208), Size = new Size(504, 24), Font = new Font("Segoe UI Semibold", 9F), ForeColor = UiTheme.Foreground, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, BackColor = Color.Transparent };
            cardDue.Controls.Add(_lblPreview);

            // ——— Карточка «Оповещение» ———
            var cardRem = UiModal.Card(12, 458, 536, 84, "Оповещение", "bell", withHeader: false);
            Controls.Add(cardRem);
            cardRem.Controls.Add(UiModal.FieldLabel("Напомнить", 16, 12));
            _cbRemind = new ComboBox
            {
                Location = new Point(16, 34),
                Size = new Size(220, 26),
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Segoe UI", 10F),
                BackColor = Color.White
            };
            _cbRemind.Items.AddRange(new object[] {
                "В срок", "За 10 минут", "За 15 минут", "За 20 минут", "За 30 минут" });
            _cbRemind.SelectedIndex = 1;
            Helpers.UiCombo.Apply(_cbRemind);
            cardRem.Controls.Add(_cbRemind);
            _chkRepeatRem = new CheckBox
            {
                Text = "Повторять оповещение",
                Location = new Point(252, 37),
                AutoSize = true,
                Font = new Font("Segoe UI", 9F),
                ForeColor = UiTheme.Foreground,
                BackColor = Color.Transparent
            };
            cardRem.Controls.Add(_chkRepeatRem);

            var btnSave = UiButtons.MakeCompactFilled("Сохранить", 120);
            btnSave.Location = new Point(560 - 273, 556);
            Controls.Add(btnSave);
            var btnCancel = UiButtons.MakeCompactFilled("Отмена", 120, bgOverride: UiTheme.MutedForeground);
            btnCancel.Location = new Point(560 - 145, 556);
            Controls.Add(btnCancel);

            // ——— Пилы повтора и дней ———
            var modes = new[] { ("Не повторять", TodoRepeat.None), ("Каждый день", TodoRepeat.Daily), ("Каждую неделю", TodoRepeat.Weekly), ("Раз в месяц", TodoRepeat.Monthly) };
            foreach (var (name, mode) in modes)
            {
                var pill = UiPills.Create(name, mode == _repeat);
                var md = mode;
                pill.Click += (se, ev) => { _repeat = md; RefreshModeButtons(); UpdatePreview(); };
                _modePills.Add(pill);
                modePanel.Controls.Add(pill);
            }

            for (int i = 0; i < DayNames.Length; i++)
            {
                int dint = DayIdx[i];
                var pill = UiPills.Create(DayNames[i], false);
                pill.Click += (se, ev) =>
                {
                    if (_selectedDays.Contains(dint)) _selectedDays.Remove(dint);
                    else _selectedDays.Add(dint);
                    RefreshDayPills();
                    UpdatePreview();
                };
                _dayPills.Add(pill);
                _dayPanel.Controls.Add(pill);
            }

            _dtDate.ValueChanged += (se, ev) => UpdatePreview();
            _dtTime.ValueChanged += (se, ev) => UpdatePreview();
            _nud.ValueChanged += (se, ev) => UpdatePreview();
            btnSave.Click += (se, ev) => OnSave();
            btnCancel.Click += (se, ev) => DialogResult = DialogResult.Cancel;

            if (isEdit) Prefill(editItem!);
            RefreshModeButtons();
            RefreshDayPills();
            UpdatePreview();
        }

        private void Prefill(TodoItem it)
        {
            _tbText.Text = it.Text ?? "";
            if (it.DueAt.HasValue)
            {
                try { _dtDate.Value = it.DueAt.Value.Date; } catch { }
                try { _dtTime.Value = DateTime.Today.Add(it.DueAt.Value.TimeOfDay); } catch { }
            }
            _repeat = it.Repeat;
            _selectedDays.Clear();
            if (it.RepeatDays != null) _selectedDays.AddRange(it.RepeatDays.Where(d => d >= 0 && d <= 6).Distinct());
            if (it.RepeatDayOfMonth >= 1 && it.RepeatDayOfMonth <= 31)
                _nud.Value = it.RepeatDayOfMonth;
            int ri = Array.IndexOf(RemindMinutes, it.RemindMinutes);
            _cbRemind.SelectedIndex = ri >= 0 ? ri : 1;
            _chkRepeatRem.Checked = it.RepeatRemind;
        }

        private void OnSave()
        {
            if (string.IsNullOrWhiteSpace(_tbText.Text))
            {
                MessageBox.Show(this, "Введи текст задачи.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (_repeat == TodoRepeat.Weekly && _selectedDays.Count == 0)
            {
                MessageBox.Show(this, "Выбери хотя бы один день недели.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            int rem = (_cbRemind.SelectedIndex >= 0 && _cbRemind.SelectedIndex < RemindMinutes.Length)
                ? RemindMinutes[_cbRemind.SelectedIndex] : 10;
            var due = NextAt();
            if (_editItem == null)
            {
                TodoStore.AddTask(_tbText.Text, due, _repeat,
                    new List<int>(_selectedDays), (int)_nud.Value, rem, _chkRepeatRem.Checked);
            }
            else
            {
                var oldDue = _editItem.DueAt;
                _editItem.Text = _tbText.Text.Trim();
                _editItem.DueAt = due;
                _editItem.Repeat = _repeat;
                _editItem.RepeatDays = new List<int>(_selectedDays);
                _editItem.RepeatDayOfMonth = _repeat == TodoRepeat.Monthly ? Math.Clamp((int)_nud.Value, 1, 31) : 0;
                _editItem.RemindMinutes = rem;
                _editItem.RepeatRemind = _chkRepeatRem.Checked;
                if (oldDue != due)
                {
                    _editItem.Notified = false;
                    _editItem.LastRemindedAt = null;
                    _editItem.Status = TodoStatus.Active;
                }
                TodoStore.Update(_editItem);
            }
            DialogResult = DialogResult.OK;
        }

        private DateTime NextAt()
        {
            var now = DateTime.Now;
            int h = _dtTime.Value.Hour, m = _dtTime.Value.Minute;
            switch (_repeat)
            {
                case TodoRepeat.Daily:
                    var d = now.Date.AddHours(h).AddMinutes(m);
                    return d <= now ? d.AddDays(1) : d;
                case TodoRepeat.Weekly:
                    for (int i = 0; i < 8; i++)
                    {
                        var c = now.Date.AddDays(i).AddHours(h).AddMinutes(m);
                        if (c > now && _selectedDays.Contains((int)c.DayOfWeek)) return c;
                    }
                    return now.Date.AddDays(7).AddHours(h).AddMinutes(m);
                case TodoRepeat.Monthly:
                    int target = Math.Clamp((int)_nud.Value, 1, 31);
                    var cand = new DateTime(now.Year, now.Month, Math.Min(target, DateTime.DaysInMonth(now.Year, now.Month)), h, m, 0);
                    if (cand <= now)
                    {
                        var nxt = now.AddMonths(1);
                        cand = new DateTime(nxt.Year, nxt.Month, Math.Min(target, DateTime.DaysInMonth(nxt.Year, nxt.Month)), h, m, 0);
                    }
                    return cand;
                default:
                    return _dtDate.Value.Date.AddHours(h).AddMinutes(m);
            }
        }

        private void RefreshModeButtons()
        {
            var order = new[] { TodoRepeat.None, TodoRepeat.Daily, TodoRepeat.Weekly, TodoRepeat.Monthly };
            for (int i = 0; i < _modePills.Count; i++) UiPills.Apply(_modePills[i], order[i] == _repeat);
            bool weekly = _repeat == TodoRepeat.Weekly;
            bool monthly = _repeat == TodoRepeat.Monthly;
            _dayLabel.Visible = _dayPanel.Visible = weekly;
            _monthLabel.Visible = _nud.Visible = monthly;
        }

        private void RefreshDayPills()
        {
            for (int i = 0; i < _dayPills.Count; i++) UiPills.Apply(_dayPills[i], _selectedDays.Contains(DayIdx[i]));
        }

        private void UpdatePreview()
        {
            try
            {
                string r = _repeat == TodoRepeat.None ? "без повтора" : TodoSchedule.Mark(_repeat);
                _lblPreview.Text = "Срок: " + NextAt().ToString("dd.MM.yyyy HH:mm") + " · " + r;
            }
            catch { }
        }
    }
}
