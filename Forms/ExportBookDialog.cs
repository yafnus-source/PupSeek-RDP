using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using ShadowRDP.Helpers;
using ShadowRDP.Services;

namespace ShadowRDP.Forms
{
    // v2.3.13 — Modal dialog that asks the user what to export from the
    // address book before writing CSV. Returns the selected group filter
    // (null = "All groups") and the list of columns to include.
    //
    // Layout (380×340):
    //   Группа:    [Все группы ▾]
    //   Колонки:   ☑ Компьютер
    //              ☑ Полное имя
    //              ☑ Пользователь
    //              ...
    //                              [Экспорт] [Отмена]
    public class ExportBookDialog : Form
    {
        private readonly ComboBox _groupBox;
        private readonly CheckedListBox _columnsBox;

        /// <param name="groups">Existing group names to populate the group
        /// dropdown. Empty strings are filtered out by the caller.</param>
        public static (string? Group, List<string> Columns)? Show(IWin32Window owner, IEnumerable<string> groups)
        {
            using var dlg = new ExportBookDialog(groups);
            if (dlg.ShowDialog(owner) != DialogResult.OK) return null;
            var group = dlg._groupBox.SelectedItem as string;
            if (group == AllGroupsItem) group = null;
            return (group, dlg.GetSelectedColumnKeys());
        }

        private const string AllGroupsItem = "Все группы";

        private ExportBookDialog(IEnumerable<string> groups)
        {
            Text = "Экспорт книги";
            ClientSize = new Size(380, 340);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Font = new Font("Segoe UI", 9F);
            BackColor = Helpers.UiTheme.Background;

            // === Group filter ===
            // v3.0.2: ComboBox widened 50px to the left (label shrunk 120→70).
            var lblGroup = new Label
            {
                Text = "Группа:",
                Location = new Point(20, 20),
                Size = new Size(70, 22),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Helpers.UiTheme.Foreground
            };
            Controls.Add(lblGroup);

            _groupBox = new ComboBox
            {
                Location = new Point(98, 18),
                Size = new Size(262, 26),
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Segoe UI", 10F)
            };
            _groupBox.Items.Add(AllGroupsItem);
            foreach (var g in groups.Where(x => !string.IsNullOrWhiteSpace(x))
                                     .Distinct(StringComparer.OrdinalIgnoreCase)
                                     .OrderBy(x => x))
            {
                _groupBox.Items.Add(g);
            }
            _groupBox.SelectedIndex = 0;
            Controls.Add(_groupBox);

            // === Columns ===
            var lblColumns = new Label
            {
                Text = "Колонки:",
                Location = new Point(20, 58),
                Size = new Size(70, 22),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Helpers.UiTheme.Foreground
            };
            Controls.Add(lblColumns);

            // v3.0.4: padding removed (per user request) — listbox is now a
            // plain border-only control. Width/height unchanged.
            _columnsBox = new CheckedListBox
            {
                Location = new Point(98, 56),
                Size = new Size(262, 226),
                Font = new Font("Segoe UI", 9F),
                CheckOnClick = true,
                BorderStyle = BorderStyle.FixedSingle
            };
            // Russian display labels keyed off BookCsvExporter.AvailableColumns.
            // Items wrap the column key + display string so we can map back.
            var labels = new Dictionary<string, string>
            {
                { "Computer",  "Компьютер" },
                { "FullName",  "Полное имя" },
                { "User",      "Пользователь" },
                { "Phone",     "Телефон" },
                { "IPs",       "IP-адреса" },
                { "LastSeen",  "Последний визит" },
                { "Group",     "Группа" }
            };
            foreach (var col in BookCsvExporter.AvailableColumns)
            {
                var display = labels.TryGetValue(col, out var l) ? l : col;
                _columnsBox.Items.Add(new ColumnItem(col, display), isChecked: true);
            }
            Controls.Add(_columnsBox);

            // === Buttons ===
            // Hidden Button hooks for AcceptButton / CancelButton — the
            // visible buttons are Labels (MakeFilled), which don't implement
            // IButtonControl. See memory: feedback_winforms_accept_button_needs_visible.
            var exportBtn = UiButtons.MakeFilled("Экспорт", null,
                ShadowRDP.Helpers.UiTheme.HeaderBar, width: 110, radius: 1);
            exportBtn.Location = new Point(ClientSize.Width - 110 - 96 - 20 - 12, ClientSize.Height - 20 - 22 - 10);
            Controls.Add(exportBtn);
            var exportHook = new Button { Visible = false, Location = new Point(-1000, -1000) };
            exportHook.Click += (s, e) => OnExport();
            Controls.Add(exportHook);
            AcceptButton = exportHook;
            // Click on the visible label routes to the same handler.
            exportBtn.Click += (s, e) => OnExport();

            var cancelBtn = UiButtons.MakeFilled("Отмена", null,
                Helpers.UiTheme.MutedForeground, width: 96, radius: 1);
            cancelBtn.Location = new Point(ClientSize.Width - 96 - 20, ClientSize.Height - 20 - 22 - 10);
            Controls.Add(cancelBtn);
            var cancelHook = new Button { Visible = false, Location = new Point(-1000, -1000) };
            cancelHook.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(cancelHook);
            CancelButton = cancelHook;
            cancelBtn.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
        }

        private void OnExport()
        {
            // Validate at least one column is checked.
            int checkedCount = 0;
            for (int i = 0; i < _columnsBox.Items.Count; i++)
                if (_columnsBox.GetItemChecked(i)) checkedCount++;
            if (checkedCount == 0)
            {
                MessageBox.Show(this, "Выберите хотя бы одну колонку.", "Экспорт",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        }

        // Convert the CheckedListBox selection back to exporter column keys.
        private List<string> GetSelectedColumnKeys()
        {
            var keys = new List<string>();
            for (int i = 0; i < _columnsBox.Items.Count; i++)
            {
                if (!_columnsBox.GetItemChecked(i)) continue;
                if (_columnsBox.Items[i] is ColumnItem ci) keys.Add(ci.Key);
            }
            return keys;
        }

        private sealed class ColumnItem
        {
            public string Key { get; }
            private readonly string _display;
            public ColumnItem(string key, string display) { Key = key; _display = display; }
            public override string ToString() => _display;
        }
    }
}
