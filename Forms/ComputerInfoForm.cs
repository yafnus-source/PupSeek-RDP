using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using ShadowRDP.Controls;
using ShadowRDP.Services;

namespace ShadowRDP.Forms
{
    // "Отчёт о компьютере" dialog. Mirror of Show-ComputerInfo from the .ps1.
    //
    // Layout: header (computer name + subtitle + separator), then a DataGridView
    // with two columns "Параметр" / "Значение" rendered as Excel-like banded
    // rows with section headers spanning both columns in accent blue.
    //
    // The report runs synchronously (GetComputerInfo blocks the UI thread),
    // but the dialog opens instantly with a "Загрузка..." placeholder. Each
    // WMI query inside has its own timeout so the worst case is bounded.
    public class ComputerInfoForm : Form
    {
        private readonly string _computer;
        private readonly DataGridView _grid;
        private readonly Label _refreshBtn;
        private readonly Label _copyBtn;
        private readonly Label _closeBtn;

        public ComputerInfoForm(string computer)
        {
            _computer = computer;
            Text = $"Отчёт о компьютере: {computer}";
            ClientSize = new Size(820, 720);
            MinimumSize = new Size(560, 440);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9F);
            BackColor = ShadowRDP.Helpers.UiTheme.Background;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;

            // === Header (v2.2.15: blue #296AA0 bar + white text/icon) ===
            var headerPanel = Helpers.UiHeader.Create(computer, "Отчёт о компьютере", "info");
            headerPanel.Dock = DockStyle.Top;
            Controls.Add(headerPanel);

            // === Grid ===
            _grid = new DataGridView
            {
                Location = new Point(24, 76),
                Size = new Size(752, 540),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                ReadOnly = true,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                BackgroundColor = ShadowRDP.Helpers.UiTheme.Card,
                BorderStyle = BorderStyle.FixedSingle,
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
                GridColor = ShadowRDP.Helpers.UiTheme.BorderSubtle,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
                ColumnHeadersHeight = 30,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                ColumnHeadersDefaultCellStyle = {
                    BackColor = ShadowRDP.Helpers.UiTheme.Muted,
                    ForeColor = ShadowRDP.Helpers.UiTheme.MutedForeground,
                    Font = new Font("Segoe UI Semibold", 9F),
                    Alignment = DataGridViewContentAlignment.MiddleLeft
                },
                DefaultCellStyle = {
                    Font = new Font("Segoe UI", 9.5F),
                    SelectionBackColor = ShadowRDP.Helpers.UiTheme.AccentSoft,
                    SelectionForeColor = ShadowRDP.Helpers.UiTheme.Accent
                },
                RowTemplate = { Height = 26 }
            };
_grid.EnableHeadersVisualStyles = false;
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Property", HeaderText = "Параметр", FillWeight = 110 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Value", HeaderText = "Значение", FillWeight = 200 });
            ShadowRDP.Helpers.UiGridStyle.PaintHeaders(_grid);
            Controls.Add(_grid);

            // === Buttons ===
            _refreshBtn = ShadowRDP.Helpers.UiButtons.MakeFlat("Обновить");
            _refreshBtn.Location = new Point(24, 640);
            _refreshBtn.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _refreshBtn.Click += async (s, e) => await OnRefreshClickedAsync();
            Controls.Add(_refreshBtn);

            _copyBtn = ShadowRDP.Helpers.UiButtons.MakeFlat("Копировать в Excel");
            _copyBtn.Location = new Point(144, 640);
            _copyBtn.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _copyBtn.Click += (s, e) => OnCopyClicked();
            Controls.Add(_copyBtn);

            _closeBtn = ShadowRDP.Helpers.UiButtons.MakeFlat("Закрыть");
            _closeBtn.Location = new Point(666, 640);
            _closeBtn.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            _closeBtn.Click += (s, e) => Close();
            Controls.Add(_closeBtn);
            // CancelButton requires IButtonControl; Label doesn't implement it.

            // Restore last-used window size — saved on close via FormClosed.
            WindowStateStore.Register(this, "computerInfo");

            Shown += async (s, e) => await LoadAsync();
        }

        // Render the report into the grid. Sections get accent-blue backcolor
        // and bold font; data rows alternate white / very light grey.
        private void RenderRows(IEnumerable<ComputerInfoService.Row> rows)
        {
            _grid.Rows.Clear();
            var sectionFont = new Font("Segoe UI Semibold", 9.5F);
            var sectionBg = ShadowRDP.Helpers.UiTheme.AccentSoft;
            var sectionFg = ShadowRDP.Helpers.UiTheme.Accent;
            var bandBg = ShadowRDP.Helpers.UiTheme.Background;
            var whiteBg = Color.White;
            var propColor = Color.FromArgb(90, 95, 105);
            var valueColor = ShadowRDP.Helpers.UiTheme.Foreground;
            int bandIdx = 0;

            foreach (var row in rows)
            {
                if (row.IsSection)
                {
                    bandIdx = 0;
                    var idx = _grid.Rows.Add(row.Property, "");
                    var gridRow = _grid.Rows[idx];
                    gridRow.Cells[0].Style.BackColor = sectionBg;
                    gridRow.Cells[0].Style.ForeColor = sectionFg;
                    gridRow.Cells[0].Style.Font = sectionFont;
                    gridRow.Cells[1].Style.BackColor = sectionBg;
                    gridRow.Cells[1].Style.ForeColor = sectionFg;
                    gridRow.Cells[1].Style.Font = sectionFont;
                    gridRow.Height = 28;
                }
                else
                {
                    var bg = (bandIdx % 2 == 1) ? bandBg : whiteBg;
                    var idx = _grid.Rows.Add(row.Property, row.Value);
                    var gridRow = _grid.Rows[idx];
                    gridRow.Cells[0].Style.BackColor = bg;
                    gridRow.Cells[0].Style.ForeColor = propColor;
                    gridRow.Cells[1].Style.BackColor = bg;
                    gridRow.Cells[1].Style.ForeColor = valueColor;
                    bandIdx++;
                }
            }
        }

        // Show "Loading..." row, run the (synchronous) report on a worker
        // thread, marshal back to UI for rendering.
        private async Task LoadAsync()
        {
            _grid.Rows.Clear();
            _grid.Rows.Add("Загрузка...", $"Сбор информации о '{_computer}'");
            _grid.Rows[0].Cells[1].Style.ForeColor = Color.Gray;
            _refreshBtn.Enabled = false;
            await Task.Yield(); // let the loading row paint
            try
            {
                var rows = await Task.Run(() => ComputerInfoService.Collect(_computer));
                RenderRows(rows);
            }
            catch (Exception ex)
            {
                _grid.Rows.Clear();
                _grid.Rows.Add("Ошибка", ex.Message);
            }
            finally
            {
                _refreshBtn.Enabled = true;
            }
        }

        private async Task OnRefreshClickedAsync() => await LoadAsync();

        // TSV — pastes into Excel with proper column split.
        private void OnCopyClicked()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                foreach (DataGridViewRow row in _grid.Rows)
                {
                    var p = row.Cells[0].Value?.ToString() ?? "";
                    var v = row.Cells[1].Value?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(p) && !string.IsNullOrEmpty(v))
                        sb.AppendLine($"{p}\t{v}");
                    else if (!string.IsNullOrEmpty(p))
                        sb.AppendLine(p);
                    else if (!string.IsNullOrEmpty(v))
                        sb.AppendLine($"\t{v}");
                }
                Clipboard.SetText(sb.ToString());
                _copyBtn.Text = "Скопировано!";
                var t = new System.Windows.Forms.Timer { Interval = 600 };
                t.Tick += (s, e) => { _copyBtn.Text = "Копировать в Excel"; t.Stop(); t.Dispose(); };
                t.Start();
            }
            catch { }
        }

        private static NeoButton MakeAccentButton(string text, int x, int y, int w, int h)
        {
            return new NeoButton
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(w, h),
                Accent = true
            };
        }

        private static NeoButton MakeOutlineButton(string text, int x, int y, int w, int h)
        {
            return new NeoButton
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(w, h),
                Accent = false
            };
        }
    }
}
