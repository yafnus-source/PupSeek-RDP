using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using ShadowRDP.Controls;
using ShadowRDP.Helpers;
using ShadowRDP.Models;
using ShadowRDP.Services;

namespace ShadowRDP.Forms
{
    // Edit-address-book-entry dialog. Opens from the context menu's
    // "Редактировать..." item. Allows editing ALL fields of a record,
    // including ones that are read-only in the grid (Computer, User, IPs,
    // LastSeen). When the user clicks Save:
    //   1. Find the original entry by (oldComputer, oldUser) — case-insensitive.
    //   2. Replace its fields with the dialog values.
    //   3. Save to AddressBookStore.
    //   4. Caller (AddressBookForm) refreshes the grid.
    //
    // Returns true from ShowEditDialog if the user clicked Save AND something
    // actually changed.
    public class EditEntryForm : Form
    {
        private readonly TextBox _computerBox;
        private readonly TextBox _fullNameBox;
        private readonly TextBox _userBox;
        private readonly TextBox _phoneBox;
        private readonly TextBox _ipsBox;
        private readonly TextBox _lastSeenBox;
        private readonly ComboBox _groupBox;
        private readonly Panel _card;
        private readonly Label _saveBtn;
        private readonly Label _cancelBtn;

        private readonly string _origComputer;
        private readonly string _origUser;
        private bool _saved;
        // v2.2.10 — add-mode tracking + result entry (mirrors the values
        // typed by the user so ShowAddDialog can return them).
        private readonly bool _isAddMode;
        private AddressBookEntry? _addResult;

        private EditEntryForm(AddressBookEntry entry, bool isAddMode = false)
        {
            _origComputer = entry.Computer ?? "";
            _origUser = entry.User ?? "";
            _isAddMode = isAddMode;

            // v2.2.10: was 440 — Save + Cancel made of MakeFlat Label were
            // overlapping on hover (AutoSize=true + close X positions).
            // Now 480 + MakeFilled buttons width 120/96 with proper gap.
            // v4.9.34 — корпоративный вид: hero + карточка + футер.
            Text = isAddMode ? "Добавить запись в книгу" : "Редактирование записи";
            ClientSize = new Size(480, 488);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Font = new Font("Segoe UI", 9F);
            BackColor = ShadowRDP.Helpers.UiTheme.DialogBackground;

            var hero = ShadowRDP.Helpers.UiModal.Hero(
                isAddMode ? "Добавить запись в книгу" : "Редактирование записи",
                "Данные записи адресной книги",
                "add");
            Controls.Add(hero);

            _card = ShadowRDP.Helpers.UiModal.Card(12, 90, 456, 358, "Запись", "add", withHeader: false);
            Controls.Add(_card);

            int y = 14;

            _computerBox  = AddRow(ref y, "Компьютер:",    entry.Computer ?? "");
            _fullNameBox  = AddRow(ref y, "Имя (ФИО):",    entry.FullName ?? "");
            _userBox      = AddRow(ref y, "Логин:",         entry.User ?? "");
            _phoneBox     = AddRow(ref y, "Телефон:",       entry.Phone);
            _ipsBox       = AddRow(ref y, "IP-адрес(а):",   entry.IPs);
            _lastSeenBox  = AddRow(ref y, "Последний раз:", entry.LastSeen);
            // Group row — ComboBox with the 4 groups + "Без группы".
            _groupBox = AddGroupRow(ref y, "Группа:", entry.Group ?? "");

            // v2.2.10: Save = MakeFilled (#296AA0), Cancel = MakeFilled
            // (MutedForeground gray). Same width range → no overlap on hover.
            // v4.9.34 — футер корпоративного диалога.
            _saveBtn = ShadowRDP.Helpers.UiButtons.MakeCompactFilled(
                isAddMode ? "Добавить" : "Сохранить", 100);
            _saveBtn.Location = new Point(247, 458);
            _saveBtn.Click += (s, e) => OnSave();
            Controls.Add(_saveBtn);

            _cancelBtn = ShadowRDP.Helpers.UiButtons.MakeCompactFilled("Отмена", 100,
                bgOverride: ShadowRDP.Helpers.UiTheme.MutedForeground);
            _cancelBtn.Location = new Point(355, 458);
            _cancelBtn.Click += (s, e) => { _saved = false; Close(); };
            Controls.Add(_cancelBtn);

            // Hidden buttons for AcceptButton/CancelButton (Label doesn't
            // implement IButtonControl).
            var hiddenOk = new Button { Visible = true, TabStop = false,
                Location = new Point(-100, -100), Size = new Size(1, 1) };
            var hiddenCancel = new Button { Visible = true, TabStop = false,
                Location = new Point(-200, -200), Size = new Size(1, 1),
                DialogResult = DialogResult.Cancel };
            Controls.Add(hiddenOk);
            Controls.Add(hiddenCancel);
            AcceptButton = hiddenOk;
            CancelButton = hiddenCancel;
            hiddenOk.Click += (s, e) => OnSave();
        }

        private TextBox AddRow(ref int y, string label, string value)
        {
            // v4.9.34 — корпоративный ряд: подпись 7.5pt над полем.
            var lbl = ShadowRDP.Helpers.UiModal.FieldLabel(label.TrimEnd(':'), 16, y);
            _card.Controls.Add(lbl);
            var tb = new TextBox
            {
                Text = value ?? "",
                Location = new Point(16, y + 16),
                Size = new Size(424, 26),
                Font = new Font("Segoe UI", 10F)
            };
            _card.Controls.Add(tb);
            y += 48;
            return tb;
        }

        // Group picker — ComboBox dropdown with the 4 group labels + a blank
        // "(Без группы)" entry. Stores the ComputerGroup key in SelectedItem
        // (not the display label) so OnSave writes the right value to JSON.
        private ComboBox AddGroupRow(ref int y, string label, string currentValue)
        {
            var lbl = ShadowRDP.Helpers.UiModal.FieldLabel(label.TrimEnd(':'), 16, y);
            _card.Controls.Add(lbl);
            var cb = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(16, y + 16),
                Size = new Size(424, 26),
                Font = new Font("Segoe UI", 10F)
            };
            // Build items by adding display strings directly to Items — bypasses
            // data binding entirely. Two earlier attempts (ValueTuple, then a
            // small GroupOption class with DisplayMember/ValueMember) both
            // ended up with an empty drop-down, and SelectedIndex=0 threw.
            // Using Items.Add + a parallel keys list (stored on Tag) is
            // bulletproof: Items.Add always populates synchronously.
            var keys = new List<string>();
            cb.Items.Add("(Без группы)");
            keys.Add(ComputerGroup.None);
            foreach (var info in ComputerGroup.All)
            {
                cb.Items.Add(info.Label);
                keys.Add(info.Key);
            }
            // v2.2.28: also add custom groups from addressbook.json.
            var allEntries = AddressBookStore.Load();
            foreach (var g in allEntries
                         .Select(e => e.Group ?? "")
                         .Where(g => !string.IsNullOrEmpty(g))
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(g => g))
            {
                if (ComputerGroup.All.Any(i =>
                    string.Equals(i.Key, g, StringComparison.OrdinalIgnoreCase))) continue;
                cb.Items.Add(g);
                keys.Add(g);
            }
            cb.Tag = keys;
            // Select the row matching the current value (default to "none").
            int sel = keys.FindIndex(k => string.Equals(k, currentValue, StringComparison.OrdinalIgnoreCase));
            if (sel < 0) sel = 0;
            if (cb.Items.Count > 0) cb.SelectedIndex = sel;
            _card.Controls.Add(cb);
            y += 48;
            return cb;
        }

        private void OnSave()
        {
            if (string.IsNullOrWhiteSpace(_computerBox.Text) &&
                string.IsNullOrWhiteSpace(_userBox.Text))
            {
                MessageBox.Show(this,
                    "Хотя бы одно из полей «Компьютер» или «Логин» должно быть заполнено.",
                    "Редактирование", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                var groupKey = "";
                if (_groupBox.Tag is List<string> keys &&
                    _groupBox.SelectedIndex >= 0 &&
                    _groupBox.SelectedIndex < keys.Count)
                {
                    groupKey = keys[_groupBox.SelectedIndex];
                }

                // v2.2.10: add-mode branch — append a new entry instead of
                // looking up an existing one.
                if (_isAddMode)
                {
                    var entries = AddressBookStore.Load();
                    var fresh = new AddressBookEntry
                    {
                        Computer = _computerBox.Text.Trim(),
                        FullName = _fullNameBox.Text.Trim(),
                        User = _userBox.Text.Trim(),
                        Phone = _phoneBox.Text.Trim(),
                        IPs = _ipsBox.Text.Trim(),
                        LastSeen = _lastSeenBox.Text.Trim(),
                        Group = groupKey ?? ""
                    };
                    entries.Add(fresh);
                    AddressBookStore.Save(entries);
                    // Also mirror the values onto the entry the caller passed
                    // (so ShowAddDialog's return value reflects what user typed).
                    _addResult = fresh;
                    _saved = true;
                    Close();
                    return;
                }

                var existing = AddressBookStore.Load();
                var match = existing.FirstOrDefault(e =>
                    string.Equals(e.Computer ?? "", _origComputer, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(e.User ?? "", _origUser, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                {
                    MessageBox.Show(this,
                        "Запись не найдена — возможно, её удалили в параллельной сессии.",
                        "Редактирование", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                match.Computer = _computerBox.Text.Trim();
                match.FullName = _fullNameBox.Text.Trim();
                match.User     = _userBox.Text.Trim();
                match.Phone    = _phoneBox.Text.Trim();
                match.IPs      = _ipsBox.Text.Trim();
                match.LastSeen = _lastSeenBox.Text.Trim();
                match.Group = groupKey ?? "";
                AddressBookStore.Save(existing);
                _saved = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Не удалось сохранить: {ex.Message}",
                    "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // Public entry point. Returns true if the user clicked Save and the
        // entry was actually updated.
        public static bool ShowEditDialog(IWin32Window owner, AddressBookEntry entry)
        {
            using var dlg = new EditEntryForm(entry);
            UiModal.ShowDimmed(owner, dlg);
            return dlg._saved;
        }

        // v2.2.10 — Add-mode entry point. Opens the dialog with empty fields,
        // returns a NEW AddressBookEntry on Save (or null on Cancel). The
        // OnSave handler writes to AddressBookStore directly AND populates
        // _addResult with the same values so the caller can pick it up.
        public static AddressBookEntry? ShowAddDialog(IWin32Window owner)
        {
            var fresh = new AddressBookEntry();
            using var dlg = new EditEntryForm(fresh, isAddMode: true);
            UiModal.ShowDimmed(owner, dlg);
            if (!dlg._saved) return null;
            return dlg._addResult;
        }
    }
}
