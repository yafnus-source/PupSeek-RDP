using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using ShadowRDP.Controls;
using ShadowRDP.Helpers;
using ShadowRDP.Models;
using ShadowRDP.Services;

namespace ShadowRDP.Forms
{
    // Диалог импорта календаря: выбор .ics файла, просмотр событий,
    // выявление уже импортированных (по calendarUid), подтверждение.
    // v4.9.5 — окно переделано в «стиле FTP» (FtpSettingsForm): UiModal.Hero +
    // section-cards + UiModal.Field, кнопки/футер как в FTP.
    public class CalendarImportForm : Form
    {
        private DataGridView _grid = null!;
        private Label _info = null!;
        private Label _btnImport = null!;
        private Panel _hero = null!;
        private Panel _pickPanel = null!;
        private Panel _gridPanel = null!;
        private Label _hint = null!;
        private string _filePath = "";
        private List<CalendarEvent> _parsed = new();
        private List<CalendarEvent> _newEvents = new();
        private TextBox _urlBox = null!;
        private Label _syncStatus = null!;

        private static readonly Color CalTint = Color.FromArgb(0xEA, 0xF0, 0xFF);

        public CalendarImportForm()
        {
            Text = "Импорт календаря";
            ClientSize = new Size(700, 560);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = UiTheme.DialogBackground;
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { e.Handled = true; Close(); } };

            // ——— 1. Hero ———
            _hero = UiModal.Hero("Импорт календаря", "Outlook, Google Calendar, Exchange — .ics", "calendar");

            // ——— 2. Section cards ———
            int cardW = 328;
            // Контейнер двух верхних карточек (файл + автосинк).
            _pickPanel = new Panel { Dock = DockStyle.Top, Height = 158, BackColor = UiTheme.DialogBackground };
            var cardFile = UiModal.Card(14, 14, cardW, 112, "Файл .ics", "exchange");
            var btnBrowse = FtpButton("Выбрать файл…", 130);
            btnBrowse.Location = new Point(16, 50);
            btnBrowse.Click += OnBrowse;
            cardFile.Controls.Add(btnBrowse);
            _hint = new Label
            {
                Text = "Файл не выбран",
                Location = new Point(16, 84),
                AutoSize = false,
                Size = new Size(cardW - 32, 22),
                Font = new Font("Segoe UI", 9f),
                ForeColor = UiTheme.MutedForeground,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft
            };
            cardFile.Controls.Add(_hint);
            // v3.9.3Y — UiModal.Card гладкая сама, Region давал лесенку.
            try { cardFile.Region?.Dispose(); cardFile.Region = null; } catch { }

            var cardUrl = UiModal.Card(14 + cardW + 12, 14, cardW, 112, "Автосинк по URL", "gear");
            var wUrl = UiModal.Field(new TextBox { Text = "", PlaceholderText = "https://…/calendar.ics" }, cardW - 32);
            wUrl.Location = new Point(16, 50);
            cardUrl.Controls.Add(wUrl);
            _urlBox = (TextBox)wUrl.Controls[0];
            var btnSync = FtpButton("Синхронизировать", 110, bgOverride: Color.FromArgb(0x1E, 0x3A, 0x5F));
            btnSync.Location = new Point(16, 84);
            btnSync.Click += async (s, e) =>
            {
                try
                {
                    btnSync.Enabled = false; btnSync.Text = "...";
                    await SaveUrlAndSyncAsync();
                    UpdateSyncInfo();
                }
                catch { }
                finally { btnSync.Enabled = true; btnSync.Text = "Синхронизировать"; }
            };
            cardUrl.Controls.Add(btnSync);
            _syncStatus = new Label
            {
                Text = "",
                Location = new Point(136, 86),
                AutoSize = false,
                Size = new Size(cardW - 32 - 130, 20),
                Font = new Font("Segoe UI", 8f),
                ForeColor = UiTheme.MutedForeground,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft
            };
            cardUrl.Controls.Add(_syncStatus);
            // v3.9.3Y — UiModal.Card гладкая сама, Region давал лесенку.
            try { cardUrl.Region?.Dispose(); cardUrl.Region = null; } catch { }

            _pickPanel.Controls.Add(cardFile);
            _pickPanel.Controls.Add(cardUrl);
            Controls.Add(_pickPanel);

            // ——— 3. Info bar ———
            _info = new Label { Dock = DockStyle.Top, Height = 24, Font = new Font("Segoe UI", 8.5f), ForeColor = UiTheme.MutedForeground, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(28, 0, 0, 0), BackColor = UiTheme.DialogBackground };
            Controls.Add(_info);

            // ——— 4. Grid (карточка-список событий) ———
            _gridPanel = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.DialogBackground };
            var cardGrid = UiModal.Card(14, 0, 0, 0, "События", "list");
            cardGrid.Dock = DockStyle.Fill;
            _grid = new DoubleBufferedDgv();
            _grid.Dock = DockStyle.Fill;
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _grid.MultiSelect = true;
            _grid.ReadOnly = true;
            _grid.RowTemplate.Height = 32;
            _grid.BackgroundColor = UiTheme.Card;
            _grid.BorderStyle = BorderStyle.None;
            _grid.GridColor = UiTheme.BorderSubtle;
            _grid.CellBorderStyle = DataGridViewCellBorderStyle.None;
            _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.White;
            _grid.ColumnHeadersDefaultCellStyle.ForeColor = UiTheme.Foreground;
            _grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI Semibold", 9f);
            _grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
            _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            _grid.ColumnHeadersHeight = 32;
            _grid.RowHeadersVisible = false;
            _grid.EnableHeadersVisualStyles = false;
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Date", HeaderText = "Дата", FillWeight = 90, MinimumWidth = 70 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Time", HeaderText = "Время", FillWeight = 80, MinimumWidth = 60 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Summary", HeaderText = "Событие", FillWeight = 300, MinimumWidth = 150 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Location", HeaderText = "Место", FillWeight = 120, MinimumWidth = 80 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status", HeaderText = "Статус", FillWeight = 80, MinimumWidth = 60 });
            _grid.CellPainting += OnGridCellPainting;
            ShadowScroll.AttachGrid(_grid);
            cardGrid.Controls.Add(_grid);
            // v3.9.3Y — UiModal.Card гладкая сама, Region давал лесенку.
            try { cardGrid.Region?.Dispose(); cardGrid.Region = null; } catch { }

            _gridPanel.Controls.Add(cardGrid);
            _gridPanel.Padding = new Padding(14, 2, 14, 6);
            Controls.Add(_gridPanel);

            // ——— 5. Footer ———
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = UiTheme.DialogCard };
            footer.Paint += (_, e) => { using var pen = new Pen(UiTheme.Border, 1); e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };
            _btnImport = FtpButton("Импортировать", 140);
            _btnImport.Location = new Point(0, 15);
            _btnImport.Enabled = false;
            _btnImport.Click += OnImport;
            footer.Controls.Add(_btnImport);
            var btnClose = FtpButton("Закрыть", 100, bgOverride: UiTheme.MutedForeground);
            btnClose.Location = new Point(0, 15);
            btnClose.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
            footer.Controls.Add(btnClose);
            Controls.Add(footer);
            Controls.Add(_hero);
            Controls.Add(_pickPanel);

            Controls.SetChildIndex(_hero, 4);
            Controls.SetChildIndex(_pickPanel, 3);
            Controls.SetChildIndex(_info, 2);
            Controls.SetChildIndex(_gridPanel, 1);
            Controls.SetChildIndex(footer, 0);

            // v4.9.4 — предзаполняем URL из настроек и показываем статус автосинка.
            Load += (s, e) =>
            {
                try
                {
                    var st = TodoStore.LoadSettings();
                    _urlBox.Text = st.CalendarUrl ?? "";
                    UpdateSyncInfo();
                }
                catch { }
            };
            Shown += (a, b) =>
            {
                try
                {
                    // Финал раскладки: кнопки футера — по правому краю, как в FTP.
                    _btnImport.Location = new Point(footer.Width - 256, 15);
                    btnClose.Location = new Point(footer.Width - 116, 15);
                    try { BeginInvoke(new Action(() => { try { Refresh(); } catch { } })); } catch { }
                }
                catch { }
            };
        }

        private async Task SaveUrlAndSyncAsync()
        {
            try
            {
                var st = TodoStore.LoadSettings();
                st.CalendarUrl = (_urlBox.Text ?? "").Trim();
                TodoStore.SaveSettings(st);
            }
            catch { }
            int added = await CalendarSyncService.SyncNowAsync();
            try { CalendarSyncService.Restart(); } catch { }
            if (added > 0)
                MessageBox.Show(this, $"Добавлено новых событий из календаря: {added}", "Автосинк календаря",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void UpdateSyncInfo()
        {
            try
            {
                var st = TodoStore.LoadSettings();
                bool on = !string.IsNullOrWhiteSpace(st.CalendarUrl);
                var last = st.CalendarLastSync;
                _syncStatus.Text = on
                    ? $"каждые {Math.Max(5, st.CalendarSyncMinutes)} мин" +
                      (last.HasValue ? $" · {last.Value:dd.MM HH:mm}" : "")
                    : "выключен";
            }
            catch { }
        }

        private void OnBrowse(object? s, EventArgs e)
        {
            using var ofd = new OpenFileDialog
            {
                Title = "Выберите файл календаря",
                Filter = "ICS файлы (*.ics)|*.ics|Все файлы (*.*)|*.*"
            };
            if (UiModal.ShowDimmed(this, ofd) != DialogResult.OK) return;
            _filePath = ofd.FileName;
            _hint.Text = Path.GetFileName(_filePath);
            try
            {
                _parsed = IcsParser.ParseFile(_filePath);
                var existing = new HashSet<string>(
                    TodoStore.Load()
                        .Where(x => x.Source == "calendar" && !string.IsNullOrEmpty(x.CalendarUid))
                        .Select(x => x.CalendarUid!),
                    StringComparer.OrdinalIgnoreCase);
                _newEvents = _parsed.Where(ev => !existing.Contains(ev.Uid)).ToList();
                PopulateGrid();
                int newCount = _newEvents.Count;
                _info.Text = $"Найдено: {_parsed.Count} · Новых: {newCount} · Уже в списке: {_parsed.Count - newCount}";
                _btnImport.Enabled = newCount > 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Ошибка чтения файла:\n{ex.Message}", "Импорт календаря",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void PopulateGrid()
        {
            _grid.SuspendLayout();
            _grid.Rows.Clear();
            foreach (var ev in _parsed)
            {
                string date = ev.Start.ToString("dd.MM.yyyy");
                string time = ev.AllDay ? "весь день" : ev.Start.ToString("HH:mm");
                string status = _newEvents.Contains(ev) ? "новое" : "в списке";
                int idx = _grid.Rows.Add(date, time, ev.Summary, ev.Location ?? "", status);
                var row = _grid.Rows[idx];
                row.Tag = ev;
                if (_newEvents.Contains(ev))
                    row.DefaultCellStyle.BackColor = CalTint;
                else
                    row.DefaultCellStyle.ForeColor = Color.Gray;
            }
            _grid.ResumeLayout();
        }

        private void OnGridCellPainting(object? sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex != _grid.Columns["Status"].Index) return;
            var ev = _grid.Rows[e.RowIndex].Tag as CalendarEvent;
            if (ev == null) return;
            bool isNew = _newEvents.Contains(ev);
            e.CellStyle.ForeColor = isNew ? Color.FromArgb(0x16, 0xA3, 0x4A) : Color.Gray;
        }

        private void OnImport(object? s, EventArgs e)
        {
            int imported = 0;
            foreach (var ev in _newEvents)
            {
                TodoStore.Add(
                    text: ev.Summary,
                    source: "calendar",
                    dueAt: ev.Start,
                    calendarUid: ev.Uid);
                imported++;
            }
            MessageBox.Show(this, $"Импортировано: {imported} событий", "Импорт календаря",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.OK;
            Close();
        }

        // Локальная кнопка ТОЛЬКО для этого окна (как FtpButton в FTP):
        // тот же вид MakeCompactFilled, но текст из lbl.Text рисуется с
        // SingleLine|NoPadding|EndEllipsis — не вылезает на соседей.
        private static Label FtpButton(string text, int width, Color? bgOverride = null)
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

        
        private class DoubleBufferedDgv : DataGridView { public DoubleBufferedDgv() { DoubleBuffered = true; } }
    }
}