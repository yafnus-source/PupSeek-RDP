using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
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
            BackColor = ShadowRDP.Helpers.UiTheme.Background;
            FormBorderStyle = FormBorderStyle.Sizable;

            // Header
            var headerPanel = Helpers.UiHeader.Create("Поиск пользователя", null, "search");
            headerPanel.Dock = DockStyle.Top;
            Controls.Add(headerPanel);

            // Top row
            var lblUser = new Label { Text = "Пользователь:", Location = new Point(16, 84), Size = new Size(90, 22), TextAlign = ContentAlignment.MiddleLeft };
            _userBox = new TextBox { Location = new Point(110, 82), Size = new Size(180, 22) };
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

            var lblScope = new Label { Text = "Где искать:", Location = new Point(300, 84), Size = new Size(80, 22), TextAlign = ContentAlignment.MiddleLeft };
            _scopeCombo = new ComboBox { Location = new Point(384, 82), Size = new Size(220, 22), DropDownStyle = ComboBoxStyle.DropDownList };
            _scopeCombo.Items.AddRange(new object[]
            {
                $"Только на {_currentComputer}",
                "Все компьютеры AD (медленно)",
                "Все компьютеры из адресной книги"
            });
            _scopeCombo.SelectedIndex = 0;

            _searchBtn = ShadowRDP.Helpers.UiButtons.MakeFlat("Найти");
            _searchBtn.Location = new Point(614, 80);
            _searchBtn.Click += async (s, e) => await OnSearchAsync();
            _cancelBtn = ShadowRDP.Helpers.UiButtons.MakeFlat("Прервать");
            _cancelBtn.Location = new Point(710, 80);
            _cancelBtn.Enabled = false;
            _cancelBtn.Click += (s, e) => OnCancel();

            Controls.AddRange(new Control[] { lblUser, _userBox, lblScope, _scopeCombo, _searchBtn, _cancelBtn });

            _grid = new DataGridView
            {
                Location = new Point(16, 116),
                Size = new Size(788, 480),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                AllowUserToAddRows = false,
                ReadOnly = true,
                RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor = ShadowRDP.Helpers.UiTheme.Card,
                BorderStyle = BorderStyle.None,
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
                GridColor = ShadowRDP.Helpers.UiTheme.Background,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                ColumnHeadersDefaultCellStyle = {
                    BackColor = ShadowRDP.Helpers.UiTheme.Muted,
                    ForeColor = ShadowRDP.Helpers.UiTheme.MutedForeground,
                    Font = new Font("Segoe UI Semibold", 9F)
                },
                RowTemplate = { Height = 24 }
            };
_grid.EnableHeadersVisualStyles = false;
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Computer", HeaderText = "Компьютер", FillWeight = 120 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "UserName", HeaderText = "Пользователь", FillWeight = 100 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "SessionId", HeaderText = "ID", FillWeight = 40 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "State", HeaderText = "Состояние", FillWeight = 80 });

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

            ShadowRDP.Helpers.UiGridStyle.PaintHeaders(_grid);
            Controls.Add(_grid);

            _statusLabel = new Label
            {
                Location = new Point(16, 606),
                Size = new Size(700, 22),
                ForeColor = Color.Gray,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                Text = "Введите имя пользователя и нажмите «Найти»."
            };
            Controls.Add(_statusLabel);

            _closeBtn = ShadowRDP.Helpers.UiButtons.MakeFlat("Закрыть");
            _closeBtn.Location = new Point(714, 604);
            _closeBtn.Click += (s, e) => Close();
            Controls.Add(_closeBtn);
            // CancelButton requires IButtonControl; Label doesn't implement it.

            // When invoked from a context menu where a user is already
            // selected, auto-start the search immediately on form open.
            if (autoStart && !string.IsNullOrEmpty(initialSam))
            {
                Shown += async (s, e) =>
                {
                    // Tiny delay so the form finishes painting before the
                    // (blocking) search starts.
                    await Task.Delay(50);
                    await OnSearchAsync();
                };
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
