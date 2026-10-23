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

            // ——— 2. Фильтры: тулбар-карточка (как в «Инвентаризации SOFT») ———
            var content = new Panel { Dock = DockStyle.Fill, BackColor = Helpers.UiTheme.DialogBackground, Padding = new Padding(14) };

            var toolbarCard = Helpers.UiModal.Card(0, 0, 100, 100, "", "", withHeader: false);
            toolbarCard.Dock = DockStyle.Top;
            toolbarCard.Height = 48;
            // v3.9.3Y — UiModal.Card гладкая сама, Region давал лесенку.
            try { toolbarCard.Region?.Dispose(); toolbarCard.Region = null; } catch { }

            var lblFont = new Font("Segoe UI Semibold", 9F);
            var lblLog = new Label { Text = "Журнал:", Location = new Point(12, 14), AutoSize = true, Font = lblFont, ForeColor = Helpers.UiTheme.MutedForeground };
            _logCombo = new ComboBox { Location = new Point(70, 13), Size = new Size(150, 22), DropDownStyle = ComboBoxStyle.DropDownList };
            _logCombo.Items.AddRange(new object[] { "System", "Application", "Security", "Setup", "Windows PowerShell" });
            _logCombo.SelectedIndex = 0;
            Helpers.UiCombo.Apply(_logCombo);

            var lblPeriod = new Label { Text = "Период:", Location = new Point(234, 14), AutoSize = true, Font = lblFont, ForeColor = Helpers.UiTheme.MutedForeground };
            _periodCombo = new ComboBox { Location = new Point(292, 13), Size = new Size(100, 22), DropDownStyle = ComboBoxStyle.DropDownList };
            _periodCombo.Items.AddRange(new object[] { "1 час", "4 часа", "24 часа", "7 дней", "30 дней", "Всё время" });
            _periodCombo.SelectedIndex = 2;
            Helpers.UiCombo.Apply(_periodCombo);

            var lblLevel = new Label { Text = "Уровень:", Location = new Point(404, 14), AutoSize = true, Font = lblFont, ForeColor = Helpers.UiTheme.MutedForeground };
            _levelCombo = new ComboBox { Location = new Point(466, 13), Size = new Size(116, 22), DropDownStyle = ComboBoxStyle.DropDownList };
            _levelCombo.Items.AddRange(new object[] { "Все", "Critical", "Error", "Warning", "Information" });
            _levelCombo.SelectedIndex = 1;
            Helpers.UiCombo.Apply(_levelCombo);

            toolbarCard.Controls.AddRange(new Control[] { lblLog, _logCombo, lblPeriod, _periodCombo, lblLevel, _levelCombo });

            // ——— 3. Грид в белой карточке ———
            var gridCard = Helpers.UiModal.Card(0, 0, 100, 100, "", "", withHeader: false);
            gridCard.Dock = DockStyle.Fill;
            gridCard.Padding = new Padding(8);
            try { gridCard.Region?.Dispose(); gridCard.Region = null; } catch { }

            // === Grid ===
            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
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

            gridCard.Controls.Add(_grid);
            // v4.4.0 — своя тонкая полоса вместо системной.
            ShadowScroll.AttachGrid(_grid);

            content.Controls.Add(gridCard);
            content.Controls.Add(toolbarCard);

            // ——— 4. Статус-полоса (тонкая, над футером) ———
            var statusPanel = new Panel { Dock = DockStyle.Bottom, Height = 24, BackColor = Helpers.UiTheme.DialogCard };
            statusPanel.Paint += (s, e) => { using var pen = new Pen(Helpers.UiTheme.Border, 1); e.Graphics.DrawLine(pen, 0, 0, statusPanel.Width, 0); };
            _statusLabel = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = Color.Gray,
                Font = new Font("Segoe UI", 8F),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(16, 0, 0, 0),
                Text = "Нажмите «Найти» для запроса событий."
            };
            statusPanel.Controls.Add(_statusLabel);

            // ——— 5. Футер с кнопками действий ———
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = Helpers.UiTheme.DialogCard };
            footer.Paint += (s, e) => { using var pen = new Pen(Helpers.UiTheme.Border, 1); e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };
            var btnFont = new Font("Segoe UI Semibold", 9F);
            int w(string text) => TextRenderer.MeasureText(text, btnFont).Width + 28;
            _findBtn = ShadowRDP.Helpers.UiButtons.MakeCompactFilled("Найти", w("Найти"));
            _findBtn.Click += async (s, e) => await FindAsync();
            // v4.0.0.1 — ИИ-помощник по текущей выборке грида.
            _aiBtn = ShadowRDP.Helpers.UiButtons.MakeCompactFilled("Спросить ИИ", w("Спросить ИИ"));
            _aiBtn.Click += async (s, e) => await AiAnalyzeAsync();
            _exportBtn = ShadowRDP.Helpers.UiButtons.MakeCompactFilled("Экспорт CSV", w("Экспорт CSV"));
            _exportBtn.Click += OnExport;
            _closeBtn = ShadowRDP.Helpers.UiButtons.MakeCompactFilled("Закрыть", w("Закрыть"));
            _closeBtn.Click += (s, e) => Close();
            footer.Controls.AddRange(new Control[] { _findBtn, _aiBtn, _exportBtn, _closeBtn });
            _findBtn.Location = new Point(16, 15);
            _aiBtn.Location = new Point(_findBtn.Right + 8, 15);
            _exportBtn.Location = new Point(_aiBtn.Right + 8, 15);
            footer.Resize += (s, e) => { _closeBtn.Left = footer.ClientSize.Width - 16 - _closeBtn.Width; _closeBtn.Top = 15; };

            // Dock order: content Fill first, hero Top, then the two Bottom
            // strips (footer added last docks first → very bottom, status
            // above it). Same rule as the FTP sample modal.
            Controls.Add(content);
            Controls.Add(headerPanel);
            Controls.Add(statusPanel);
            Controls.Add(footer);

            Shown += (s, e) =>
            {
                _closeBtn.Left = footer.ClientSize.Width - 16 - _closeBtn.Width;
                _closeBtn.Top = 15;
            };
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
