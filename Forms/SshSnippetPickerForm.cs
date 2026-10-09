using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using ShadowRDP.Controls;
using ShadowRDP.Models;
using ShadowRDP.Services;

namespace ShadowRDP.Forms
{
    // v3.9.5 — окно-пикер сниппетов SSH (кнопка «Сниппеты» в панели SSH).
    // Раньше кнопка открывала контекстное меню с заголовком «Сниппеты SSH» —
    // таким же, как у окна менеджера, и при пустом сторе меню состояло из
    // одного пункта «Управление сниппетами…»: обе кнопки выглядели одинаково.
    // Теперь «Сниппеты» → список с запуском, «Управление…» → редактор.
    public sealed class SshSnippetPickerForm : Form
    {
        private readonly DataGridView _grid;
        private readonly TextBox _filterBox;
        private readonly ComboBox _groupCombo;
        private readonly List<string> _groupValues = new() { "" };
        private bool _fillingGroups;
        private readonly Label _hint;
        private readonly Label _runBtn;
        private SshSnippet? _chosen;

        private SshSnippetPickerForm()
        {
            Text = "Сниппеты SSH";
            StartPosition = FormStartPosition.CenterParent;
            // v3.9.5 — регулировка мышкой + запоминание размера.
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = false;
            MinimumSize = new Size(760, 520);
            Font = new Font("Segoe UI", 9F);
            BackColor = Helpers.UiTheme.DialogBackground;
            // v3.9.5 — высота ×2.5 (420 → 1050): 18+ сниппетов видно почти все.
            // v3.9.5 — ширина ×2 (560 → 1120) под 4 колонки с пояснениями.
            ClientSize = new Size(1120, 1050);

            var hero = Helpers.UiModal.Hero("Сниппеты SSH",
                "команды для быстрого запуска в терминале", "terminal");
            hero.Dock = DockStyle.Top;
            Controls.Add(hero);

            var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = Helpers.UiTheme.DialogCard };
            footer.Paint += (s, e) =>
            {
                using var pen = new Pen(Helpers.UiTheme.Border, 1);
                e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
            };
            _runBtn = Helpers.UiButtons.MakeCompactFilled("Выполнить", 110);
            var mgrBtn = Helpers.UiButtons.MakeCompactFilled("Управление…", 130,
                bgOverride: Helpers.UiTheme.MutedForeground);
            var closeBtn = Helpers.UiButtons.MakeCompactFilled("Закрыть", 100,
                bgOverride: Helpers.UiTheme.MutedForeground);
            footer.Controls.Add(_runBtn);
            footer.Controls.Add(mgrBtn);
            footer.Controls.Add(closeBtn);
            _hint = new Label
            {
                AutoSize = true,
                Location = new Point(16, 19),
                Font = new Font("Segoe UI", 8.5F),
                ForeColor = Helpers.UiTheme.MutedForeground,
                BackColor = Color.Transparent,
                Text = ""
            };
            footer.Controls.Add(_hint);
            footer.Resize += (s, e) =>
            {
                try
                {
                    int w = footer.ClientSize.Width;
                    closeBtn.Location = new Point(w - 120, 15);
                    mgrBtn.Location = new Point(w - 260, 15);
                    _runBtn.Location = new Point(w - 380, 15);
                }
                catch { }
            };
            Controls.Add(footer);

            var lblFilter = Helpers.UiModal.FieldLabel("Фильтр", 16, 96);
            Controls.Add(lblFilter);
            _filterBox = new TextBox();
            var filterWrap = Helpers.UiModal.Field(_filterBox, 780);
            filterWrap.Location = new Point(16, 112);
            Controls.Add(filterWrap);
            var lblGroup = Helpers.UiModal.FieldLabel("Группа", 806, 116);
            Controls.Add(lblGroup);
            _groupCombo = new ComboBox
            {
                Location = new Point(866, 112),
                Size = new Size(238, 26),
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Segoe UI", 9F)
            };
            Helpers.UiCombo.Apply(_groupCombo);
            Controls.Add(_groupCombo);
            _groupCombo.SelectedIndexChanged += (_, _) =>
            {
                if (!_fillingGroups) Render();
            };

            // v3.9.5 — список в стиле «Открытых файлов»: headerless-карточка
            // + грид ApplyCardGrid.
            var cardList = Helpers.UiModal.Card(12, 148, 1096, 838, "", "", withHeader: false);
            try { cardList.Region?.Dispose(); cardList.Region = null; } catch { }
            Controls.Add(cardList);

            _grid = new DoubleBufferedDataGridView
            {
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                MultiSelect = false
            };
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            { Name = "Name", HeaderText = "Имя", FillWeight = 22, MinimumWidth = 100 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            { Name = "Group", HeaderText = "Группа", FillWeight = 12, MinimumWidth = 60 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            { Name = "Command", HeaderText = "Команда", FillWeight = 44, MinimumWidth = 140 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            { Name = "Description", HeaderText = "Пояснение", FillWeight = 22, MinimumWidth = 100 });
            Helpers.UiGridStyle.ApplyCardGrid(_grid, cardList);
            _grid.SelectionChanged += (_, _) => _runBtn.Enabled = Current() != null;
            _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) Accept(); };
            _grid.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    e.Handled = true;
                    Accept();
                }
            };

            _filterBox.TextChanged += (_, _) => Render();
            _runBtn.Click += (_, _) => Accept();
            mgrBtn.Click += (_, _) =>
            {
                try { SnippetManagerForm.Show(this); }
                catch (Exception ex) { Logger.Warn($"Сниппеты: менеджер не открылся: {ex.Message}"); }
                Render();
            };
            closeBtn.Click += (_, _) => Close();

            Render();
            Shown += (_, _) => _filterBox.Focus();

            // v3.9.5 — ресайз: «Группа» прижата вправо, фильтр в остаток,
            // список на всю ширину и остаток высоты.
            Resize += (_, _) => LayoutContent();
            LayoutContent();
            Services.WindowStateStore.Register(this, "snippetPicker");

            void LayoutContent()
            {
                try
                {
                    int w = ClientSize.Width, h = ClientSize.Height;
                    _groupCombo.Left = Math.Max(300, w - 16 - _groupCombo.Width);
                    lblGroup.Left = _groupCombo.Left - 60;
                    filterWrap.Width = Math.Max(240, lblGroup.Left - 16 - 10);
                    cardList.Width = Math.Max(400, w - 24);
                    cardList.Height = Math.Max(200, h - 148 - 52 - 12);
                }
                catch { }
            }
        }

        // Пикер модальный: выполняем команду ПОСЛЕ закрытия окна, чтобы
        // терминал был виден сразу (владелец — SSH-панель).
        public static void Show(IWin32Window owner, Action<SshSnippet> run)
        {
            try
            {
                using var f = new SshSnippetPickerForm();
                if (Helpers.UiModal.ShowDimmed(owner, f) == DialogResult.OK && f._chosen != null)
                    run(f._chosen);
            }
            catch (Exception ex) { Logger.Warn($"Сниппеты: пикер не открылся: {ex.Message}"); }
        }

        private SshSnippet? Current()
        {
            if (_grid.SelectedRows.Count == 0) return null;
            return _grid.SelectedRows[0].Tag as SshSnippet;
        }

        private void Accept()
        {
            var s = Current();
            if (s == null) return;
            _chosen = s;
            DialogResult = DialogResult.OK;
            Close();
        }

        private void RenderGroups()
        {
            _fillingGroups = true;
            try
            {
                string cur = _groupValues.Count > 0 && _groupCombo.SelectedIndex >= 0
                    && _groupCombo.SelectedIndex < _groupValues.Count
                    ? _groupValues[_groupCombo.SelectedIndex] : "";
                var groups = SshSnippetStore.All()
                    .Select(s => s.Group)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                _groupValues.Clear();
                _groupValues.Add("");
                _groupCombo.Items.Clear();
                _groupCombo.Items.Add("Все группы");
                foreach (var grp in groups)
                {
                    _groupValues.Add(grp);
                    _groupCombo.Items.Add(grp.Length == 0 ? "(без группы)" : grp);
                }
                int idx = _groupValues.FindIndex(v =>
                    string.Equals(v, cur, StringComparison.OrdinalIgnoreCase));
                _groupCombo.SelectedIndex = idx >= 0 ? idx : 0;
            }
            finally { _fillingGroups = false; }
        }

        private void Render()
        {
            try
            {
                string q = (_filterBox.Text ?? "").Trim();
                RenderGroups();
                string g = _groupValues.Count > 0 && _groupCombo.SelectedIndex >= 0
                    && _groupCombo.SelectedIndex < _groupValues.Count
                    ? _groupValues[_groupCombo.SelectedIndex] : "";
                var list = SshSnippetStore.All();
                if (g.Length > 0)
                    list = list.Where(s => string.Equals(s.Group, g,
                        StringComparison.OrdinalIgnoreCase)).ToList();
                if (q.Length > 0)
                    list = list.Where(s =>
                        s.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        s.Group.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        s.Command.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        s.Description.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                list = list
                    .OrderBy(s => s.Group, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                _grid.SuspendLayout();
                _grid.Rows.Clear();
                foreach (var s in list)
                {
                    int i = _grid.Rows.Add(s.Name, s.Group, s.Command, s.Description);
                    _grid.Rows[i].Tag = s;
                }
                if (_grid.Rows.Count > 0)
                {
                    _grid.Rows[0].Selected = true;
                    _grid.FirstDisplayedScrollingRowIndex = 0;
                }
                _grid.ResumeLayout();

                _runBtn.Enabled = list.Count > 0;
                _hint.Text = SshSnippetStore.All().Count == 0
                    ? "Пока нет сниппетов — нажмите «Управление…»"
                    : (q.Length > 0 && list.Count == 0
                        ? "Ничего не найдено"
                        : $"{list.Count} команд(ы) — Enter или двойной клик — запуск");
            }
            catch (Exception ex) { Logger.Warn($"Сниппеты: рендер пикера: {ex.Message}"); }
        }
    }
}
