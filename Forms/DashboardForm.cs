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
        private ProgressBar _progressBar = new ProgressBar();
        private FlowLayoutPanel _filterLevel = new FlowLayoutPanel();
        private FlowLayoutPanel _filterPeriod = new FlowLayoutPanel();
        private List<DashboardEvent> _all = new List<DashboardEvent>();
        private string _level = "Все";
        private string _period = "2 дня";
        private string _selectedHost = "";
        private Dictionary<string, Label> _levelPills = new Dictionary<string, Label>();
        private Dictionary<string, Label> _periodPills = new Dictionary<string, Label>();
        private System.Windows.Forms.Timer _pollUiTimer = new System.Windows.Forms.Timer();
        private int _lastHoverRow = -1, _lastHoverCol = -1;

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

            var sep = new Panel { Dock = DockStyle.Top, Height = 4, BackColor = UiTheme.BorderSubtle };
            Controls.Add(sep);

            // Grid — DoubleBuffered for smooth rendering
            _grid.Dock = DockStyle.Fill;
            _grid.BackgroundColor = UiTheme.Card;
            _grid.BorderStyle = BorderStyle.None;
            _grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            _grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
            _grid.RowHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            _grid.AdvancedCellBorderStyle.All = DataGridViewAdvancedCellBorderStyle.Single;
            _grid.AdvancedColumnHeadersBorderStyle.All = DataGridViewAdvancedCellBorderStyle.Single;
            _grid.GridColor = UiTheme.BorderSubtle;
            _grid.RowHeadersVisible = false;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.AllowUserToResizeRows = false;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _grid.ReadOnly = true;
            _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
            _grid.RowTemplate.Height = 29;
            _grid.ColumnHeadersHeight = 28;
            _grid.EnableHeadersVisualStyles = false;
            _grid.ColumnHeadersDefaultCellStyle.BackColor = UiTheme.HeaderBar;
            _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
            _grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI Semibold", 8.5f);
            _grid.DefaultCellStyle.Font = new Font("Segoe UI", 8.5f);
            _grid.DefaultCellStyle.BackColor = UiTheme.Card;
            _grid.AlternatingRowsDefaultCellStyle.BackColor = UiTheme.RowAlt;
            _grid.DefaultCellStyle.SelectionBackColor = UiTheme.AccentSoft;
            _grid.DefaultCellStyle.SelectionForeColor = UiTheme.Foreground;
            _grid.AlternatingRowsDefaultCellStyle.SelectionBackColor = UiTheme.AccentSoft;
            _grid.AlternatingRowsDefaultCellStyle.SelectionForeColor = UiTheme.Foreground;
            // Performance: enable double buffering via reflection
            typeof(DataGridView).InvokeMember("DoubleBuffered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.SetProperty, null, _grid, new object[] { true });

            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Time", HeaderText = "Время", Width = 130 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Level", HeaderText = "Уровень", Width = 80, MinimumWidth = 80 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Source", HeaderText = "Источник", Width = 120 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "EventId", HeaderText = "ID", Width = 60 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Computer", HeaderText = "Хост", Width = 110 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Message", HeaderText = "Сообщение", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            var btnCol = new DataGridViewButtonColumn { Name = "Decode", HeaderText = "", Width = 110, Text = "Расшифровать", UseColumnTextForButtonValue = false };
            _grid.Columns.Add(btnCol);

            _grid.CellClick += OnCellClick;
            _grid.CellPainting += OnCellPainting;
            _grid.MouseMove += (s, e) =>
            {
                var ht = _grid.HitTest(e.X, e.Y);
                if (ht.ColumnIndex == _grid.Columns["Decode"].Index && ht.RowIndex >= 0)
                {
                    if (ht.RowIndex != _lastHoverRow || ht.ColumnIndex != _lastHoverCol)
                    {
                        if (_lastHoverRow >= 0) _grid.InvalidateCell(_lastHoverCol, _lastHoverRow);
                        _lastHoverRow = ht.RowIndex; _lastHoverCol = ht.ColumnIndex;
                        _grid.InvalidateCell(ht.ColumnIndex, ht.RowIndex);
                    }
                }
                else if (_lastHoverRow >= 0)
                {
                    _grid.InvalidateCell(_lastHoverCol, _lastHoverRow);
                    _lastHoverRow = -1; _lastHoverCol = -1;
                }
            };

            // Status bar with progress bar
            var statusPanel = new Panel { Dock = DockStyle.Bottom, Height = 28, BackColor = UiTheme.Card };
            _progressBar = new ProgressBar { Dock = DockStyle.Top, Height = 3, Style = ProgressBarStyle.Continuous, Visible = false };
            _status = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI", 8f), ForeColor = UiTheme.MutedForeground, Padding = new Padding(12, 0, 0, 0) };
            statusPanel.Controls.Add(_status);
            statusPanel.Controls.Add(_progressBar);
            Controls.Add(statusPanel);

            Controls.Add(_grid);

            // fix Dock z-order: header topmost
            Controls.SetChildIndex(header, 5);
            Controls.SetChildIndex(filterPanel, 4);
            Controls.SetChildIndex(_hostPanel, 3);
            Controls.SetChildIndex(sep, 2);
            Controls.SetChildIndex(_grid, 1);
            Controls.SetChildIndex(statusPanel, 0);

            Load += async (s, e) => { await LoadEventsAsync(); EventPollerService.OnNewEvents += OnNewEvents; EventPollerService.OnCritical += OnCritical; EventPollerService.OnProgress += OnProgress; };
            FormClosed += (s, e) => { EventPollerService.OnNewEvents -= OnNewEvents; EventPollerService.OnCritical -= OnCritical; EventPollerService.OnProgress -= OnProgress; };
        }

        private void OnProgress(string host, int current, int total)
        {
            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (string.IsNullOrEmpty(host))
                    {
                        _title.Text = "ДАШБОРД СОБЫТИЙ";
                        _progressBar.Visible = false;
                        _progressBar.Value = 0;
                    }
                    else
                    {
                        _title.Text = $"ДАШБОРД — {host} ({current}/{total})";
                        _status.Text = $"Опрос {current} из {total}: {host}...";
                        _progressBar.Visible = true;
                        _progressBar.Maximum = total;
                        _progressBar.Value = current;
                    }
                }));
            }
            catch { }
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
                bool isActive = string.Equals(_selectedHost, h, StringComparison.OrdinalIgnoreCase);

                var tab = new Label
                {
                    Text = "",
                    AutoSize = false,
                    Size = new Size(130, 24),
                    Font = new Font("Segoe UI Semibold", 8.5f),
                    TextAlign = ContentAlignment.MiddleCenter,
                    Cursor = Cursors.Hand,
                    Margin = new Padding(0, 0, 2, 0),
                    BackColor = Color.Transparent,
                    ForeColor = Color.White
                };
                Color baseBg = hasCrit ? Color.FromArgb(0xB0, 0x7A, 0x72) : hasAny ? Color.FromArgb(0xB8, 0x9A, 0x7A) : UiTheme.Card;
                Color tabBg = isActive ? ControlPaint.Dark(baseBg, 0.15f) : baseBg;
                Color tabFg = hasAny ? Color.White : UiTheme.Foreground;
                Color tabBorder = isActive ? Color.FromArgb(0x48, 0x30, 0x2F) : hasAny ? tabBg : UiTheme.BorderSubtle;
                string tabText = total > 0 ? $"{h}  • {total}" : h;
                tab.Text = tabText;
                tab.ForeColor = tabFg;
                int borderW = isActive ? 2 : 1;
                tab.Paint += (s, e) =>
                {
                    e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    var r = new Rectangle(0, 0, tab.Width - 1, tab.Height - 1);
                    using var path = UiButtons.TabRect(r, 6);
                    using var brush = new SolidBrush(tabBg);
                    using var pen = new Pen(tabBorder, borderW);
                    e.Graphics.FillPath(brush, path);
                    e.Graphics.DrawPath(pen, path);
                    TextRenderer.DrawText(e.Graphics, tabText, tab.Font, r, tabFg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                };
                string captured = h;
                tab.Click += (s, e) =>
                {
                    _selectedHost = string.Equals(_selectedHost, captured, StringComparison.OrdinalIgnoreCase) ? "" : captured;
                    RebuildHostBar();
                    ApplyFilters();
                };
                tab.DoubleClick += (s, e) =>
                {
                    _selectedHost = "";
                    RebuildHostBar();
                    ApplyFilters();
                };
                _hostPanel.Controls.Add(tab);
            }
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

            var filtered = _all.Where(e => e.Time >= cutoff).Where(e => _level == "Все" || string.Equals(e.Level, _level, StringComparison.OrdinalIgnoreCase)).Where(e => string.IsNullOrEmpty(_selectedHost) || string.Equals(e.Computer, _selectedHost, StringComparison.OrdinalIgnoreCase)).OrderByDescending(e => e.Time).ToList();

            // Batch update — SuspendLayout + suspend painting for smooth render
            _grid.SuspendLayout();
            _grid.Rows.Clear();
            foreach (var ev in filtered.Take(500))
            {
                int idx = _grid.Rows.Add(ev.Time.ToString("dd.MM HH:mm"), ev.Level, ev.Source, ev.EventId.ToString(), ev.Computer, ev.Message, string.IsNullOrWhiteSpace(ev.Decoded) ? "Расшифровать" : "Показать");
                _grid.Rows[idx].Tag = ev;
            }
            _grid.ResumeLayout();
            if (_grid.Rows.Count > 0)
            {
                try { _grid.FirstDisplayedScrollingRowIndex = 0; _grid.ClearSelection(); } catch { }
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
            _status.Text = "Опрос хостов...";
            _progressBar.Visible = true;
            _progressBar.Style = ProgressBarStyle.Marquee;
            await EventPollerService.PollOnceAsync();
            _progressBar.Visible = false;
            _progressBar.Style = ProgressBarStyle.Continuous;
            _all = EventDashboardStore.LoadEvents();
            ApplyFilters();
            RebuildHostBar();
            _title.Text = "ДАШБОРД СОБЫТИЙ";
        }

        private void ShowSettings()
        {
            using var dlg = new Form { Text = "Настройки дашборда", Size = new Size(380, 260), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, BackColor = UiTheme.Card };
            var settings = EventDashboardStore.LoadSettings();
            var chkCrit = new CheckBox { Text = "Уведомлять о Critical", Checked = settings.NotifyCritical, Location = new Point(16, 20), AutoSize = true, ForeColor = UiTheme.Foreground };
            var chkErr = new CheckBox { Text = "Уведомлять о Error", Checked = settings.NotifyError, Location = new Point(16, 50), AutoSize = true, ForeColor = UiTheme.Foreground };
            var chkSound = new CheckBox { Text = "Звук", Checked = settings.NotifySound, Location = new Point(16, 80), AutoSize = true, ForeColor = UiTheme.Foreground };
            var lblInterval = new Label { Text = "Интервал (мин):", Location = new Point(16, 115), AutoSize = true, ForeColor = UiTheme.Foreground };
            var numInterval = new NumericUpDown { Value = settings.PollIntervalMinutes, Minimum = 5, Maximum = 240, Location = new Point(130, 113), Width = 60 };
            var lblRet = new Label { Text = "Хранение (дн):", Location = new Point(16, 145), AutoSize = true, ForeColor = UiTheme.Foreground };
            var numRet = new NumericUpDown { Value = settings.RetentionDays, Minimum = 1, Maximum = 7, Location = new Point(130, 143), Width = 60 };

            var btnOk = UiButtons.MakeCompactFilled("Сохранить", 100);
            btnOk.Location = new Point(190, 185);
            btnOk.Click += (s, e) => { dlg.DialogResult = DialogResult.OK; dlg.Close(); };

            var btnCancel = UiButtons.MakeCompactFilled("Отмена", 80, bgOverride: UiTheme.Muted);
            btnCancel.Location = new Point(300, 185);
            btnCancel.Click += (s, e) => { dlg.DialogResult = DialogResult.Cancel; dlg.Close(); };

            dlg.Controls.AddRange(new Control[] { chkCrit, chkErr, chkSound, lblInterval, numInterval, lblRet, numRet, btnOk, btnCancel });
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
            _grid.Rows[e.RowIndex].Cells["Decode"].Value = "Показать";
            MessageBox.Show(this, decoded, $"Событие {ev.EventId} — {ev.Source}", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void OnCellPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            // === Header cells: custom paint to override system #0078D7 ===
            if (e.RowIndex == -1 && e.ColumnIndex >= 0)
            {
                e.Handled = true;
                using var bgBrush = new SolidBrush(UiTheme.HeaderBar);
                e.Graphics.FillRectangle(bgBrush, e.CellBounds);
                var col = _grid.Columns[e.ColumnIndex];
                var headerText = col.HeaderText;
                int textW = TextRenderer.MeasureText(headerText, new Font("Segoe UI Semibold", 8.5f)).Width;
                int glyphSpace = col.HeaderCell.SortGlyphDirection != SortOrder.None ? 16 : 0;
                int textX = e.CellBounds.X + 8;
                int textWAvail = e.CellBounds.Width - 8 - glyphSpace;
                var textRect = new Rectangle(textX, e.CellBounds.Y, textWAvail, e.CellBounds.Height);
                TextRenderer.DrawText(e.Graphics, headerText, new Font("Segoe UI Semibold", 8.5f), textRect, Color.White, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                // Sort glyph — triangle in #626F5E
                if (col.HeaderCell.SortGlyphDirection != SortOrder.None)
                {
                    int gx = e.CellBounds.Right - 18;
                    int gy = e.CellBounds.Y + (e.CellBounds.Height - 8) / 2;
                    var glyphColor = Color.FromArgb(0x62, 0x6F, 0x5E);
                    using var glyphBrush = new SolidBrush(glyphColor);
                    if (col.HeaderCell.SortGlyphDirection == SortOrder.Ascending)
                    {
                        var pts = new[] { new Point(gx + 4, gy), new Point(gx + 8, gy + 6), new Point(gx, gy + 6) };
                        e.Graphics.FillPolygon(glyphBrush, pts);
                    }
                    else
                    {
                        var pts = new[] { new Point(gx, gy), new Point(gx + 8, gy), new Point(gx + 4, gy + 6) };
                        e.Graphics.FillPolygon(glyphBrush, pts);
                    }
                }
                return;
            }

            if (e.RowIndex < 0) return;

            // Level column — fixed-width badge (20% smaller)
            if (e.ColumnIndex == _grid.Columns["Level"].Index)
            {
                e.Handled = true;
                e.PaintBackground(e.CellBounds, true);
                var level = e.Value?.ToString() ?? "";
                Color bg = level == "Critical" ? Color.FromArgb(0xB0, 0x7A, 0x72) : level == "Error" ? Color.FromArgb(0xB8, 0x9A, 0x7A) : UiTheme.Muted;
                int badgeW = 46;
                int badgeH = e.CellBounds.Height - 10;
                int x = e.CellBounds.X + (e.CellBounds.Width - badgeW) / 2;
                int y = e.CellBounds.Y + (e.CellBounds.Height - badgeH) / 2;
                var rect = new Rectangle(x, y, badgeW, badgeH);
                using var path = UiButtons.RoundedRect(rect, 6);
                using var brush = new SolidBrush(bg);
                e.Graphics.FillPath(brush, path);
                TextRenderer.DrawText(e.Graphics, level, new Font("Segoe UI Semibold", 7f), rect, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            // Decode column — Accent-colored button (20% smaller)
            if (e.ColumnIndex == _grid.Columns["Decode"].Index)
            {
                e.Handled = true;
                e.PaintBackground(e.CellBounds, true);
                var val = e.Value?.ToString() ?? "";
                bool isDecoded = val == "Показать";
                Color bg = isDecoded ? UiTheme.Accent : UiTheme.HoverBg;
                Color fg = Color.White;
                int pad = 5;
                int btnH = e.CellBounds.Height - 10;
                var rect = new Rectangle(e.CellBounds.X + pad, e.CellBounds.Y + (e.CellBounds.Height - btnH) / 2, e.CellBounds.Width - pad * 2, btnH);
                using var path = UiButtons.RoundedRect(rect, 4);
                using var brush = new SolidBrush(bg);
                e.Graphics.FillPath(brush, path);
                var font = new Font("Segoe UI Semibold", 7f);
                TextRenderer.DrawText(e.Graphics, val, font, rect, fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                if (e.RowIndex == _lastHoverRow && e.ColumnIndex == _lastHoverCol)
                {
                    using var hoverBrush = new SolidBrush(Color.FromArgb(40, 255, 255, 255));
                    e.Graphics.FillPath(hoverBrush, path);
                }
                return;
            }
        }

        private void OnNewEvents(List<DashboardEvent> list) => BeginInvoke(new Action(() => { _all = EventDashboardStore.LoadEvents(); ApplyFilters(); }));
        private void OnCritical(DashboardEvent ev)
        {
            try
            {
                var settings = EventDashboardStore.LoadSettings();
                if (!settings.NotifyCritical && ev.Level == "Critical") return;
                if (!settings.NotifyError && ev.Level == "Error") return;
                var all = EventDashboardStore.LoadEvents();
                var idx = all.FindIndex(x => x.Computer == ev.Computer && x.EventId == ev.EventId && x.Time == ev.Time);
                if (idx >= 0 && !all[idx].Notified) { all[idx].Notified = true; EventDashboardStore.SaveEvents(all); }
            }
            catch { }
        }
    }
}
