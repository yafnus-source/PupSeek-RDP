using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Management;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using ShadowRDP.Controls;
using ShadowRDP.Services;

namespace ShadowRDP.Forms
{
    // "События (Event Log)" dialog. Queries WMI Win32_NTLogEvent on the
    // target computer and shows a filterable grid of recent events.
    //
    // WMI Win32_NTLogEvent fields used:
    //   TimeGenerated  — yyyymmddHHMMSS.mmmmmm+UUU (WMI datetime)
    //   SourceName     — the logging application
    //   EventCode      — numeric event ID
    //   Type           — Warning / Error / Information / ...
    //   Message        — full event text
    //
    // TimeGenerated comes back as a string. We parse with
    // ManagementDateTimeConverter.ToDateTime for sorting + display.
    public class EventLogForm : Form
    {
        private readonly string _computer;
        private readonly ComboBox _logCombo;
        private readonly ComboBox _periodCombo;
        private readonly ComboBox _levelCombo;
        private readonly DataGridView _grid;
        private readonly Label _statusLabel;
        private readonly Label _findBtn;
        private readonly Label _exportBtn;
        private readonly Label _aiBtn;
        private readonly Label _closeBtn;

        public EventLogForm(string computer)
        {
            _computer = computer;
            Text = $"События на {computer}";
            ClientSize = new Size(920, 726);
            MinimumSize = new Size(640, 546);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9F);
            BackColor = ShadowRDP.Helpers.UiTheme.Background;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;

            // Header
            var headerPanel = Helpers.UiHeader.Create("Журнал событий", _computer, "list");
            headerPanel.Dock = DockStyle.Top;
            Controls.Add(headerPanel);
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { e.Handled = true; DialogResult = DialogResult.Cancel; Close(); } };

            // === Filter row ===
            var lblLog = new Label { Text = "Журнал:", Location = new Point(16, 82), Size = new Size(50, 22), TextAlign = ContentAlignment.MiddleLeft };
            _logCombo = new ComboBox { Location = new Point(70, 80), Size = new Size(140, 22), DropDownStyle = ComboBoxStyle.DropDownList };
            _logCombo.Items.AddRange(new object[] { "System", "Application", "Security", "Setup", "Windows PowerShell" });
            _logCombo.SelectedIndex = 0;
            Helpers.UiCombo.Apply(_logCombo);

            var lblPeriod = new Label { Text = "Период:", Location = new Point(220, 82), Size = new Size(50, 22), TextAlign = ContentAlignment.MiddleLeft };
            _periodCombo = new ComboBox { Location = new Point(274, 80), Size = new Size(90, 22), DropDownStyle = ComboBoxStyle.DropDownList };
            _periodCombo.Items.AddRange(new object[] { "1 час", "4 часа", "24 часа", "7 дней", "30 дней", "Всё время" });
            _periodCombo.SelectedIndex = 2;
            Helpers.UiCombo.Apply(_periodCombo);

            var lblLevel = new Label { Text = "Уровень:", Location = new Point(374, 82), Size = new Size(60, 22), TextAlign = ContentAlignment.MiddleLeft };
            _levelCombo = new ComboBox { Location = new Point(434, 80), Size = new Size(120, 22), DropDownStyle = ComboBoxStyle.DropDownList };
            _levelCombo.Items.AddRange(new object[] { "Все", "Critical", "Error", "Warning", "Information" });
            _levelCombo.SelectedIndex = 1;
            Helpers.UiCombo.Apply(_levelCombo);

            _findBtn = ShadowRDP.Helpers.UiButtons.MakeFlat("Найти");
            _findBtn.Location = new Point(564, 78);
            _findBtn.Click += async (s, e) => await FindAsync();

            _exportBtn = ShadowRDP.Helpers.UiButtons.MakeFlat("Экспорт CSV");
            _exportBtn.Location = new Point(618, 78);
            _exportBtn.Click += OnExport;

            // v4.0.0.1 — ИИ-помощник по текущей выборке грида.
            _aiBtn = ShadowRDP.Helpers.UiButtons.MakeFlat("Спросить ИИ");
            _aiBtn.Location = new Point(726, 78);
            _aiBtn.Click += async (s, e) => await AiAnalyzeAsync();

            _closeBtn = ShadowRDP.Helpers.UiButtons.MakeFlat("Закрыть");
            _closeBtn.Location = new Point(836, 78);
            _closeBtn.Click += (s, e) => Close();
            // CancelButton requires IButtonControl; Label doesn't implement it.

            Controls.AddRange(new Control[] { lblLog, _logCombo, lblPeriod, _periodCombo, lblLevel, _levelCombo, _findBtn, _exportBtn, _aiBtn, _closeBtn });

            // === Grid ===
            _grid = new DataGridView
            {
                Location = new Point(16, 116),
                Size = new Size(888, 580),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            };
            AddCol(_grid, "Time", "Время", 130);
            AddCol(_grid, "Source", "Источник", 140);
            AddCol(_grid, "EventID", "ID", 60);
            AddCol(_grid, "Type", "Тип", 70);
            AddCol(_grid, "Message", "Сообщение", 400);
            ShadowRDP.Helpers.UiGridStyle.ApplyUnified(_grid, 36, 36);

            // v3.9.0 — right-click menu shown as a themed popup.
            _grid.CellMouseDown += (s, args) =>
            {
                if (args.Button != MouseButtons.Right) return;
                if (args.RowIndex < 0) return;
                _grid.ClearSelection();
                _grid.Rows[args.RowIndex].Selected = true;
                var menu = new ShadowRDP.Helpers.ThemedContextMenu();
                menu.AddHeader("Событие");
                menu.AddItem("Копировать сообщение", "copy", () =>
                {
                    if (_grid.SelectedRows.Count == 0) return;
                    var v = _grid.SelectedRows[0].Cells["Message"].Value?.ToString() ?? "";
                    try { Clipboard.SetText(v); } catch { }
                });
                menu.AddItem("Фильтр по этому ID", "search", () =>
                {
                    if (_grid.SelectedRows.Count == 0) return;
                    var v = _grid.SelectedRows[0].Cells["EventID"].Value?.ToString() ?? "";
                    MessageBox.Show(this, $"Event ID: {v}", "Фильтр",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                });
                menu.Show(this, Control.MousePosition);
            };

            Controls.Add(_grid);
            // v4.4.0 — своя тонкая полоса вместо системной.
            ShadowScroll.AttachGrid(_grid);

            _statusLabel = new Label
            {
                Location = new Point(16, _grid.Bottom + 6),
                Size = new Size(880, 20),
                ForeColor = Color.Gray,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                Text = "Нажмите «Найти» для запроса событий."
            };
            Controls.Add(_statusLabel);

            Shown += async (s, e) => await FindAsync();
        }

        private static void AddCol(DataGridView grid, string name, string header, int fillWeight)
        {
            var col = new DataGridViewTextBoxColumn { Name = name, HeaderText = header, FillWeight = fillWeight };
            grid.Columns.Add(col);
        }

        private DateTime PeriodStart()
        {
            return _periodCombo.SelectedIndex switch
            {
                0 => DateTime.Now.AddHours(-1),
                1 => DateTime.Now.AddHours(-4),
                2 => DateTime.Now.AddDays(-1),
                3 => DateTime.Now.AddDays(-7),
                4 => DateTime.Now.AddDays(-30),
                _ => DateTime.MinValue,
            };
        }

        private string LevelFilter(string logType)
        {
            // WMI Type values: Error / Warning / Information / AuditSuccess /
            // AuditFailure. Map the combobox selection to a filter clause.
            return _levelCombo.SelectedIndex switch
            {
                1 => " AND Type='Error'",
                2 => " AND Type='Warning'",
                3 => " AND Type='Information'",
                _ => ""
            };
        }

        private async Task FindAsync()
        {
            _grid.Rows.Clear();
            _statusLabel.Text = "Запрос событий...";
            _findBtn.Enabled = false;
            try
            {
                var log = _logCombo.SelectedItem?.ToString() ?? "System";
                var since = PeriodStart();
                var sinceWmi = since == DateTime.MinValue ? "" :
                    $" AND TimeGenerated > '{ManagementDateTimeConverter.ToDmtfDateTime(since)}'";
                var wql = $"SELECT * FROM Win32_NTLogEvent WHERE Logfile='{log}'{sinceWmi}{LevelFilter(log)}";

                var rows = await Task.Run(() => QueryEvents(_computer, wql));
                foreach (var r in rows)
                {
                    _grid.Rows.Add(r.Time, r.Source, r.EventId, r.Type, r.Message);
                }
                _statusLabel.Text = $"Найдено: {rows.Count} событий (лог: {log}).";
                Logger.Info($"EventLog query on {_computer}: {rows.Count} events");
            }
            catch (Exception ex)
            {
                _statusLabel.Text = $"Ошибка: {ex.Message}";
                Logger.Warn($"EventLog query failed on {_computer}: {ex.Message}");
            }
            finally
            {
                _findBtn.Enabled = true;
            }
        }

        // v4.0.0.1 — ИИ-помощник: анализ того, что сейчас в гриде (с учётом
        // фильтров пользователя). Гейт на модуль "ai"; проверка Ollama,
        // пустая выборка и показ ответа — внутри Helpers.AiEventAnalyzer.
        private async Task AiAnalyzeAsync()
        {
            if (!Helpers.AiEventAnalyzer.Gate(this)) return;
            if (_grid.Rows.Count == 0)
            {
                MessageBox.Show(this, "В списке нет событий. Сначала нажмите «Найти».", "Спросить ИИ",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var raw = new List<(int id, string source, string level, DateTime time, string msg)>();
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.IsNewRow) continue;
                if (!DateTime.TryParse(row.Cells["Time"].Value?.ToString(), out var time)) time = DateTime.MinValue;
                if (!int.TryParse(row.Cells["EventID"].Value?.ToString(), out var id)) id = 0;
                raw.Add((id,
                    row.Cells["Source"].Value?.ToString() ?? "",
                    row.Cells["Type"].Value?.ToString() ?? "",
                    time,
                    row.Cells["Message"].Value?.ToString() ?? ""));
            }
            var aggs = Helpers.AiEventAnalyzer.Aggregate(raw);
            string scope = $"Лог: {_logCombo.SelectedItem} • Период: {_periodCombo.SelectedItem} • Уровень: {_levelCombo.SelectedItem}";

            var prevStatus = _statusLabel.Text;
            _statusLabel.Text = "ИИ: анализирую события…";
            _aiBtn.Enabled = false;
            try
            {
                await Helpers.AiEventAnalyzer.RunAsync(this, _computer, scope, aggs);
            }
            finally
            {
                _statusLabel.Text = prevStatus;
                _aiBtn.Enabled = true;
            }
        }

        private static List<(string Time, string Source, string EventId, string Type, string Message)> QueryEvents(string computer, string wql)
        {
            var list = new List<(string, string, string, string, string)>();
            try
            {
                var scope = new ManagementScope(new ManagementPath($"\\\\{computer}\\root\\cimv2"),
                    new ConnectionOptions { Timeout = TimeSpan.FromSeconds(8), Authentication = AuthenticationLevel.PacketPrivacy });
                scope.Connect();
                using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql));
                searcher.Options.ReturnImmediately = false;
                using var results = searcher.Get();
                foreach (ManagementObject mo in results)
                {
                    try
                    {
                        var timeRaw = mo["TimeGenerated"]?.ToString();
                        var time = "";
                        if (!string.IsNullOrEmpty(timeRaw))
                        {
                            try { time = ManagementDateTimeConverter.ToDateTime(timeRaw).ToString("yyyy-MM-dd HH:mm:ss"); }
                            catch { time = timeRaw; }
                        }
                        list.Add((
                            time,
                            mo["SourceName"]?.ToString() ?? "",
                            mo["EventCode"]?.ToString() ?? "",
                            mo["Type"]?.ToString() ?? "",
                            mo["Message"]?.ToString()?.Trim() ?? ""
                        ));
                    }
                    finally { mo.Dispose(); }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"EventLog QueryEvents failed on {computer}: {ex.Message}");
            }
            return list;
        }

        private void OnExport(object? sender, EventArgs e)
        {
            if (_grid.Rows.Count == 0) return;
            using var sfd = new SaveFileDialog
            {
                Filter = "CSV (*.csv)|*.csv|All files|*.*",
                FileName = $"events_{_computer}_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
            };
            if (ShadowRDP.Helpers.UiModal.ShowDimmed(this, sfd) != DialogResult.OK) return;
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("Time;Source;EventID;Type;Message");
                foreach (DataGridViewRow row in _grid.Rows)
                {
                    var cells = new[] { "Time", "Source", "EventID", "Type", "Message" }
                        .Select(n => CsvEscape(row.Cells[n].Value?.ToString() ?? ""));
                    sb.AppendLine(string.Join(";", cells));
                }
                System.IO.File.WriteAllText(sfd.FileName, sb.ToString(), Encoding.UTF8);
                _statusLabel.Text = $"Экспортировано в {sfd.FileName}";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Не удалось сохранить: {ex.Message}", "Экспорт",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // Wrap value in quotes if it contains ; or newline, double internal quotes.
        private static string CsvEscape(string v)
        {
            if (v.Contains(';') || v.Contains('"') || v.Contains('\n') || v.Contains('\r'))
            {
                return "\"" + v.Replace("\"", "\"\"") + "\"";
            }
            return v;
        }
    }
}
