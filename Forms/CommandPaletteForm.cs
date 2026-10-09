using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using ShadowRDP.Controls;

namespace ShadowRDP.Forms
{
    // v3.9.4 — команда-палитра (Ctrl+K): быстрый поиск действий, Enter —
    // выполнить, Esc — закрыть. Форма только выбирает элемент и возвращает
    // его вызывающему — выполнение остаётся на стороне хозяина (он знает
    // свои private-методы).
    // v3.9.5 — визуал по стилю диалогов программы (hero + поле в рамке +
    // список с отступами + футер-хинт со счётчиком); пустой запрос больше
    // не сортирует список — открытые сессии хозяина остаются сверху.
    public sealed class PaletteItem
    {
        public string Title { get; set; } = "";
        public string Category { get; set; } = "";
        public Action? Action { get; set; }
        // v3.9.5 — стабильный ключ статичной строки каталога
        // (Services.PaletteCatalog). Пустой = динамика (открытые
        // сессии/консоли): настройкой не скрывается.
        public string Key { get; set; } = "";
    }

    public class CommandPaletteForm : Form
    {
        private readonly List<PaletteItem> _items;
        private readonly TextBox _searchBox;
        private readonly DataGridView _grid;
        private readonly Label _hint;
        private readonly Button _heroSettingsBtn;
        private HashSet<string> _hidden;
        private List<PaletteItem> _view = new();
        private PaletteItem? _chosen;

        // Чистый фильтр (харнесс гоняет без UI): точные совпадения начала
        // строки выше, далее Contains, регистр не важен. Пустой запрос —
        // список как есть (в исходном порядке, без сортировки).
        public static List<PaletteItem> Filter(IEnumerable<PaletteItem> items, string query)
        {
            var list = items.ToList();
            if (string.IsNullOrWhiteSpace(query))
                return list;
            var q = query.Trim();
            var start = new List<PaletteItem>();
            var contain = new List<PaletteItem>();
            foreach (var it in list)
            {
                var t = it.Title ?? "";
                var c = it.Category ?? "";
                if (t.StartsWith(q, StringComparison.OrdinalIgnoreCase)) start.Add(it);
                else if (t.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                         c.Contains(q, StringComparison.OrdinalIgnoreCase)) contain.Add(it);
            }
            start.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));
            contain.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));
            start.AddRange(contain);
            return start;
        }

        // Модально показывает палитру; null — отмена (Esc).
        public static PaletteItem? Run(IWin32Window owner, IEnumerable<PaletteItem> items)
        {
            using var f = new CommandPaletteForm(items);
            Helpers.UiModal.ShowDimmed(owner, f);
            return f._chosen;
        }

        public CommandPaletteForm(IEnumerable<PaletteItem> items)
        {
            _items = items.ToList();
            Text = "Палитра команд";
            StartPosition = FormStartPosition.CenterParent;
            // v3.9.5 — регулировка мышкой + запоминание размера.
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = false;
            MinimumSize = new Size(760, 560);
            Font = new Font("Segoe UI", 9.5F);
            BackColor = Helpers.UiTheme.DialogBackground;
            // v3.9.5 — окно ×2 (было 600×480; влезает в 1920×1080).
            ClientSize = new Size(1200, 960);

            // Градиентный hero сверху — как в остальных диалогах.
            var hero = Helpers.UiModal.Hero("Палитра команд",
                "Поиск действий, сессий и настроек — Enter выполняет", "list");
            hero.Dock = DockStyle.Top;
            Controls.Add(hero);

            // v3.9.5 — «Настройка» переехала в хедер справа (просьба
            // пользователя): окно со ВСЕМ каталогом. Плоская кнопка
            // в стиле ✕ hero; TabStop=false — фокус не уводит.
            _heroSettingsBtn = new Button
            {
                Text = "Настройка…",
                Size = new Size(140, 30),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.Transparent,
                ForeColor = Helpers.UiTheme.DialogHeaderFg,
                Font = new Font("Segoe UI", 9F),
                TabStop = false,
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _heroSettingsBtn.FlatAppearance.BorderSize = 0;
            _heroSettingsBtn.FlatAppearance.MouseOverBackColor = Color.FromArgb(45, 255, 255, 255);
            _heroSettingsBtn.FlatAppearance.MouseDownBackColor = Color.FromArgb(70, 255, 255, 255);
            _heroSettingsBtn.Click += (_, _) => OpenSettings();
            hero.Controls.Add(_heroSettingsBtn);

            // Поле поиска в рамке (UiModal.Field) под hero.
            var searchPanel = new Panel
            {
                Location = new Point(0, 80),
                Size = new Size(1200, 64),
                BackColor = Helpers.UiTheme.DialogBackground
            };
            _searchBox = new TextBox();
            var searchWrap = Helpers.UiModal.Field(_searchBox, 1160, 36);
            _searchBox.Font = new Font("Segoe UI", 11.5F);
            _searchBox.ForeColor = Helpers.UiTheme.Foreground;
            searchWrap.Location = new Point(14, 14);
            searchPanel.Controls.Add(searchWrap);
            Controls.Add(searchPanel);

            // Список команд — в отступе от краёв, стиль «Открытых файлов».
            var body = new Panel
            {
                Location = new Point(0, 144),
                Size = new Size(1200, 782),
                BackColor = Helpers.UiTheme.DialogBackground,
                Padding = new Padding(14, 4, 14, 4)
            };
            _grid = new DoubleBufferedDataGridView
            {
                Dock = DockStyle.Fill,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                MultiSelect = false,
                TabStop = false
            };
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            { Name = "Title", HeaderText = "Команда", FillWeight = 70, MinimumWidth = 200 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            { Name = "Category", HeaderText = "Раздел", FillWeight = 30, MinimumWidth = 90 });
            Helpers.UiGridStyle.ApplyCardGrid(_grid, 36, 36);
            body.Controls.Add(_grid);
            Controls.Add(body);

            // v3.9.5 — активная строка заметнее штатного выбора (#E9EFF6
            // почти неотличим от ховера): фон глубже + акцентная полоска
            // 3px слева. Рисуем сами: Background → своя заливка → полоска
            // → ContentForeground (текст из стиля колонки).
            _grid.CellPainting += (s, e) =>
            {
                try
                {
                    if (e == null || e.RowIndex < 0 || e.ColumnIndex < 0) return;
                    if (!_grid.Rows[e.RowIndex].Selected) return;
                    e.Paint(e.ClipBounds,
                        DataGridViewPaintParts.Background | DataGridViewPaintParts.Border);
                    using (var bg = new SolidBrush(ActiveRowBg))
                        e.Graphics.FillRectangle(bg, e.CellBounds);
                    if (e.ColumnIndex == 0)
                    {
                        using var bar = new SolidBrush(Helpers.UiTheme.Accent);
                        e.Graphics.FillRectangle(bar,
                            e.CellBounds.X, e.CellBounds.Y, 3, e.CellBounds.Height);
                    }
                    e.Paint(e.ClipBounds, DataGridViewPaintParts.ContentForeground);
                    e.Handled = true;
                }
                catch { }
            };

            // Футер-хинт: счётчик найденного + клавиши.
            _hint = new Label
            {
                Location = new Point(0, 926),
                Size = new Size(1200, 34),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(14, 0, 0, 0),
                Font = new Font("Segoe UI", 8.5F),
                BackColor = Helpers.UiTheme.DialogCard,
                ForeColor = Helpers.UiTheme.MutedForeground,
                Text = ""
            };
            _hint.Paint += (s, e) =>
            {
                using var pen = new Pen(Helpers.UiTheme.Border, 1);
                e.Graphics.DrawLine(pen, 0, 0, _hint.Width, 0);
            };
            Controls.Add(_hint);

            _searchBox.TextChanged += (_, _) => Render();
            _searchBox.KeyDown += SearchBox_KeyDown;
            _grid.CellDoubleClick += (_, _) => Accept();

            // v3.9.5 — Enter/стрелки работают из любого места формы, а не
            // только из поля поиска: фокус в гриде после клика мышью —
            // превью перехватывает клавишу раньше грида (известный рецепт
            // KeyPreview + SuppressKeyPress). Двойного срабатывания нет:
            // превью ест клавишу, ProcessDialogKey её уже не видит.
            KeyPreview = true;
            KeyDown += (_, e) =>
            {
                try
                {
                    if (ActiveControl == _searchBox) return;
                    if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Accept(); }
                    else if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down)
                    { e.SuppressKeyPress = true; MoveSelection(e.KeyCode == Keys.Down ? 1 : -1); }
                }
                catch { }
            };

            _hidden = Services.PaletteStore.LoadHidden();
            Shown += (_, _) =>
            {
                Render();
                _searchBox.Focus();
                // Дим-подложка UiModal.ShowDimmed может увести активацию:
                // TopMost-мигание (приём ActivateRdpSession) + Activate +
                // фокус в поиск. Иначе первый Enter уходит мимо.
                BeginInvoke(new Action(() =>
                {
                    try { TopMost = true; TopMost = false; } catch { }
                    try { Activate(); } catch { }
                    try { _searchBox.Focus(); } catch { }
                }));
            };

            var hiddenClose = new Button
            {
                Visible = true, TabStop = false,
                Location = new Point(-200, -200), Size = new Size(1, 1),
                DialogResult = DialogResult.Cancel
            };
            Controls.Add(hiddenClose);
            CancelButton = hiddenClose;

            // v3.9.5 — ресайз: панель поиска/список/футер-хинт по окну.
            Resize += (_, _) => LayoutContent();
            LayoutContent();
            Services.WindowStateStore.Register(this, "commandPalette");

            void LayoutContent()
            {
                try
                {
                    int w = ClientSize.Width, h = ClientSize.Height;
                    searchPanel.Width = w;
                    searchWrap.Width = Math.Max(300, w - 40); // 14 + 26 по краям
                    body.Width = w;
                    body.Height = Math.Max(200, h - 144 - 34);
                    _hint.Width = w;
                    _hint.Top = h - 34;
                    // Кнопка «Настройка» в хедере справа, по центру band 80.
                    _heroSettingsBtn.Location = new Point(
                        Math.Max(0, hero.ClientSize.Width - 156), 25);
                }
                catch { }
            }
        }

        // Фон активной строки (в синей теме — глубже ховера #E9EFF6).
        private static Color ActiveRowBg => Helpers.UiTheme.CurrentName != "Olive"
            ? Color.FromArgb(0xD6, 0xE4, 0xF5)
            : Helpers.UiTheme.AccentSoft;

        private void SearchBox_KeyDown(object? sender, KeyEventArgs e)
        {
            // Enter/стрелки перехватываются раньше KeyDown: превью формы
            // (фокус в гриде) или ProcessDialogKey (фокус в поиске).
            // Здесь остался только Esc (ShowDimmed тоже закрывает по Esc).
            if (e.KeyCode == Keys.Escape)
            {
                _chosen = null;
                Close();
            }
        }

        // v3.9.5 — Enter/стрелки независимо от фокуса (паттерн
        // EscModalForm): препроцессинг диалога срабатывает раньше
        // KeyDown контролов. Возврат true = клавиша съедена, дальше
        // не идёт — двойного выполнения Accept нет.
        protected override bool ProcessDialogKey(Keys keyData)
        {
            try
            {
                if (keyData == Keys.Enter) { Accept(); return true; }
                if (keyData == Keys.Up || keyData == Keys.Down)
                { MoveSelection(keyData == Keys.Down ? 1 : -1); return true; }
            }
            catch { }
            return base.ProcessDialogKey(keyData);
        }

        private void MoveSelection(int delta)
        {
            if (_grid.Rows.Count == 0) return;
            int idx = _grid.SelectedRows.Count > 0 ? _grid.SelectedRows[0].Index : -1;
            idx += delta;
            if (idx < 0) idx = 0;
            if (idx >= _grid.Rows.Count) idx = _grid.Rows.Count - 1;
            _grid.ClearSelection();
            _grid.Rows[idx].Selected = true;
            try { _grid.FirstDisplayedScrollingRowIndex = idx; } catch { }
        }

        private void OpenSettings()
        {
            try
            {
                if (PaletteSettingsForm.Run(this))
                {
                    _hidden = Services.PaletteStore.LoadHidden();
                    Render();
                }
            }
            catch (Exception ex)
            {
                try { Logger.Warn($"Палитра/настройка: {ex.Message}"); } catch { }
            }
        }

        private void Accept()
        {
            if (_grid.SelectedRows.Count == 0 && _grid.Rows.Count > 0)
            {
                _grid.Rows[0].Selected = true;
            }
            if (_grid.SelectedRows.Count == 0) return;
            _chosen = _grid.SelectedRows[0].Tag as PaletteItem;
            Close();
        }

        private void Render()
        {
            try
            {
                // Скрытые настройкой строки каталога (Key из PaletteCatalog)
                // не показываем; динамика (пустой Key) видна всегда.
                var pool = _hidden.Count == 0
                    ? _items
                    : _items.Where(it => string.IsNullOrEmpty(it.Key) || !_hidden.Contains(it.Key)).ToList();
                _view = Filter(pool, _searchBox.Text);
                _grid.SuspendLayout();
                _grid.Rows.Clear();
                foreach (var it in _view)
                {
                    int ri = _grid.Rows.Add(it.Title, it.Category);
                    _grid.Rows[ri].Tag = it;
                }
                if (_grid.Rows.Count > 0)
                {
                    _grid.ClearSelection();
                    _grid.Rows[0].Selected = true;
                }
                _grid.ResumeLayout();
                _hint.Text = _grid.Rows.Count == 0
                    ? "Ничего не найдено · Esc — закрыть"
                    : $"{_view.Count} команд · Enter — выполнить · Esc — закрыть";
            }
            catch (Exception ex)
            {
                Logger.Warn($"Палитра: {ex.Message}");
            }
        }
    }
}
