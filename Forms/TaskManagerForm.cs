using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using System.Windows.Forms;
using ShadowRDP.Controls;
using ShadowRDP.Helpers;
using ShadowRDP.Services;

namespace ShadowRDP.Forms
{
    // "Диспетчер задач" for a remote PC. Lists running processes via
    // Win32_Process with Name / PID / Memory. Kill button terminates the
    // selected PID via Win32_Process.Terminate.
    //
    // v4.3.3 — редизайн в стиле FTP-эталона (как «Открытые файлы»):
    // градиентный hero (поиск справа), таблица в карточке, действия
    // в футере. Вся логика (опрос WMI, фильтр, снятие задач) без изменений.
    //
    // CPU time would require sampling two snapshots and subtracting — too
    // expensive for the typical use case (just check what's running).
    public class TaskManagerForm : Form
    {
        private readonly string _computer;
        private readonly DataGridView _grid;
        private readonly Label _refreshBtn;
        private readonly Label _killBtn;
        private readonly Label _closeBtn;
        private readonly Label _statusLabel;
        private readonly TextBox _filterBox;
        private readonly Panel _filterWrapper;
        private readonly Label _lblHint;
        private readonly System.Windows.Forms.Timer _filterDebounce;

        public TaskManagerForm(string computer)
        {
            _computer = computer;
            Text = $"Диспетчер задач на {computer}";
            ClientSize = new Size(780, 646);
            MinimumSize = new Size(560, 506);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9F);
            BackColor = ShadowRDP.Helpers.UiTheme.DialogBackground;
            FormBorderStyle = FormBorderStyle.Sizable;
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { e.Handled = true; DialogResult = DialogResult.Cancel; Close(); } };

            // ——— 1. Hero (без крестика: у окна есть штатный) ———
            var hero = UiModal.Hero("Диспетчер задач", computer, "tasks");

            // Search lives in the hero (right side): white box, no anchors —
            // position comes from the real hero width (Shown/Resize).
            _filterWrapper = Helpers.UiSearchBox.Create("Поиск…", txt => { _filterDebounce.Stop(); _filterDebounce.Start(); }, width: 364, height: 27, background: Color.White);
            _filterWrapper.Location = new Point(70, 18);
            _filterWrapper.Size = new Size(364, 27);
            _filterBox = Helpers.UiSearchBox.GetBox(_filterWrapper);
            _lblHint = new Label { Text = "(по имени процесса)", Location = new Point(70, 48), Size = new Size(364, 18), Font = new Font("Segoe UI", 8F), ForeColor = Color.FromArgb(210, 228, 238), TextAlign = ContentAlignment.MiddleLeft, BackColor = Color.Transparent };
            hero.Controls.Add(_filterWrapper);
            hero.Controls.Add(_lblHint);

            // ——— 2. Content: single card with the grid ———
            var content = new Panel { Dock = DockStyle.Fill, AutoScroll = false, BackColor = ShadowRDP.Helpers.UiTheme.DialogBackground, Padding = new Padding(14) };
            var cardGrid = UiModal.Card(0, 0, 100, 100, "", "", withHeader: false);
            cardGrid.Dock = DockStyle.Fill;
            content.Controls.Add(cardGrid);
            // v3.9.3Y — UiModal.Card гладкая сама, Region давал лесенку.
            try { cardGrid.Region?.Dispose(); cardGrid.Region = null; } catch { }

            _grid = new DoubleBufferedDataGridView
            {
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            };
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "Имя процесса", FillWeight = 200 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "PID", HeaderText = "PID", FillWeight = 60 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Memory", HeaderText = "Память, МБ", FillWeight = 80 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Description", HeaderText = "Описание", FillWeight = 200 });
            ShadowRDP.Helpers.UiGridStyle.ApplyUnified(_grid, 36, 36);
            cardGrid.Controls.Add(_grid);
            // v4.4.0 — своя тонкая полоса вместо системной.
            ShadowScroll.AttachGrid(_grid);
            RoundCorners(_grid, 7);
            cardGrid.Resize += (s, e) => { FitCardChild(cardGrid, _grid); try { cardGrid.Invalidate(true); } catch { } };
            _grid.Resize += (s, e) => { try { _grid.Invalidate(); } catch { } };
            _grid.ColumnWidthChanged += (s, e) => { try { _grid.Invalidate(); } catch { } };
            _grid.SelectionChanged += (s, e) => _killBtn.Enabled = _grid.SelectedRows.Count > 0;

            // v3.9.0 — right-click menu shown as a themed popup.
            _grid.CellMouseDown += (sender, args) =>
            {
                if (args.Button != MouseButtons.Right) return;
                if (args.RowIndex < 0) return;
                _grid.ClearSelection();
                _grid.Rows[args.RowIndex].Selected = true;
                var menu = new ShadowRDP.Helpers.ThemedContextMenu();
                menu.AddHeader("Диспетчер задач");
                if (_grid.SelectedRows.Count > 0)
                    menu.AddItem("Завершить задачу", "stop", () => OnKillClicked(_grid, EventArgs.Empty));
                menu.Show(this, Control.MousePosition);
            };

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
                Text = "Загрузка..."
            };
            statusPanel.Controls.Add(_statusLabel);

            // ——— 4. Footer with action buttons ———
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = ShadowRDP.Helpers.UiTheme.DialogCard };
            footer.Paint += (s, e) => { using var pen = new Pen(UiTheme.Border, 1); e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };
            var btnFont = new Font("Segoe UI Semibold", 9F);
            int w(string text) => TextRenderer.MeasureText(text, btnFont).Width + 28;
            _refreshBtn = UiButtons.MakeCompactFilled("Обновить", w("Обновить"));
            _refreshBtn.Location = new Point(16, 15);
            _refreshBtn.Click += async (s, e) => await LoadAsync();
            _killBtn = UiButtons.MakeCompactFilled("Снять задачу", w("Снять задачу"), danger: true);
            _killBtn.Location = new Point(16 + _refreshBtn.Width + 8, 15);
            _killBtn.Click += OnKillClicked;
            _closeBtn = UiButtons.MakeCompactFilled("Закрыть", w("Закрыть"));
            _closeBtn.Click += (s, e) => Close();
            footer.Controls.AddRange(new Control[] { _refreshBtn, _killBtn, _closeBtn });
            footer.Resize += (s, e) =>
            {
                _closeBtn.Left = footer.ClientSize.Width - 16 - _closeBtn.Width;
                _closeBtn.Top = 15;
            };

            // Filter row lives in the hero now; keep the debounce wiring.
            _filterDebounce = new System.Windows.Forms.Timer { Interval = 120 };
            _filterDebounce.Tick += (s, e) => { _filterDebounce.Stop(); if (IsDisposed) return; ApplyFilter(); };
            FormClosed += (s, e) => { try { _filterDebounce.Stop(); _filterDebounce.Dispose(); } catch { } };

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

            content.Resize += (s, e) => PositionSearch();
            hero.Resize += (s, e) => PositionSearch();
            Shown += async (s, e) =>
            {
                PositionSearch();
                _closeBtn.Left = footer.ClientSize.Width - 16 - _closeBtn.Width;
                _closeBtn.Top = 15;
                // Поиск не активен при открытии: фокус на сетку.
                ActiveControl = _grid;
                _grid.Focus();
                try { BeginInvoke(new Action(() => { try { Refresh(); } catch { } })); } catch { }
                await LoadAsync();
            };

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

        // Child of a card: uniform 14px padding on all sides.
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

        private async Task LoadAsync()
        {
            _grid.Rows.Clear();
            _statusLabel.Text = "Запрос процессов...";
            _refreshBtn.Enabled = false;
            try
            {
                var procs = await Task.Run(() => QueryProcesses(_computer));
                foreach (var p in procs.OrderBy(p => p.Name))
                {
                    _grid.Rows.Add(p.Name, p.PID, p.MemoryMB, p.Description);
                }
                ApplyFilter();
                _statusLabel.Text = $"Найдено: {procs.Count} процессов";
                Logger.Info($"TaskMgr on {_computer}: {procs.Count} processes");
            }
            catch (Exception ex)
            {
                _statusLabel.Text = $"Ошибка: {ex.Message}";
            }
            finally
            {
                _refreshBtn.Enabled = true;
            }
        }

        private void ApplyFilter()
        {
            var term = _filterBox.Text.Trim().ToLowerInvariant();
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (string.IsNullOrEmpty(term))
                {
                    row.Visible = true;
                    continue;
                }
                var name = row.Cells["Name"].Value?.ToString()?.ToLowerInvariant() ?? "";
                row.Visible = name.Contains(term);
            }
        }

        private void OnKillClicked(object? sender, EventArgs e)
        {
            if (_grid.SelectedRows.Count == 0) return;
            var row = _grid.SelectedRows[0];
            var name = row.Cells["Name"].Value?.ToString() ?? "";
            var pidStr = row.Cells["PID"].Value?.ToString() ?? "";
            if (!int.TryParse(pidStr, out var pid) || pid <= 0) return;

            var r = MessageBox.Show(this,
                $"Завершить процесс '{name}' (PID {pid}) на {_computer}?",
                "Снять задачу", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r != DialogResult.Yes) return;

            if (KillProcess(_computer, pid))
            {
                _statusLabel.Text = $"Завершён: {name} (PID {pid})";
                _grid.Rows.Remove(row);
            }
            else
            {
                MessageBox.Show(this, "Не удалось завершить процесс. Возможно, нет прав.",
                    "Снять задачу", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private sealed class ProcInfo
        {
            public string Name { get; set; } = "";
            public int PID { get; set; }
            public string MemoryMB { get; set; } = "";
            public string Description { get; set; } = "";
        }

        private static List<ProcInfo> QueryProcesses(string computer)
        {
            var list = new List<ProcInfo>();
            try
            {
                var scope = new ManagementScope(new ManagementPath($"\\\\{computer}\\root\\cimv2"),
                    new ConnectionOptions { Timeout = TimeSpan.FromSeconds(8), Authentication = AuthenticationLevel.PacketPrivacy });
                scope.Connect();
                using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM Win32_Process"));
                using var results = searcher.Get();
                foreach (ManagementObject mo in results)
                {
                    try
                    {
                        var name = mo["Name"]?.ToString() ?? "";
                        var pidObj = mo["ProcessId"];
                        var pid = pidObj != null ? Convert.ToInt32(pidObj) : 0;
                        var memMB = "";
                        if (mo["WorkingSetSize"] != null &&
                            ulong.TryParse(mo["WorkingSetSize"].ToString(), out var ws))
                        {
                            memMB = (ws / 1024.0 / 1024).ToString("0.0");
                        }
                        list.Add(new ProcInfo
                        {
                            Name = name,
                            PID = pid,
                            MemoryMB = memMB,
                            Description = mo["Description"]?.ToString() ?? ""
                        });
                    }
                    finally { mo.Dispose(); }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"TaskMgr query failed on {computer}: {ex.Message}");
            }
            return list;
        }

        private static bool KillProcess(string computer, int pid)
        {
            try
            {
                var scope = new ManagementScope(new ManagementPath($"\\\\{computer}\\root\\cimv2"),
                    new ConnectionOptions { Timeout = TimeSpan.FromSeconds(8), Authentication = AuthenticationLevel.PacketPrivacy });
                scope.Connect();
                using var searcher = new ManagementObjectSearcher(scope,
                    new ObjectQuery($"SELECT * FROM Win32_Process WHERE ProcessId = {pid}"));
                using var results = searcher.Get();
                foreach (ManagementObject mo in results)
                {
                    try
                    {
                        var retVal = mo.InvokeMethod("Terminate", null);
                        Logger.Info($"TaskMgr KillProcess on {computer} (pid={pid}) returned {retVal}");
                        return true;
                    }
                    finally { mo.Dispose(); }
                }
                return false;
            }
            catch (Exception ex)
            {
                Logger.Warn($"TaskMgr KillProcess failed on {computer} (pid={pid}): {ex.Message}");
                return false;
            }
        }
    }
}
