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
    //
    // v4.3.3 — редизайн в стиле FTP-эталона: градиентный hero (поиск справа),
    // обе таблицы-табы в одной карточке, действия в футере. Вся логика
    // (скан, фильтр, экспорт, персистентность) без изменений.
    public class SoftwareInventoryForm : Form
    {
        private readonly TextBox _filterBox;
        private readonly Panel _filterWrapper;
        private readonly Label _lblHint;
        private readonly Button _helpBtn;
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

        public SoftwareInventoryForm()
        {
            Text = "Инвентаризация SOFT (домен)";
            ClientSize = new Size(960, 660);
            MinimumSize = new Size(700, 500);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9F);
            BackColor = UiTheme.DialogBackground;
            FormBorderStyle = FormBorderStyle.Sizable;
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { e.Handled = true; DialogResult = DialogResult.Cancel; Close(); } };

            // ——— 1. Hero (без крестика: у окна есть штатный) ———
            var hero = UiModal.Hero("Инвентаризация SOFT", "все компьютеры из адресной книги", "computer");

            // Search lives in the hero (right side): white box, no anchors —
            // position comes from the real hero width (Shown/Resize).
            _filterWrapper = Helpers.UiSearchBox.Create("Поиск…", txt => { _filterDebounce.Stop(); _filterDebounce.Start(); }, width: 364, height: 27, background: Color.White);
            _filterWrapper.Location = new Point(70, 18);
            _filterWrapper.Size = new Size(364, 27);
            _filterBox = Helpers.UiSearchBox.GetBox(_filterWrapper);
            _lblHint = new Label { Text = "(по имени программы или компьютеру)", Location = new Point(70, 48), Size = new Size(364, 18), Font = new Font("Segoe UI", 8F), ForeColor = Color.FromArgb(210, 228, 238), TextAlign = ContentAlignment.MiddleLeft, BackColor = Color.Transparent };
            hero.Controls.Add(_filterWrapper);
            hero.Controls.Add(_lblHint);

            // v3.9.5 — «Помощь в настройке» справа от названия хедера:
            // та же Flat-кнопка, что «Настройка…» в палитре команд.
            _helpBtn = UiButtons.MakeHeroButton("Помощь в настройке");
            _helpBtn.Click += (s, e) => InventoryHelpDialog.Show(this, InventoryHelpMode.Soft);
            hero.Controls.Add(_helpBtn);

            // ——— 2. Content: single card with both tabs ———
            var content = new Panel { Dock = DockStyle.Fill, AutoScroll = false, BackColor = UiTheme.DialogBackground, Padding = new Padding(14) };
            var cardTabs = UiModal.Card(0, 0, 100, 100, "", "", withHeader: false);
            cardTabs.Dock = DockStyle.Fill;
            cardTabs.Padding = new Padding(14);
            content.Controls.Add(cardTabs);
            // v3.9.3Y — UiModal.Card гладкая сама, Region давал лесенку.
            try { cardTabs.Region?.Dispose(); cardTabs.Region = null; } catch { }

            // === TabControl with two tabs ===
            _tabs = new TabControl
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F)
            };

            // Tab 1: By Software (aggregated)
            var tabSoftware = new TabPage("По ПО") { BackColor = Color.White };
            _gridBySoftware = CreateGrid();
            _gridBySoftware.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "Программа", FillWeight = 160, MinimumWidth = 120 });
            _gridBySoftware.Columns.Add(new DataGridViewTextBoxColumn { Name = "Version", HeaderText = "Версия", FillWeight = 60, MinimumWidth = 60 });
            _gridBySoftware.Columns.Add(new DataGridViewTextBoxColumn { Name = "Count", HeaderText = "Кол-во машин", FillWeight = 40, MinimumWidth = 70 });
            _gridBySoftware.Columns.Add(new DataGridViewTextBoxColumn { Name = "Computers", HeaderText = "Компьютеры", FillWeight = 200, MinimumWidth = 150 });
            tabSoftware.Controls.Add(_gridBySoftware);
            // v4.4.0 — своя тонкая полоса вместо системной.
            ShadowScroll.AttachGrid(_gridBySoftware);
            _tabs.TabPages.Add(tabSoftware);

            // Tab 2: By Computer (raw)
            var tabComputer = new TabPage("По компьютерам") { BackColor = Color.White };
            _gridByComputer = CreateGrid();
            _gridByComputer.Columns.Add(new DataGridViewTextBoxColumn { Name = "Computer", HeaderText = "Компьютер", FillWeight = 80, MinimumWidth = 90 });
            _gridByComputer.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "Программа", FillWeight = 160, MinimumWidth = 120 });
            _gridByComputer.Columns.Add(new DataGridViewTextBoxColumn { Name = "Version", HeaderText = "Версия", FillWeight = 60, MinimumWidth = 60 });
            tabComputer.Controls.Add(_gridByComputer);
            // v4.4.0 — своя тонкая полоса вместо системной.
            ShadowScroll.AttachGrid(_gridByComputer);
            _tabs.TabPages.Add(tabComputer);

            cardTabs.Controls.Add(_tabs);

            // ——— 3. Status bar (thin strip above the footer) ———
            var statusPanel = new Panel { Dock = DockStyle.Bottom, Height = 24, BackColor = UiTheme.DialogCard };
            statusPanel.Paint += (s, e) => { using var pen = new Pen(UiTheme.Border, 1); e.Graphics.DrawLine(pen, 0, 0, statusPanel.Width, 0); };
            _statusLabel = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = Color.Gray,
                Font = new Font("Segoe UI", 8F),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(16, 0, 0, 0),
                Text = "Нажмите «Сканировать»."
            };
            statusPanel.Controls.Add(_statusLabel);

            // ——— 4. Footer with action buttons ———
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = UiTheme.DialogCard };
            footer.Paint += (s, e) => { using var pen = new Pen(UiTheme.Border, 1); e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };
            var btnFont = new Font("Segoe UI Semibold", 9F);
            int w(string text) => TextRenderer.MeasureText(text, btnFont).Width + 28;
            _scanBtn = UiButtons.MakeCompactFilled("Сканировать", w("Сканировать"));
            _scanBtn.Location = new Point(16, 15);
            _scanBtn.Click += async (s, e) => await ScanAsync();
            _cancelBtn = UiButtons.MakeCompactFilled("Прервать", w("Прервать"));
            _cancelBtn.Location = new Point(16 + _scanBtn.Width + 8, 15);
            _cancelBtn.Enabled = false;
            _cancelBtn.Click += (s, e) => OnCancel();
            _exportBtn = UiButtons.MakeCompactFilled("Экспорт CSV", w("Экспорт CSV"));
            _exportBtn.Location = new Point(_cancelBtn.Left + _cancelBtn.Width + 8, 15);
            _exportBtn.Click += (s, e) => OnExportCsv();
            _closeBtn = UiButtons.MakeCompactFilled("Закрыть", w("Закрыть"));
            _closeBtn.Click += (s, e) => Close();
            footer.Controls.AddRange(new Control[] { _scanBtn, _cancelBtn, _exportBtn, _closeBtn });
            footer.Resize += (s, e) =>
            {
                _closeBtn.Left = footer.ClientSize.Width - 16 - _closeBtn.Width;
                _closeBtn.Top = 15;
            };

            // Dock order: content Fill first, hero Top, then the two Bottom
            // strips (footer added last docks first → very bottom, status
            // above it). Same rule as the FTP sample modal.
            Controls.Add(content);
            Controls.Add(hero);
            Controls.Add(statusPanel);
            Controls.Add(footer);

            var dpiScale = UiModal.ApplyDpiScale(this);
            if (dpiScale > 1.001f)
                ClientSize = new Size((int)Math.Round(ClientSize.Width * dpiScale), (int)Math.Round(ClientSize.Height * dpiScale));

            _filterDebounce.Tick += (s, e) =>
            {
                _filterDebounce.Stop();
                ApplyFilter();
            };

            content.Resize += (s, e) => LayoutContent();
            hero.Resize += (s, e) => PositionSearch();
            Shown += async (s, e) =>
            {
                LayoutContent();
                PositionSearch();
                _closeBtn.Left = footer.ClientSize.Width - 16 - _closeBtn.Width;
                _closeBtn.Top = 15;
                // v3.9.1 — поиск не активен при открытии окна: фокус на сетку,
                // поле активируется только кликом мыши (как в остальных формах).
                ActiveControl = _gridBySoftware;
                _gridBySoftware.Focus();
                try { BeginInvoke(new Action(() => { try { Refresh(); } catch { } })); } catch { }
                await ScanAsync();
            };

            void LayoutContent()
            {
                // Single files card is already Dock=Fill in padded content —
                // nothing to position manually.
            }
            LayoutContent();

            // Search at the hero's right edge (no anchors — they capture
            // garbage mid-layout; positions come from the real width).
            void PositionSearch()
            {
                // Кнопка помощи — слева от поиска, по центру его высоты.
                // Поиск держит правый край; на узком окне сужается,
                // кнопка — следом, название не трогает.
                int titleW;
                using (var tf = new Font("Segoe UI Semibold", 12.5f))
                    titleW = TextRenderer.MeasureText("Инвентаризация SOFT", tf).Width;
                int titleEnd = 72 + titleW;
                int searchW = Math.Max(200, Math.Min(364, hero.Width - titleEnd - _helpBtn.Width - 44));
                int x = hero.Width - 16 - searchW;
                _filterWrapper.Size = new Size(searchW, 27);
                _filterWrapper.Location = new Point(x, 18);
                _lblHint.Location = new Point(x, 48);
                _lblHint.Size = new Size(searchW, 18);
                int bx = x - 16 - _helpBtn.Width;
                if (bx < titleEnd + 12) bx = titleEnd + 12;
                _helpBtn.Location = new Point(bx, 17);
            }
            PositionSearch();
        }

        private DataGridView CreateGrid()
        {
            var grid = new DoubleBufferedDataGridView
            {
                Dock = DockStyle.Fill,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                MultiSelect = false,
            };
            ShadowRDP.Helpers.UiGridStyle.ApplyUnified(grid, 36, 36);
            grid.Resize += (s, e) => { try { grid.Invalidate(); } catch { } };
            grid.ColumnWidthChanged += (s, e) => { try { grid.Invalidate(); } catch { } };
            return grid;
        }

        // Скругление углов: режем Region контрола по скруглённому rect.
        
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
                _gridBySoftware.Rows.Add(g.Key.Name, g.Key.Version, g.Count(), computers);
            }
            _gridBySoftware.ResumeLayout();

            // === By Computer (raw) ===
            var byComputer = _allEntries
                .OrderBy(e => e.Computer, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _gridByComputer.SuspendLayout();
            foreach (var e in byComputer)
            {
                _gridByComputer.Rows.Add(e.Computer, e.Name, e.Version);
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
            if (UiModal.ShowDimmed(this, sfd) != DialogResult.OK) return;

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
