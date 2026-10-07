using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using ShadowRDP.Controls;
using ShadowRDP.Helpers;
using ShadowRDP.Services;

namespace ShadowRDP.Forms
{
    // "Отчёт о компьютере" dialog. Mirror of Show-ComputerInfo from the .ps1.
    //
    // v4.3.3 — redesigned in the FTP sample-modal style: gradient hero band,
    // report grid inside a rounded section card, action buttons in the footer.
    //
    // Layout: hero (computer name + subtitle), then a DataGridView
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
            BackColor = UiTheme.DialogBackground;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { e.Handled = true; DialogResult = DialogResult.Cancel; Close(); } };

            // ——— 1. Hero (без крестика: у окна есть штатный) ———
            var hero = UiModal.Hero(computer, "Отчёт о компьютере", "computer");

            // ——— 2. Content: report grid inside a section card ———
            var content = new Panel { Dock = DockStyle.Fill, AutoScroll = false, BackColor = UiTheme.DialogBackground, Padding = new Padding(14) };
            var card = UiModal.Card(0, 0, 100, 100, "Параметры", "list");
            card.Dock = DockStyle.Fill;
            content.Controls.Add(card);
            // v3.9.3Y — UiModal.Card гладкая сама, Region давал лесенку.
            try { card.Region?.Dispose(); card.Region = null; } catch { }

            // === Grid ===
            _grid = new Controls.DoubleBufferedDataGridView
            {
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                MultiSelect = false,
            };
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Property", HeaderText = "Параметр", FillWeight = 110 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Value", HeaderText = "Значение", FillWeight = 200 });
            UiGridStyle.ApplyUnified(_grid, 36, 36);
            card.Controls.Add(_grid);
            // v4.4.0 — своя тонкая полоса вместо системной.
            ShadowScroll.AttachGrid(_grid);
            RoundCorners(_grid, 7);
            // Полный Invalidate на каждый шаг ресайза: иначе при живом
            // изменении размера окна остаются застойные вертикальные штрихи
            // (неперерисованные полосы после Fill-перераскладки колонок).
            card.Resize += (s, e) => { FitCardChild(card, _grid); try { card.Invalidate(true); } catch { } };
            _grid.Resize += (s, e) => { try { _grid.Invalidate(); } catch { } };
            _grid.ColumnWidthChanged += (s, e) => { try { _grid.Invalidate(); } catch { } };
            FitCardChild(card, _grid);

            // ——— 3. Footer with action buttons ———
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = UiTheme.DialogCard };
            footer.Paint += (s, e) => { using var pen = new Pen(UiTheme.Border, 1); e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };
            var btnFont = new Font("Segoe UI Semibold", 9F);
            int w(string text) => TextRenderer.MeasureText(text, btnFont).Width + 28;

            _refreshBtn = ActionButton("Обновить", w("Обновить"));
            _refreshBtn.Location = new Point(16, 15);
            _refreshBtn.Click += async (s, e) => await OnRefreshClickedAsync();
            _copyBtn = ActionButton("Копировать в Excel", w("Копировать в Excel"));
            _copyBtn.Location = new Point(16 + _refreshBtn.Width + 8, 15);
            _copyBtn.Click += (s, e) => OnCopyClicked();
            _closeBtn = ActionButton("Закрыть", w("Закрыть"));
            _closeBtn.Click += (s, e) => Close();
            footer.Controls.AddRange(new Control[] { _refreshBtn, _copyBtn, _closeBtn });
            // CancelButton requires IButtonControl; Label doesn't implement it
            // (Esc handled via KeyDown above).
            footer.Resize += (s, e) =>
            {
                _closeBtn.Left = footer.ClientSize.Width - 16 - _closeBtn.Width;
                _closeBtn.Top = 15;
            };

            // Dock order: content Fill first, hero Top, footer Bottom last —
            // the last added docks first, so the footer hugs the bottom edge.
            Controls.Add(content);
            Controls.Add(hero);
            Controls.Add(footer);

            var dpiScale = UiModal.ApplyDpiScale(this);
            if (dpiScale > 1.001f)
                ClientSize = new Size((int)Math.Round(ClientSize.Width * dpiScale), (int)Math.Round(ClientSize.Height * dpiScale));

            // Restore last-used window size — saved on close via FormClosed.
            WindowStateStore.Register(this, "computerInfo");

            Shown += async (s, e) =>
            {
                _closeBtn.Left = footer.ClientSize.Width - 16 - _closeBtn.Width;
                _closeBtn.Top = 15;
                try { BeginInvoke(new Action(() => { try { Refresh(); } catch { } })); } catch { }
                await LoadAsync();
            };
        }

        // Child of a card: padded area below the card header (header ends ~y44).
        private static void FitCardChild(Panel card, Control child)
        {
            child.Bounds = new Rectangle(14, 52,
                Math.Max(50, card.ClientSize.Width - 28),
                Math.Max(50, card.ClientSize.Height - 66));
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

        // Локальная filled-кнопка: тот же вид, что MakeCompactFilled, но текст
        // рисуется из lbl.Text — смена Text ("Скопировано!") реально видна.
        private static Label ActionButton(string text, int width, Color? bgOverride = null)
        {
            Color bg = bgOverride ?? UiTheme.HeaderBar;
            Color hover = ControlPaint.Dark(bg, 0.08f);
            var fill = new SolidBrush(bg);
            var lbl = new Label
            {
                Text = text,
                AutoSize = false,
                Size = new Size(width, 22),
                Font = new Font("Segoe UI Semibold", 9F),
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                BackColor = Color.Transparent
            };
            lbl.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                var r = new Rectangle(0, 0, lbl.Width, lbl.Height);
                using var path = UiButtons.RoundedRect(r, 1);
                if (lbl.Enabled) e.Graphics.FillPath(fill, path);
                else { using var dis = new SolidBrush(Color.FromArgb(0xE2, 0xE4, 0xE8)); e.Graphics.FillPath(dis, path); }
                var fg = lbl.Enabled ? Color.White : Color.FromArgb(0x8A, 0x8E, 0x94);
                TextRenderer.DrawText(e.Graphics, lbl.Text, lbl.Font, r, fg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            };
            lbl.MouseEnter += (s, e) => { if (lbl.Enabled) { fill.Color = hover; lbl.Invalidate(); } };
            lbl.MouseLeave += (s, e) => { if (lbl.Enabled) { fill.Color = bg; lbl.Invalidate(); } };
            lbl.EnabledChanged += (s, e) => lbl.Invalidate();
            return lbl;
        }

        // Render the report into the grid. Sections get accent-blue backcolor
        // and bold font; data rows alternate white / very light grey.
        private void RenderRows(IEnumerable<ComputerInfoService.Row> rows)
        {
            _grid.Rows.Clear();
            var sectionFont = new Font("Segoe UI Semibold", 9.5F);
            var sectionBg = ShadowRDP.Helpers.UiTheme.AccentSoft;
            var sectionFg = Color.FromArgb(0x3E, 0x43, 0x16);
            var bandBg = Color.FromArgb(0xFB, 0xF7, 0xEE);
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
                    // v3.9 — row-level style so every section header (including
                    // the first) recolours reliably. Selection styles match so
                    // the auto-selected first row also shows the section colours.
                    gridRow.DefaultCellStyle.BackColor = sectionBg;
                    gridRow.DefaultCellStyle.ForeColor = sectionFg;
                    gridRow.DefaultCellStyle.Font = sectionFont;
                    gridRow.DefaultCellStyle.SelectionBackColor = sectionBg;
                    gridRow.DefaultCellStyle.SelectionForeColor = sectionFg;
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
            // v3.9 — clear the default first-row selection so it doesn't
            // override the section header colours via SelectionForeColor.
            _grid.ClearSelection();
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
    }
}
