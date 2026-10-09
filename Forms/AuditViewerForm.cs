using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using ShadowRDP.Controls;
using ShadowRDP.Services;

namespace ShadowRDP.Forms
{
    // v3.9.4 — просмотрщик аудит-журнала (вместо «notepad на .jsonl»).
    // Загрузка всех месяцев (.jsonl + .gz) в фоне, фильтр по тексту и
    // результату, лимит 5000 строк. Блокнот остался кнопкой-запасным
    // путём.
    public class AuditViewerForm : Form
    {
        private readonly TextBox _searchBox;
        private readonly Panel _searchWrap;
        private readonly ComboBox _resultCombo;
        private readonly Label _statusLabel;
        private readonly DataGridView _grid;
        private readonly Label _reloadBtn;
        private readonly Label _notepadBtn;
        private readonly Label _closeBtn;
        private List<AuditReader.Record> _all = new();
        private bool _loading;

        public static void Show(IWin32Window owner)
        {
            using var f = new AuditViewerForm();
            Helpers.UiModal.ShowDimmed(owner, f);
        }

        public AuditViewerForm()
        {
            Text = "Журнал действий";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            MinimumSize = new Size(760, 420);
            Font = new Font("Segoe UI", 9F);
            BackColor = Helpers.UiTheme.DialogBackground;
            ClientSize = new Size(960, 560);

            var hero = Helpers.UiModal.Hero("Журнал действий",
                "аудит: кто / когда / что (пароли не пишутся)", "list");
            hero.Dock = DockStyle.Top;
            Controls.Add(hero);

            var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = Helpers.UiTheme.DialogCard };
            footer.Paint += (s, e) =>
            {
                using var pen = new Pen(Helpers.UiTheme.Border, 1);
                e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
            };
            _reloadBtn = Helpers.UiButtons.MakeCompactFilled("Обновить", 110);
            _notepadBtn = Helpers.UiButtons.MakeCompactFilled("В блокноте", 110,
                bgOverride: Helpers.UiTheme.MutedForeground);
            _closeBtn = Helpers.UiButtons.MakeCompactFilled("Закрыть", 100,
                bgOverride: Helpers.UiTheme.MutedForeground);
            footer.Controls.Add(_reloadBtn);
            footer.Controls.Add(_notepadBtn);
            footer.Controls.Add(_closeBtn);
            footer.Resize += (s, e) =>
            {
                try
                {
                    int w = footer.ClientSize.Width;
                    _reloadBtn.Location = new Point(w - 344, 15);
                    _notepadBtn.Location = new Point(w - 226, 15);
                    _closeBtn.Location = new Point(w - 108, 15);
                }
                catch { }
            };
            Controls.Add(footer);

            var card = Helpers.UiModal.Card(12, 90, 936, 106, "Фильтр", "search");
            Controls.Add(card);

            card.Controls.Add(Helpers.UiModal.FieldLabel("Поиск", 16, 48));
            _searchBox = new TextBox();
            _searchWrap = Helpers.UiModal.Field(_searchBox, 420);
            _searchWrap.Location = new Point(16, 64);
            card.Controls.Add(_searchWrap);

            card.Controls.Add(Helpers.UiModal.FieldLabel("Результат", 460, 48));
            _resultCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(460, 64),
                Size = new Size(140, 28),
                BackColor = Helpers.UiTheme.DialogCard,
                ForeColor = Helpers.UiTheme.Foreground
            };
            _resultCombo.Items.AddRange(new object[] { "все", "ok", "fail" });
            _resultCombo.SelectedIndex = 0;
            Helpers.UiCombo.Apply(_resultCombo);
            card.Controls.Add(_resultCombo);

            _statusLabel = new Label
            {
                Location = new Point(620, 66),
                Size = new Size(300, 24),
                Font = new Font("Segoe UI", 8.5F),
                ForeColor = Helpers.UiTheme.MutedForeground,
                BackColor = Color.Transparent,
                Text = "Загрузка…"
            };
            card.Controls.Add(_statusLabel);

            // v3.9.5 — журнал в стиле «Открытых файлов»: headerless-карточка
            // (Anchor на карточке, грид подгоняется через FitCardChild).
            var cardList = Helpers.UiModal.Card(12, 212, 936, 288, "", "", withHeader: false);
            try { cardList.Region?.Dispose(); cardList.Region = null; } catch { }
            cardList.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            Controls.Add(cardList);

            _grid = new DoubleBufferedDataGridView
            {
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                MultiSelect = false
            };
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            { Name = "Ts", HeaderText = "Время", FillWeight = 14, MinimumWidth = 110 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            { Name = "User", HeaderText = "Пользователь", FillWeight = 10, MinimumWidth = 80 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            { Name = "Action", HeaderText = "Действие", FillWeight = 14, MinimumWidth = 100 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            { Name = "Target", HeaderText = "Цель", FillWeight = 27, MinimumWidth = 160 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            { Name = "Result", HeaderText = "Результат", FillWeight = 8, MinimumWidth = 70 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            { Name = "Details", HeaderText = "Детали", FillWeight = 27, MinimumWidth = 160 });
            Helpers.UiGridStyle.ApplyCardGrid(_grid, cardList);

            var hiddenClose = new Button
            {
                Visible = true, TabStop = false,
                Location = new Point(-200, -200), Size = new Size(1, 1),
                DialogResult = DialogResult.Cancel
            };
            Controls.Add(hiddenClose);
            CancelButton = hiddenClose;

            _searchBox.TextChanged += (_, _) => Render();
            _resultCombo.SelectedIndexChanged += (_, _) => Render();
            _reloadBtn.Click += async (_, _) => await LoadAsync();
            _notepadBtn.Click += (_, _) =>
            {
                try { Process.Start("notepad.exe", AppPaths.AuditLogPath); }
                catch (Exception ex) { Logger.Warn($"Журнал: блокнот: {ex.Message}"); }
            };
            _closeBtn.Click += (_, _) => Close();
            Shown += async (_, _) => await LoadAsync();
        }

        // Фоновая загрузка: чтение файлов в Task.Run, отрисовка в потоке UI.
        public async Task LoadAsync()
        {
            if (_loading) return;
            _loading = true;
            _statusLabel.Text = "Загрузка…";
            try
            {
                var records = await Task.Run(() => AuditReader.ReadAll(5000));
                _all = records;
            }
            catch (Exception ex)
            {
                Logger.Warn($"Журнал: загрузка: {ex.Message}");
            }
            finally
            {
                _loading = false;
            }
            Render();
        }

        private void Render()
        {
            try
            {
                var text = _searchBox.Text.Trim();
                var resultFilter = _resultCombo.SelectedIndex;
                var view = new List<AuditReader.Record>();
                foreach (var r in _all)
                {
                    if (resultFilter == 1 && r.Result != "ok") continue;
                    if (resultFilter == 2 && r.Result != "fail") continue;
                    if (text.Length > 0 &&
                        !(Contains(r.Ts, text) || Contains(r.User, text) || Contains(r.Action, text) ||
                          Contains(r.Target, text) || Contains(r.Result, text) || Contains(r.Details, text)))
                        continue;
                    view.Add(r);
                }
                _grid.SuspendLayout();
                _grid.Rows.Clear();
                foreach (var r in view)
                {
                    int ri = _grid.Rows.Add(r.Ts, r.User, r.Action, r.Target, r.Result, r.Details);
                    var row = _grid.Rows[ri];
                    if (r.Result == "fail") row.DefaultCellStyle.ForeColor = Color.FromArgb(214, 73, 73);
                    else if (r.Result == "ok") row.DefaultCellStyle.ForeColor = Color.FromArgb(56, 161, 105);
                }
                _grid.ResumeLayout();
                _statusLabel.Text = view.Count == _all.Count
                    ? $"Записей: {_all.Count}"
                    : $"Показано: {view.Count} из {_all.Count}";
            }
            catch (Exception ex)
            {
                Logger.Warn($"Журнал: отрисовка: {ex.Message}");
            }
        }

        private static bool Contains(string hay, string needle) =>
            hay.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }
}
