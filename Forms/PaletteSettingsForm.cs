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

            var body = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Helpers.UiTheme.DialogBackground,
                Padding = new Padding(14, 8, 14, 8)
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
            body.Controls.Add(_grid);
            Controls.Add(body);

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

        // Заполняет грид из каталога (дергается и из Shown не надо —
        // строки добавляются в конструкторе, харнесс проверяет без показа).
        internal void ReloadRows()
        {
            var hidden = PaletteStore.LoadHidden();
            _grid.Rows.Clear();
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
                foreach (DataGridViewRow r in _grid.Rows)
                {
                    bool on = r.Cells["Show"].Value is true;
                    if (!on && r.Tag is string key && !string.IsNullOrEmpty(key))
                        off.Add(key);
                }
                PaletteStore.SaveHidden(off);
            }
            catch (Exception ex)
            {
                try { Logger.Warn($"Настройка палитры: {ex.Message}"); } catch { }
            }
        }
    }
}
