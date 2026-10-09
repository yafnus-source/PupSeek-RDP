using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using ShadowRDP.Controls;
using ShadowRDP.Helpers;
using ShadowRDP.Models;
using ShadowRDP.Services;

namespace ShadowRDP.Forms
{
    // TODO/шня — isolated todo tab, no RDP/WMI dependency.
    // Embedded like AddressBookForm / DashboardForm: TopLevel=false, Dock=Fill in RdpManagerForm._mainArea via _todoHost.
    // Data: %PROGRAMDATA%\PupSeekRDP\todo.json  (TodoStore) — deleting resets feature.
    public class TodoForm : Form
    {
        private DataGridView _grid = null!;
        private FlowLayoutPanel _filterPanel = null!;
        private Label _status = null!;
        private TextBox _search = null!;
        private string _filter = "Все";
        private string _searchText = "";
        private List<TodoItem> _all = new();
        private FlowLayoutPanel _headerActions = null!;
        private System.Windows.Forms.Timer _debounce = null!;
        private Label _btnDelSel = null!;
        private bool _closeHooked;
        private bool _suppressWidthSave;

        // v3.9.3BG — hero #48302F. v3.9.3BH — hero #36391E.
        private static readonly Color TodoHeader = Color.FromArgb(0x36, 0x39, 0x1E);
        private static readonly Color Accent = Color.FromArgb(0x5B, 0x5F, 0xF0);
        private static readonly Color CalTint = Color.FromArgb(0xEA, 0xF0, 0xFF);
        private static readonly Color CalIcon = Color.FromArgb(0x3B, 0x82, 0xF6);
        // v3.9.3CZ — пилы 1:1 как действия Монитора (Olive как было, Blue стальные).
        private static bool IsBlue => UiTheme.CurrentName != "Olive";
        private static Color ActCard => IsBlue ? Color.FromArgb(0xFC, 0xFE, 0xFF) : Color.FromArgb(0xFF, 0xFD, 0xF8);
        private static Color ActBot => IsBlue ? Color.FromArgb(0xE3, 0xE9, 0xF1) : Color.FromArgb(0xF3, 0xEF, 0xE2);
        private static Color ActBorder => IsBlue ? Color.FromArgb(0xC2, 0xCD, 0xDB) : Color.FromArgb(0xD5, 0xCF, 0xB9);
        private static Color ActShadow => IsBlue ? Color.FromArgb(36, 47, 74, 99) : Color.FromArgb(36, 61, 66, 35);
        private static Color ActInk => IsBlue ? Color.FromArgb(0x2F, 0x4A, 0x63) : Color.FromArgb(0x56, 0x5C, 0x30);
        private static Color ActInkDark => IsBlue ? Color.FromArgb(0x20, 0x36, 0x4E) : Color.FromArgb(0x3D, 0x42, 0x23);

        public TodoForm()
        {
            Text = "TODO/шня";
            BackColor = UiTheme.Background;
            FormBorderStyle = FormBorderStyle.None;
            Dock = DockStyle.Fill;

            // Header — цвет hero выбирается пользователем (v3.9.3BH).
            var header = new Panel { Dock = DockStyle.Top, Height = 48, BackColor = UiTheme.HeroBg };
            var title = new Label { Text = "TODO/шня", Font = new Font("Segoe UI Semibold", 11f), ForeColor = UiTheme.HeroFg, BackColor = Color.Transparent, AutoSize = true, Location = new Point(16, 14) };
            header.Controls.Add(title);
            void RefreshHeroColors()
            {
                try
                {
                    if (header.IsDisposed) return;
                    header.BackColor = UiTheme.HeroBg;
                    title.ForeColor = UiTheme.HeroFg;
                    header.Invalidate(true);
                }
                catch { }
            }
            Helpers.UiTheme.HeroColorChanged += RefreshHeroColors;
            header.Disposed += (s, e) => { try { Helpers.UiTheme.HeroColorChanged -= RefreshHeroColors; } catch { } };
            // v3.9.3CY — выбор цвета hero перенесён в «Инструменты → Настройки цвета».

            // v3.9.3CZ — действия пилами 1:1 как в Мониторе (Даш):
            // градиент V2Card→ActBot, рамка ActBorder, блик/тень, глиф Darken.
            // v3.9.3CY — выбор цвета hero перенесён в «Инструменты → Настройки цвета».

            var actHost = new Panel { Height = 28, BackColor = Color.Transparent };
            {
                int ax = 0;
                var abtnFont = new Font("Segoe UI Semibold", 8.5f);
                Label MakeActPill(string atext, string aicon, Action aact, bool isDelete)
                {
                    Image? aglyph = null;
                    try
                    {
                var gg = MenuIcons.GetScaled(aicon, 14);
                if (gg != null) aglyph = MenuIcons.Darken(gg);
                    }
                    catch { }
                    int aw = TextRenderer.MeasureText(atext, abtnFont).Width + 10 + 6 + 10;
                    if (aglyph != null) aw += 20;
                    var ab = new Label
                    {
                        Text = "",
                        AutoSize = false,
                        Size = new Size(aw, 28),
                        Location = new Point(ax, 0),
                        Font = abtnFont,
                        TextAlign = ContentAlignment.MiddleCenter,
                        Cursor = Cursors.Hand,
                        BackColor = Color.Transparent,
                        Tag = new object?[] { false, isDelete }
                    };
                    ax += aw + 8;
                    ab.Paint += (s, e) =>
                    {
                        try
                        {
                            var tag = (object?[])ab.Tag;
                            bool hov = tag[0] is true;
                            bool dis = isDelete && !ab.Enabled;
                            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                            e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                            var r = new Rectangle(0, 0, ab.Width - 1, ab.Height - 1);
                            using var path = UiButtons.RoundedRect(r, 9);
                            using var lg = new LinearGradientBrush(r, ActCard, ActBot, 90f);
                            e.Graphics.FillPath(lg, path);
                            using var pen = new Pen(hov && !dis ? ActInk : ActBorder, 1)
                            { Alignment = System.Drawing.Drawing2D.PenAlignment.Inset };
                            e.Graphics.DrawPath(pen, path);
                            using var hi = new Pen(Color.FromArgb(115, 255, 255, 255), 1);
                            e.Graphics.DrawLine(hi, r.X + 2, r.Y + 1, r.Right - 2, r.Y + 1);
                            using var sh = new Pen(ActShadow, 1);
                            e.Graphics.DrawLine(sh, r.X + 2, r.Bottom - 1, r.Right - 2, r.Bottom - 1);
                            int tx = 10;
                            if (aglyph != null)
                            {
                                e.Graphics.DrawImage(aglyph, new Rectangle(10, (ab.Height - 14) / 2, 14, 14));
                                tx = 30;
                            }
                            var fg = dis ? Color.FromArgb(150, 150, 150) : hov ? ActInkDark : ActInk;
                            TextRenderer.DrawText(e.Graphics, atext, ab.Font,
                                new Rectangle(tx, 0, Math.Max(0, ab.Width - tx - 10), ab.Height), fg,
                                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
                        }
                        catch { }
                    };
                    ab.MouseEnter += (s, e) => { ((object?[])ab.Tag)[0] = true; try { ab.Invalidate(); } catch { } };
                    ab.MouseLeave += (s, e) => { ((object?[])ab.Tag)[0] = false; try { ab.Invalidate(); } catch { } };
                    ab.Click += (s, e) => { if (!ab.Enabled) return; try { aact(); } catch { } };
                    actHost.Controls.Add(ab);
                    return ab;
                }
                MakeActPill("Добавить", "add", () => OnAdd(), false);
                MakeActPill("Импорт календаря", "calendar", () => OnImportCalendar(), false);
                _btnDelSel = MakeActPill("Удалить", "delete", () => DeleteSelected(), true);
                _btnDelSel.Enabled = false;
                actHost.Width = ax;
            }
            // v3.9.5 — пилюли сразу за названием (как в Даше «События»/
            // «Монитор»): отступ 12px от правого края заголовка, а не
            // фиксированные x=200.
            actHost.Location = new Point(
                16 + TextRenderer.MeasureText(title.Text, title.Font).Width + 12, 10);
            header.Controls.Add(actHost);

            Controls.Add(header);

            // Filter + search bar
            var bar = new Panel { Dock = DockStyle.Top, Height = 48, BackColor = UiTheme.Card, Padding = new Padding(12, 8, 12, 8) };
            _filterPanel = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.Transparent };
            foreach (var f in new[] { "Все", "Активные", "Выполненные", "Отложенные", "Сегодня" })
            {
                var pill = CreatePill(f, f == _filter);
                pill.Click += (s, e) => { _filter = f; UpdatePills(); ApplyFilters(); };
                _filterPanel.Controls.Add(pill);
            }
            bar.Controls.Add(_filterPanel);

            var searchPanel = new Panel { Dock = DockStyle.Right, Width = 260, BackColor = Color.Transparent };
            var searchWrapper = Helpers.UiSearchBox.Create("Поиск…", txt => { _searchText = txt; ApplyFilters(); }, width: 260, background: Color.White);
            _search = Helpers.UiSearchBox.GetBox(searchWrapper);
            searchWrapper.Dock = DockStyle.Fill;
            searchPanel.Controls.Add(searchWrapper);
            bar.Controls.Add(searchPanel);
            Controls.Add(bar);

            // Grid
            _grid = new DoubleBufferedDataGridView();
            _grid.Dock = DockStyle.Fill;
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
            _grid.AllowUserToResizeColumns = true;
            _grid.AllowUserToOrderColumns = true;

            var colDone = new DataGridViewCheckBoxColumn { Name = "Done", HeaderText = "✓", FillWeight = 36, MinimumWidth = 36, Resizable = DataGridViewTriState.True };
            var colText = new DataGridViewTextBoxColumn { Name = "Text", HeaderText = "Задача", FillWeight = 320, MinimumWidth = 120 };
            var colDue = new DataGridViewTextBoxColumn { Name = "Due", HeaderText = "Срок", FillWeight = 130, MinimumWidth = 80 };
            var colCreated = new DataGridViewTextBoxColumn { Name = "Created", HeaderText = "Создано", FillWeight = 120, MinimumWidth = 80 };
            var colSource = new DataGridViewTextBoxColumn { Name = "Source", HeaderText = "Источник", FillWeight = 80, MinimumWidth = 60 };
            _grid.Columns.AddRange(colDone, colText, colDue, colCreated, colSource);
            Helpers.GridColumnOrder.Wire(_grid, "TodoForm");
            // v3.9.1 — ширины колонок восстанавливаем на первом показе грида, чтобы
            // первичная раскладка не перетёрла сохранённые FillWeight пропорции.
            _grid.HandleCreated += (s, e) =>
            {
                LoadWidths();
                // Финальный flush при закрытии ПРИЛОЖЕНИЯ: форма встроенная
                // (TopLevel=false) — её собственный FormClosed при выходе
                // ненадёжен, цепляемся к топ-форме как GridColumnOrder.
                if (!_closeHooked)
                {
                    try
                    {
                        var top = FindForm();
                        if (top != null)
                        {
                            _closeHooked = true;
                            top.FormClosed += (fs, fe) => { try { if (!IsDisposed && !Disposing && !_grid.IsDisposed) SaveWidths(); } catch { } };
                        }
                    }
                    catch { }
                }
            };
            ShadowRDP.Helpers.UiGridStyle.ApplyUnified(_grid, 36, 36);
            // v4.9.5 — горизонтальные линии-рамки ячеек (CellBorderStyle
            // SingleHorizontal из унифицированного стиля) после ресайза колонок
            // рисовались неровно/не до края. В тудушной линии не нужны вообще:
            // снимаем рамки полностью, разделение строк и так видно по фону.
            _grid.CellBorderStyle = DataGridViewCellBorderStyle.None;
            _grid.GridColor = UiTheme.Background;
            _grid.CellClick += OnCellClick;
            _grid.CellDoubleClick += OnCellDoubleClick;
            _grid.CellMouseDown += OnCellMouseDown;
            _grid.ColumnWidthChanged += (s, e) =>
            {
                if (_suppressWidthSave) return;
                ScheduleSave();
                // v4.9.4 — при ресайзе колонок рамки в футере тянутся за
                // границами грида: перерисовываем, чтобы они легли по новой ширине.
                try { if (!_grid.IsDisposed) _grid.Refresh(); } catch { }
            };
            _grid.SelectionChanged += (s, e) => UpdateDelButton();
            _grid.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Delete && !e.Control && !e.Alt && !_grid.IsCurrentCellInEditMode && _grid.SelectedRows.Count > 0)
                {
                    e.Handled = true;
                    DeleteSelected();
                }
            };
            _grid.CellPainting += OnCellPainting;
            // ПКМ в пустом месте таблицы — Добавить запись
            _grid.MouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Right) return;
                var hit = _grid.HitTest(e.X, e.Y);
                if (hit.RowIndex != -1) return; // на строке — уже обработал CellMouseDown
                var menu = new Helpers.ThemedContextMenu();
                menu.AddHeader("TODO/шня");
                menu.AddItem("Добавить запись", "add", () => OnAdd());
                menu.Show(this, _grid.PointToScreen(e.Location));
            };
            Controls.Add(_grid);
            // v4.4.0 — своя тонкая полоса вместо системной.
            ShadowScroll.AttachGrid(_grid);

            // Status bar
            var statusPanel = new Panel { Dock = DockStyle.Bottom, Height = 28, BackColor = UiTheme.Card };
            _status = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI", 8f), ForeColor = UiTheme.MutedForeground, Padding = new Padding(12, 0, 0, 0) };
            statusPanel.Controls.Add(_status);
            Controls.Add(statusPanel);

            // Порядок Dock: как в DashboardForm — Fill должен быть между Top и Bottom, фиксим SetChildIndex иначе первые 4 строки под шапкой
            Controls.SetChildIndex(header, 3);
            Controls.SetChildIndex(bar, 2);
            Controls.SetChildIndex(_grid, 1);
            Controls.SetChildIndex(statusPanel, 0);

            Load += (s, e) => { Reload(); CalendarSyncService.OnCalendarSynced += OnCalSync; };
            FormClosed += (s, e) => { CalendarSyncService.OnCalendarSynced -= OnCalSync; SaveWidths(); };
            _debounce = new System.Windows.Forms.Timer { Interval = 400 };
            _debounce.Tick += (s, e) => { _debounce.Stop(); SaveWidths(); };
        }

        // v4.9.4 — автосинк календаря по URL: подхватываем новые события
        // (added>0 → показываем отчёт, 0 → просто перечитать на случай удалений).
        private void OnCalSync(int added)
        {
            try
            {
                if (InvokeRequired) { BeginInvoke(new Action(() => { Reload(); if (added > 0) _status.Text = $"Календарь: +{added} событий"; })); return; }
                Reload();
                if (added > 0) _status.Text = $"Календарь: +{added} событий";
            }
            catch { }
        }

        public void Reload()
        {
            _all = TodoStore.Load();
            ApplyFilters();
        }

        private void ApplyFilters()
        {
            var filtered = _all.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(_searchText))
                filtered = filtered.Where(x => x.Text.Contains(_searchText, StringComparison.OrdinalIgnoreCase));
            filtered = _filter switch
            {
                "Активные" => filtered.Where(x => !x.Done && x.Status == TodoStatus.Active),
                "Выполненные" => filtered.Where(x => x.Done),
                "Отложенные" => filtered.Where(x => x.Status == TodoStatus.Postponed),
                "Сегодня" => filtered.Where(x => x.DueAt.HasValue && x.DueAt.Value.Date == DateTime.Today),
                _ => filtered
            };
            var list = filtered.OrderBy(x => x.Done).ThenByDescending(x => x.CreatedAt).ToList();
            _grid.SuspendLayout();
            _grid.Rows.Clear();
            foreach (var it in list)
            {
                string due = it.DueAt.HasValue ? it.DueAt.Value.ToString("dd.MM HH:mm") + Mark(it.Repeat) + RemindMark(it) : "—";
                int idx = _grid.Rows.Add(it.Done, it.Text, due, it.CreatedAt.ToString("dd.MM HH:mm"), it.Source);
                _grid.Rows[idx].Tag = it;
                if (it.Done) _grid.Rows[idx].DefaultCellStyle.ForeColor = Color.Gray;
                if (it.Source == "calendar" && !it.Done)
                    _grid.Rows[idx].DefaultCellStyle.BackColor = CalTint;
            }
            _grid.ResumeLayout();
            _status.Text = $"Всего: {_all.Count}{CalendarSuffix()} | Показано: {list.Count} | Активных: {_all.Count(x => !x.Done)}";
            UpdatePills();
            UpdateDelButton();
        }

        private void UpdateDelButton()
        {
            try
            {
                // Текст кнопки статичен: MakeCompactFilled рисует closure-текст,
                // поздний Text не отобразится — счётчик живёт в диалоге и статусе.
                _btnDelSel.Enabled = _grid.SelectedRows.Count > 0;
            }
            catch { }
        }

        private void DeleteSelected()
        {
            try
            {
                var items = _grid.SelectedRows.Cast<DataGridViewRow>()
                    .Select(r => r.Tag as TodoItem)
                    .Where(x => x != null)
                    .ToList();
                if (items.Count == 0) return;
                var preview = string.Join("\n", items.Take(3).Select(x => "• " + (((x!.Text ?? "").Length > 60) ? x.Text.Substring(0, 60) + "..." : x.Text)));
                if (items.Count > 3) preview += $"\n…и ещё {items.Count - 3}";
                if (MessageBox.Show(this, $"Удалить выбранные задачи ({items.Count})?\n\n{preview}", "TODO/шня",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                TodoStore.DeleteMany(items.Select(x => x!.Id));
                Reload();
                _status.Text = $"Удалено задач: {items.Count}";
            }
            catch { }
        }

        private void UpdatePills()
        {
            // v3.9.1 — БЕЗ BringToFront(): во FlowLayoutPanel он меняет порядок
            // элементов, и активная пила перескакивает на первое место.
            foreach (Label pill in _filterPanel.Controls) Helpers.UiPills.Apply(pill, pill.Text == _filter);
        }

        private Label CreatePill(string text, bool active) => Helpers.UiPills.Create(text, active);

        private static string Mark(Models.TodoRepeat r) => Helpers.TodoSchedule.Mark(r);

        private static string RemindMark(TodoItem it)
        {
            if (it == null || it.RemindMinutes <= 0) return "";
            return $" · за {it.RemindMinutes} мин" + (it.RepeatRemind ? " ↻" : "");
        }

        private void OnAdd()
        {
            // v3.9.3CZ — полноценное окно «Новая задача» (вместо PromptDialog).
            if (TodoTaskDialog.Show(this)) Reload();
        }

        private string CalendarSuffix()
        {
            int calCount = _all.Count(x => x.Source == "calendar");
            return calCount > 0 ? $" ({calCount} из календаря)" : "";
        }

        private void OnImportCalendar()
        {
            using var dlg = new CalendarImportForm();
            var result = UiModal.ShowDimmed(this, dlg);
            if (result == DialogResult.OK) Reload();
        }

        private void OnCellPainting(object? sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            var row = _grid.Rows[e.RowIndex];
            var it = row.Tag as TodoItem;
            if (it == null || it.Source != "calendar") return;

            // Рисуем иконку календаря в колонке «Источник» слева от текста.
            if (_grid.Columns[e.ColumnIndex].Name == "Source")
            {
                e.PaintBackground(e.ClipBounds, true);
                var icon = MenuIcons.Get("calendar");
                int pad = 4;
                int iw = 16, ih = 16;
                int x = e.CellBounds.X + pad;
                int y = e.CellBounds.Y + (e.CellBounds.Height - ih) / 2;
                // v3.9.3 — 1:1 без интерполяции чётче (bicubic на немасштабе мылит).
                var imPrev = e.Graphics.InterpolationMode;
                e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                e.Graphics.DrawImage(icon, x, y, iw, ih);
                e.Graphics.InterpolationMode = imPrev;
                var textRect = new Rectangle(x + iw + 2, e.CellBounds.Y, e.CellBounds.Width - iw - pad - 2, e.CellBounds.Height);
                TextRenderer.DrawText(e.Graphics, "Календарь", _grid.Font, textRect,
                    e.CellStyle.ForeColor, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                e.Handled = true;
            }
        }

        private void OnCellClick(object? sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;
            if (e.ColumnIndex == _grid.Columns["Done"].Index)
            {
                var it = _grid.Rows[e.RowIndex].Tag as TodoItem;
                if (it == null) return;
                TodoStore.ToggleDone(it.Id);
                Reload();
            }
        }

        private void OnCellDoubleClick(object? sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;
            var it = _grid.Rows[e.RowIndex].Tag as TodoItem;
            if (it == null) return;
            // v3.9.3DA — полноценное окно редактирования (как «Новая задача»).
            if (TodoTaskDialog.Edit(this, it)) Reload();
        }

        private void OnCellMouseDown(object? sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right || e.RowIndex < 0) return;
            // ПКМ внутри уже выбранного — выбор не трогаем (мультиселект Ctrl/Shift живёт)
            if (!_grid.Rows[e.RowIndex].Selected)
            {
                _grid.ClearSelection();
                _grid.Rows[e.RowIndex].Selected = true;
            }
            _grid.CurrentCell = _grid.Rows[e.RowIndex].Cells[0];
            var it = _grid.Rows[e.RowIndex].Tag as TodoItem;
            if (it == null) return;
            var menu = new ThemedContextMenu();
            menu.AddHeader(it.Text.Length > 30 ? it.Text.Substring(0, 30) + "..." : it.Text);
            menu.AddItem(it.Done ? "Снять галочку" : "Выполнено ✓", "check", () => { TodoStore.ToggleDone(it.Id); Reload(); });
            menu.AddItem("Отложить на 1 час", "clock", () => { TodoStore.Postpone(it.Id, DateTime.Now.AddHours(1)); Reload(); });
            menu.AddItem("Отложить на завтра", "calendar", () => { TodoStore.Postpone(it.Id, DateTime.Today.AddDays(1).AddHours(9)); Reload(); });
            menu.AddItem("Выбрать дату...", "calendar", () =>
            {
                var picked = Helpers.DateTimeDialog.Show(this, it.DueAt ?? DateTime.Now.AddHours(1));
                if (picked.HasValue) { TodoStore.Postpone(it.Id, picked.Value); Reload(); }
            });
            menu.AddItem("Задать расписание...", "clock", () => { if (TodoScheduleDialog.Show(this, it, Models.TodoRepeat.None)) Reload(); });
            menu.AddSeparator();
            menu.AddItem("Редактировать...", "edit", () => { if (TodoTaskDialog.Edit(this, it)) Reload(); });
            menu.AddItem("Удалить", "delete", () =>
            {
                if (MessageBox.Show(this, $"Удалить задачу?\n{it.Text}", "TODO/шня", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) { TodoStore.Delete(it.Id); Reload(); }
            });
            int selCount = _grid.SelectedRows.Count;
            if (selCount > 1)
                menu.AddItem($"Удалить выбранные ({selCount})", "delete", () => DeleteSelected());
            menu.Show(this, Cursor.Position);
        }

        private void ScheduleSave() { _debounce.Stop(); _debounce.Start(); }
        private void LoadWidths()
        {
            try
            {
                var path = AppPaths.ColumnWidthsPath;
                if (!File.Exists(path)) return;
                var json = File.ReadAllText(path);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("todo", out var fe)) return;
                // v4.9.4 — ширины храним как пиксели и восстанавливаем напрямую
                // (как AddressBookForm: Width в режиме Fill WinForms сам
                // пересчитает FillWeight). Пропорции при этом сохраняются.
                // w<40 — мусорное значение, пропускаем.
                // v3.9.3DA — восстановление через FillWeight (точно), а не
                // последовательным Width: в режиме Fill каждый Width тут же
                // пересчитывался заливкой и пропорции «забывались».
                _suppressWidthSave = true;
                try
                {
                    var saved = new Dictionary<string, int>();
                    int n = 0, total = 0;
                    foreach (var prop in fe.EnumerateObject())
                    {
                        if (!_grid.Columns.Contains(prop.Name)) continue;
                        int w = prop.Value.GetInt32();
                        if (w < 40) continue;
                        saved[prop.Name] = w;
                        total += w;
                    }
                    if (total > 0)
                    {
                        foreach (var kv in saved)
                        {
                            _grid.Columns[kv.Key]!.FillWeight = Math.Max(1f, kv.Value * 100f / total);
                            n++;
                        }
                    }
                    try { Logger.Info($"TodoForm LoadWidths: restored {n} cols"); } catch { }
                }
                finally { _suppressWidthSave = false; }
            }
            catch (Exception ex) { try { Logger.Warn($"TodoForm LoadWidths failed: {ex.Message}"); } catch { } }
        }
        private void SaveWidths()
        {
            try
            {
                // Грид ещё не разложен (уже узкого размера): не печём переходные/
                // клампленные по MinimumWidth значения поверх пользовательских.
                if (_grid.ClientSize.Width < 400) return;
                Dictionary<string, Dictionary<string, int>> data;
                var path = AppPaths.ColumnWidthsPath;
                if (File.Exists(path)) data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(File.ReadAllText(path)) ?? new();
                else data = new();
                var per = new Dictionary<string, int>();
                foreach (DataGridViewColumn c in _grid.Columns) per[c.Name] = c.Width;
                data["todo"] = per;
                File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(data, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                try { Logger.Info($"TodoForm SaveWidths: saved {per.Count} cols"); } catch { }
            }
            catch (Exception ex) { try { Logger.Warn($"TodoForm SaveWidths failed: {ex.Message}"); } catch { } }
        }

        // DoubleBuffered grid helper
        private class DoubleBufferedDataGridView : DataGridView
        {
            public DoubleBufferedDataGridView() { DoubleBuffered = true; }
        }
    }
}
