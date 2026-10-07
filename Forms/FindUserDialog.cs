using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ShadowRDP.Controls;
using ShadowRDP.Services;

namespace ShadowRDP.Forms
{
    // "Поиск где залогинен пользователь" — вводим имя (или часть), программа
    // обходит все компьютеры AD, запрашивает qwinsta на каждом, ищет совпадение
    // по имени пользователя. Показывает результаты в гриде.
    //
    // Использует тот же параллелизм SemaphoreSlim+Task.WhenAll что и
    // AddressBookForm. Не блокирует UI.
    public class FindUserDialog : Form
    {
        private readonly string _currentComputer;
        private readonly TextBox _userBox;
        private readonly ComboBox _scopeCombo;
        private readonly Label _searchBtn;
        private readonly Label _cancelBtn;
        private readonly Label _closeBtn;
        private readonly DataGridView _grid;
        private readonly Label _statusLabel;
        private CancellationTokenSource? _cts;
        // v4.5.4 — персистентность как в OpenFilesForm: размер окна + ширины
        // колонок (сохраняем на resize/close, чтобы переживало выход).
        private readonly System.Windows.Forms.Timer _saveDebounce = new() { Interval = 400 };

        public FindUserDialog(string currentComputer, string? initialUser = null)
            : this(currentComputer, initialUser, autoStart: false) { }

        // When invoked from a context menu where the user is already
        // selected, pass autoStart=true so the search begins immediately.
        public FindUserDialog(string currentComputer, string? initialUser, bool autoStart)
        {
            _currentComputer = currentComputer;
            Text = "Поиск пользователя в домене";
            ClientSize = new Size(820, 646);
            MinimumSize = new Size(640, 506);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9F);
            BackColor = ShadowRDP.Helpers.UiTheme.DialogBackground;
            FormBorderStyle = FormBorderStyle.Sizable;

            // ——— 1. Hero (стиль OpenFilesForm v4.3.3) ———
            var hero = Helpers.UiModal.Hero("Поиск пользователя", "qwinsta по компьютерам домена", "search");

            // ——— 2. Content: params card + results card ———
            var content = new Panel { Dock = DockStyle.Fill, AutoScroll = false, BackColor = ShadowRDP.Helpers.UiTheme.DialogBackground };

            var cardParams = Helpers.UiModal.Card(0, 0, 100, 100, "Параметры поиска", "search");
            content.Controls.Add(cardParams);
            // v3.9.3Y — UiModal.Card гладкая сама, Region давал лесенку.
            try { cardParams.Region?.Dispose(); cardParams.Region = null; } catch { }

            var cardResults = Helpers.UiModal.Card(0, 0, 100, 100, "", "", withHeader: false);
            content.Controls.Add(cardResults);
            try { cardResults.Region?.Dispose(); cardResults.Region = null; } catch { }


            // Поиск живёт в hero справа (как фильтр в OpenFiles): белый бокс
            // + подсказка. Позиция — от реальной ширины hero (Shown/Resize).
            var searchWrapper = Helpers.UiSearchBox.Create("Поиск…", _ => { }, width: 300, height: 27, background: Color.White);
            _userBox = Helpers.UiSearchBox.GetBox(searchWrapper);
            // Pre-populate with the user from the row that was right-clicked —
            // typical flow is "I see this user, where else are they logged in?".
            // Strip "DOMAIN\" prefix because qwinsta matches on bare sAMAccountName.
            var initialSam = string.IsNullOrEmpty(initialUser)
                ? ""
                : (initialUser.Contains('\\') ? initialUser.Substring(initialUser.IndexOf('\\') + 1) : initialUser);
            _userBox.Text = initialSam;
            _userBox.KeyDown += async (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await OnSearchAsync(); }
            };
            var searchHint = new Label { Text = "(имя или часть имени пользователя)", Location = new Point(70, 48), Size = new Size(300, 18), Font = new Font("Segoe UI", 8F), ForeColor = Color.FromArgb(210, 228, 238), TextAlign = ContentAlignment.MiddleLeft, BackColor = Color.Transparent };
            hero.Controls.Add(searchWrapper);
            hero.Controls.Add(searchHint);

            // Scope — белое rounded-поле в карточке параметров.
            _scopeCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            var scopeWrap = Helpers.UiModal.Field(_scopeCombo, width: 340);
            scopeWrap.Location = new Point(16, 68);
            _scopeCombo.Items.AddRange(new object[]
            {
                $"Только на {_currentComputer}",
                "Все компьютеры AD (медленно)",
                "Все компьютеры из адресной книги"
            });
            _scopeCombo.SelectedIndex = 0;
            cardParams.Controls.Add(Helpers.UiModal.FieldLabel("Где искать:", 16, 50));
            cardParams.Controls.Add(scopeWrap);

            // === Grid (unified white style) inside its card ===
            _grid = new DoubleBufferedDataGridView
            {
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                MultiSelect = true,
            };
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Computer", HeaderText = "Компьютер", FillWeight = 120, MinimumWidth = 90 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "UserName", HeaderText = "Пользователь", FillWeight = 100, MinimumWidth = 95 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "SessionId", HeaderText = "ID", FillWeight = 40, MinimumWidth = 50 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "State", HeaderText = "Состояние", FillWeight = 80, MinimumWidth = 90 });
            ShadowRDP.Helpers.UiGridStyle.ApplyUnified(_grid, 36, 36);
            // Чётные строки светлее RowBase — как в OpenFilesForm, тему не трогаем.
            _grid.DefaultCellStyle.BackColor = Color.FromArgb(0xFD, 0xFB, 0xF7);
            cardResults.Controls.Add(_grid);
            RoundCorners(_grid, 7);
            cardResults.Resize += (s, e) => { FitCardChild(cardResults, _grid); try { cardResults.Invalidate(true); } catch { } };
            _grid.Resize += (s, e) => { try { _grid.Invalidate(); } catch { } };
            _grid.ColumnWidthChanged += (s, e) => { try { _grid.Invalidate(); } catch { } };
            // v4.4.0 — своя тонкая полоса вместо системной.
            ShadowScroll.AttachGrid(_grid);

            // v3.9.0 — right-click menu shown on demand as a themed popup.
            _grid.CellMouseDown += (sender, args) =>
            {
                if (args.Button != MouseButtons.Right) return;
                if (args.RowIndex < 0) return;
                _grid.ClearSelection();
                _grid.Rows[args.RowIndex].Selected = true;
                var menu = Helpers.ComputerContextMenu.BuildThemed(
                    owner: this,
                    computerResolver: () => _grid.SelectedRows.Count == 0 ? "" : _grid.SelectedRows[0].Cells["Computer"].Value?.ToString() ?? "",
                    sessionResolver: () => { if (_grid.SelectedRows.Count == 0) return 0; var v = _grid.SelectedRows[0].Cells["SessionId"].Value; return v != null && int.TryParse(v.ToString(), out var id) ? id : 0; },
                    userResolver: () => _grid.SelectedRows.Count == 0 ? "" : _grid.SelectedRows[0].Cells["UserName"].Value?.ToString() ?? "",
                    onSessionChanged: null);
                menu.Show(this, Control.MousePosition);
            };

            // ——— 3. Status bar (thin strip above the footer) ———
            var statusPanel = new Panel { Dock = DockStyle.Bottom, Height = 24, BackColor = ShadowRDP.Helpers.UiTheme.DialogCard };
            statusPanel.Paint += (s, e) => { using var pen = new Pen(ShadowRDP.Helpers.UiTheme.Border, 1); e.Graphics.DrawLine(pen, 0, 0, statusPanel.Width, 0); };
            _statusLabel = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = Color.Gray,
                Font = new Font("Segoe UI", 8F),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(16, 0, 0, 0),
                Text = "Введите имя пользователя и нажмите «Найти»."
            };
            statusPanel.Controls.Add(_statusLabel);

            // ——— 4. Footer with action buttons ———
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = ShadowRDP.Helpers.UiTheme.DialogCard };
            footer.Paint += (s, e) => { using var pen = new Pen(ShadowRDP.Helpers.UiTheme.Border, 1); e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };
            var btnFont = new Font("Segoe UI Semibold", 9F);
            int w(string text) => TextRenderer.MeasureText(text, btnFont).Width + 28;
            _searchBtn = Helpers.UiButtons.MakeCompactFilled("Найти", w("Найти"));
            _searchBtn.Location = new Point(16, 15);
            _searchBtn.Click += async (s, e) => await OnSearchAsync();
            _cancelBtn = Helpers.UiButtons.MakeCompactFilled("Прервать", w("Прервать"));
            _cancelBtn.Location = new Point(16 + _searchBtn.Width + 8, 15);
            _cancelBtn.Enabled = false;
            _cancelBtn.Click += (s, e) => OnCancel();
            _closeBtn = Helpers.UiButtons.MakeCompactFilled("Закрыть", w("Закрыть"));
            _closeBtn.Click += (s, e) => Close();
            footer.Controls.AddRange(new Control[] { _searchBtn, _cancelBtn, _closeBtn });
            footer.Resize += (s, e) =>
            {
                _closeBtn.Left = footer.ClientSize.Width - 16 - _closeBtn.Width;
                _closeBtn.Top = 15;
            };
            // CancelButton requires IButtonControl; Label doesn't implement it.

            // Dock order: content Fill first, hero Top, then the two Bottom
            // strips (footer added last docks first → very bottom, status
            // above it). Same rule as the FTP sample modal.
            Controls.Add(content);
            Controls.Add(hero);
            Controls.Add(statusPanel);
            Controls.Add(footer);

            var dpiScale = Helpers.UiModal.ApplyDpiScale(this);
            if (dpiScale > 1.001f)
                ClientSize = new Size((int)Math.Round(ClientSize.Width * dpiScale), (int)Math.Round(ClientSize.Height * dpiScale));

            // Restore window size + grid column widths; save on close AND
            // whenever the user resizes the window or drags a column (mirror
            // of OpenFilesForm).
            ShadowRDP.Services.WindowStateStore.Register(this, "findUser");
            LoadColumnWidths();
            FormClosing += (s, e) => SaveColumnWidths();
            Resize += (s, e) => { _saveDebounce.Stop(); _saveDebounce.Start(); };
            _grid.ColumnWidthChanged += (s, e) => { _saveDebounce.Stop(); _saveDebounce.Start(); };
            _saveDebounce.Tick += (s, e) =>
            {
                _saveDebounce.Stop();
                if (IsDisposed) return;
                SaveColumnWidths();
                ShadowRDP.Services.WindowStateStore.Save(this, "findUser");
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
                // When invoked from a context menu where a user is already
                // selected, auto-start the search immediately on form open.
                if (autoStart && !string.IsNullOrEmpty(initialSam))
                {
                    // Tiny delay so the form finishes painting before the
                    // (blocking) search starts.
                    await Task.Delay(50);
                    await OnSearchAsync();
                }
            };

            // Params card (fixed height) on top, results card fills the rest;
            // search sits at the hero's right edge (no anchors — they capture
            // garbage mid-layout; positions come from the real width).
            void LayoutContent()
            {
                int W = content.ClientSize.Width, H = content.ClientSize.Height;
                cardParams.Bounds = new Rectangle(14, 14, Math.Max(100, W - 28), 112);
                int y2 = 14 + 112 + 12;
                cardResults.Bounds = new Rectangle(14, y2, Math.Max(100, W - 28), Math.Max(100, H - y2 - 14));
            }
            LayoutContent();

            void PositionSearch()
            {
                int x = Math.Max(16, hero.Width - 316);
                searchWrapper.Location = new Point(x, 18);
                searchHint.Location = new Point(x, 48);
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

        // === Per-form column width persistence (mirror of OpenFilesForm) ===
        private const string ColumnWidthsFormKey = "findUser";

        private void LoadColumnWidths()
        {
            try
            {
                var path = ShadowRDP.AppPaths.ColumnWidthsPath;
                if (!File.Exists(path)) return;
                var json = File.ReadAllText(path);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty(ColumnWidthsFormKey, out var formEntry)) return;
                foreach (var prop in formEntry.EnumerateObject())
                {
                    if (!_grid.Columns.Contains(prop.Name)) continue;
                    int w2 = prop.Value.GetInt32();
                    if (w2 < 40) continue;  // sanity floor
                    // Restore via FillWeight (proportion), not Width: in Fill
                    // mode any subsequent grid resize redistributes columns by
                    // FillWeight, silently discarding explicit Widths.
                    _grid.Columns[prop.Name]!.FillWeight = w2;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"FindUser LoadColumnWidths failed: {ex.Message}");
            }
        }

        private void SaveColumnWidths()
        {
            try
            {
                Dictionary<string, Dictionary<string, int>> data;
                var path = ShadowRDP.AppPaths.ColumnWidthsPath;
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
            }
            catch (Exception ex)
            {
                Logger.Warn($"FindUser SaveColumnWidths failed: {ex.Message}");
            }
        }

        private async Task OnSearchAsync()
        {
            var user = _userBox.Text.Trim();
            if (string.IsNullOrEmpty(user))
            {
                MessageBox.Show(this, "Введите имя пользователя.", "Поиск",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _grid.Rows.Clear();
            _searchBtn.Enabled = false;
            _cancelBtn.Enabled = true;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            // Resolve computer list based on scope.
            List<string> computers;
            try
            {
                computers = _scopeCombo.SelectedIndex switch
                {
                    1 => AdService.GetComputers(),
                    2 => AddressBookStore.Load()
                        .Select(e => e.Computer)
                        .Where(c => !string.IsNullOrWhiteSpace(c))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    _ => new List<string> { _currentComputer }
                };
            }
            catch (Exception ex)
            {
                _statusLabel.Text = $"Ошибка получения списка компьютеров: {ex.Message}";
                _searchBtn.Enabled = true;
                _cancelBtn.Enabled = false;
                return;
            }

            int total = computers.Count;
            int done = 0;
            int found = 0;

            _statusLabel.Text = $"Поиск по {total} компьютерам...";
            Logger.Info($"FindUser '{user}': scope={_scopeCombo.SelectedIndex}, {total} computers");

            try
            {
                await ParallelForEachAsync(computers, 15, token, async (computer, ct) =>
                {
                    var match = await Task.Run(() => SearchOne(computer, user));
                    Interlocked.Increment(ref done);
                    if (match != null)
                    {
                        Interlocked.Increment(ref found);
                        AddGridRow(match.Value);
                    }
                    UpdateStatusFromBackground($"Поиск: {done}/{total}, найдено: {found}");
                });
                _statusLabel.Text = $"Готово. Проверено {done}, найдено совпадений: {found}.";
            }
            catch (OperationCanceledException)
            {
                _statusLabel.Text = $"Прервано. Проверено {done}, найдено: {found}.";
            }
            catch (Exception ex)
            {
                _statusLabel.Text = $"Ошибка: {ex.Message}";
            }
            finally
            {
                _searchBtn.Enabled = true;
                _cancelBtn.Enabled = false;
                try { _cts?.Dispose(); } catch { }
                _cts = null;
            }
        }

        private void OnCancel()
        {
            try { _cts?.Cancel(); } catch { }
            _statusLabel.Text = "Прерываю...";
        }

        // Run qwinsta on one computer, return first session matching the user
        // pattern (case-insensitive substring).
        private (string Computer, string UserName, int Id, string State)? SearchOne(string computer, string user)
        {
            try
            {
                var result = QwinstaService.Query(computer, 4000);
                if (result.TimedOut) return null;
                foreach (var s in result.Sessions)
                {
                    if (string.IsNullOrEmpty(s.UserName)) continue;
                    var sam = s.UserName.Contains('\\') ? s.UserName.Substring(s.UserName.IndexOf('\\') + 1) : s.UserName;
                    if (sam.IndexOf(user, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return (computer, s.UserName, s.Id, s.State);
                    }
                }
            }
            catch { }
            return null;
        }

        private void AddGridRow((string Computer, string UserName, int Id, string State) match)
        {
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<(string, string, int, string)>(AddGridRow), match); } catch { }
                return;
            }
            _grid.Rows.Add(match.Computer, match.UserName, match.Id, match.State);
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
