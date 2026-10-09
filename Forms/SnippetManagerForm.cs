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
    // v3.9.4 — менеджер сниппетов SSH: список + карточка редактирования.
    // Save = upsert по имени; при переименовании выбранной записи старое
    // имя удаляется, чтобы не оставалось дублей.
    public class SnippetManagerForm : Form
    {
        private readonly DataGridView _grid;
        private readonly TextBox _nameBox;
        private readonly TextBox _groupBox;
        private readonly TextBox _descBox;
        private readonly TextBox _cmdBox;
        private readonly TextBox _searchBox;
        private readonly ComboBox _groupCombo;
        private readonly List<string> _groupValues = new() { "" };
        private bool _fillingGroups;
        private readonly Label _saveBtn;
        private readonly Label _delBtn;
        private readonly Label _closeBtn;
        private string? _selected;

        public static void Show(IWin32Window owner)
        {
            using var f = new SnippetManagerForm();
            Helpers.UiModal.ShowDimmed(owner, f);
        }

        public SnippetManagerForm()
        {
            Text = "Управление сниппетами";
            StartPosition = FormStartPosition.CenterParent;
            // v3.9.5 — регулировка мышкой + запоминание размера; минимум
            // под фильтры + список + карточку редактирования.
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = false;
            MinimumSize = new Size(900, 860);
            Font = new Font("Segoe UI", 9F);
            BackColor = Helpers.UiTheme.DialogBackground;
            // v3.9.5 — размер как у пикера «Сниппеты SSH» (1120×1050).
            ClientSize = new Size(1120, 1050);

            var hero = Helpers.UiModal.Hero("Управление сниппетами",
                "сохранённые команды для терминала", "terminal");
            hero.Dock = DockStyle.Top;
            Controls.Add(hero);

            var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = Helpers.UiTheme.DialogCard };
            footer.Paint += (s, e) =>
            {
                using var pen = new Pen(Helpers.UiTheme.Border, 1);
                e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
            };
            _saveBtn = Helpers.UiButtons.MakeCompactFilled("Сохранить", 140);
            _delBtn = Helpers.UiButtons.MakeCompactFilled("Удалить", 100,
                bgOverride: Helpers.UiTheme.MutedForeground);
            _closeBtn = Helpers.UiButtons.MakeCompactFilled("Закрыть", 100,
                bgOverride: Helpers.UiTheme.MutedForeground);
            footer.Controls.Add(_saveBtn);
            footer.Controls.Add(_delBtn);
            footer.Controls.Add(_closeBtn);
            footer.Resize += (s, e) =>
            {
                try
                {
                    int w = footer.ClientSize.Width;
                    _saveBtn.Location = new Point(w - 376, 15);
                    _delBtn.Location = new Point(w - 228, 15);
                    _closeBtn.Location = new Point(w - 120, 15);
                }
                catch { }
            };
            Controls.Add(footer);

            // v3.9.5 — поиск + фильтр по группам над списком.
            var filterBar = new Panel
            {
                Location = new Point(12, 86),
                Size = new Size(1096, 40),
                BackColor = Helpers.UiTheme.DialogBackground
            };
            filterBar.Controls.Add(Helpers.UiModal.FieldLabel("Поиск", 0, 10));
            _searchBox = new TextBox();
            var searchWrap = Helpers.UiModal.Field(_searchBox, 380);
            searchWrap.Location = new Point(56, 6);
            filterBar.Controls.Add(searchWrap);
            filterBar.Controls.Add(Helpers.UiModal.FieldLabel("Группа", 448, 10));
            _groupCombo = new ComboBox
            {
                Location = new Point(508, 6),
                Size = new Size(240, 26),
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Segoe UI", 9F)
            };
            Helpers.UiCombo.Apply(_groupCombo);
            filterBar.Controls.Add(_groupCombo);
            var resetBtn = Helpers.UiButtons.MakeCompactFlat("Сброс", 70);
            resetBtn.Location = new Point(760, 8);
            resetBtn.Click += (_, _) =>
            {
                try { _searchBox.Text = ""; } catch { }
                try { _groupCombo.SelectedIndex = 0; } catch { }
            };
            filterBar.Controls.Add(resetBtn);
            Controls.Add(filterBar);
            _searchBox.TextChanged += (_, _) => RenderGrid(null);
            _groupCombo.SelectedIndexChanged += (_, _) =>
            {
                if (!_fillingGroups) RenderGrid(null);
            };

            // v3.9.5 — список сниппетов в стиле «Открытых файлов».
            var cardList = Helpers.UiModal.Card(12, 134, 1096, 358, "", "", withHeader: false);
            try { cardList.Region?.Dispose(); cardList.Region = null; } catch { }
            Controls.Add(cardList);

            _grid = new DoubleBufferedDataGridView
            {
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                MultiSelect = false
            };
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            { Name = "Name", HeaderText = "Имя", FillWeight = 25, MinimumWidth = 90 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            { Name = "Group", HeaderText = "Группа", FillWeight = 15, MinimumWidth = 60 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            { Name = "Command", HeaderText = "Команда", FillWeight = 40, MinimumWidth = 120 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            { Name = "Description", HeaderText = "Пояснение", FillWeight = 20, MinimumWidth = 100 });
            Helpers.UiGridStyle.ApplyCardGrid(_grid, cardList);
            _grid.SelectionChanged += (_, _) => OnSelect();

            var card = Helpers.UiModal.Card(12, 504, 1096, 482, "Команда", "terminal");
            Controls.Add(card);

            card.Controls.Add(Helpers.UiModal.FieldLabel("Имя", 16, 48));
            _nameBox = new TextBox();
            var nameWrap = Helpers.UiModal.Field(_nameBox, 1064);
            nameWrap.Location = new Point(16, 64);
            card.Controls.Add(nameWrap);

            card.Controls.Add(Helpers.UiModal.FieldLabel("Группа (необязательно)", 16, 104));
            _groupBox = new TextBox();
            var groupWrap = Helpers.UiModal.Field(_groupBox, 1064);
            groupWrap.Location = new Point(16, 120);
            card.Controls.Add(groupWrap);

            card.Controls.Add(Helpers.UiModal.FieldLabel("Пояснение (необязательно)", 16, 160));
            _descBox = new TextBox();
            var descWrap = Helpers.UiModal.Field(_descBox, 1064);
            descWrap.Location = new Point(16, 176);
            card.Controls.Add(descWrap);

            card.Controls.Add(Helpers.UiModal.FieldLabel("Команда", 16, 216));
            _cmdBox = new TextBox
            {
                Multiline = true,
                AcceptsReturn = true,
                ScrollBars = ScrollBars.Vertical,
                Location = new Point(16, 232),
                Size = new Size(1064, 238),
                BorderStyle = BorderStyle.FixedSingle
            };
            card.Controls.Add(_cmdBox);

            var hiddenClose = new Button
            {
                Visible = true, TabStop = false,
                Location = new Point(-200, -200), Size = new Size(1, 1),
                DialogResult = DialogResult.Cancel
            };
            Controls.Add(hiddenClose);
            CancelButton = hiddenClose;

            _saveBtn.Click += (_, _) => Save();
            _delBtn.Click += (_, _) => Delete();
            _closeBtn.Click += (_, _) => Close();

            Render(null);

            // v3.9.5 — ресайз: фильтры/список/карточка по ширине окна,
            // карточка редактирования прижата к футеру, список тянется.
            Resize += (_, _) => LayoutContent();
            LayoutContent();
            Services.WindowStateStore.Register(this, "snippetManager");

            void LayoutContent()
            {
                try
                {
                    int w = ClientSize.Width, h = ClientSize.Height;
                    int cw = Math.Max(600, w - 24);
                    filterBar.Width = cw;
                    cardList.Width = cw;
                    card.Width = cw;
                    card.Top = Math.Max(314, h - 52 - 12 - 482);
                    cardList.Height = Math.Max(140, card.Top - 134 - 12);

                    int iw = Math.Max(300, card.ClientSize.Width - 32);
                    nameWrap.Width = iw;
                    groupWrap.Width = iw;
                    descWrap.Width = iw;
                    _cmdBox.Width = iw;
                    _cmdBox.Height = Math.Max(80, card.ClientSize.Height - 232 - 12);
                }
                catch { }
            }
        }

        private void Render(string? selectName)
        {
            RenderGroups();
            RenderGrid(selectName);
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
                    .Select(s => s.Group ?? "")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(g => g, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                _groupValues.Clear();
                _groupValues.Add("");
                _groupCombo.Items.Clear();
                _groupCombo.Items.Add("Все группы");
                foreach (var g in groups)
                {
                    _groupValues.Add(g);
                    _groupCombo.Items.Add(g.Length == 0 ? "(без группы)" : g);
                }
                int idx = _groupValues.FindIndex(v =>
                    string.Equals(v, cur, StringComparison.OrdinalIgnoreCase));
                _groupCombo.SelectedIndex = idx >= 0 ? idx : 0;
            }
            finally { _fillingGroups = false; }
        }

        private void RenderGrid(string? selectName)
        {
            string q = (_searchBox.Text ?? "").Trim();
            string g = _groupValues.Count > 0 && _groupCombo.SelectedIndex >= 0
                && _groupCombo.SelectedIndex < _groupValues.Count
                ? _groupValues[_groupCombo.SelectedIndex] : "";
            var list = SshSnippetStore.All();
            if (g.Length > 0)
                list = list.Where(s => string.Equals(s.Group ?? "", g,
                    StringComparison.OrdinalIgnoreCase)).ToList();
            if (q.Length > 0)
                list = list.Where(s =>
                    s.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (s.Group ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    s.Command.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    s.Description.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            list = list
                .OrderBy(s => s.Group ?? "", StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Name ?? "", StringComparer.OrdinalIgnoreCase)
                .ToList();
            _grid.SuspendLayout();
            _grid.Rows.Clear();
            foreach (var s in list)
            {
                int ri = _grid.Rows.Add(s.Name, s.Group, s.Command, s.Description);
                _grid.Rows[ri].Tag = s.Name;
                if (selectName != null && string.Equals(s.Name, selectName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    _grid.ClearSelection();
                    _grid.Rows[ri].Selected = true;
                    try { _grid.FirstDisplayedScrollingRowIndex = ri; } catch { }
                }
            }
            _grid.ResumeLayout();
        }

        private void OnSelect()
        {
            if (_grid.SelectedRows.Count == 0) return;
            var name = _grid.SelectedRows[0].Cells[0].Value?.ToString() ?? "";
            var s = SshSnippetStore.Find(name);
            if (s == null) return;
            _nameBox.Text = s.Name;
            _groupBox.Text = s.Group;
            _descBox.Text = s.Description;
            _cmdBox.Text = s.Command;
            _selected = s.Name;
        }

        private void ClearFields()
        {
            _nameBox.Text = "";
            _groupBox.Text = "";
            _descBox.Text = "";
            _cmdBox.Text = "";
            _selected = null;
            _grid.ClearSelection();
        }

        private void Save()
        {
            var name = _nameBox.Text.Trim();
            if (string.IsNullOrEmpty(name))
            {
                MessageBox.Show(this, "Укажите имя сниппета.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _nameBox.Focus();
                return;
            }
            var snippet = new SshSnippet
            {
                Name = name,
                Group = _groupBox.Text.Trim(),
                Description = _descBox.Text.Trim(),
                Command = _cmdBox.Text
            };
            // Переименование выбранной записи: сначала убрать старое имя.
            if (_selected != null && !string.Equals(_selected, name, StringComparison.OrdinalIgnoreCase))
                SshSnippetStore.Remove(_selected);
            SshSnippetStore.Upsert(snippet);
            ClearFields();
            Render(name);
        }

        private void Delete()
        {
            var name = _selected ?? _nameBox.Text.Trim();
            if (string.IsNullOrEmpty(name))
            {
                MessageBox.Show(this, "Выберите сниппет для удаления.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (MessageBox.Show(this, $"Удалить сниппет «{name}»?", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            SshSnippetStore.Remove(name);
            ClearFields();
            Render(null);
        }
    }
}
