using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ShadowRDP.Controls;
using ShadowRDP.Helpers;
using ShadowRDP.Services;

namespace ShadowRDP.Forms
{
    // "Открытые файлы (домен)" — собирает список всех файлов/папок, открытых
    // на файловых серверах домена, в одну таблицу. Обходит серверы из AD
    // параллельно (SemaphoreSlim + Task.WhenAll, как FindUserDialog), на
    // каждом вызывает NetFileEnum (тот же API, что в оснастке «Общие папки →
    // Открытые файлы»).
    //
    // Колонки: Компьютер | Файл/папка | Пользователь | #Блокировок | Тип.
    // FileId + компьютер хранятся в Tag строки, поэтому сортировка не ломает
    // закрытие. Фильтрация клиентская: поиск по имени/пользователю.
    public class OpenFilesForm : Form
    {
        private readonly TextBox _filterBox;
        private readonly Label _lblFilter;
        private readonly Label _lblHint;
        private readonly Label _scanBtn;
        private readonly Label _cancelBtn;
        private readonly Label _closeAllBtn;
        private readonly Label _closeBtn;
        private readonly DataGridView _grid;
        private readonly Label _statusLabel;
        private CancellationTokenSource? _cts;
        private readonly object _filesLock = new object();
        private List<OpenFilesService.OpenFile> _allFiles = new List<OpenFilesService.OpenFile>();
        private readonly System.Windows.Forms.Timer _saveDebounce = new() { Interval = 400 };
        private readonly System.Windows.Forms.Timer _filterDebounce = new() { Interval = 120 };
        private string? _lastFilterTerm;

        // v3.1.x: WM_SETREDRAW for batch updates in AddGridRows — without it
        // DataGridView fires a repaint per row added (Rows.Add → OnRowsAdded →
        // paint). For 300 rows that's 300 repaints on a single scan batch.
        // With the wrap only ONE repaint happens at the end (same fix as
        // AddressBookForm v2.1.78).
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        private const int WM_SETREDRAW = 0x000B;

        // v3.8.x — restyled the grid to match the "Обмен файлами" (SFTP)
        // table: zebra rows, pastel-blue selection #CCE4F7, flat muted header,
        // 1px #D9D9D9 column dividers, 1px #C0C0C0 outer border. Mirror of
        // SftpForm's ListView OwnerDraw palette.
        private static Color RowAlt => ShadowRDP.Helpers.UiTheme.RowAlt;
        private static Color RowBase => ShadowRDP.Helpers.UiTheme.RowBase;
        private static readonly Color SelectionColor = Color.FromArgb(0xCC, 0xE4, 0xF7);
        private static readonly Color FieldBorder = Color.FromArgb(0xC0, 0xC0, 0xC0);
        private static readonly Color Divider = Color.FromArgb(0xD9, 0xD9, 0xD9);

        public OpenFilesForm()
        {
            Text = "Открытые файлы (домен)";
            ClientSize = new Size(920, 646);
            MinimumSize = new Size(680, 506);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9F);
            BackColor = ShadowRDP.Helpers.UiTheme.Background;
            FormBorderStyle = FormBorderStyle.Sizable;

            var headerPanel = Helpers.UiHeader.Create("Открытые файлы", "все файловые серверы домена", "list", ShadowRDP.Helpers.UiTheme.HeaderBar);
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
            _lblHint = new Label { Text = "(по имени файла/папки или пользователю)", Location = new Point(336, 83), Size = new Size(230, 22), ForeColor = Color.Gray, TextAlign = ContentAlignment.MiddleLeft };

            Controls.AddRange(new Control[] { _lblFilter, _filterBox, _lblHint });

            var btnFont = new Font("Segoe UI Semibold", 9F);
            _scanBtn = UiButtons.MakeCompactFilled("Сканировать", TextRenderer.MeasureText("Сканировать", btnFont).Width + 28);
            _cancelBtn = UiButtons.MakeCompactFilled("Прервать", TextRenderer.MeasureText("Прервать", btnFont).Width + 28);
            _scanBtn.Location = new Point(650, 83);
            _scanBtn.Click += async (s, e) => await ScanAsync();
            _cancelBtn.Location = new Point(760, 83);
            _cancelBtn.Enabled = false;
            _cancelBtn.Click += (s, e) => OnCancel();
            Controls.AddRange(new Control[] { _scanBtn, _cancelBtn });

            // === Grid ===
            // DoubleBufferedDataGridView for smooth scrolling (see
            // OptimizedControls.cs). Every border style is None, so the grid
            // never paints its own lines.
            _grid = new DoubleBufferedDataGridView
            {
                Location = new Point(16, 120),
                Size = new Size(888, 480),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                AllowUserToAddRows = false,
                AllowUserToResizeRows = false,
                ReadOnly = true,
                RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor = RowBase,
                BorderStyle = BorderStyle.None,
                ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
                CellBorderStyle = DataGridViewCellBorderStyle.None,
                // GridColor is a harmless fallback: every border edge is None
                // (CellBorderStyle, AdvancedCellBorderStyle and dividers
                // below), so the grid never paints grid lines.
                GridColor = Divider,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = true,
                // v3.8.x — SFTP-style palette: zebra rows (White / #F7F9FC),
                // pastel-blue selection #CCE4F7 with dark text, Segoe UI 10F.
                Font = new Font("Segoe UI", 10F),
                DefaultCellStyle = {
                    BackColor = RowBase,
                    SelectionBackColor = SelectionColor,
                    SelectionForeColor = ShadowRDP.Helpers.UiTheme.Foreground,
                    Font = new Font("Segoe UI", 10F)
                },
                ColumnHeadersDefaultCellStyle = {
                    BackColor = ShadowRDP.Helpers.UiTheme.Muted,
                    ForeColor = ShadowRDP.Helpers.UiTheme.Foreground,
                    Font = new Font("Segoe UI", 10F)
                },
                RowTemplate = { Height = 22 }
            };
            _grid.EnableHeadersVisualStyles = false;
            // Fully borderless grid: every border edge (cells and column
            // headers) is None, so the DataGridView paints no lines at all.
            _grid.AdvancedCellBorderStyle.All = DataGridViewAdvancedCellBorderStyle.None;
            _grid.AdvancedCellBorderStyle.Top = DataGridViewAdvancedCellBorderStyle.None;
            _grid.AdvancedCellBorderStyle.Bottom = DataGridViewAdvancedCellBorderStyle.None;
            _grid.AdvancedCellBorderStyle.Left = DataGridViewAdvancedCellBorderStyle.None;
            _grid.AdvancedCellBorderStyle.Right = DataGridViewAdvancedCellBorderStyle.None;
            _grid.AdvancedColumnHeadersBorderStyle.All = DataGridViewAdvancedCellBorderStyle.None;
            _grid.AdvancedColumnHeadersBorderStyle.Bottom = DataGridViewAdvancedCellBorderStyle.None;
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Computer", HeaderText = "Компьютер", FillWeight = 70, MinimumWidth = 90 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Path", HeaderText = "Файл / папка", FillWeight = 180, MinimumWidth = 150 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "User", HeaderText = "Пользователь", FillWeight = 80, MinimumWidth = 95 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Locks", HeaderText = "#Блокировок", FillWeight = 30, MinimumWidth = 90 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Type", HeaderText = "Тип", FillWeight = 30, MinimumWidth = 50 });
            // Zero the cell/row dividers too — separate from CellBorderStyle
            // and a common source of 1px lines between rows AND columns.
            foreach (DataGridViewColumn c in _grid.Columns) c.DividerWidth = 0;
            _grid.RowTemplate.DividerHeight = 0;
            // Size the "Тип" column from the actual header text measurement so
            // the full label fits at any display DPI (SystemAware app: fonts
            // scale with DPI while column widths are physical pixels).
            using (var headerFont = new Font("Segoe UI", 10F))
            {
                var minW = TextRenderer.MeasureText("Тип", headerFont).Width + 14;
                if (_grid.Columns["Type"]!.MinimumWidth < minW)
                    _grid.Columns["Type"]!.MinimumWidth = minW;
            }

            // v3.9.0 — right-click menu shown as a themed popup.
            _grid.CellMouseDown += (sender, args) =>
            {
                if (args.Button != MouseButtons.Right) return;
                if (args.RowIndex < 0) return;
                _grid.ClearSelection();
                _grid.Rows[args.RowIndex].Selected = true;
                var openFile = GetSelectedOpenFile();
                var menu = new ShadowRDP.Helpers.ThemedContextMenu();
                menu.AddHeader("Открытые файлы");
                if (openFile != null && !string.IsNullOrEmpty(openFile.Path))
                {
                    if (openFile.IsFolder())
                        menu.AddItem("Перейти к папке", "computer", () => OpenInExplorer(goToFile: false));
                    else
                        menu.AddItem("Перейти к файлу", "computer", () => OpenInExplorer(goToFile: true));
                }
                menu.AddSeparator();
                menu.AddItem("Закрыть у всех", "stop", () => OnCloseAllClicked(_grid, EventArgs.Empty));
                menu.AddItem("Копировать путь", "copy", () =>
                {
                    if (_grid.SelectedRows.Count == 0) return;
                    var v = _grid.SelectedRows[0].Cells["Path"].Value?.ToString() ?? "";
                    try { Clipboard.SetText(v); } catch { }
                });
                menu.Show(this, Control.MousePosition);
            };

            // v3.8.x — SFTP-style table (flat muted headers, #CCE4F7 hover,
            // column dividers, outer border) replaces the shared UiGridStyle
            // look. GridColumnOrder.Wire is kept inside StyleGridLikeSftp so
            // drag-to-reorder columns still works.
            StyleGridLikeSftp();
            _grid.CellPainting += PaintTypeCell;
            _grid.CellPainting += PaintRowDividers;
            Controls.Add(_grid);

            // === Bottom row ===
            _statusLabel = new Label
            {
                Location = new Point(16, 606),
                Size = new Size(620, 22),
                ForeColor = Color.Gray,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
                Text = "Нажмите «Сканировать»."
            };
            Controls.Add(_statusLabel);

            _closeAllBtn = MakeButton("Закрыть у всех", new Font("Segoe UI", 10F), danger: true);
            _closeAllBtn.Location = new Point(702, 604);
            _closeAllBtn.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            _closeAllBtn.Enabled = false;
            _closeAllBtn.Click += OnCloseAllClicked;
            _closeBtn = MakeButton("Закрыть", new Font("Segoe UI", 10F));
            _closeBtn.Location = new Point(842, 604);
            _closeBtn.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            _closeBtn.Click += (s, e) => Close();
            Controls.AddRange(new Control[] { _closeAllBtn, _closeBtn });
            _grid.SelectionChanged += (s, e) => _closeAllBtn.Enabled = _grid.SelectedRows.Count > 0;

            Shown += (s, e) => SyncLayout();
            Shown += async (s, e) => await ScanAsync();
            Resize += (s, e) => SyncLayout();
            SyncLayout();

            // Restore window size + grid column widths; save on close AND
            // whenever the user resizes the window or drags a column, so the
            // layout survives even if the app exits while this dialog is open
            // (the tray-exit path can skip per-form FormClosing).
            ShadowRDP.Services.WindowStateStore.Register(this, "openFiles");
            LoadColumnWidths();
            FormClosing += (s, e) => SaveColumnWidths();
            Resize += (s, e) => { _saveDebounce.Stop(); _saveDebounce.Start(); };
            _grid.ColumnWidthChanged += (s, e) => { _saveDebounce.Stop(); _saveDebounce.Start(); };
            _saveDebounce.Tick += (s, e) =>
            {
                _saveDebounce.Stop();
                if (IsDisposed) return;
                SaveColumnWidths();
                ShadowRDP.Services.WindowStateStore.Save(this, "openFiles");
            };
            _filterDebounce.Tick += (s, e) =>
            {
                _filterDebounce.Stop();
                if (IsDisposed) return;
                ApplyFilter();
            };
            FormClosed += (s, e) =>
            {
                _filterDebounce.Stop();
                _filterDebounce.Dispose();
            };
        }

        // === Per-form column width persistence ===
        // Stored in column_widths.json (shared with the other forms, keyed by
        // form name so grids don't collide). Mirrors AddressBookForm.
        private const string ColumnWidthsFormKey = "openFiles";

        private void LoadColumnWidths()
        {
            try
            {
                var path = AppPaths.ColumnWidthsPath;
                if (!File.Exists(path)) return;
                var json = File.ReadAllText(path);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty(ColumnWidthsFormKey, out var formEntry)) return;
                foreach (var prop in formEntry.EnumerateObject())
                {
                    if (!_grid.Columns.Contains(prop.Name)) continue;
                    int w = prop.Value.GetInt32();
                    if (w < 40) continue;  // sanity floor
                    // Restore via FillWeight (proportion), not Width: in Fill
                    // mode any subsequent grid resize redistributes columns by
                    // FillWeight, silently discarding explicit Widths. Setting
                    // FillWeight keeps the saved proportions through resizes.
                    _grid.Columns[prop.Name]!.FillWeight = w;
                }
                Logger.Info("OpenFiles LoadColumnWidths: restored proportions");
            }
            catch (Exception ex)
            {
                Logger.Warn($"OpenFiles LoadColumnWidths failed: {ex.Message}");
            }
        }

        private void SaveColumnWidths()
        {
            try
            {
                Dictionary<string, Dictionary<string, int>> data;
                var path = AppPaths.ColumnWidthsPath;
                if (File.Exists(path))
                {
                    var existing = File.ReadAllText(path);
                    data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(existing)
                        ?? new Dictionary<string, Dictionary<string, int>>();
                }
                else
                {
                    data = new Dictionary<string, Dictionary<string, int>>();
                }
                var perForm = new Dictionary<string, int>();
                foreach (DataGridViewColumn c in _grid.Columns)
                {
                    perForm[c.Name] = c.Width;
                }
                data[ColumnWidthsFormKey] = perForm;

                var opts = new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(data, opts));
                Logger.Info("OpenFiles SaveColumnWidths: saved");
            }
            catch (Exception ex)
            {
                Logger.Warn($"OpenFiles SaveColumnWidths failed: {ex.Message}");
            }
        }

        // Self-painted flat button — same look as UiButtons.MakeFlat, but the
        // text is drawn manually. A DISABLED Label renders its text in the
        // system GrayText colour regardless of ForeColor; that dark gray
        // reads as black on this form's light background. Drawing the text
        // ourselves lets the disabled state show light gray instead.
        private static Label MakeButton(string text, Font font, bool danger = false)
        {
            var lbl = new Label
            {
                Text = "",
                AutoSize = false,
                Font = font,
                Cursor = Cursors.Hand,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                BackColor = Color.Transparent
            };
            var ts = TextRenderer.MeasureText(text, font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
            lbl.Size = new Size(ts.Width + 24, Math.Max(ts.Height + 16, 22));

            using var path = Helpers.UiButtons.RoundedRect(new Rectangle(0, 0, lbl.Width, lbl.Height), 1);
            lbl.Region = new Region(path);

            Color fgBase = danger ? Helpers.UiTheme.Danger : Helpers.UiTheme.MutedForeground;
            Color fgDisabled = Color.FromArgb(0xA8, 0xA8, 0xA8);
            bool hovered = false;

            lbl.Paint += (s, e) =>
            {
                if (hovered && lbl.Enabled)
                {
                    using var bg = new SolidBrush(Helpers.UiTheme.HoverBg);
                    e.Graphics.FillRectangle(bg, new Rectangle(0, 0, lbl.Width, lbl.Height));
                }
                var fg = !lbl.Enabled ? fgDisabled : (hovered ? Helpers.UiTheme.HoverText : fgBase);
                TextRenderer.DrawText(e.Graphics, text, font, new Rectangle(0, 0, lbl.Width, lbl.Height),
                    fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            };
            lbl.MouseEnter += (s, e) => { if (lbl.Enabled) { hovered = true; lbl.Invalidate(); } };
            lbl.MouseLeave += (s, e) => { hovered = false; lbl.Invalidate(); };
            lbl.EnabledChanged += (s, e) => lbl.Invalidate();
            return lbl;
        }

        // Positions every row from the current size. MakeFlat buttons are
        // AutoSize, so fixed coordinates overlap at different DPI/widths —
        // this keeps the top row (Фильтр/Тип/Сканировать/Прервать) and the
        // bottom row (статус/Закрыть у всех/Закрыть) inside the form.
        private void SyncLayout()
        {
            var w = ClientSize.Width;
            var h = ClientSize.Height;

            // Bottom row — close anchored right, close-all to its left.
            var bottomY = h - 42;
            _closeBtn.Location = new Point(w - 16 - _closeBtn.Width, bottomY);
            _closeAllBtn.Location = new Point(_closeBtn.Left - 10 - _closeAllBtn.Width, bottomY);
            _statusLabel.Location = new Point(16, bottomY + 2);
            _statusLabel.Size = new Size(Math.Max(120, _closeAllBtn.Left - 16 - 12), 22);

            // Top row — cancel right-aligned, then scan, then filter + hint.
            // All controls sit on the same 22px-high line (aligned Top).
            var topY = 83;
            _cancelBtn.Location = new Point(w - 16 - _cancelBtn.Width, topY);
            _scanBtn.Location = new Point(_cancelBtn.Left - 10 - _scanBtn.Width, topY);
            _lblFilter.Location = new Point(16, topY + 2);

            // Filter box fixed width; hint fills up to the scan button.
            _filterBox.Location = new Point(70, topY);
            _filterBox.Size = new Size(260, 22);

            var hintEnd = _scanBtn.Left - 10;
            _lblHint.Location = new Point(336, topY);
            _lblHint.Size = new Size(Math.Max(0, hintEnd - 336 - 6), 22);
            _lblHint.Visible = hintEnd - 336 - 6 >= 40;

            // Grid between the filter row and the bottom row.
            _grid.Location = new Point(16, 120);
            _grid.Size = new Size(w - 32, Math.Max(120, bottomY - 8 - 120));
        }

        private async Task ScanAsync()
        {
            _grid.SuspendLayout();
            try { _grid.Rows.Clear(); }
            finally { _grid.ResumeLayout(); }
            _grid.Invalidate();
            _allFiles = new List<OpenFilesService.OpenFile>();
            _lastFilterTerm = null;  // re-apply the active filter to fresh data
            _scanBtn.Enabled = false;
            _cancelBtn.Enabled = true;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            _statusLabel.Text = "Получение списка файловых серверов из AD...";
            Logger.Info("OpenFiles: starting domain scan");

            List<string> servers;
            try
            {
                servers = await Task.Run(() => OpenFilesService.GetFileServers());
            }
            catch (Exception ex)
            {
                _statusLabel.Text = $"Ошибка получения списка серверов: {ex.Message}";
                _scanBtn.Enabled = true;
                _cancelBtn.Enabled = false;
                return;
            }

            if (servers.Count == 0)
            {
                _statusLabel.Text = "Файловые серверы не найдены. Проверьте доступ к домену.";
                _scanBtn.Enabled = true;
                _cancelBtn.Enabled = false;
                return;
            }

            int total = servers.Count;
            int done = 0;
            _statusLabel.Text = $"Опрос {total} серверов...";

            try
            {
                await ParallelForEachAsync(servers, 15, token, async (server, ct) =>
                {
                    var result = await Task.Run(() => OpenFilesService.Query(server));
                    Interlocked.Increment(ref done);
                    if (result.TimedOut || !string.IsNullOrEmpty(result.ErrorText))
                    {
                        Logger.Warn($"OpenFiles scan {server}: {result.ErrorText}");
                        UpdateStatusFromBackground($"Опрос: {done}/{total} (ошибка: {server})");
                        return;
                    }
                    var snapshot = result.Files;
                    foreach (var f in snapshot) f.Computer = server;
                    lock (_filesLock) { _allFiles.AddRange(snapshot); }
                    if (snapshot.Count > 0) AddGridRows(snapshot);
                    int fileCount;
                    lock (_filesLock) { fileCount = _allFiles.Count; }
                    UpdateStatusFromBackground($"Опрос: {done}/{total}, открытых файлов: {fileCount}");
                });

                var files = _allFiles.Count(f => !f.IsFolder());
                var folders = _allFiles.Count - files;
                _statusLabel.Text = $"Готово. Проверено {done} серверов, найдено: {_allFiles.Count} (файлов {files}, папок {folders}).";
                Logger.Info($"OpenFiles: domain scan done, {_allFiles.Count} entries from {done} servers");
            }
            catch (OperationCanceledException)
            {
                _statusLabel.Text = $"Прервано. Проверено {done}, найдено: {_allFiles.Count}.";
            }
            catch (Exception ex)
            {
                _statusLabel.Text = $"Ошибка: {ex.Message}";
                Logger.Warn($"OpenFiles scan failed: {ex.Message}");
            }
            finally
            {
                _scanBtn.Enabled = true;
                _cancelBtn.Enabled = false;
                try { _cts?.Dispose(); } catch { }
                _cts = null;
                ApplyFilter();
                // Force a fresh repaint so the last batch of rows renders
                // immediately after the scan.
                _grid.Invalidate();
            }
        }

        private void OnCancel()
        {
            try { _cts?.Cancel(); } catch { }
            _statusLabel.Text = "Прерываю...";
        }

        private void ApplyFilter()
        {
            var term = _filterBox.Text.Trim().ToLowerInvariant();
            if (term == _lastFilterTerm) return;
            _lastFilterTerm = term;

            _grid.SuspendLayout();
            try
            {
                foreach (DataGridViewRow row in _grid.Rows)
                {
                    var visible = true;
                    if (!string.IsNullOrEmpty(term))
                    {
                        var path = row.Cells["Path"].Value?.ToString()?.ToLowerInvariant() ?? "";
                        var user = row.Cells["User"].Value?.ToString()?.ToLowerInvariant() ?? "";
                        visible = path.Contains(term) || user.Contains(term);
                    }
                    row.Visible = visible;
                }
            }
            finally
            {
                _grid.ResumeLayout();
            }
            _grid.Invalidate();
        }

        // v3.8.x — SFTP-style table chrome: flat muted header band with a
        // 1px #D9D9D9 divider at every column's right edge, row hover in the
        // same pastel blue as selection (#CCE4F7), and a 1px #C0C0C0 border
        // around the grid. Keeps column drag-reorder (GridColumnOrder.Wire).
        private void StyleGridLikeSftp()
        {
            GridColumnOrder.Wire(_grid);

            _grid.CellPainting += (sender, e) =>
            {
                if (sender is not DataGridView grid) return;
                if (e is null || e.RowIndex != -1 || e.ColumnIndex < 0) return;

                // Flat continuous header band (no per-cell gaps, like SFTP).
                using var bgBrush = new SolidBrush(ShadowRDP.Helpers.UiTheme.Muted);
                e.Graphics!.FillRectangle(bgBrush, e.CellBounds);

                // Header text — Segoe UI 10F regular, dark, left-aligned.
                var font = _grid.ColumnHeadersDefaultCellStyle.Font;
                var textRect = new Rectangle(e.CellBounds.X + 6, e.CellBounds.Y,
                    e.CellBounds.Width - 6, e.CellBounds.Height);
                TextRenderer.DrawText(e.Graphics, grid.Columns[e.ColumnIndex].HeaderText,
                    font, textRect, ShadowRDP.Helpers.UiTheme.Foreground,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.EndEllipsis);

                // 1px divider on the right edge of every header column.
                using var divPen = new Pen(Divider, 1);
                e.Graphics.DrawLine(divPen, e.CellBounds.Right, e.CellBounds.Top,
                    e.CellBounds.Right, e.CellBounds.Bottom);
                e.Handled = true;
            };

            // Row hover — SFTP hot state is the same #CCE4F7 as selection.
            // Track the single row under the cursor and only restyle on
            // change (per-cell CellMouseEnter/Leave restyles every cell on
            // every mouse move).
            int hoverRow = -1;

            void HighlightRow(int rowIndex)
            {
                if (rowIndex < 0 || rowIndex >= _grid.Rows.Count) return;
                foreach (DataGridViewCell cell in _grid.Rows[rowIndex].Cells)
                {
                    if (cell.Style.BackColor == SelectionColor) continue;
                    cell.Tag = cell.Style.BackColor;
                    cell.Style.BackColor = SelectionColor;
                    cell.Style.SelectionBackColor = SelectionColor;
                }
            }

            void RestoreRow(int rowIndex)
            {
                if (rowIndex < 0 || rowIndex >= _grid.Rows.Count) return;
                foreach (DataGridViewCell cell in _grid.Rows[rowIndex].Cells)
                {
                    Color orig = cell.Tag is Color c
                        ? c
                        : ((rowIndex % 2 == 0) ? RowBase : RowAlt);
                    cell.Style.BackColor = orig;
                    cell.Style.SelectionBackColor = SelectionColor;
                }
            }

            _grid.CellMouseMove += (sender, e) =>
            {
                if (e.RowIndex < 0 || e.RowIndex == hoverRow) return;
                if (hoverRow >= 0) RestoreRow(hoverRow);
                hoverRow = e.RowIndex;
                HighlightRow(hoverRow);
            };
            _grid.MouseLeave += (sender, e) =>
            {
                if (hoverRow < 0) return;
                RestoreRow(hoverRow);
                hoverRow = -1;
            };

            // 1px #C0C0C0 border around the whole grid (mirrors the SFTP
            // ListView's FieldBorder).
            _grid.Paint += (s, e) =>
            {
                ControlPaint.DrawBorder(e.Graphics, _grid.ClientRectangle,
                    FieldBorder, ButtonBorderStyle.Solid);
            };
        }

        // 1px #D9D9D9 vertical divider on the right edge of every data cell
        // except the last column (SFTP draws dividers only between columns).
        // Runs even for the handled "Тип" cell (CellPainting handlers chain;
        // e.Handled only suppresses the default renderer).
        private void PaintRowDividers(object? sender, DataGridViewCellPaintingEventArgs e)
        {
            if (sender is not DataGridView grid) return;
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            if (e.ColumnIndex >= grid.Columns.Count - 1) return;
            using var divPen = new Pen(Divider, 1);
            e.Graphics!.DrawLine(divPen, e.CellBounds.Right, e.CellBounds.Top,
                e.CellBounds.Right, e.CellBounds.Bottom);
        }

        // Paint the "Тип" column ourselves: a small file/folder glyph + label.
        // Selection and row-hover keep working because StyleGridLikeSftp bakes
        // the hover tint into cell.Style.BackColor, which we honor as the fill.
        // The glyph scales with display DPI (SystemAware app: fonts scale while
        // cell bounds stay physical pixels).
        private void PaintTypeCell(object? sender, DataGridViewCellPaintingEventArgs e)
        {
            if (sender is not DataGridView grid || e.RowIndex < 0) return;
            if (e.ColumnIndex != grid.Columns["Type"].Index) return;

            // AntiAlias is scoped with Save/Restore: the DataGridView shares
            // one Graphics across the whole paint pass, so a leaked AA mode
            // would bleed into the rest of the grid's painting.
            var gstate = e.Graphics.Save();
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var selected = (e.State & DataGridViewElementStates.Selected) != 0;
            var bg = selected ? e.CellStyle.SelectionBackColor : e.CellStyle.BackColor;
            var fg = selected ? e.CellStyle.SelectionForeColor : e.CellStyle.ForeColor;

            using (var bgBrush = new SolidBrush(bg))
                e.Graphics.FillRectangle(bgBrush, e.CellBounds);

            var isFolder = (grid.Rows[e.RowIndex].Tag as OpenFilesService.OpenFile)?.IsFolder() == true;
            var scale = e.Graphics.DpiX / 96f;
            var glyph = GetGlyphs(scale, isFolder);
            var glyphRect = new Rectangle(
                e.CellBounds.X + (int)Math.Round(6 * scale),
                e.CellBounds.Y + (e.CellBounds.Height - glyph.Width) / 2,
                glyph.Width, glyph.Height);
            e.Graphics.DrawImageUnscaled(glyph, glyphRect.X, glyphRect.Y);

            var gap = (int)Math.Round(5 * scale);
            var textRect = new Rectangle(
                glyphRect.Right + gap, e.CellBounds.Y,
                e.CellBounds.Right - glyphRect.Right - gap, e.CellBounds.Height);
            TextRenderer.DrawText(e.Graphics, isFolder ? "Папка" : "Файл",
                e.CellStyle.Font, textRect, fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            e.Graphics.Restore(gstate);

            // Cover the 1px dark edge the DataGridView paints on the top and
            // left of a handled cell with the cell's own background — the grid
            // is fully borderless (every border style is None, see ctor), so
            // the "Тип" column must blend seamlessly with the rest.
            using (var edgeBrush = new SolidBrush(bg))
            {
                e.Graphics.FillRectangle(edgeBrush,
                    e.CellBounds.Left, e.CellBounds.Top,
                    e.CellBounds.Width, 1);
                e.Graphics.FillRectangle(edgeBrush,
                    e.CellBounds.Left, e.CellBounds.Top,
                    1, e.CellBounds.Height);
            }
            e.Handled = true;
        }

        // Outline-only glyphs (каркас, no fill). Colour = the Книга brand
        // blue (#296AA0) softened into a pastel so the icons sit quietly in
        // the file list instead of shouting.
        private static readonly Color GlyphColor = Color.FromArgb(0xBD, 0xD3, 0xE3);

        // Glyphs are pre-rendered once per DPI scale and blitted on paint,
        // instead of rebuilding GraphicsPaths with anti-aliasing for every
        // visible row on every scroll tick.
        private static readonly Dictionary<float, Bitmap[]> GlyphCache = new();

        private static Bitmap GetGlyphs(float scale, bool folder)
        {
            var size = Math.Max(8, (int)Math.Round(14 * scale));
            if (GlyphCache.TryGetValue(size, out var pair)) return folder ? pair[0] : pair[1];

            pair = new Bitmap[2];
            pair[0] = RenderGlyph(size, g => DrawFolderGlyph(g, new Rectangle(0, 0, size, size)));
            pair[1] = RenderGlyph(size, g => DrawFileGlyph(g, new Rectangle(0, 0, size, size)));
            GlyphCache[size] = pair;
            return folder ? pair[0] : pair[1];
        }

        private static Bitmap RenderGlyph(int size, Action<Graphics> draw)
        {
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                draw(g);
            }
            return bmp;
        }

        // Folder skeleton: a small tab outline over a rounded body outline.
        private static void DrawFolderGlyph(Graphics g, Rectangle r)
        {
            var tab = new Rectangle(r.X, r.Y, (int)Math.Round(r.Width * 0.48), (int)Math.Round(r.Height * 0.34));
            var body = new Rectangle(r.X, r.Y + (int)Math.Round(r.Height * 0.26), r.Width, (int)Math.Round(r.Height * 0.74));
            var radius = Math.Max(1, (int)Math.Round(r.Width * 0.15));

            using var pen = new Pen(GlyphColor, Math.Max(1f, r.Width / 9f));
            using (var tabPath = UiButtons.RoundedRect(tab, radius))
                g.DrawPath(pen, tabPath);
            using (var bodyPath = UiButtons.RoundedRect(body, radius))
                g.DrawPath(pen, bodyPath);
        }

        // File skeleton: sheet outline with a folded top-right corner.
        private static void DrawFileGlyph(Graphics g, Rectangle r)
        {
            var w = (int)Math.Round(r.Width * 0.76);
            var h = (int)Math.Round(r.Height * 0.9);
            var x = r.X + (int)Math.Round(r.Width * 0.12);
            var y = r.Y + (int)Math.Round(r.Height * 0.05);
            var fold = (int)Math.Round(r.Width * 0.24);

            using var pen = new Pen(GlyphColor, Math.Max(1f, r.Width / 9f));
            g.DrawLines(pen, new[]
            {
                new Point(x, y + h),
                new Point(x, y + fold),
                new Point(x + fold, y),
                new Point(x + w, y),
                new Point(x + w, y + h),
                new Point(x, y + h)
            });
        }

        private OpenFilesService.OpenFile? GetSelectedOpenFile()
        {
            if (_grid.SelectedRows.Count == 0) return null;
            return _grid.SelectedRows[0].Tag as OpenFilesService.OpenFile;
        }

        private async void OpenInExplorer(bool goToFile)
        {
            var openFile = GetSelectedOpenFile();
            if (openFile == null || string.IsNullOrEmpty(openFile.Path)) return;
            var unc = await Task.Run(() => OpenFilesService.ResolveUncPath(openFile.Computer, openFile.Path));
            if (string.IsNullOrEmpty(unc))
            {
                Logger.Warn($"OpenInExplorer: could not resolve UNC path for {openFile.Computer}\\{openFile.Path}");
                MessageBox.Show(this,
                    $"Не удалось определить сетевой путь к\n«{openFile.Path}»\nна {openFile.Computer}.",
                    "Открыть в проводнике", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try
            {
                var args = goToFile ? $"/select,\"{unc}\"" : $"\"{unc}\"";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = args,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Logger.Warn($"OpenInExplorer failed: {ex.Message}");
            }
        }

        private async void OnCloseAllClicked(object? sender, EventArgs e)
        {
            if (_grid.SelectedRows.Count == 0) return;
            var selected = _grid.SelectedRows.Cast<DataGridViewRow>()
                .Where(r => r.Tag is OpenFilesService.OpenFile)
                .Select(r => (OpenFilesService.OpenFile)r.Tag!)
                .ToList();

            // Close every handle of the selected (computer, path) pairs —
            // one file may be open by several users on the same server.
            var targets = selected
                .GroupBy(f => (f.Computer, f.Path))
                .Select(g => g.First())
                .ToList();

            var totalHandles = selected.Count;
            var text = targets.Count == 1
                ? $"Закрыть '{targets[0].Path}' на {targets[0].Computer} у всех пользователей?\n\n" +
                  $"Будет закрыто {totalHandles} дескриптор(ов).\n" +
                  $"Пользователи, которые сейчас работают с файлом, могут потерять\n" +
                  $"несохранённые данные."
                : $"Закрыть выбранные {targets.Count} файлов/папок у всех пользователей?\n\n" +
                  $"Будет закрыто {totalHandles} дескриптор(ов).\n" +
                  $"Пользователи, которые сейчас работают с файлами, могут потерять\n" +
                  $"несохранённые данные.";

            var r = MessageBox.Show(this, text, "Закрыть у всех",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (r != DialogResult.Yes) return;

            var closed = 0;
            foreach (var t in targets)
            {
                var snapshot = _allFiles.Where(f =>
                    string.Equals(f.Computer, t.Computer, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(f.Path, t.Path, StringComparison.OrdinalIgnoreCase)).ToList();
                closed += OpenFilesService.CloseAll(t.Computer, snapshot, t.Path);
            }
            _statusLabel.Text = $"Закрыто дескрипторов: {closed} из {totalHandles}";
            Logger.Info($"OpenFiles close-all: {closed}/{totalHandles}");

            await ScanAsync();
        }

        // Inserts one server's files in a single marshalled call (instead of
        // one BeginInvoke per file) and adds them inside SuspendLayout so the
        // grid does one layout pass per batch instead of per row.
        private void AddGridRows(List<OpenFilesService.OpenFile> files)
        {
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<List<OpenFilesService.OpenFile>>(AddGridRows), files); } catch { }
                return;
            }
            if (files.Count == 0) return;
            var gridHandle = _grid.Handle;
            SendMessage(gridHandle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
            _grid.SuspendLayout();
            try
            {
                foreach (var f in files)
                {
                    var row = _grid.Rows.Add(f.Computer, f.Path, f.User, f.Locks, f.IsFolder() ? "Папка" : "Файл");
                    // v3.8.x — zebra striping (SFTP style). The running index
                    // stays monotonic across scan batches, so rows keep their
                    // alternating White / #F7F9FC pattern.
                    _grid.Rows[row].DefaultCellStyle.BackColor = (row % 2 == 0) ? RowBase : RowAlt;
                    _grid.Rows[row].DefaultCellStyle.SelectionBackColor = SelectionColor;
                    _grid.Rows[row].Tag = f;
                }
            }
            finally
            {
                _grid.ResumeLayout();
                SendMessage(gridHandle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
            }
            _grid.Invalidate();
        }

        private void UpdateStatusFromBackground(string text)
        {
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<string>(UpdateStatusFromBackground), text); } catch { }
                return;
            }
            _statusLabel.Text = text;
        }

        private static async Task ParallelForEachAsync<T>(IEnumerable<T> items, int maxConcurrency, CancellationToken ct, Func<T, CancellationToken, Task> body)
        {
            using var sem = new SemaphoreSlim(maxConcurrency);
            var tasks = items.Select(async item =>
            {
                await sem.WaitAsync(ct);
                try { await body(item, ct); }
                finally { sem.Release(); }
            });
            await Task.WhenAll(tasks);
        }
    }
}
