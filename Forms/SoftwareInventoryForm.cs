using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ShadowRDP.Controls;
using ShadowRDP.Helpers;
using ShadowRDP.Services;

namespace ShadowRDP.Forms
{
    // "Инвентаризация ПО (домен)" — scans all computers from the address
    // book for installed software via Remote Registry / StdRegProv WMI,
    // displays two views: aggregated (software → count → computers) and
    // per-computer (computer → software list). CSV export available.
    public class SoftwareInventoryForm : Form
    {
        private readonly TextBox _filterBox;
        private readonly Label _lblFilter;
        private readonly Label _lblHint;
        private readonly Label _scanBtn;
        private readonly Label _cancelBtn;
        private readonly Label _exportBtn;
        private readonly Label _closeBtn;
        private readonly Label _statusLabel;
        private readonly TabControl _tabs;
        private readonly DataGridView _gridBySoftware;
        private readonly DataGridView _gridByComputer;
        private CancellationTokenSource? _cts;

        private List<SoftwareInventoryService.SoftwareEntry> _allEntries = new();
        private readonly System.Windows.Forms.Timer _filterDebounce = new() { Interval = 120 };

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        private const int WM_SETREDRAW = 0x000B;
        private static Color RowAlt => ShadowRDP.Helpers.UiTheme.RowAlt;

        private static Color RowBase => ShadowRDP.Helpers.UiTheme.RowBase;
        private static readonly Color SelectionColor = Color.FromArgb(0xCC, 0xE4, 0xF7);
        private static readonly Color Divider = Color.FromArgb(0xD9, 0xD9, 0xD9);

        public SoftwareInventoryForm()
        {
            Text = "Инвентаризация ПО (домен)";
            ClientSize = new Size(960, 660);
            MinimumSize = new Size(700, 500);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9F);
            BackColor = UiTheme.Background;
            FormBorderStyle = FormBorderStyle.Sizable;

            var headerPanel = UiHeader.Create("Инвентаризация ПО", "все компьютеры из адресной книги", "list", Color.FromArgb(0x56, 0x5C, 0x30));
            headerPanel.Dock = DockStyle.Top;
            Controls.Add(headerPanel);

            // === Filter row ===
            _lblFilter = new Label
            {
                Text = "Фильтр:",
                Font = new Font("Segoe UI", 9F),
                Location = new Point(16, 83),
                Size = new Size(50, 22),
                TextAlign = ContentAlignment.MiddleLeft
            };
            _filterBox = new TextBox { Location = new Point(70, 83), Size = new Size(260, 22) };
            _filterBox.TextChanged += (s, e) => { _filterDebounce.Stop(); _filterDebounce.Start(); };
            _lblHint = new Label { Text = "(по имени программы или компьютеру)", Location = new Point(336, 83), Size = new Size(230, 22), ForeColor = Color.Gray, TextAlign = ContentAlignment.MiddleLeft };
            Controls.AddRange(new Control[] { _lblFilter, _filterBox, _lblHint });

            var btnFont = new Font("Segoe UI Semibold", 9F);
            _scanBtn = UiButtons.MakeCompactFilled("Сканировать", TextRenderer.MeasureText("Сканировать", btnFont).Width + 28);
            _cancelBtn = UiButtons.MakeCompactFilled("Прервать", TextRenderer.MeasureText("Прервать", btnFont).Width + 28);
            _exportBtn = UiButtons.MakeCompactFilled("Экспорт CSV", TextRenderer.MeasureText("Экспорт CSV", btnFont).Width + 28);
            _exportBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _cancelBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _scanBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            LayoutButtons();
            _scanBtn.Click += async (s, e) => await ScanAsync();
            _cancelBtn.Enabled = false;
            _cancelBtn.Click += (s, e) => OnCancel();
            _exportBtn.Click += (s, e) => OnExportCsv();
            Controls.AddRange(new Control[] { _scanBtn, _cancelBtn, _exportBtn });

            // === TabControl with two tabs ===
            _tabs = new TabControl
            {
                Location = new Point(8, 115),
                Size = new Size(936, 495),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                Font = new Font("Segoe UI", 9F)
            };

            // Tab 1: By Software (aggregated)
            var tabSoftware = new TabPage("По ПО");
            _gridBySoftware = CreateGrid();
            _gridBySoftware.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "Программа", FillWeight = 160, MinimumWidth = 120 });
            _gridBySoftware.Columns.Add(new DataGridViewTextBoxColumn { Name = "Version", HeaderText = "Версия", FillWeight = 60, MinimumWidth = 60 });
            _gridBySoftware.Columns.Add(new DataGridViewTextBoxColumn { Name = "Count", HeaderText = "Кол-во машин", FillWeight = 40, MinimumWidth = 70 });
            _gridBySoftware.Columns.Add(new DataGridViewTextBoxColumn { Name = "Computers", HeaderText = "Компьютеры", FillWeight = 200, MinimumWidth = 150 });
            tabSoftware.Controls.Add(_gridBySoftware);
            _tabs.TabPages.Add(tabSoftware);

            // Tab 2: By Computer (raw)
            var tabComputer = new TabPage("По компьютерам");
            _gridByComputer = CreateGrid();
            _gridByComputer.Columns.Add(new DataGridViewTextBoxColumn { Name = "Computer", HeaderText = "Компьютер", FillWeight = 80, MinimumWidth = 90 });
            _gridByComputer.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "Программа", FillWeight = 160, MinimumWidth = 120 });
            _gridByComputer.Columns.Add(new DataGridViewTextBoxColumn { Name = "Version", HeaderText = "Версия", FillWeight = 60, MinimumWidth = 60 });
            tabComputer.Controls.Add(_gridByComputer);
            _tabs.TabPages.Add(tabComputer);

            Controls.Add(_tabs);

            // === Bottom row ===
            _statusLabel = new Label
            {
                Location = new Point(16, 618),
                Size = new Size(650, 22),
                ForeColor = Color.Gray,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
                Text = "Нажмите «Сканировать»."
            };
            _closeBtn = UiButtons.MakeCompactFilled("Закрыть", TextRenderer.MeasureText("Закрыть", btnFont).Width + 28);
            _closeBtn.Location = new Point(842, 616);
            _closeBtn.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            _closeBtn.Click += (s, e) => Close();
            Controls.AddRange(new Control[] { _statusLabel, _closeBtn });

            _filterDebounce.Tick += (s, e) =>
            {
                _filterDebounce.Stop();
                ApplyFilter();
            };

            Resize += (s, e) => LayoutButtons();
            Shown += async (s, e) => await ScanAsync();
        }

        private void LayoutButtons()
        {
            int gap = 8;
            int right = ClientSize.Width - 16;
            int y = 83;

            right -= _exportBtn.Width;
            _exportBtn.Location = new Point(right, y);
            right -= gap + _cancelBtn.Width;
            _cancelBtn.Location = new Point(right, y);
            right -= gap + _scanBtn.Width;
            _scanBtn.Location = new Point(right, y);
        }

        private DataGridView CreateGrid()
        {
            var grid = new DoubleBufferedDataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToResizeRows = false,
                ReadOnly = true,
                RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor = RowBase,
                BorderStyle = BorderStyle.None,
                ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
                CellBorderStyle = DataGridViewCellBorderStyle.None,
                GridColor = Divider,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                Font = new Font("Segoe UI", 10F),
                DefaultCellStyle = {
                    BackColor = RowBase,
                    SelectionBackColor = SelectionColor,
                    SelectionForeColor = UiTheme.Foreground,
                    Font = new Font("Segoe UI", 10F)
                },
                ColumnHeadersDefaultCellStyle = {
                    BackColor = UiTheme.Muted,
                    ForeColor = UiTheme.Foreground,
                    SelectionBackColor = UiTheme.Muted,
                    SelectionForeColor = UiTheme.Foreground,
                    Font = new Font("Segoe UI", 10F)
                },
                RowTemplate = { Height = 22 }
            };
            grid.EnableHeadersVisualStyles = false;
            grid.AdvancedCellBorderStyle.All = DataGridViewAdvancedCellBorderStyle.None;
            grid.AdvancedColumnHeadersBorderStyle.All = DataGridViewAdvancedCellBorderStyle.None;
            foreach (DataGridViewColumn c in grid.Columns) c.DividerWidth = 0;
            grid.RowTemplate.DividerHeight = 0;
            return grid;
        }

        private async Task ScanAsync()
        {
            _scanBtn.Enabled = false;
            _cancelBtn.Enabled = true;
            _exportBtn.Enabled = false;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            _allEntries.Clear();
            _gridBySoftware.Rows.Clear();
            _gridByComputer.Rows.Clear();

            _statusLabel.Text = "Чтение данных из общей папки...";

            try
            {
                var sharePath = Services.SoftwareInventoryService.DefaultSharePath;
                _allEntries = await Task.Run(() => Services.SoftwareInventoryService.LoadFromShare(sharePath, token), token);

                PopulateGrids();
                _exportBtn.Enabled = _allEntries.Count > 0;

                var computers = _allEntries.Select(e => e.Computer).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                _statusLabel.Text = $"Готово. Компьютеров: {computers}, записей ПО: {_allEntries.Count}";
            }
            catch (OperationCanceledException)
            {
                _statusLabel.Text = "Отменено.";
            }
            catch (Exception ex)
            {
                _statusLabel.Text = $"Ошибка: {ex.Message}";
                Logger.Warn($"SoftwareInventory load failed: {ex.Message}");
            }
            finally
            {
                _scanBtn.Enabled = true;
                _cancelBtn.Enabled = false;
            }
        }

        private void PopulateGrids()
        {
            // === By Software (aggregated) ===
            var bySoftware = _allEntries
                .GroupBy(e => (e.Name, e.Version))
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _gridBySoftware.SuspendLayout();
            foreach (var g in bySoftware)
            {
                var computers = string.Join(", ", g.Select(e => e.Computer).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c));
                var row = _gridBySoftware.Rows.Add(g.Key.Name, g.Key.Version, g.Count(), computers);
                _gridBySoftware.Rows[row].DefaultCellStyle.BackColor = (row % 2 == 0) ? RowBase : RowAlt;
                _gridBySoftware.Rows[row].DefaultCellStyle.SelectionBackColor = SelectionColor;
            }
            _gridBySoftware.ResumeLayout();

            // === By Computer (raw) ===
            var byComputer = _allEntries
                .OrderBy(e => e.Computer, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _gridByComputer.SuspendLayout();
            int idx = 0;
            foreach (var e in byComputer)
            {
                var row = _gridByComputer.Rows.Add(e.Computer, e.Name, e.Version);
                _gridByComputer.Rows[row].DefaultCellStyle.BackColor = (idx++ % 2 == 0) ? RowBase : RowAlt;
                _gridByComputer.Rows[row].DefaultCellStyle.SelectionBackColor = SelectionColor;
            }
            _gridByComputer.ResumeLayout();
        }

        private void ApplyFilter()
        {
            var term = _filterBox.Text.Trim();
            if (string.IsNullOrEmpty(term))
            {
                foreach (DataGridViewRow r in _gridBySoftware.Rows) r.Visible = true;
                foreach (DataGridViewRow r in _gridByComputer.Rows) r.Visible = true;
                return;
            }

            foreach (DataGridViewRow r in _gridBySoftware.Rows)
            {
                var name = r.Cells["Name"].Value?.ToString() ?? "";
                var computers = r.Cells["Computers"].Value?.ToString() ?? "";
                r.Visible = name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                            computers.Contains(term, StringComparison.OrdinalIgnoreCase);
            }
            foreach (DataGridViewRow r in _gridByComputer.Rows)
            {
                var computer = r.Cells["Computer"].Value?.ToString() ?? "";
                var name = r.Cells["Name"].Value?.ToString() ?? "";
                r.Visible = computer.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                            name.Contains(term, StringComparison.OrdinalIgnoreCase);
            }
        }

        private void OnCancel()
        {
            _cts?.Cancel();
        }

        private void OnExportCsv()
        {
            using var sfd = new SaveFileDialog
            {
                Filter = "CSV (*.csv)|*.csv|Все файлы (*.*)|*.*",
                FileName = "software_inventory.csv",
                DefaultExt = "csv"
            };
            if (sfd.ShowDialog(this) != DialogResult.OK) return;

            try
            {
                var activeGrid = _tabs.SelectedIndex == 0 ? _gridBySoftware : _gridByComputer;
                var viewName = _tabs.SelectedIndex == 0 ? "По ПО" : "По компьютерам";

                using var sw = new StreamWriter(sfd.FileName, false, new UTF8Encoding(true));
                // Header
                var headers = activeGrid.Columns.Cast<DataGridViewColumn>()
                    .Select(c => c.HeaderText).ToList();
                sw.WriteLine(string.Join(";", headers));

                // Rows (visible only — respects filter)
                foreach (DataGridViewRow row in activeGrid.Rows)
                {
                    if (!row.Visible) continue;
                    var cells = row.Cells.Cast<DataGridViewCell>()
                        .Select(c => EscapeCsv(c.Value?.ToString() ?? "")).ToList();
                    sw.WriteLine(string.Join(";", cells));
                }

                _statusLabel.Text = $"Экспортировано ({viewName}): {sfd.FileName}";
                Logger.Info($"SoftwareInventory CSV export ({viewName}): {sfd.FileName}");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Ошибка экспорта: {ex.Message}", "Экспорт",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static string EscapeCsv(string value)
        {
            if (value.Contains(';') || value.Contains('"') || value.Contains('\n'))
                return $"\"{value.Replace("\"", "\"\"")}\"";
            return value;
        }

        private void UpdateStatus(string text)
        {
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<string>(UpdateStatus), text); } catch { }
                return;
            }
            _statusLabel.Text = text;
        }
    }
}
