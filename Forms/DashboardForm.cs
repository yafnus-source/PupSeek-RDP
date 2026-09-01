using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using ShadowRDP.Helpers;
using ShadowRDP.Models;
using ShadowRDP.Services;

namespace ShadowRDP.Forms
{
    public class DashboardForm : Form
    {
        private readonly DataGridView _grid = new DataGridView();
        private FlowLayoutPanel _hostPanel = new FlowLayoutPanel();
        private Label _status = new Label();
        private Label _title = new Label();
        private FlowLayoutPanel _filterLevel = new FlowLayoutPanel();
        private FlowLayoutPanel _filterPeriod = new FlowLayoutPanel();
        private List<DashboardEvent> _all = new List<DashboardEvent>();
        private string _level = "Все";
        private string _period = "2 дня";
        private Dictionary<string, Label> _levelPills = new Dictionary<string, Label>();
        private Dictionary<string, Label> _periodPills = new Dictionary<string, Label>();
        private System.Windows.Forms.Timer _pollUiTimer = new System.Windows.Forms.Timer();

        public DashboardForm()
        {
            Text = "Дашбоард событий";
            BackColor = UiTheme.Background;
            FormBorderStyle = FormBorderStyle.None;
            Dock = DockStyle.Fill;

            // Header band
            var header = new Panel { Dock = DockStyle.Top, Height = 48, BackColor = UiTheme.HeaderBar };
            _title = new Label { Text = "ДАШБОРД СОБЫТИЙ", Font = new Font("Segoe UI Semibold", 11f), ForeColor = Color.White, BackColor = Color.Transparent, AutoSize = true, Location = new Point(16, 14) };
            header.Controls.Add(_title);

            var btnRefresh = UiButtons.MakeCompactFilled("Обновить", 90);
            btnRefresh.Location = new Point(220, 13);
            btnRefresh.Click += async (s, e) => await RefreshNowAsync();
            header.Controls.Add(btnRefresh);

            var btnInstallAi = UiButtons.MakeCompactFilled("Установка ИИ", 110);
            btnInstallAi.Location = new Point(btnRefresh.Right + 8, 13);
            btnInstallAi.Click += (s, e) => OllamaInstaller.ShowInstallGuide(this);
            header.Controls.Add(btnInstallAi);
            Task.Run(async () => { var ok = await EventDecoderService.IsOllamaAvailableAsync(); try { BeginInvoke(new Action(() => btnInstallAi.Visible = !ok)); } catch { } });

            var btnSettings = UiButtons.MakeCompactFilled("Настройки", 90);
            btnSettings.Location = new Point(btnInstallAi.Right + 8, 13);
            btnSettings.Click += (s, e) => ShowSettings();
            header.Controls.Add(btnSettings);

            Controls.Add(header);

            // Filter pills area
            var filterPanel = new Panel { Dock = DockStyle.Top, Height = 56, BackColor = UiTheme.Card, Padding = new Padding(12, 8, 12, 8) };
            _filterLevel = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.Transparent };
            foreach (var lv in new[] { "Все", "Critical", "Error" })
            {
                var pill = CreatePill(lv, lv == _level);
                pill.Click += (s, e) => { _level = lv; UpdatePills(); ApplyFilters(); };
                _levelPills[lv] = pill;
                _filterLevel.Controls.Add(pill);
            }
            filterPanel.Controls.Add(_filterLevel);

            _filterPeriod = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.Transparent };
            foreach (var p in new[] { "30 мин", "1 час", "24 часа", "2 дня" })
            {
                var pill = CreatePill(p, p == _period);
                pill.Click += (s, e) => { _period = p; UpdatePills(); ApplyFilters(); };
                _periodPills[p] = pill;
                _filterPeriod.Controls.Add(pill);
            }
            filterPanel.Controls.Add(_filterPeriod);
            Controls.Add(filterPanel);

            // Host bar
            _hostPanel = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, BackColor = UiTheme.Background, Padding = new Padding(12, 6, 12, 6), AutoSize = false };
            Controls.Add(_hostPanel);

            var sep = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = UiTheme.BorderSubtle };
            Controls.Add(sep);

            // Grid — first row was clipping under hostBar due to Fill/Top z-order; separator fixes it
            _grid.Dock = DockStyle.Fill;
            _grid.BackgroundColor = UiTheme.Card;
            _grid.BorderStyle = BorderStyle.None;
            _grid.GridColor = UiTheme.BorderSubtle;
            _grid.RowHeadersVisible = false;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.AllowUserToResizeRows = false;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _grid.ReadOnly = true;
            _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
            _grid.ColumnHeadersHeight = 28;
            _grid.EnableHeadersVisualStyles = false;
            _grid.ColumnHeadersDefaultCellStyle.BackColor = UiTheme.HeaderBar;
            _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
            _grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI Semibold", 8.5f);
            _grid.DefaultCellStyle.Font = new Font("Segoe UI", 8.5f);
            _grid.DefaultCellStyle.BackColor = UiTheme.Card;
            _grid.AlternatingRowsDefaultCellStyle.BackColor = UiTheme.RowAlt;

            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Time", HeaderText = "Время", Width = 130 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Level", HeaderText = "Уровень", Width = 80 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Source", HeaderText = "Источник", Width = 120 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "EventId", HeaderText = "ID", Width = 60 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Computer", HeaderText = "Хост", Width = 110 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Message", HeaderText = "Сообщение", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            var btnCol = new DataGridViewButtonColumn { Name = "Decode", HeaderText = "", Width = 110, Text = "Расшифровать", UseColumnTextForButtonValue = false };
            _grid.Columns.Add(btnCol);
            var filler = new DataGridViewTextBoxColumn { Name = "_filler", HeaderText = "", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 1, MinimumWidth = 2 };
            _grid.Columns.Add(filler); // not used, placeholder

            _grid.CellClick += OnCellClick;
            _grid.CellPainting += OnCellPainting;

            // Status bar — add BEFORE grid so Fill docks between hostBar and status
            var statusPanel = new Panel { Dock = DockStyle.Bottom, Height = 24, BackColor = UiTheme.Card };
            _status = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI", 8f), ForeColor = UiTheme.MutedForeground, Padding = new Padding(12, 0, 0, 0) };
            statusPanel.Controls.Add(_status);
            Controls.Add(statusPanel);

            Controls.Add(_grid);

            Load += async (s, e) => { await LoadEventsAsync(); EventPollerService.OnNewEvents += OnNewEvents; EventPollerService.OnCritical += OnCritical; };
            FormClosed += (s, e) => { EventPollerService.OnNewEvents -= OnNewEvents; EventPollerService.OnCritical -= OnCritical; };
        }

        private void UpdatePills()
        {
            foreach (var kv in _levelPills) ApplyPillStyle(kv.Value, kv.Key == _level);
            foreach (var kv in _periodPills) ApplyPillStyle(kv.Value, kv.Key == _period);
        }

        private Label CreatePill(string text, bool active)
        {
            var lbl = new Label { Text = text, AutoSize = true, Font = active ? new Font("Segoe UI Semibold", 9f) : new Font("Segoe UI", 9f), TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand, Padding = new Padding(10, 6, 10, 6), Margin = new Padding(4, 0, 4, 0) };
            ApplyPillStyle(lbl, active);
            // rounded pill background
            lbl.Paint += (s, e) =>
            {
                if (lbl.BackColor == Color.Transparent) return;
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var path = UiButtons.RoundedRect(new Rectangle(0, 0, lbl.Width - 1, lbl.Height - 1), 8);
                using var brush = new SolidBrush(lbl.BackColor);
                e.Graphics.FillPath(brush, path);
                TextRenderer.DrawText(e.Graphics, lbl.Text, lbl.Font, lbl.ClientRectangle, lbl.ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            };
            return lbl;
        }

        private void ApplyPillStyle(Label lbl, bool active)
        {
            if (active) { lbl.BackColor = UiTheme.PillActiveBg; lbl.ForeColor = UiTheme.PillActiveFg; lbl.Font = new Font("Segoe UI Semibold", 9f); }
            else { lbl.BackColor = Color.Transparent; lbl.ForeColor = UiTheme.MutedForeground; lbl.Font = new Font("Segoe UI", 9f); }
            lbl.Invalidate();
        }

        private async Task LoadEventsAsync()
        {
            _all = EventDashboardStore.LoadEvents();
            RebuildHostBar();
            ApplyFilters();
            UpdateStatus();
            // if no events and hosts monitored, trigger poll
            if (_all.Count == 0 && EventDashboardStore.GetMonitoredComputers().Count > 0)
                await RefreshNowAsync();
        }

        private void RebuildHostBar()
        {
            _hostPanel.Controls.Clear();
            var hosts = EventDashboardStore.GetMonitoredComputers();
            if (hosts.Count == 0)
            {
                var lbl = new Label { Text = "Нет отслеживаемых хостов — добавьте в Книге ПКМ → Отслеживать события", AutoSize = true, ForeColor = UiTheme.MutedForeground, Font = new Font("Segoe UI", 8.5f) };
                _hostPanel.Controls.Add(lbl);
                return;
            }
            var counts = _all.GroupBy(e => e.Computer, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            var critCounts = _all.Where(e => e.Level == "Critical").GroupBy(e => e.Computer, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            foreach (var h in hosts)
            {
                int total = counts.TryGetValue(h, out var c) ? c : 0;
                int crit = critCounts.TryGetValue(h, out var cc) ? cc : 0;
                bool hasCrit = crit > 0;
                bool hasAny = total > 0;

                var tab = new Label
                {
                    Text = "",
                    AutoSize = false,
                    Size = new Size(150, 26),
                    Font = new Font("Segoe UI Semibold", 8.5f),
                    TextAlign = ContentAlignment.MiddleCenter,
                    Cursor = Cursors.Hand,
                    Margin = new Padding(0, 0, 4, 0),
                    BackColor = Color.Transparent,
                    ForeColor = Color.White
                };
                Color tabBg = hasCrit ? Color.FromArgb(0xC0, 0x39, 0x2B) : hasAny ? Color.FromArgb(0xE6, 0x7E, 0x22) : UiTheme.Card;
                Color tabFg = hasAny ? Color.White : UiTheme.Foreground;
                Color tabBorder = hasAny ? tabBg : UiTheme.BorderSubtle;
                string tabText = total > 0 ? $"{h}  • {total}" : h;
                tab.Text = tabText;
                tab.ForeColor = tabFg;
                tab.Paint += (s, e) =>
                {
                    e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    var r = new Rectangle(0, 0, tab.Width - 1, tab.Height - 1);
                    using var path = UiButtons.TabRect(r, 6);
                    using var brush = new SolidBrush(tabBg);
                    using var pen = new Pen(tabBorder, 1);
                    e.Graphics.FillPath(brush, path);
                    e.Graphics.DrawPath(pen, path);
                    TextRenderer.DrawText(e.Graphics, tabText, tab.Font, r, tabFg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                };
                tab.Click += (s, e) => { /* quick filter to this host could be added */ };
                _hostPanel.Controls.Add(tab);
            }
            // add diagnostic hint if no events
            if (_all.Count == 0)
            {
                var hint = new Label { Text = "  • Событий пока нет — нажмите «Обновить» или проверьте WMI-доступ к хостам", AutoSize = true, ForeColor = UiTheme.MutedForeground, Font = new Font("Segoe UI", 8f), Margin = new Padding(12, 0, 0, 0) };
                _hostPanel.Controls.Add(hint);
            }
        }

        private void ApplyFilters()
        {
            var now = DateTime.Now;
            TimeSpan periodSpan = TimeSpan.FromDays(2);
            if (_period == "30 мин") periodSpan = TimeSpan.FromMinutes(30);
            else if (_period == "1 час") periodSpan = TimeSpan.FromHours(1);
            else if (_period == "24 часа") periodSpan = TimeSpan.FromHours(24);
            var cutoff = now - periodSpan;

            var filtered = _all.Where(e => e.Time >= cutoff).Where(e => _level == "Все" || string.Equals(e.Level, _level, StringComparison.OrdinalIgnoreCase)).OrderByDescending(e => e.Time).ToList();

            _grid.Rows.Clear();
            foreach (var ev in filtered.Take(500))
            {
                int idx = _grid.Rows.Add(ev.Time.ToString("dd.MM HH:mm"), ev.Level, ev.Source, ev.EventId.ToString(), ev.Computer, ev.Message, string.IsNullOrWhiteSpace(ev.Decoded) ? "Расшифровать" : "Показать");
                _grid.Rows[idx].Tag = ev;
            }
            UpdateStatus(filtered.Count);
        }

        private void UpdateStatus(int shown = -1)
        {
            var total = _all.Count;
            var crit = _all.Count(e => e.Level == "Critical");
            var err = _all.Count(e => e.Level == "Error");
            var hosts = EventDashboardStore.GetMonitoredComputers().Count;
            var shownStr = shown >= 0 ? $"Показано: {shown} | " : "";
            var settings = EventDashboardStore.LoadSettings();
            _status.Text = $"{shownStr}Всего: {total} | Critical: {crit} | Error: {err} | Хостов: {hosts} | Обновлено: {DateTime.Now:HH:mm} | Следующее: {DateTime.Now.AddMinutes(settings.PollIntervalMinutes):HH:mm}";
        }

        private async Task RefreshNowAsync()
        {
            _title.Text = "ДАШБОРД — обновление...";
            await EventPollerService.PollOnceAsync();
            _all = EventDashboardStore.LoadEvents();
            ApplyFilters();
            RebuildHostBar();
            _title.Text = "ДАШБОРД СОБЫТИЙ";
        }

        private void ShowSettings()
        {
            using var dlg = new Form { Text = "Настройки дашборда", Size = new Size(380, 260), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false };
            var settings = EventDashboardStore.LoadSettings();
            var chkCrit = new CheckBox { Text = "Уведомлять о Critical", Checked = settings.NotifyCritical, Location = new Point(16, 20), AutoSize = true };
            var chkErr = new CheckBox { Text = "Уведомлять о Error", Checked = settings.NotifyError, Location = new Point(16, 50), AutoSize = true };
            var chkSound = new CheckBox { Text = "Звук", Checked = settings.NotifySound, Location = new Point(16, 80), AutoSize = true };
            var lblInterval = new Label { Text = "Интервал (мин):", Location = new Point(16, 115), AutoSize = true };
            var numInterval = new NumericUpDown { Value = settings.PollIntervalMinutes, Minimum = 5, Maximum = 240, Location = new Point(130, 113), Width = 60 };
            var lblRet = new Label { Text = "Хранение (дн):", Location = new Point(16, 145), AutoSize = true };
            var numRet = new NumericUpDown { Value = settings.RetentionDays, Minimum = 1, Maximum = 7, Location = new Point(130, 143), Width = 60 };
            var btnOk = new Button { Text = "Сохранить", DialogResult = DialogResult.OK, Location = new Point(200, 180) };
            var btnCancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, Location = new Point(280, 180) };
            dlg.Controls.AddRange(new Control[] { chkCrit, chkErr, chkSound, lblInterval, numInterval, lblRet, numRet, btnOk, btnCancel });
            dlg.AcceptButton = btnOk; dlg.CancelButton = btnCancel;
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                settings.NotifyCritical = chkCrit.Checked;
                settings.NotifyError = chkErr.Checked;
                settings.NotifySound = chkSound.Checked;
                settings.PollIntervalMinutes = (int)numInterval.Value;
                settings.RetentionDays = (int)numRet.Value;
                EventDashboardStore.SaveSettings(settings);
                EventPollerService.Restart();
            }
        }

        private async void OnCellClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex != _grid.Columns["Decode"].Index) return;
            var ev = _grid.Rows[e.RowIndex].Tag as DashboardEvent;
            if (ev == null) return;
            _grid.Rows[e.RowIndex].Cells["Decode"].Value = "...";
            string decoded = await EventDecoderService.DecodeAsync(ev);
            MessageBox.Show(this, decoded, $"Событие {ev.EventId} — {ev.Source}", MessageBoxButtons.OK, MessageBoxIcon.Information);
            _grid.Rows[e.RowIndex].Cells["Decode"].Value = "Показать";
        }

        private void OnCellPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex != _grid.Columns["Level"].Index) return;
            e.Handled = true;
            e.PaintBackground(e.CellBounds, true);
            var level = e.Value?.ToString() ?? "";
            Color bg = level == "Critical" ? Color.FromArgb(0xC0, 0x39, 0x2B) : Color.FromArgb(0xE6, 0x7E, 0x22);
            if (level == "Все") bg = UiTheme.PillActiveBg;
            var rect = new Rectangle(e.CellBounds.X + 6, e.CellBounds.Y + 4, e.CellBounds.Width - 12, e.CellBounds.Height - 8);
            using var path = UiButtons.RoundedRect(rect, 6);
            using var brush = new SolidBrush(bg);
            e.Graphics.FillPath(brush, path);
            TextRenderer.DrawText(e.Graphics, level, new Font("Segoe UI Semibold", 8f), rect, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        private void OnNewEvents(List<DashboardEvent> list) => BeginInvoke(new Action(() => { _all = EventDashboardStore.LoadEvents(); ApplyFilters(); }));
        private void OnCritical(DashboardEvent ev)
        {
            try
            {
                var settings = EventDashboardStore.LoadSettings();
                if (!settings.NotifyCritical && ev.Level == "Critical") return;
                if (!settings.NotifyError && ev.Level == "Error") return;
                // mark notified
                var all = EventDashboardStore.LoadEvents();
                var idx = all.FindIndex(x => x.Computer == ev.Computer && x.EventId == ev.EventId && x.Time == ev.Time);
                if (idx >= 0 && !all[idx].Notified) { all[idx].Notified = true; EventDashboardStore.SaveEvents(all); }
            }
            catch { }
        }
    }
}
