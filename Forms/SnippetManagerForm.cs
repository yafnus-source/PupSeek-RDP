using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using ShadowRDP.Models;
using ShadowRDP.Services;

namespace ShadowRDP.Forms
{
    // v3.9.4 — менеджер сниппетов SSH: список + карточка редактирования.
    // Save = upsert по имени; при переименовании выбранной записи старое
    // имя удаляется, чтобы не оставалось дублей.
    public class SnippetManagerForm : Form
    {
        private readonly ListView _list;
        private readonly TextBox _nameBox;
        private readonly TextBox _groupBox;
        private readonly TextBox _cmdBox;
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
            Text = "Сниппеты SSH";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Font = new Font("Segoe UI", 9F);
            BackColor = Helpers.UiTheme.DialogBackground;
            ClientSize = new Size(470, 620);

            var hero = Helpers.UiModal.Hero("Сниппеты SSH",
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

            _list = new ListView
            {
                Location = new Point(12, 92),
                Size = new Size(446, 196),
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                HideSelection = false,
                BackColor = Helpers.UiTheme.DialogCard,
                ForeColor = Helpers.UiTheme.Foreground,
                BorderStyle = BorderStyle.FixedSingle
            };
            _list.Columns.Add("Имя", 130, HorizontalAlignment.Left);
            _list.Columns.Add("Группа", 90, HorizontalAlignment.Left);
            _list.Columns.Add("Команда", 210, HorizontalAlignment.Left);
            _list.SelectedIndexChanged += (_, _) => OnSelect();
            Controls.Add(_list);

            var card = Helpers.UiModal.Card(12, 300, 446, 246, "Команда", "terminal");
            Controls.Add(card);

            card.Controls.Add(Helpers.UiModal.FieldLabel("Имя", 16, 48));
            _nameBox = new TextBox();
            var nameWrap = Helpers.UiModal.Field(_nameBox, 414);
            nameWrap.Location = new Point(16, 64);
            card.Controls.Add(nameWrap);

            card.Controls.Add(Helpers.UiModal.FieldLabel("Группа (необязательно)", 16, 108));
            _groupBox = new TextBox();
            var groupWrap = Helpers.UiModal.Field(_groupBox, 414);
            groupWrap.Location = new Point(16, 124);
            card.Controls.Add(groupWrap);

            card.Controls.Add(Helpers.UiModal.FieldLabel("Команда", 16, 168));
            _cmdBox = new TextBox
            {
                Multiline = true,
                AcceptsReturn = true,
                ScrollBars = ScrollBars.Vertical,
                Location = new Point(16, 184),
                Size = new Size(414, 50),
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
        }

        private void Render(string? selectName)
        {
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var s in SshSnippetStore.All())
            {
                var item = new ListViewItem(new[] { s.Name, s.Group, s.Command });
                item.Tag = s.Name;
                _list.Items.Add(item);
                if (selectName != null && string.Equals(s.Name, selectName,
                    StringComparison.OrdinalIgnoreCase))
                    item.Selected = true;
            }
            _list.EndUpdate();
        }

        private void OnSelect()
        {
            if (_list.SelectedItems.Count == 0) return;
            var name = _list.SelectedItems[0].Text;
            var s = SshSnippetStore.Find(name);
            if (s == null) return;
            _nameBox.Text = s.Name;
            _groupBox.Text = s.Group;
            _cmdBox.Text = s.Command;
            _selected = s.Name;
        }

        private void ClearFields()
        {
            _nameBox.Text = "";
            _groupBox.Text = "";
            _cmdBox.Text = "";
            _selected = null;
            foreach (ListViewItem i in _list.SelectedItems) i.Selected = false;
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
