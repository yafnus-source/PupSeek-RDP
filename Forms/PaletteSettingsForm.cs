using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using ShadowRDP.Controls;
using ShadowRDP.Services;

namespace ShadowRDP.Forms
{
    // v3.9.5 — настройка палитры команд (Ctrl+K): кнопка «Настройка…»
    // в футере палитры. Окно показывает ВЕСЬ каталог (PaletteCatalog.All)
    // с галочками «В палитре»: снять — строка пропадёт из палитры,
    // вернуть — снова появится. Скрытые ключи лежат в palette.json
    // (PaletteStore). Открытые сессии/консоли — динамические, настройкой
    // не скрываются и в список не входят.
    public sealed class PaletteSettingsForm : Form
    {
        private readonly DataGridView _grid;
        private bool _saved;

        // true — пользователь нажал «Готово», палитре надо перерисоваться.
        public static bool Run(IWin32Window owner)
        {
            using var f = new PaletteSettingsForm();
            Helpers.UiModal.ShowDimmed(owner, f);
            return f._saved;
        }

        public PaletteSettingsForm()
        {
            Text = "Настройка палитры";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = false;
            MinimumSize = new Size(560, 480);
            Font = new Font("Segoe UI", 9F);
            BackColor = Helpers.UiTheme.DialogBackground;
            ClientSize = new Size(640, 600);

            var hero = Helpers.UiModal.Hero("Настройка палитры",
                "галочка — строка видна в палитре (Ctrl+K)", "list");
            hero.Dock = DockStyle.Top;
            Controls.Add(hero);

            var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = Helpers.UiTheme.DialogCard };
            footer.Paint += (s, e) =>
            {
                using var pen = new Pen(Helpers.UiTheme.Border, 1);
                e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
            };
            var okBtn = Helpers.UiButtons.MakeCompactFilled("Готово", 120);
            okBtn.Click += (_, _) => { Save(); _saved = true; Close(); };
            var cancelBtn = Helpers.UiButtons.MakeCompactFilled("Отмена", 100,
                bgOverride: Helpers.UiTheme.MutedForeground);
            cancelBtn.Click += (_, _) => Close();
            var allBtn = Helpers.UiButtons.MakeCompactFlat("Выбрать все", 110);
            allBtn.Click += (_, _) => SetAll(true);
            var noneBtn = Helpers.UiButtons.MakeCompactFlat("Снять все", 100);
            noneBtn.Click += (_, _) => SetAll(false);
            footer.Controls.Add(okBtn);
            footer.Controls.Add(cancelBtn);
            footer.Controls.Add(allBtn);
            footer.Controls.Add(noneBtn);
            footer.Resize += (s, e) =>
            {
                try
                {
                    int w = footer.ClientSize.Width;
                    okBtn.Location = new Point(w - 140, 15);
                    cancelBtn.Location = new Point(w - 260, 15);
                    noneBtn.Location = new Point(16, 15);
                    allBtn.Location = new Point(126, 15);
                }
                catch { }
            };
            Controls.Add(footer);

            // v3.9.5 — явные границы, НЕ Dock=Fill: Fill после hero/footer
            // при док-раскладке забирал всё окно и сплющивал их в ноль
            // (грид на всё окно без шапки и кнопок — баг пользователя).
            // Отступы сверху/снизу увеличены по просьбе (дыхание списка).
            var body = new Panel
            {
                Location = new Point(0, 80),
                Size = new Size(640, 468),
                BackColor = Helpers.UiTheme.DialogBackground,
                Padding = new Padding(14, 12, 14, 12)
            };
            _grid = new DoubleBufferedDataGridView
            {
                Dock = DockStyle.Fill,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                MultiSelect = false,
                TabStop = false,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false
            };
            _grid.Columns.Add(new DataGridViewCheckBoxColumn
            { Name = "Show", HeaderText = "В палитре", FillWeight = 25, MinimumWidth = 70 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            { Name = "Title", HeaderText = "Команда", FillWeight = 50, MinimumWidth = 180, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            { Name = "Category", HeaderText = "Раздел", FillWeight = 25, MinimumWidth = 90, ReadOnly = true });
            body.Controls.Add(_grid);
            // СТРОГО после добавления в родителя: AttachGrid внутри
            // ApplyCardGrid вешает кастомный скролл на Parent и делает
            // early-return без него (тогда полос нет вообще — баг).
            Helpers.UiGridStyle.ApplyCardGrid(_grid, 32, 32);
            // ApplyCardGrid ставит ReadOnly=true на весь грид (стиль
            // карточек) — для колонки галочек редактирование возвращаем,
            // иначе одиночные клики игнорируются (баг пользователя).
            _grid.ReadOnly = false;
            // Один клик по галочке сразу фиксирует значение (иначе
            // DataGridView требует два клика: вход в edit + переключение).
            _grid.CurrentCellDirtyStateChanged += (s, e) =>
            {
                try
                {
                    if (_grid.IsCurrentCellDirty)
                        _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
                }
                catch { }
            };
            Controls.Add(body);

            Resize += (_, _) => LayoutBody();
            LayoutBody();
            void LayoutBody()
            {
                try
                {
                    body.Width = ClientSize.Width;
                    body.Height = Math.Max(200, ClientSize.Height - 80 - 52);
                }
                catch { }
            }

            var hiddenClose = new Button
            {
                Visible = true, TabStop = false,
                Location = new Point(-200, -200), Size = new Size(1, 1),
                DialogResult = DialogResult.Cancel
            };
            Controls.Add(hiddenClose);
            CancelButton = hiddenClose;

            ReloadRows();
            Services.WindowStateStore.Register(this, "paletteSettings");
        }

        // Заполняет грид: сначала недавние ММС из истории «Встроить ММС»
        // (у каждой свой чекбокс — иначе их видно в палитре, а в настройке
        // нет), затем ВЕСЬ статичный каталог. Строки добавляются в
        // конструкторе, харнесс проверяет без показа.
        internal void ReloadRows()
        {
            var hidden = PaletteStore.LoadHidden();
            _grid.Rows.Clear();
            try
            {
                foreach (var entry in Services.EmbeddedAppsStore.Load())
                {
                    string key = Services.PaletteCatalog.RecentKey(entry.Path);
                    string name = string.IsNullOrWhiteSpace(entry.Name) ? entry.Path : entry.Name;
                    int ri = _grid.Rows.Add(!hidden.Contains(key), $"ММС: {name}", "ММС");
                    _grid.Rows[ri].Tag = key;
                }
            }
            catch { }
            foreach (var def in PaletteCatalog.All)
            {
                int ri = _grid.Rows.Add(!hidden.Contains(def.Key), def.Title, def.Category);
                _grid.Rows[ri].Tag = def.Key;
            }
        }

        private void SetAll(bool on)
        {
            try
            {
                foreach (DataGridViewRow r in _grid.Rows)
                    r.Cells["Show"].Value = on;
                _grid.EndEdit();
            }
            catch { }
        }

        private void Save()
        {
            try
            {
                _grid.EndEdit();
                var off = new List<string>();
                var shown = new HashSet<string>(StringComparer.Ordinal);
                foreach (DataGridViewRow r in _grid.Rows)
                {
                    if (r.Tag is string key && !string.IsNullOrEmpty(key))
                    {
                        shown.Add(key);
                        if (!(r.Cells["Show"].Value is true))
                            off.Add(key);
                    }
                }
                // Протухшие ключи недавних ММС (пути уже нет в истории)
                // подчищаем, остальное прошлое hidden переносим ТОЛЬКО если
                // его нет ни в каталоге, ни на экране: иначе снятая когда-то
                // галочка воскресала бы при каждом сохранении (баг: вернуть
                // пункт было невозможно, особенно после «Снять все»).
                var live = new HashSet<string>(StringComparer.Ordinal);
                bool historyOk = false;
                try
                {
                    foreach (var e in Services.EmbeddedAppsStore.Load())
                        live.Add(Services.PaletteCatalog.RecentKey(e.Path));
                    historyOk = true;
                }
                catch { }
                HashSet<string> catalogKeys;
                try { catalogKeys = new HashSet<string>(PaletteCatalog.All.Select(d => d.Key), StringComparer.Ordinal); }
                catch { catalogKeys = new HashSet<string>(StringComparer.Ordinal); }
                var final = new HashSet<string>(off, StringComparer.Ordinal);
                try
                {
                    foreach (var k in PaletteStore.LoadHidden())
                    {
                        if (catalogKeys.Contains(k)) continue; // решает чекбокс
                        if (shown.Contains(k)) continue; // решает чекбокс
                        if (k.StartsWith(Services.PaletteCatalog.RecentPrefix, StringComparison.Ordinal))
                        {
                            // Недавняя не на экране: оставляем скрытой только
                            // если путь ещё в истории (гонка) или историю
                            // прочитать не вышло (нечем доказать протухлость).
                            if ((historyOk && live.Contains(k)) || !historyOk)
                                final.Add(k);
                        }
                        else final.Add(k); // чужой ключ (будущие версии) — не трогаем
                    }
                }
                catch { }
                PaletteStore.SaveHidden(final);
            }
            catch (Exception ex)
            {
                try { Logger.Warn($"Настройка палитры: {ex.Message}"); } catch { }
            }
        }
    }
}
