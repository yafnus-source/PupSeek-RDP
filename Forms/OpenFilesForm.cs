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
    // v4.3.3 — редизайн в стиле FTP-эталона: градиентный hero, фильтр и
    // таблица каждый в своей карточке, действия в футере. Вся логика
    // (скан, фильтр, закрытие дескрипторов, персистентность) без изменений.
    //
    // Колонки: Компьютер | Файл/папка | Пользователь | #Блокировок | Тип.
    // FileId + компьютер хранятся в Tag строки, поэтому сортировка не ломает
    // закрытие. Фильтрация клиентская: поиск по имени/пользователю.
    public class OpenFilesForm : Form
    {
        private readonly TextBox _filterBox;
        private readonly Panel _filterWrapper;
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

        private static Color RowAlt => ShadowRDP.Helpers.UiTheme.RowAlt;
        private static Color RowBase => ShadowRDP.Helpers.UiTheme.RowBase;

        public OpenFilesForm()
        {
            Text = "Открытые файлы (домен)";
            ClientSize = new Size(920, 646);
            MinimumSize = new Size(680, 506);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9F);
            BackColor = ShadowRDP.Helpers.UiTheme.DialogBackground;
            FormBorderStyle = FormBorderStyle.Sizable;
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { e.Handled = true; DialogResult = DialogResult.Cancel; Close(); } };

            // ——— 1. Hero (без крестика: у окна есть штатный) ———
            var hero = UiModal.Hero("Открытые файлы", "все файловые серверы домена", "list");

            // ——— 2. Content: filter card + files card ———
            var content = new Panel { Dock = DockStyle.Fill, AutoScroll = false, BackColor = ShadowRDP.Helpers.UiTheme.DialogBackground };

            var cardFiles = UiModal.Card(0, 0, 100, 100, "", "", withHeader: false);
            content.Controls.Add(cardFiles);
            // v3.9.3Y — UiModal.Card гладкая сама, Region давал лесенку.
            try { cardFiles.Region?.Dispose(); cardFiles.Region = null; } catch { }

            // Search lives in the hero (right side): same box + debounced
            // filter, no anchors — position comes from the real hero width
            // (Shown/Resize), otherwise anchors capture garbage mid-layout.
            _filterWrapper = Helpers.UiSearchBox.Create("Поиск…", txt => { _filterDebounce.Stop(); _filterDebounce.Start(); }, width: 364, height: 27, background: Color.White);
            _filterWrapper.Location = new Point(70, 18);
            _filterWrapper.Size = new Size(364, 27);
            _filterBox = Helpers.UiSearchBox.GetBox(_filterWrapper);
            _lblHint = new Label { Text = "(по имени файла/папки или пользователю)", Location = new Point(70, 48), Size = new Size(364, 18), Font = new Font("Segoe UI", 8F), ForeColor = Color.FromArgb(210, 228, 238), TextAlign = ContentAlignment.MiddleLeft, BackColor = Color.Transparent };
            hero.Controls.Add(_filterWrapper);
            hero.Controls.Add(_lblHint);

            // === Grid (unified white style) inside its card ===
            _grid = new DoubleBufferedDataGridView
            {
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                MultiSelect = true,
            };
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Computer", HeaderText = "Компьютер", FillWeight = 70, MinimumWidth = 90 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Path", HeaderText = "Файл / папка", FillWeight = 180, MinimumWidth = 150 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "User", HeaderText = "Пользователь", FillWeight = 80, MinimumWidth = 95 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Locks", HeaderText = "#Блокировок", FillWeight = 30, MinimumWidth = 90 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Type", HeaderText = "Тип", FillWeight = 30, MinimumWidth = 50 });
            ShadowRDP.Helpers.UiGridStyle.ApplyUnified(_grid, 36, 36);
            // Чётные строки вдвое светлее темного RowBase (#FBF7EE -> #FDFBF7,
            // половина пути к белому). Только эта таблица, тему не трогаем.
            _grid.DefaultCellStyle.BackColor = Color.FromArgb(0xFD, 0xFB, 0xF7);
            // Size the "Тип" column from the actual header text measurement so
            // the full label fits at any display DPI (SystemAware app: fonts
            // scale with DPI while column widths are physical pixels).
            using (var headerFont = new Font("Segoe UI Semibold", 9F))
            {
                var minW = TextRenderer.MeasureText("Тип", headerFont).Width + 14;
                if (_grid.Columns["Type"]!.MinimumWidth < minW)
                    _grid.Columns["Type"]!.MinimumWidth = minW;
            }
            cardFiles.Controls.Add(_grid);
            RoundCorners(_grid, 7);
            cardFiles.Resize += (s, e) => { FitCardChild(cardFiles, _grid); try { cardFiles.Invalidate(true); } catch { } };
            _grid.Resize += (s, e) => { try { _grid.Invalidate(); } catch { } };
            _grid.ColumnWidthChanged += (s, e) => { try { _grid.Invalidate(); } catch { } };
            // v4.4.0 — своя тонкая полоса вместо системной (пилот).
            ShadowScroll.AttachGrid(_grid);

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

            _grid.CellPainting += PaintTypeCell;

            // ——— 3. Status bar (thin strip above the footer) ———
            var statusPanel = new Panel { Dock = DockStyle.Bottom, Height = 24, BackColor = ShadowRDP.Helpers.UiTheme.DialogCard };
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
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = ShadowRDP.Helpers.UiTheme.DialogCard };
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
            _closeAllBtn = UiButtons.MakeCompactFilled("Закрыть у всех", w("Закрыть у всех"), danger: true);
            _closeAllBtn.Location = new Point(_cancelBtn.Left + _cancelBtn.Width + 8, 15);
            _closeAllBtn.Enabled = false;
            _closeAllBtn.Click += OnCloseAllClicked;
            _closeBtn = UiButtons.MakeCompactFilled("Закрыть", w("Закрыть"));
            _closeBtn.Click += (s, e) => Close();
            footer.Controls.AddRange(new Control[] { _scanBtn, _cancelBtn, _closeAllBtn, _closeBtn });
            footer.Resize += (s, e) =>
            {
                _closeBtn.Left = footer.ClientSize.Width - 16 - _closeBtn.Width;
                _closeBtn.Top = 15;
            };
            _grid.SelectionChanged += (s, e) => _closeAllBtn.Enabled = _grid.SelectedRows.Count > 0;

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

            content.Resize += (s, e) => LayoutContent();
            hero.Resize += (s, e) => PositionSearch();
            Shown += async (s, e) =>
            {
                LayoutContent();
                PositionSearch();
                _closeBtn.Left = footer.ClientSize.Width - 16 - _closeBtn.Width;
                _closeBtn.Top = 15;
                try { BeginInvoke(new Action(() => { try { Refresh(); } catch { } })); } catch { }
                await ScanAsync();
            };

            // Single files card fills the content; search sits in the hero.
            void LayoutContent()
            {
                int W = content.ClientSize.Width, H = content.ClientSize.Height;
                cardFiles.Bounds = new Rectangle(14, 14, Math.Max(100, W - 28), Math.Max(100, H - 28));
            }
            LayoutContent();

            // Search at the hero's right edge (no anchors — they capture
            // garbage mid-layout; positions come from the real width).
            void PositionSearch()
            {
                int x = Math.Max(16, hero.Width - 380);
                _filterWrapper.Location = new Point(x, 18);
                _lblHint.Location = new Point(x, 48);
            }
            PositionSearch();
        }

        // Child of a headerless card: uniform 14px padding on all sides.
        private static void FitCardChild(Panel card, Control child)
        {
            child.Bounds = new Rectangle(14, 14,
                Math.Max(50, card.ClientSize.Width - 28),
                Math.Max(50, card.ClientSize.Height - 28));
        }

        // Скругление углов: режем Region контрола по скруглённому rect.
        private static void RoundCorners(Control c, int radius)
        {
            void Update()
            {
                if (c.IsDisposed || c.Width <= 0 || c.Height <= 0) return;
                c.Region?.Dispose();
                using var path = new System.Drawing.Drawing2D.GraphicsPath();
                var r = new Rectangle(0, 0, c.Width, c.Height);
                int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
                path.AddArc(r.X, r.Y, d, d, 180, 90);
                path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                c.Region = new Region(path);
            }
            c.Resize += (s, e) => Update();
            Update();
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

        // Paint the "Тип" column ourselves: a small file/folder glyph + label.
        // Selection and row-hover keep working because UiGridStyle bakes
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
