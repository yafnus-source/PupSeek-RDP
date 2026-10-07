using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using ShadowRDP.Controls;
using ShadowRDP.Helpers;
using ShadowRDP.Services;

namespace ShadowRDP.Forms
{
    // v4.3.3 — окно в стиле FTP-эталона (hero + карточки + футер).
    //
    // Modal dialog that asks the user what to export from the
    // address book before writing CSV. Returns the selected group filter
    // (null = "All groups") and the list of columns to include.
    //
    // Layout (560×596):
    //   hero:    Экспорт книги
    //   card 1:  Группа ......... [Все группы ▾]
    //   card 2:  Колонки ........ ☑ Компьютер / Полное имя / ...
    //   footer:                          [Экспорт] [Отмена]
    public static class ExportBookDialog
    {
        private const string AllGroupsItem = "Все группы";

        /// <param name="groups">Existing group names to populate the group
        /// dropdown. Empty strings are filtered out by the caller.</param>
        public static (string? Group, List<string> Columns)? Show(IWin32Window owner, IEnumerable<string> groups)
        {
            using var dlg = new Form
            {
                Text = "Экспорт книги",
                Size = new Size(560, 596),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                BackColor = UiTheme.DialogBackground,
                AutoScroll = false
            };
            dlg.KeyPreview = true;
            dlg.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { e.Handled = true; dlg.Close(); } };

            // ——— 1. Hero ———
            var hero = UiModal.Hero("Экспорт книги", "Адресная книга — выбор группы и колонок", "book");

            // ——— 2. Section cards ———
            // Card #1: Группа
            var cardGroup = UiModal.Card(14, 14, 516, 120, "Группа", "search");
            var lGroup = UiModal.FieldLabel("Группа", 16, 46);
            var cmb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            cmb.Items.Add(AllGroupsItem);
            foreach (var g in groups.Where(x => !string.IsNullOrWhiteSpace(x))
                                     .Distinct(StringComparer.OrdinalIgnoreCase)
                                     .OrderBy(x => x))
            {
                cmb.Items.Add(g);
            }
            cmb.SelectedIndex = 0;
            var wGroup = UiModal.Field(cmb, 484);
            wGroup.Location = new Point(16, 62);
            CenterEditor(wGroup);

            // Card #2: Колонки
            var cardCols = UiModal.Card(14, 14 + 120 + 12, 516, 248, "Колонки", "list");
            var lColumns = UiModal.FieldLabel("Колонки", 20, 50);
            // Белое поле с внутренними отступами 20px слева/сверху: список лежит
            // в белой панели с полями — обычная геометрия, видно всегда,
            // без owner-draw. Рамка одна (у панели), у списка её нет.
            // v3.9.3Y — гладкое скругление без Region (давал лесенку).
            var listPanel = new Panel
            {
                Location = new Point(20, 66),
                Size = new Size(480, 168),
                BorderStyle = BorderStyle.None
            };
            UiButtons.SmoothPanel(listPanel, 7, Color.White, UiTheme.Border);
            var columnsBox = new Controls.ScrollHiddenCheckedListBox
            {
                Location = new Point(10, 10),
                Size = new Size(460, 148),
                Font = new Font("Segoe UI", 9F),
                CheckOnClick = true,
                BorderStyle = BorderStyle.None,
                BackColor = Color.White,
                ForeColor = UiTheme.Foreground,
                IntegralHeight = false
            };
            listPanel.Controls.Add(columnsBox);
            // v4.4.0 — своя тонкая полоса вместо системной.
            ShadowScroll.AttachCheckedListBox(columnsBox);
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
                columnsBox.Items.Add(new ColumnItem(col, display), isChecked: true);
            }
            // v3.9.3Y — Field/Card гладкие сами; Region давал лесенку.
            try { wGroup.Region?.Dispose(); wGroup.Region = null; } catch { }
            foreach (var c in new Control[] { cardGroup, cardCols })
            { try { c.Region?.Dispose(); c.Region = null; } catch { } }

            // ——— 3. Footer (Экспорт / Отмена) ———
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = UiTheme.DialogCard };
            footer.Paint += (s, e) => { using var pen = new Pen(UiTheme.Border, 1); e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };
            var exportBtn = UiButtons.MakeCompactFilled("Экспорт", 100);
            exportBtn.Location = new Point(504 - 233, 15);
            exportBtn.Click += (s, e) => DoExport();
            var cancelBtn = UiButtons.MakeCompactFilled("Отмена", 100,
                bgOverride: Helpers.UiTheme.MutedForeground);
            cancelBtn.Location = new Point(504 - 125, 15);
            cancelBtn.Click += (s, e) => { dlg.DialogResult = DialogResult.Cancel; dlg.Close(); };
            // Hidden Button hooks for AcceptButton / CancelButton — the
            // visible buttons are Labels, which don't implement IButtonControl.
            var exportHook = new Button { Visible = false, Location = new Point(-100, -100), Size = new Size(1, 1) };
            var cancelHook = new Button { DialogResult = DialogResult.Cancel, Visible = false, Location = new Point(-100, -100), Size = new Size(1, 1) };
            footer.Controls.Add(exportBtn);
            footer.Controls.Add(cancelBtn);
            footer.Controls.Add(exportHook);
            footer.Controls.Add(cancelHook);
            dlg.AcceptButton = exportHook;
            dlg.CancelButton = cancelHook;
            // Accept через Enter должен валидировать колонки, а не просто закрывать:
            exportHook.Click += (s, e) => DoExport();

            void DoExport()
            {
                // Validate at least one column is checked.
                int checkedCount = 0;
                for (int i = 0; i < columnsBox.Items.Count; i++)
                    if (columnsBox.GetItemChecked(i)) checkedCount++;
                if (checkedCount == 0)
                {
                    MessageBox.Show(dlg, "Выберите хотя бы одну колонку.", "Экспорт",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                dlg.DialogResult = DialogResult.OK;
                dlg.Close();
            }

            // ——— assemble: hero (top) + footer (bottom) + scrollable content ———
            // Widgets are CHILDREN of their card (relative coordinates), so the
            // card paints its rounded fill first and the widgets render on top.
            // Фон страницы = фону карточек: скруглённые вырезы по углам
            // карточек сливаются и не читаются как белые уголки.
            var content = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = UiTheme.DialogCard };
            cardGroup.Controls.AddRange(new Control[] { lGroup, wGroup });
            cardCols.Controls.AddRange(new Control[] { lColumns, listPanel });
            content.Controls.AddRange(new Control[] { cardGroup, cardCols });
            // v4.4.0 — своя тонкая полоса вместо системной.
            ShadowScroll.AttachPanel(content);
            dlg.Controls.Add(content);
            dlg.Controls.Add(hero);
            dlg.Controls.Add(footer);

            var dpiScale = UiModal.ApplyDpiScale(dlg);
            if (dpiScale > 1.001f)
                dlg.ClientSize = new Size((int)Math.Round(dlg.ClientSize.Width * dpiScale), (int)Math.Round(dlg.ClientSize.Height * dpiScale));

            // Финальные координаты — по факту, когда у панелей реальный размер
            // (якоря до layout увозят кнопки за экран). Форма FixedDialog.
            dlg.Shown += (a, b) =>
            {
                // Фокус — в никуда: иначе первый комбобокс открывается
                // с синей подсветкой выбранного значения.
                dlg.ActiveControl = null;
                exportBtn.Location = new Point(footer.Width - 233, 15);
                cancelBtn.Location = new Point(footer.Width - 125, 15);
                try { dlg.BeginInvoke(new Action(() => { try { dlg.Refresh(); } catch { } })); } catch { }
            };

            if (UiModal.ShowDimmed(owner, dlg) != DialogResult.OK) return null;
            var group = cmb.SelectedItem as string;
            if (group == AllGroupsItem) group = null;
            return (group, GetSelectedColumnKeys(columnsBox));
        }

        // ComboBox игнорирует заданный Height (высота от шрифта) —
        // центрируем редактор в 30px-обёртке.
        private static void CenterEditor(Panel wrap)
        {
            if (wrap.Controls.Count == 0) return;
            var ed = wrap.Controls[0];
            int pref = 0;
            try
            {
                if (ed is ComboBox cb) pref = cb.PreferredHeight;
                else return;
            }
            catch { return; }
            if (pref < 1 || pref > wrap.Height - 4) return;
            ed.Height = pref;
            ed.Location = new Point(wrap.Padding.Left, (wrap.Height - pref) / 2);
        }

        // Convert the CheckedListBox selection back to exporter column keys.
        private static List<string> GetSelectedColumnKeys(CheckedListBox box)
        {
            var keys = new List<string>();
            for (int i = 0; i < box.Items.Count; i++)
            {
                if (!box.GetItemChecked(i)) continue;
                if (box.Items[i] is ColumnItem ci) keys.Add(ci.Key);
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
