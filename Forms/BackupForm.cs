using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using ShadowRDP.Services;

namespace ShadowRDP.Forms
{
    // v3.9.5 — окно Backup: галочки наборов + папка + расписание.
    // Кнопка «Backup…» в Инструментах. Состав — BackupService.Catalog
    // (максимально полный); расписание мотает BackupService.StartScheduler.
    public sealed class BackupForm : Form
    {
        private readonly CheckedListBox _list;
        private readonly TextBox _pathBox;
        private readonly Label _status;
        private readonly CheckBox _schedCheck;
        private readonly ComboBox _periodCombo;
        private readonly DateTimePicker _timePicker;
        private readonly NumericUpDown _keepNum;
        private readonly Label _lastLabel;
        private readonly Label _runBtn;
        private readonly TextBox _zipBox;
        private readonly TextBox _pwdBox1;
        private readonly TextBox _pwdBox2;
        private readonly List<string> _ids = new();

        public static void Show(IWin32Window owner)
        {
            using var f = new BackupForm();
            Helpers.UiModal.ShowDimmed(owner, f);
        }

        private BackupForm()
        {
            Text = "Backup";
            StartPosition = FormStartPosition.CenterParent;
            // v3.9.5 — регулировка мышкой + запоминание размера; минимум
            // под 4 карточки (список «Что бекапить» — гибкая по высоте).
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = false;
            MinimumSize = new Size(560, 860);
            Font = new Font("Segoe UI", 9F);
            BackColor = Helpers.UiTheme.DialogBackground;
            ClientSize = new Size(640, 996);

            var hero = Helpers.UiModal.Hero("Backup",
                "полная копия данных программы в zip", "exchange");
            hero.Dock = DockStyle.Top;
            Controls.Add(hero);

            var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = Helpers.UiTheme.DialogCard };
            footer.Paint += (s, e) =>
            {
                using var pen = new Pen(Helpers.UiTheme.Border, 1);
                e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
            };
            _runBtn = Helpers.UiButtons.MakeCompactFilled("Создать бэкап", 150);
            var closeBtn = Helpers.UiButtons.MakeCompactFilled("Закрыть", 100,
                bgOverride: Helpers.UiTheme.MutedForeground);
            footer.Controls.Add(_runBtn);
            footer.Controls.Add(closeBtn);
            footer.Resize += (s, e) =>
            {
                try
                {
                    int w = footer.ClientSize.Width;
                    closeBtn.Location = new Point(w - 120, 15);
                    _runBtn.Location = new Point(w - 280, 15);
                }
                catch { }
            };
            Controls.Add(footer);

            // --- Что бекапить ---
            var cardWhat = Helpers.UiModal.Card(12, 90, 616, 300, "Что бекапить", "exchange");
            Controls.Add(cardWhat);
            var allBtn = Helpers.UiButtons.MakeCompactFlat("Все", 60);
            allBtn.Location = new Point(442, 46);
            allBtn.Click += (_, _) => SetAll(true);
            cardWhat.Controls.Add(allBtn);
            var noneBtn = Helpers.UiButtons.MakeCompactFlat("Ничего", 90);
            noneBtn.Location = new Point(510, 46);
            noneBtn.Click += (_, _) => SetAll(false);
            cardWhat.Controls.Add(noneBtn);
            _list = new CheckedListBox
            {
                Location = new Point(16, 76),
                Size = new Size(584, 208),
                CheckOnClick = true,
                Font = new Font("Segoe UI", 9F),
                BorderStyle = BorderStyle.FixedSingle
            };
            cardWhat.Controls.Add(_list);

            // --- Куда ---
            var cardWhere = Helpers.UiModal.Card(12, 402, 616, 192, "Куда", "gear");
            Controls.Add(cardWhere);
            cardWhere.Controls.Add(Helpers.UiModal.FieldLabel("Папка", 16, 48));
            _pathBox = new TextBox();
            var pathWrap = Helpers.UiModal.Field(_pathBox, 468);
            pathWrap.Location = new Point(16, 64);
            cardWhere.Controls.Add(pathWrap);
            var browseBtn = Helpers.UiButtons.MakeCompactFilled("Обзор…", 100);
            browseBtn.Location = new Point(500, 64);
            browseBtn.Click += (_, _) => Browse();
            cardWhere.Controls.Add(browseBtn);
            _status = new Label
            {
                Location = new Point(16, 98),
                Size = new Size(584, 18),
                Font = new Font("Segoe UI", 8.5F),
                ForeColor = Helpers.UiTheme.MutedForeground,
                BackColor = Color.Transparent,
                Text = "Готов."
            };
            cardWhere.Controls.Add(_status);
            // Пароль AES-256: пусто — обычный zip; для восстановления
            // ввести здесь же (не хранится нигде).
            cardWhere.Controls.Add(Helpers.UiModal.FieldLabel(
                "Пароль (необязательно — AES-256; забыли — не восстановить)", 16, 118));
            _pwdBox1 = new TextBox { UseSystemPasswordChar = true };
            var pwdWrap1 = Helpers.UiModal.Field(_pwdBox1, 284);
            pwdWrap1.Location = new Point(16, 134);
            cardWhere.Controls.Add(pwdWrap1);
            _pwdBox2 = new TextBox { UseSystemPasswordChar = true };
            var pwdWrap2 = Helpers.UiModal.Field(_pwdBox2, 284);
            pwdWrap2.Location = new Point(316, 134);
            cardWhere.Controls.Add(pwdWrap2);

            // --- Расписание ---
            var cardSched = Helpers.UiModal.Card(12, 606, 616, 166, "Расписание", "tasks");
            Controls.Add(cardSched);
            _schedCheck = new CheckBox
            {
                Location = new Point(16, 48),
                Size = new Size(300, 22),
                Font = new Font("Segoe UI", 9F),
                ForeColor = Helpers.UiTheme.Foreground,
                BackColor = Color.Transparent,
                Text = "Включить автобэкап"
            };
            cardSched.Controls.Add(_schedCheck);
            _periodCombo = new ComboBox
            {
                Location = new Point(16, 78),
                Size = new Size(180, 26),
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Segoe UI", 9F)
            };
            _periodCombo.Items.Add("Ежедневно");
            _periodCombo.Items.Add("Еженедельно");
            _periodCombo.SelectedIndex = 0;
            Helpers.UiCombo.Apply(_periodCombo);
            cardSched.Controls.Add(_periodCombo);
            _timePicker = new DateTimePicker
            {
                Location = new Point(204, 78),
                Size = new Size(110, 26),
                Format = DateTimePickerFormat.Time,
                ShowUpDown = true,
                Font = new Font("Segoe UI", 9F)
            };
            cardSched.Controls.Add(_timePicker);
            _keepNum = new NumericUpDown
            {
                Location = new Point(322, 78),
                Size = new Size(60, 26),
                Minimum = 1,
                Maximum = 100,
                Value = 7,
                Font = new Font("Segoe UI", 9F)
            };
            cardSched.Controls.Add(_keepNum);
            var keepLbl = new Label
            {
                Location = new Point(388, 78),
                Size = new Size(200, 26),
                Font = new Font("Segoe UI", 9F),
                ForeColor = Helpers.UiTheme.MutedForeground,
                BackColor = Color.Transparent,
                Text = "хранить шт.",
                TextAlign = ContentAlignment.MiddleLeft
            };
            cardSched.Controls.Add(keepLbl);
            _lastLabel = new Label
            {
                Location = new Point(16, 110),
                Size = new Size(584, 20),
                Font = new Font("Segoe UI", 8.5F),
                ForeColor = Helpers.UiTheme.MutedForeground,
                BackColor = Color.Transparent,
                Text = "Последний автобэкап: —"
            };
            cardSched.Controls.Add(_lastLabel);
            var schedHint = new Label
            {
                Location = new Point(16, 132),
                Size = new Size(584, 20),
                Font = new Font("Segoe UI", 8.5F),
                ForeColor = Helpers.UiTheme.MutedForeground,
                BackColor = Color.Transparent,
                Text = "Автобэкап пишет в папку выше, пока программа запущена."
            };
            cardSched.Controls.Add(schedHint);

            // --- Восстановление ---
            var cardRestore = Helpers.UiModal.Card(12, 784, 616, 148, "Восстановление", "exchange");
            Controls.Add(cardRestore);
            cardRestore.Controls.Add(Helpers.UiModal.FieldLabel("Zip-бэкап", 16, 48));
            _zipBox = new TextBox();
            var zipWrap = Helpers.UiModal.Field(_zipBox, 468);
            zipWrap.Location = new Point(16, 64);
            cardRestore.Controls.Add(zipWrap);
            var zipBrowseBtn = Helpers.UiButtons.MakeCompactFilled("Обзор…", 100);
            zipBrowseBtn.Location = new Point(500, 64);
            zipBrowseBtn.Click += (_, _) => BrowseZip();
            cardRestore.Controls.Add(zipBrowseBtn);
            // v3.9.5 — кнопка −30% (180→126), песочная #D9BC8C,
            // отступ от поля пути 8px (поле 30px: 64+30+8).
            var restoreBtn = Helpers.UiButtons.MakeCompactFilled("Восстановить…", 126,
                bgOverride: Color.FromArgb(0xD9, 0xBC, 0x8C));
            restoreBtn.Location = new Point(16, 102);
            restoreBtn.Click += (_, _) => RunRestore();
            cardRestore.Controls.Add(restoreBtn);
            var restoreHint = new Label
            {
                Location = new Point(150, 102),
                Size = new Size(380, 22),
                Font = new Font("Segoe UI", 8.5F),
                ForeColor = Helpers.UiTheme.MutedForeground,
                BackColor = Color.Transparent,
                Text = "Данные перезапишутся; страховка — автоматически.",
                TextAlign = ContentAlignment.MiddleLeft
            };
            cardRestore.Controls.Add(restoreHint);

            var hiddenClose = new Button
            {
                Visible = true, TabStop = false,
                Location = new Point(-200, -200), Size = new Size(1, 1),
                DialogResult = DialogResult.Cancel
            };
            Controls.Add(hiddenClose);
            CancelButton = hiddenClose;

            _runBtn.Click += (_, _) => RunNow();
            closeBtn.Click += (_, _) => Close();
            FormClosing += (_, _) => { try { SaveUiToSettings(); } catch { } };

            LoadUiFromSettings();

            // v3.9.5 — ресайз: 4 карточки по ширине окна, три нижние
            // прижаты к футеру, гибкая высота — у «Что бекапить».
            Resize += (_, _) => LayoutContent();
            LayoutContent();
            Services.WindowStateStore.Register(this, "backup");

            void LayoutContent()
            {
                try
                {
                    int w = ClientSize.Width, h = ClientSize.Height;
                    int cw = Math.Max(360, w - 24);
                    cardWhat.Width = cardWhere.Width = cardSched.Width = cardRestore.Width = cw;

                    // min: «Что бекапить» ≥160 → Куда ≥262 → Расписание ≥466
                    // → Восстановление ≥644 (160+90+12+192+12+166+12).
                    cardRestore.Top = Math.Max(644, h - 52 - 12 - 148);
                    cardSched.Top = cardRestore.Top - 12 - 166;
                    cardWhere.Top = cardSched.Top - 12 - 192;
                    cardWhat.Height = Math.Max(160, cardWhere.Top - 12 - 90);

                    // «Что бекапить»: кнопки в правый край, список на всю ширину.
                    noneBtn.Left = cw - 16 - noneBtn.Width;
                    allBtn.Left = noneBtn.Left - 8 - allBtn.Width;
                    _list.Width = cw - 32;
                    _list.Height = Math.Max(60, cardWhat.ClientSize.Height - 76 - 16);

                    // «Куда»: обзор справа, статус и два пароля по ширине.
                    browseBtn.Left = cw - 16 - browseBtn.Width;
                    pathWrap.Width = Math.Max(160, browseBtn.Left - 16 - 16);
                    _status.Width = Math.Max(160, cw - 32);
                    int half = Math.Max(120, (cw - 48) / 2);
                    pwdWrap1.Width = half;
                    pwdWrap2.Width = half;
                    pwdWrap2.Left = 16 + half + 16;

                    _lastLabel.Width = Math.Max(160, cw - 32);
                    schedHint.Width = Math.Max(160, cw - 32);

                    // «Восстановление»: обзор справа, поле — в остаток.
                    zipBrowseBtn.Left = cw - 16 - zipBrowseBtn.Width;
                    zipWrap.Width = Math.Max(160, zipBrowseBtn.Left - 16 - 16);
                }
                catch { }
            }
        }

        private void SetAll(bool v)
        {
            try
            {
                for (int i = 0; i < _list.Items.Count; i++)
                    _list.SetItemChecked(i, v);
            }
            catch { }
        }

        private void Browse()
        {
            try
            {
                using var d = new FolderBrowserDialog
                {
                    Description = "Папка для бэкапов",
                    SelectedPath = string.IsNullOrWhiteSpace(_pathBox.Text)
                        ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                        : _pathBox.Text
                };
                if (Helpers.UiModal.ShowDimmed(this, d) == DialogResult.OK
                    && !string.IsNullOrWhiteSpace(d.SelectedPath))
                    _pathBox.Text = d.SelectedPath;
            }
            catch (Exception ex) { Logger.Warn($"Backup: обзор папки: {ex.Message}"); }
        }

        private void BrowseZip()
        {
            try
            {
                using var d = new OpenFileDialog
                {
                    Filter = "Бэкапы (*.zip)|*.zip|Все файлы (*.*)|*.*",
                    DefaultExt = "zip",
                    Title = "Выберите zip-бэкап",
                    FileName = string.IsNullOrWhiteSpace(_zipBox.Text) ? "" : _zipBox.Text
                };
                if (Helpers.UiModal.ShowDimmed(this, d) == DialogResult.OK
                    && !string.IsNullOrWhiteSpace(d.FileName))
                    _zipBox.Text = d.FileName;
            }
            catch (Exception ex) { Logger.Warn($"Backup: обзор zip: {ex.Message}"); }
        }

        private void RunRestore()
        {
            string zip = (_zipBox.Text ?? "").Trim();
            if (!System.IO.File.Exists(zip))
            {
                MessageBox.Show(this, "Укажите существующий zip-бэкап.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _zipBox.Focus();
                return;
            }
            if (MessageBox.Show(this,
                    "Восстановить данные из бэкапа?\n\nТекущие данные будут ПЕРЕЗАПИСАНЫ.",
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            try { Cursor = Cursors.WaitCursor; Application.DoEvents(); } catch { }
            try
            {
                // Страховка текущего состояния — сначала.
                _status.Text = "Страховочная копия…";
                try { Application.DoEvents(); } catch { }
                var allIds = BackupService.Catalog.Select(i => i.Id);
                var (sZip, _, sErr) = BackupService.RunBackup(
                    Services.DataBackup.BackupDir, allIds);
                if (sZip == null)
                {
                    MessageBox.Show(this,
                        $"Не удалось создать страховку: {sErr}\nВосстановление отменено.",
                        Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    _status.Text = "Готов.";
                    return;
                }
                var (ok, skip, err) = BackupService.RestoreBackup(zip, null, _pwdBox1.Text);
                if (err != null)
                {
                    _status.Text = "Ошибка: " + err;
                    MessageBox.Show(this, err, Text,
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                _status.Text = $"Восстановлено файлов: {ok}"
                    + (skip > 0 ? $" (пропущено {skip})" : "") + ".";
                try { SaveUiToSettings(); } catch { }
                var dr = MessageBox.Show(this,
                    $"Восстановлено файлов: {ok}.\nСтраховка: {sZip}\n\nПерезапустить программу сейчас?",
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (dr == DialogResult.Yes)
                {
                    try { Application.Restart(); }
                    catch (Exception ex) { Logger.Warn($"Backup: рестарт: {ex.Message}"); }
                }
            }
            finally { try { Cursor = Cursors.Default; } catch { } }
        }

        private List<string> SelectedIds()
        {
            var ids = new List<string>();
            for (int i = 0; i < _list.Items.Count && i < _ids.Count; i++)
                if (_list.GetItemChecked(i)) ids.Add(_ids[i]);
            return ids;
        }

        private void RunNow()
        {
            var ids = SelectedIds();
            if (ids.Count == 0)
            {
                MessageBox.Show(this, "Отметьте хотя бы один набор данных.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string dir = (_pathBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(dir))
            {
                MessageBox.Show(this, "Укажите папку для бэкапа.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _pathBox.Focus();
                return;
            }
            string pwd = _pwdBox1.Text ?? "";
            if (pwd != (_pwdBox2.Text ?? ""))
            {
                MessageBox.Show(this, "Пароли не совпадают.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _pwdBox2.Focus();
                return;
            }
            try { Cursor = Cursors.WaitCursor; Application.DoEvents(); } catch { }
            try
            {
                var (zip, n, err) = BackupService.RunBackup(dir, ids, pwd);
                if (zip == null)
                {
                    _status.Text = "Ошибка: " + err;
                    MessageBox.Show(this, err ?? "Не удалось создать бэкап.", Text,
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                _status.Text = $"Готово: {System.IO.Path.GetFileName(zip)} ({n} файлов).";
                try { SaveUiToSettings(); } catch { }
            }
            finally { try { Cursor = Cursors.Default; } catch { } }
        }

        private void LoadUiFromSettings()
        {
            try
            {
                var s = BackupService.LoadSettings();
                _ids.Clear();
                _list.Items.Clear();
                var sel = new HashSet<string>(s.SelectedIds ?? new List<string>());
                foreach (var item in BackupService.Catalog)
                {
                    _ids.Add(item.Id);
                    int n = 0;
                    try { n = BackupService.ResolveFiles(new[] { item.Id }).Count; } catch { }
                    _list.Items.Add($"{item.Title} ({n})", sel.Contains(item.Id));
                }
                _pathBox.Text = s.DestDir ?? "";
                _zipBox.Text = s.ZipPath ?? "";
                _schedCheck.Checked = s.Enabled;
                _periodCombo.SelectedIndex = s.Period == "weekly" ? 1 : 0;
                if (TimeSpan.TryParse(s.Time, out var ts))
                    _timePicker.Value = DateTime.Today.Add(ts);
                _keepNum.Value = Math.Max(1, Math.Min(100, s.Keep < 1 ? 7 : s.Keep));
                _lastLabel.Text = "Последний автобэкап: "
                    + (string.IsNullOrWhiteSpace(s.LastRun) ? "—" : s.LastRun);
            }
            catch (Exception ex) { Logger.Warn($"Backup: загрузка настроек: {ex.Message}"); }
        }

        private void SaveUiToSettings()
        {
            var s = BackupService.LoadSettings();
            s.DestDir = (_pathBox.Text ?? "").Trim();
            s.ZipPath = (_zipBox.Text ?? "").Trim();
            s.SelectedIds = SelectedIds();
            s.Enabled = _schedCheck.Checked;
            s.Period = _periodCombo.SelectedIndex == 1 ? "weekly" : "daily";
            s.Time = _timePicker.Value.ToString("HH:mm");
            s.Keep = (int)_keepNum.Value;
            BackupService.SaveSettings(s);
        }
    }
}
