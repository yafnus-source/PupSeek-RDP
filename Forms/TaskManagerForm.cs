using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using System.Windows.Forms;
using ShadowRDP.Controls;
using ShadowRDP.Services;

namespace ShadowRDP.Forms
{
    // "Диспетчер задач" for a remote PC. Lists running processes via
    // Win32_Process with Name / PID / Memory. Kill button terminates the
    // selected PID via Win32_Process.Terminate.
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

        public TaskManagerForm(string computer)
        {
            _computer = computer;
            Text = $"Диспетчер задач на {computer}";
            ClientSize = new Size(780, 646);
            MinimumSize = new Size(560, 506);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9F);
            BackColor = ShadowRDP.Helpers.UiTheme.Background;
            FormBorderStyle = FormBorderStyle.Sizable;

            // Header
            var headerPanel = Helpers.UiHeader.Create($"Диспетчер задач — {_computer}", _computer, "tasks");
            headerPanel.Dock = DockStyle.Top;
            Controls.Add(headerPanel);

            // Filter row
            var lblFilter = new Label { Text = "Фильтр:", Location = new Point(16, 84), Size = new Size(50, 22), TextAlign = ContentAlignment.MiddleLeft };
            _filterBox = new TextBox { Location = new Point(70, 82), Size = new Size(220, 22) };
            _filterBox.TextChanged += (s, e) => ApplyFilter();
            var lblHint = new Label { Text = "(по имени процесса)", Location = new Point(296, 84), Size = new Size(120, 22), ForeColor = Color.Gray, TextAlign = ContentAlignment.MiddleLeft };
            Controls.AddRange(new Control[] { lblFilter, _filterBox, lblHint });

            // Top-right button cluster. Anchored Top|Right so the cluster
            // tracks the right edge when the user resizes the dialog. Layout
            // (right-to-left): Close(90) · 6 · Kill(120) · 6 · Refresh(90).
            // Total cluster width = 312, fits inside 780-16=764.
            _closeBtn = ShadowRDP.Helpers.UiButtons.MakeFlat("Закрыть");
            _closeBtn.Location = new Point(674, 80);
            _closeBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _closeBtn.Click += (s, e) => Close();
            _killBtn = ShadowRDP.Helpers.UiButtons.MakeFlat("Снять задачу", ShadowRDP.Helpers.UiButtons.Role.Danger);
            _killBtn.Location = new Point(548, 80);
            _killBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _killBtn.Click += OnKillClicked;
            _refreshBtn = ShadowRDP.Helpers.UiButtons.MakeFlat("Обновить");
            _refreshBtn.Location = new Point(452, 80);
            _refreshBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _refreshBtn.Click += async (s, e) => await LoadAsync();
            Controls.AddRange(new Control[] { _refreshBtn, _killBtn, _closeBtn });

            _grid = new DataGridView
            {
                Location = new Point(16, 116),
                Size = new Size(748, 480),
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
                DefaultCellStyle = {
                    SelectionBackColor = ShadowRDP.Helpers.UiTheme.AccentSoft,
                    SelectionForeColor = ShadowRDP.Helpers.UiTheme.Card
                },
                ColumnHeadersDefaultCellStyle = {
                    BackColor = ShadowRDP.Helpers.UiTheme.Muted,
                    ForeColor = ShadowRDP.Helpers.UiTheme.MutedForeground,
                    Font = new Font("Segoe UI Semibold", 9F)
                },
                RowTemplate = { Height = 22 }
            };
_grid.EnableHeadersVisualStyles = false;
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "Имя процесса", FillWeight = 200 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "PID", HeaderText = "PID", FillWeight = 60 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Memory", HeaderText = "Память, МБ", FillWeight = 80 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Description", HeaderText = "Описание", FillWeight = 200 });
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

            ShadowRDP.Helpers.UiGridStyle.PaintHeaders(_grid);
            Controls.Add(_grid);

            _statusLabel = new Label
            {
                Location = new Point(16, 606),
                Size = new Size(748, 22),
                ForeColor = Color.Gray,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                Text = "Загрузка..."
            };
            Controls.Add(_statusLabel);
            // CancelButton requires IButtonControl; Label doesn't implement it,
            // so ESC won't auto-close. User can click Закрыть label or X.

            Shown += async (s, e) => await LoadAsync();
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
