using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace ShadowRDP.Forms
{
    // v3.9.4 — команда-палитра (Ctrl+K): быстрый поиск действий, Enter —
    // выполнить, Esc — закрыть. Форма только выбирает элемент и возвращает
    // его вызывающему — выполнение остаётся на стороне хозяина (он знает
    // свои private-методы).
    public sealed class PaletteItem
    {
        public string Title { get; set; } = "";
        public string Category { get; set; } = "";
        public Action? Action { get; set; }
    }

    public class CommandPaletteForm : Form
    {
        private readonly List<PaletteItem> _items;
        private readonly TextBox _searchBox;
        private readonly ListView _list;
        private readonly Label _hint;
        private List<PaletteItem> _view = new();
        private PaletteItem? _chosen;

        // Чистый фильтр (харнесс гоняет без UI): точные совпадения начала
        // строки выше, далее Contains, регистр не важен.
        public static List<PaletteItem> Filter(IEnumerable<PaletteItem> items, string query)
        {
            var list = items.ToList();
            if (string.IsNullOrWhiteSpace(query))
                return list.OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase).ToList();
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
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Font = new Font("Segoe UI", 9.5F);
            BackColor = Helpers.UiTheme.DialogBackground;
            ClientSize = new Size(580, 430);

            _searchBox = new TextBox
            {
                Dock = DockStyle.Top,
                Height = 44,
                Font = new Font("Segoe UI", 12F),
                BorderStyle = BorderStyle.None,
                BackColor = Helpers.UiTheme.DialogCard,
                ForeColor = Helpers.UiTheme.Foreground,
                Margin = new Padding(8)
            };
            Controls.Add(_searchBox);

            _hint = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 24,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(8, 0, 0, 0),
                Font = new Font("Segoe UI", 8.25F),
                BackColor = Helpers.UiTheme.DialogCard,
                ForeColor = Helpers.UiTheme.MutedForeground,
                Text = "Enter — выполнить · Esc — закрыть · ввод фильтрует"
            };
            Controls.Add(_hint);

            _list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = false,
                HideSelection = false,
                BorderStyle = BorderStyle.None,
                BackColor = Helpers.UiTheme.DialogCard,
                ForeColor = Helpers.UiTheme.Foreground
            };
            _list.Columns.Add("Команда", 400);
            _list.Columns.Add("Раздел", 150);
            Controls.Add(_list);

            _searchBox.TextChanged += (_, _) => Render();
            _searchBox.KeyDown += SearchBox_KeyDown;
            _list.DoubleClick += (_, _) => Accept();
            Shown += (_, _) =>
            {
                Render();
                _searchBox.Focus();
            };

            var hiddenClose = new Button
            {
                Visible = true, TabStop = false,
                Location = new Point(-200, -200), Size = new Size(1, 1),
                DialogResult = DialogResult.Cancel
            };
            Controls.Add(hiddenClose);
            CancelButton = hiddenClose;
        }

        private void SearchBox_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Down || e.KeyCode == Keys.Up)
            {
                if (_list.Items.Count > 0)
                {
                    int idx = _list.SelectedIndices.Count > 0 ? _list.SelectedIndices[0] : -1;
                    idx += e.KeyCode == Keys.Down ? 1 : -1;
                    if (idx < 0) idx = 0;
                    if (idx >= _list.Items.Count) idx = _list.Items.Count - 1;
                    foreach (ListViewItem i in _list.SelectedItems) i.Selected = false;
                    _list.Items[idx].Selected = true;
                    _list.Items[idx].EnsureVisible();
                }
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                Accept();
            }
            else if (e.KeyCode == Keys.Escape)
            {
                _chosen = null;
                Close();
            }
        }

        private void Accept()
        {
            if (_list.SelectedItems.Count == 0 && _list.Items.Count > 0)
            {
                _list.Items[0].Selected = true;
            }
            if (_list.SelectedItems.Count == 0) return;
            _chosen = _list.SelectedItems[0].Tag as PaletteItem;
            Close();
        }

        private void Render()
        {
            try
            {
                _view = Filter(_items, _searchBox.Text);
                _list.BeginUpdate();
                _list.Items.Clear();
                foreach (var it in _view)
                {
                    var item = new ListViewItem(new[] { it.Title, it.Category }) { Tag = it };
                    _list.Items.Add(item);
                }
                if (_list.Items.Count > 0) _list.Items[0].Selected = true;
                _list.EndUpdate();
            }
            catch (Exception ex)
            {
                Logger.Warn($"Палитра: {ex.Message}");
            }
        }
    }
}
