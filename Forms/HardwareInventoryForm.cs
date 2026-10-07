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
    // v3.9.3BD — "Сводная таблица HARD": железо компьютеров книги
    // (процессор, RAM, диски) живым опросом WMI. Дизайн таблицы — как
    // у Монитора: белый хедер, зебра, бежевая первая колонка, ClearType.
    // Каркас окна — как у Инвентаризации SOFT (hero + поиск, карточка,
    // статус, футер, Esc, Сканировать/Прервать/Экспорт/Закрыть).
    public class HardwareInventoryForm : Form
    {
        private readonly TextBox _filterBox;
        private readonly Panel _filterWrapper;
        private readonly Label _lblHint;
        private readonly Label _scanBtn;
        private readonly Label _cancelBtn;
        private readonly Label _exportBtn;
        private readonly Label _closeBtn;
        private readonly Label _statusLabel;
        private readonly DataGridView _grid;
        private CancellationTokenSource? _cts;

        private List<HardwareInventoryService.HardwareEntry> _allEntries = new();
        private readonly System.Windows.Forms.Timer _filterDebounce = new() { Interval = 120 };

        private static readonly Font HostFont = new Font("Segoe UI", 9f, FontStyle.Bold);
        private static readonly Font BodyFont = new Font("Segoe UI", 9f);
        private static readonly Color OkDot = Color.FromArgb(0x3A, 0x7D, 0x3D);
        private static readonly Color ErrDot = Color.FromArgb(0xC0, 0x30, 0x20);
        private static readonly Color ErrText = Color.FromArgb(0xC0, 0x30, 0x20);
        // v3.9.3CC — синяя тема: чернила шапки переключаются.
        private static bool IsBlue => Helpers.UiTheme.CurrentName != "Olive";
        private static Color InkText => IsBlue ? Color.FromArgb(0x20, 0x36, 0x4E) : Color.FromArgb(0x2A, 0x30, 0x26);
        // v3.9.3CE — синяя тема: первая колонка и приглушённый текст.
        private static Color FirstBg => IsBlue ? Color.FromArgb(0xDC, 0xE3, 0xEB) : Color.FromArgb(0xE2, 0xDD, 0xC9);
        private static Color FirstAltBg => IsBlue ? Color.FromArgb(0xE7, 0xED, 0xF3) : Color.FromArgb(0xEC, 0xE9, 0xDC);
        private static Color MutedText => IsBlue ? Color.FromArgb(0x6E, 0x7E, 0x96) : Color.FromArgb(0x7A, 0x70, 0x60);
        private static readonly Color BlueRowA = Color.FromArgb(0xFF, 0xFF, 0xFF);
        private static readonly Color BlueRowB = Color.FromArgb(0xF5, 0xF5, 0xF5);
        private static readonly Color BlueHover = Color.FromArgb(0xE9, 0xEF, 0xF6);
        private static readonly Font HeaderFont = new Font("Segoe UI Semibold", 9f);

        public HardwareInventoryForm()
        {
            Text = "Сводная таблица HARD (домен)";
            ClientSize = new Size(1020, 660);
            MinimumSize = new Size(760, 500);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9F);
            BackColor = UiTheme.DialogBackground;
            FormBorderStyle = FormBorderStyle.Sizable;
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { e.Handled = true; DialogResult = DialogResult.Cancel; Close(); } };

            // ——— 1. Hero (без крестика: у окна есть штатный) ———
            var hero = UiModal.Hero("Сводная таблица HARD", "железо компьютеров из адресной книги (WMI)", "computer");

            _filterWrapper = Helpers.UiSearchBox.Create("Поиск…", txt => { _filterDebounce.Stop(); _filterDebounce.Start(); }, width: 364, height: 27, background: Color.White);
            _filterWrapper.Location = new Point(70, 18);
            _filterWrapper.Size = new Size(364, 27);
            _filterBox = Helpers.UiSearchBox.GetBox(_filterWrapper);
            _lblHint = new Label { Text = "(по имени компьютера)", Location = new Point(70, 48), Size = new Size(364, 18), Font = new Font("Segoe UI", 8F), ForeColor = Color.FromArgb(210, 228, 238), TextAlign = ContentAlignment.MiddleLeft, BackColor = Color.Transparent };
            hero.Controls.Add(_filterWrapper);
            hero.Controls.Add(_lblHint);

            // ——— 2. Content: карточка с таблицей ———
            var content = new Panel { Dock = DockStyle.Fill, AutoScroll = false, BackColor = UiTheme.DialogBackground, Padding = new Padding(14) };
            var cardGrid = UiModal.Card(0, 0, 100, 100, "", "", withHeader: false);
            cardGrid.Dock = DockStyle.Fill;
            cardGrid.Padding = new Padding(14);
            content.Controls.Add(cardGrid);
            try { cardGrid.Region?.Dispose(); cardGrid.Region = null; } catch { }

            _grid = new DoubleBufferedDataGridView
            {
                Dock = DockStyle.Fill,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                MultiSelect = false,
                ReadOnly = true,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false
            };
            ShadowRDP.Helpers.UiGridStyle.ApplyUnified(_grid, 36, 36);
            _grid.BorderStyle = BorderStyle.None;
            _grid.CellBorderStyle = DataGridViewCellBorderStyle.None;
            _grid.GridColor = UiTheme.Background;
            _grid.AdvancedCellBorderStyle.All = DataGridViewAdvancedCellBorderStyle.None;
            _grid.AdvancedColumnHeadersBorderStyle.All = DataGridViewAdvancedCellBorderStyle.None;
            _grid.ShowCellErrors = false;
            _grid.ShowRowErrors = false;
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Computer", HeaderText = "КОМПЬЮТЕР", FillWeight = 110, MinimumWidth = 110 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Cpu", HeaderText = "ПРОЦЕССОР", FillWeight = 220, MinimumWidth = 160 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Ram", HeaderText = "RAM", FillWeight = 70, MinimumWidth = 70 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Disks", HeaderText = "ДИСКИ", FillWeight = 170, MinimumWidth = 140 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Disks2", HeaderText = "ДИСКИ+ (занято/свободно)", FillWeight = 200, MinimumWidth = 150 });
            _grid.CellPainting += OnCellPainting;
            // v3.9.3BD — кромки ячеек кроем в последнем проходе (урок Монитора:
            // чёрная AA-кромка фреймворка после CellPainting на частичных репейнтах).
            _grid.Paint += (s, e) =>
            {
                try
                {
                    if (_grid.Width <= 0) return;
                    e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
                    int hov = -1;
                    try { hov = UiGridStyle.HoverRowFor(_grid); } catch { }
                    int first = 0;
                    try { if (_grid.Rows.Count > 0) first = _grid.FirstDisplayedScrollingRowIndex; } catch { }
                    foreach (var col in _grid.Columns.Cast<DataGridViewColumn>())
                    {
                        if (!col.Visible) continue;
                        bool isHost = col.Name == "Computer";
                        for (int r = first; r < _grid.Rows.Count; r++)
                        {
                            var row = _grid.Rows[r];
                            if (row.IsNewRow) continue;
                            var rc = _grid.GetCellDisplayRectangle(col.Index, r, false);
                            if (rc.Width <= 0 || rc.Height <= 0) break;
                            bool sel = false;
                            try { sel = row.Selected; } catch { }
                            Color bg;
                            if (isHost) bg = (r % 2 == 0 ? FirstBg : FirstAltBg);
                            else if (sel) bg = IsBlue ? BlueHover : UiTheme.AccentSoft;
                            else if (hov == r) bg = IsBlue ? BlueHover : UiTheme.HoverRow;
                            else bg = (r % 2 == 0 ? (IsBlue ? BlueRowA : UiTheme.RowBase) : (IsBlue ? BlueRowB : UiTheme.RowAlt));
                            using var br = new SolidBrush(bg);
                            e.Graphics.FillRectangle(br, rc.X, rc.Y, rc.Width, 1);
                            e.Graphics.FillRectangle(br, rc.X, rc.Y + rc.Height - 1, rc.Width, 1);
                            e.Graphics.FillRectangle(br, rc.X, rc.Y, 1, rc.Height);
                            e.Graphics.FillRectangle(br, rc.X + rc.Width - 1, rc.Y, 1, rc.Height);
                        }
                    }
                }
                catch { }
            };
            _grid.Resize += (s, e) => { try { _grid.Invalidate(); } catch { } };
            cardGrid.Controls.Add(_grid);
            ShadowScroll.AttachGrid(_grid);

            // ——— 3. Status bar ———
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

            // ——— 4. Footer ———
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

            hero.Resize += (s, e) => PositionSearch();
            Shown += (s, e) =>
            {
                PositionSearch();
                _closeBtn.Left = footer.ClientSize.Width - 16 - _closeBtn.Width;
                _closeBtn.Top = 15;
                ActiveControl = _grid;
                _grid.Focus();
                try { BeginInvoke(new Action(() => { try { Refresh(); } catch { } })); } catch { }
                // v3.9.3BD — без автоскана: показываем последний скан,
                // живой опрос только по кнопке «Сканировать».
                LoadCache();
            };

            void PositionSearch()
            {
                int x = Math.Max(16, hero.Width - 380);
                _filterWrapper.Location = new Point(x, 18);
                _lblHint.Location = new Point(x, 48);
            }
            PositionSearch();
        }

        // Роспись ячеек в духе Монитора: белая шапка — от ApplyUnified,
        // зебра строк, бежевая первая колонка с точкой статуса, ClearType.
        private void OnCellPainting(object? sender, DataGridViewCellPaintingEventArgs e)
        {
            var grid = sender as DataGridView;
            if (grid == null || e == null) return;
            try
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SystemDefault;
                // v3.9.3BD — шапка красится здесь детерминированно (белый фон,
                // тёмный текст), а не парой PaintHeaders+фреймворк: иначе на
                // некоторых машинах лезут тёмные рамки ячеек шапки.
                if (e.RowIndex == -1 && e.ColumnIndex >= 0)
                {
                    // v3.9.3CC — фон шапки через общий хелпер (синяя — градиент);
                    // rect с нахлёстом 1px — как раньше через Overlap.
                    var cb = e.CellBounds;
                    UiGridStyle.PaintHeaderBg(e, new Rectangle(cb.X, cb.Y, cb.Width + 1, cb.Height + 1));
                    var htr = new Rectangle(e.CellBounds.X + 12, e.CellBounds.Y, Math.Max(0, e.CellBounds.Width - 14), e.CellBounds.Height);
                    TextRenderer.DrawText(e.Graphics, grid.Columns[e.ColumnIndex].HeaderText, HeaderFont, htr, InkText,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
                    e.Handled = true;
                    return;
                }
                if (e.RowIndex < 0 || e.RowIndex >= grid.Rows.Count) return;
                var row = grid.Rows[e.RowIndex];
                if (row.IsNewRow) return;
                string col = grid.Columns[e.ColumnIndex].Name;
                bool selected = (e.State & DataGridViewElementStates.Selected) != 0;
                var data = row.Tag as HardwareInventoryService.HardwareEntry;
                bool offline = data != null && !data.Online;
                Color baseBg = selected ? (IsBlue ? BlueHover : UiTheme.AccentSoft)
                    : (e.RowIndex % 2 == 0 ? (IsBlue ? BlueRowA : UiTheme.RowBase) : (IsBlue ? BlueRowB : UiTheme.RowAlt));
                try
                {
                    if (!selected && UiGridStyle.HoverRowFor(grid) == e.RowIndex)
                        baseBg = IsBlue ? BlueHover : UiTheme.HoverRow;
                }
                catch { }
                if (col == "Computer")
                {
                    Color fbg = e.RowIndex % 2 == 0 ? FirstBg : FirstAltBg;
                    UiGridStyle.PaintCellBaseOverlap(e, fbg);
                    string hn = e.Value?.ToString() ?? "";
                    var dot = offline ? ErrDot : OkDot;
                    e.Graphics.FillEllipse(new SolidBrush(dot),
                        e.CellBounds.X + 8, e.CellBounds.Y + (e.CellBounds.Height - 6) / 2, 6, 6);
                    var htr = new Rectangle(e.CellBounds.X + 20, e.CellBounds.Y, Math.Max(0, e.CellBounds.Width - 26), e.CellBounds.Height);
                    TextRenderer.DrawText(e.Graphics, hn, HostFont, htr, InkText,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
                    e.Handled = true;
                    return;
                }
                UiGridStyle.PaintCellBaseOverlap(e, baseBg);
                string txt = e.Value?.ToString() ?? "";
                Color fg = offline ? ErrText
                    : (col == "Disks" || col == "Disks2") && txt.Contains("!мало") ? ErrText
                    : (col == "Cpu" || col == "Ram") ? InkText : MutedText;
                var tr = new Rectangle(e.CellBounds.X + 6, e.CellBounds.Y, Math.Max(0, e.CellBounds.Width - 12), e.CellBounds.Height);
                TextRenderer.DrawText(e.Graphics, txt, BodyFont, tr, fg,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
                e.Handled = true;
            }
            catch { }
        }

        private void LoadCache()
        {
            try
            {
                var cache = HardwareInventoryService.LoadCache();
                if (cache == null || cache.Entries.Count == 0)
                {
                    _statusLabel.Text = "Нет данных — нажмите «Сканировать».";
                    return;
                }
                _allEntries = cache.Entries;
                PopulateGrid();
                _exportBtn.Enabled = true;
                int online = _allEntries.Count(e => e.Online);
                _statusLabel.Text = $"Данные от {cache.Time:dd.MM HH:mm}. Компьютеров: {_allEntries.Count}, в сети: {online}. Для обновления нажмите «Сканировать».";
            }
            catch (Exception ex)
            {
                _statusLabel.Text = $"Ошибка чтения кэша: {ex.Message}";
            }
        }

        private async Task ScanAsync()
        {
            _scanBtn.Enabled = false;
            _cancelBtn.Enabled = true;
            _exportBtn.Enabled = false;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            _allEntries.Clear();
            _grid.Rows.Clear();
            _statusLabel.Text = "Сканирование (WMI)…";

            try
            {
                var computers = AddressBookStore.Load()
                    .Select(b => b.Computer)
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .ToList();
                var prog = new Progress<(int done, int total, string host)>(t =>
                {
                    try { _statusLabel.Text = $"Опрошено {t.done} из {t.total}… {t.host}"; } catch { }
                });
                _allEntries = await Task.Run(() => HardwareInventoryService.Scan(computers, prog, token), token);
                HardwareInventoryService.SaveCache(_allEntries);
                PopulateGrid();
                _exportBtn.Enabled = _allEntries.Count > 0;
                int online = _allEntries.Count(e => e.Online);
                _statusLabel.Text = $"Готово ({DateTime.Now:HH:mm}). Компьютеров: {_allEntries.Count}, в сети: {online}";
            }
            catch (OperationCanceledException)
            {
                _statusLabel.Text = "Отменено.";
            }
            catch (Exception ex)
            {
                _statusLabel.Text = $"Ошибка: {ex.Message}";
                Logger.Warn($"HardwareInventory scan failed: {ex.Message}");
            }
            finally
            {
                _scanBtn.Enabled = true;
                _cancelBtn.Enabled = false;
            }
        }

        private void PopulateGrid()
        {
            _grid.SuspendLayout();
            try
            {
                _grid.Rows.Clear();
                foreach (var e in _allEntries)
                {
                    string cpu = !e.Online ? "не в сети" : string.IsNullOrWhiteSpace(e.Cpu) ? "—" : e.Cpu + (string.IsNullOrWhiteSpace(e.Cores) ? "" : $" ({e.Cores})");
                    string ram = !e.Online ? "—" : e.RamGb > 0 ? $"{e.RamGb:0.#} ГБ" : "—";
                    string disks = !e.Online ? "—" : e.Disks.Count == 0 ? "—"
                        : $"{e.Disks.Count}: " + string.Join(", ", e.Disks.Select(d => $"{d.Mount} {FmtGb(d.TotalGb)}"));
                    string disks2 = !e.Online ? "—" : e.Disks.Count == 0 ? "—"
                        : string.Join(" · ", e.Disks.Select(d => $"{d.Mount} {FmtGb(d.UsedGb)}/{FmtGb(d.FreeGb)}" + (d.PFree < 10 ? " !мало" : "")));
                    int idx = _grid.Rows.Add(e.Computer, cpu, ram, disks, disks2);
                    _grid.Rows[idx].Tag = e;
                }
            }
            finally { _grid.ResumeLayout(); }
        }

        private static string FmtGb(double gb)
        {
            if (gb >= 1024) return $"{gb / 1024:0.0} ТБ";
            return $"{gb:0} ГБ";
        }

        private void ApplyFilter()
        {
            var term = _filterBox.Text.Trim();
            foreach (DataGridViewRow r in _grid.Rows)
            {
                if (string.IsNullOrEmpty(term)) { r.Visible = true; continue; }
                var computer = r.Cells["Computer"].Value?.ToString() ?? "";
                var cpu = r.Cells["Cpu"].Value?.ToString() ?? "";
                r.Visible = computer.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                            cpu.Contains(term, StringComparison.OrdinalIgnoreCase);
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
                FileName = "hardware_inventory.csv",
                DefaultExt = "csv"
            };
            if (UiModal.ShowDimmed(this, sfd) != DialogResult.OK) return;

            try
            {
                using var sw = new StreamWriter(sfd.FileName, false, new UTF8Encoding(true));
                var headers = _grid.Columns.Cast<DataGridViewColumn>()
                    .Select(c => c.HeaderText).ToList();
                sw.WriteLine(string.Join(";", headers));
                foreach (DataGridViewRow row in _grid.Rows)
                {
                    if (!row.Visible) continue;
                    var cells = row.Cells.Cast<DataGridViewCell>()
                        .Select(c => EscapeCsv(c.Value?.ToString() ?? "")).ToList();
                    sw.WriteLine(string.Join(";", cells));
                }
                _statusLabel.Text = $"Экспортировано: {sfd.FileName}";
                Logger.Info($"HardwareInventory CSV export: {sfd.FileName}");
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
    }
}
