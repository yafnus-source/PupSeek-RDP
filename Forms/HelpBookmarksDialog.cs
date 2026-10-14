using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using ShadowRDP.Helpers;
using ShadowRDP.Services;

namespace ShadowRDP.Forms
{
    // v3.9.5 — закладки справки: выделенный в «?» текст сохраняется
    // (help_bookmarks.json) и открывается переходом в один клик.
    public class HelpBookmarksDialog : Form
    {
        private readonly HelpForm _owner;
        private readonly ListBox _list;
        private readonly Label _statusLabel;
        private List<HelpBookmark> _items = new();

        public static void Show(HelpForm owner)
        {
            using var d = new HelpBookmarksDialog(owner);
            UiModal.ShowDimmed(owner, d);
        }

        public HelpBookmarksDialog(HelpForm owner)
        {
            _owner = owner;
            Text = "Закладки справки";
            ClientSize = new Size(620, 440);
            MinimumSize = new Size(520, 360);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = false;
            Font = new Font("Segoe UI", 9F);
            BackColor = UiTheme.DialogBackground;
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { e.Handled = true; Close(); } };

            var hero = UiModal.Hero("Закладки справки",
                "сохранённые фрагменты — клик возвращает к месту", "book");
            hero.Dock = DockStyle.Top;
            Controls.Add(hero);

            var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = UiTheme.DialogCard };
            footer.Paint += (s, e) => { using var pen = new Pen(UiTheme.Border, 1); e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };
            var addBtn = UiButtons.MakeCompactFilled("Добавить выделенное", 180);
            addBtn.Click += (s, e) => AddSelected();
            var goBtn = UiButtons.MakeCompactFilled("Перейти", 100);
            goBtn.Click += (s, e) => GoToSelected();
            var delBtn = UiButtons.MakeCompactFlat("Удалить", 100);
            delBtn.Click += (s, e) => DeleteSelected();
            var closeBtn = UiButtons.MakeCompactFilled("Закрыть", 100, bgOverride: UiTheme.MutedForeground);
            closeBtn.Click += (s, e) => Close();
            footer.Controls.Add(addBtn);
            footer.Controls.Add(goBtn);
            footer.Controls.Add(delBtn);
            footer.Controls.Add(closeBtn);
            footer.Resize += (s, e) =>
            {
                try
                {
                    int w = footer.ClientSize.Width;
                    addBtn.Location = new Point(16, 15);
                    closeBtn.Location = new Point(w - 16 - closeBtn.Width, 15);
                    goBtn.Location = new Point(closeBtn.Left - 8 - goBtn.Width, 15);
                    delBtn.Location = new Point(goBtn.Left - 8 - delBtn.Width, 15);
                    _statusLabel.Width = Math.Max(60, delBtn.Left - 16 - 16 - addBtn.Right);
                }
                catch { }
            };
            _statusLabel = new Label
            {
                Location = new Point(16 + 180 + 16, 15),
                Size = new Size(100, 22),
                Font = new Font("Segoe UI", 8.5F),
                ForeColor = UiTheme.MutedForeground,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft,
                Text = ""
            };
            footer.Controls.Add(_statusLabel);
            Controls.Add(footer);

            var body = new Panel
            {
                Location = new Point(0, 80),
                Size = new Size(620, 308),
                BackColor = UiTheme.DialogBackground,
                Padding = new Padding(14, 12, 14, 12)
            };
            var hint = new Label
            {
                Dock = DockStyle.Top,
                Height = 22,
                Font = new Font("Segoe UI", 8.5F),
                ForeColor = UiTheme.MutedForeground,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft,
                Text = "Выделите текст в окне справки и нажмите «Добавить выделенное»."
            };
            _list = new ListBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.White,
                ForeColor = UiTheme.Foreground,
                Font = new Font("Segoe UI", 9F),
                IntegralHeight = false
            };
            _list.DoubleClick += (s, e) => GoToSelected();
            body.Controls.Add(_list);
            body.Controls.Add(hint);
            Controls.Add(body);

            RefreshList();
            Resize += (s, e) => LayoutBody();
            LayoutBody();
            Shown += (s, e) => LayoutBody();

            void LayoutBody()
            {
                try
                {
                    body.Width = ClientSize.Width;
                    body.Height = Math.Max(150, ClientSize.Height - 80 - 52);
                }
                catch { }
            }
        }

        private void RefreshList()
        {
            try
            {
                _items = HelpBookmarkStore.Load();
                _list.BeginUpdate();
                _list.Items.Clear();
                foreach (var b in _items)
                {
                    string t = (b.Text ?? "").Replace("\r", " ").Replace("\n", " ");
                    if (t.Length > 80) t = t.Substring(0, 80) + "…";
                    _list.Items.Add($"[{b.SectionTitle}] {t}");
                }
                _list.EndUpdate();
                if (_list.Items.Count > 0) _list.SelectedIndex = 0;
                _statusLabel.Text = "";
            }
            catch { }
        }

        private void AddSelected()
        {
            try
            {
                var bm = _owner.GetSelectedSnippet();
                if (bm == null)
                {
                    _statusLabel.Text = "Сначала выделите текст в окне справки.";
                    return;
                }
                _items.Add(bm);
                HelpBookmarkStore.Save(_items);
                RefreshList();
                _list.SelectedIndex = _list.Items.Count - 1;
                _statusLabel.Text = "Добавлено.";
            }
            catch (Exception ex) { _statusLabel.Text = $"Не добавилось: {ex.Message}"; }
        }

        private void GoToSelected()
        {
            try
            {
                int i = _list.SelectedIndex;
                if (i < 0 || i >= _items.Count) return;
                _owner.JumpToBookmark(_items[i]);
                Close();
            }
            catch { }
        }

        private void DeleteSelected()
        {
            try
            {
                int i = _list.SelectedIndex;
                if (i < 0 || i >= _items.Count) return;
                _items.RemoveAt(i);
                HelpBookmarkStore.Save(_items);
                RefreshList();
            }
            catch { }
        }
    }
}
