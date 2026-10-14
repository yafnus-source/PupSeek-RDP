using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using ShadowRDP.Controls;
using ShadowRDP.Helpers;
using ShadowRDP.Models;
using ShadowRDP.Services;

namespace ShadowRDP.Forms
{
    // v4.3.2 — sample modal window.
    //
    // This form is the TEMPLATE for every modal dialog in the app. It shows
    // the "wow" recipe used by Helpers/UiModal.cs + Controls/UiToggle.cs:
    //   1. Gradient hero band (icon chip, title, subtitle, ✕ close).
    //   2. Section cards with a rounded warm border + icon title header.
    //   3. Rounded input fields with hover/focus accent highlight.
    //   4. Toggle switches instead of checkboxes.
    //   5. Dock = Top hero + Dock = Bottom footer, content scrolls in between.
    //
    // To apply the recipe to another modal, copy the structure below and swap
    // the contents of the three cards.
    public static class FtpSettingsForm
    {
        public static bool Show(IWin32Window owner)
        {
            // v4.0 — FTP-выгрузка платная.
            if (!Helpers.ModuleGate.Require(owner, "ftp-upload")) return false;
            var s = FtpUploadStore.Load();
            using var dlg = new Form
            {
                Text = "FTP выгрузка — Даш W и Z",
                Size = new Size(684, 880),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                BackColor = UiTheme.DialogBackground,
                AutoScroll = false
            };
            dlg.KeyPreview = true;
            dlg.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { e.Handled = true; dlg.Close(); } };

            // ——— 1. Hero (gradient band with icon, title, subtitle; без крестика) ———
            const int ClientW = 668; // ширина клиента при outer 684 (рамки ~16px)
            var hero = UiModal.Hero("FTP выгрузка", "Даш W и Z — настройки подключения и расписания", "exchange");

            // ——— 2. Section cards ———
            int rowH = 30, gap = 24, cardW = 308;

            // Card #1: Подключение
            // NOTE: content panel is Dock=Fill and already starts BELOW the
            // 80px hero and ABOVE the 52px footer, so cards position at y=14
            // (no extra hero offset — that double-pads and clips the bottom).
            var cardConn = UiModal.Card(14, 14, cardW, 316, "Подключение", "globe");
            var chkEnabled = new UiToggle { Text = "Включить выгрузку", Checked = s.Enabled, Location = new Point(16, 56), Width = 200 };
            var lHost = UiModal.FieldLabel("Хост", 16, 84);
            var wHost = UiModal.Field(new TextBox { Text = s.Host, PlaceholderText = "ftp.example.com" }, 200);
            wHost.Location = new Point(16, 100);
            var lPort = UiModal.FieldLabel("Порт", 228, 84);
            var wPort = UiModal.Field(new EscNumericUpDown { Minimum = 1, Maximum = 65535, Value = s.Port }, 66);
            wPort.Location = new Point(228, 100);
            var lProto = UiModal.FieldLabel("Протокол", 16, 136);
            var wProto = UiModal.Field(ProtoCombo(s.Protocol), 140);
            wProto.Location = new Point(16, 152);
            var lData = UiModal.FieldLabel("Режим", 168, 136);
            var wData = UiModal.Field(DataCombo(s.DataConnection), 112);
            wData.Location = new Point(168, 152);
            var lDir = UiModal.FieldLabel("Папка", 16, 190);
            var wDir = UiModal.Field(new TextBox { Text = s.RemoteDir, PlaceholderText = "/upload" }, 276);
            wDir.Location = new Point(16, 206);
            var chkEvents = new UiToggle { Text = "Выгружать Даш W (события)", Checked = s.UploadEvents, Location = new Point(16, 250), Width = 230 };
            var chkZabbix = new UiToggle { Text = "Выгружать Даш Z (Zabbix)", Checked = s.UploadZabbix, Location = new Point(16, 278), Width = 220 };
            var cmbProto = (ComboBox)wProto.Controls[0];
            var cmbData = (ComboBox)wData.Controls[0];
            var txtHost = (TextBox)wHost.Controls[0];
            var numPort = (EscNumericUpDown)wPort.Controls[0];
            var txtDir = (TextBox)wDir.Controls[0];

            // Card #2: KeePass
            var cardKp = UiModal.Card(334, 14, cardW, 316, "KeePass (PupSeekRDP/FTP)", "key");
            var lEntry = UiModal.FieldLabel("Запись", 16, 50);
            var wEntry = UiModal.Field(new TextBox { Text = s.KeepassEntry, PlaceholderText = "host или host:port" }, 196);
            wEntry.Location = new Point(16, 66);
            var btnPick = FtpButton("Выбрать", 68);
            btnPick.Location = new Point(218, 70);
            var lAuth = UiModal.FieldLabel("SFTP auth", 16, 114);
            var wAuth = UiModal.Field(AuthCombo(s.SftpAuthMode), 150);
            wAuth.Location = new Point(16, 130);
            var lKey = UiModal.FieldLabel("Ключ", 16, 168);
            var wKey = UiModal.Field(new TextBox { Text = s.PrivateKeyPath, PlaceholderText = @"C:\keys\id_rsa" }, 210);
            wKey.Location = new Point(16, 184);
            var btnKeyBrowse = FtpButton("...", 34);
            btnKeyBrowse.Location = new Point(232, 188);
            var hintKp = new Label
            {
                Text = "Доступы берутся из базы KeePass,\nгруппа PupSeekRDP/FTP.",
                // v3.9.5 — в нижнем левом углу карточки (было 232).
                Location = new Point(16, 274),
                AutoSize = false,
                Size = new Size(276, 30),
                Font = new Font("Segoe UI", 7.5f),
                ForeColor = UiTheme.MutedForeground,
                BackColor = Color.Transparent
            };
            var txtEntry = (TextBox)wEntry.Controls[0];
            var cmbSftpAuth = (ComboBox)wAuth.Controls[0];
            var txtKey = (TextBox)wKey.Controls[0];

            // Card #3: Расписание
            var cardSched = UiModal.Card(14, 14 + 316 + 12, 628, 226, "Расписание", "gear");
            var lSchedMode = UiModal.FieldLabel("Режим", 16, 46);
            var wSchedMode = UiModal.Field(SchedCombo(s.ScheduleMode), 150);
            wSchedMode.Location = new Point(16, 62);
            var lInterval = UiModal.FieldLabel("Каждые (мин)", 182, 46);
            var wInterval = UiModal.Field(new EscNumericUpDown { Minimum = 5, Maximum = 1440, Value = Math.Clamp(s.IntervalMinutes, 5, 1440) }, 90);
            wInterval.Location = new Point(182, 62);
            var lDaily = UiModal.FieldLabel("Время", 288, 46);
            var wDaily = UiModal.Field(new EscDateTimePicker { Format = DateTimePickerFormat.Time, ShowUpDown = true }, 120);
            wDaily.Location = new Point(288, 62);
            if (TimeSpan.TryParse(s.DailyTime, out var ts)) ((DateTimePicker)wDaily.Controls[0]).Value = DateTime.Today + ts;
            else ((DateTimePicker)wDaily.Controls[0]).Value = DateTime.Today.AddHours(2);
            var lWeekly = UiModal.FieldLabel("Дни недели", 292, 108);
            // v3.9.3AA — список в гладкой белой обёртке (сам квадратный,
            // его углы торчали из скругления; Region давал лесенку).
            var clbWrap = new Panel
            {
                Location = new Point(292, 124),
                Size = new Size(320, 62),
                BorderStyle = BorderStyle.None
            };
            UiButtons.SmoothPanel(clbWrap, 7, Color.White, UiTheme.Border);
            var clbDays = new CheckedListBox
            {
                Location = new Point(8, 6),
                Size = new Size(304, 50),
                CheckOnClick = true,
                BorderStyle = BorderStyle.None,
                BackColor = Color.White,
                ForeColor = UiTheme.Foreground,
                Font = new Font("Segoe UI", 9f),
                IntegralHeight = false
            };
            clbWrap.Controls.Add(clbDays);
            var dayNames = new[] { "Пн", "Вт", "Ср", "Чт", "Пт", "Сб", "Вс" };
            var dayVals = new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday };
            for (int i = 0; i < dayNames.Length; i++) clbDays.Items.Add(dayNames[i], s.WeeklyDays.Contains(dayVals[i]));
            var chkStartup = new UiToggle { Text = "Запускать при старте", Checked = s.RunOnStartup, Location = new Point(16, 124), Width = 210 };
            var cmbSched = (ComboBox)wSchedMode.Controls[0];
            var numInterval = (EscNumericUpDown)wInterval.Controls[0];
            var dtDaily = (EscDateTimePicker)wDaily.Controls[0];

            // Card #4: Действия
            var cardAct = UiModal.Card(14, 14 + 316 + 12 + 226 + 12, 628, 108, "Действия", "terminal");
            var btnTest = FtpButton("Тест", 70, bgOverride: Color.FromArgb(0x1E, 0x3A, 0x5F));
            btnTest.Location = new Point(16, 57);
            btnTest.Click += async (a, b) =>
            {
                var tmp = BuildSettings(txtHost, numPort, cmbProto, cmbData, txtDir, chkEvents, chkZabbix, txtEntry, cmbSftpAuth, txtKey, cmbSched, numInterval, dtDaily, clbDays, chkStartup, chkEnabled);
                btnTest.Enabled = false; btnTest.Text = "...";
                var err = await FtpUploadService.TestConnectionAsync(tmp);
                btnTest.Enabled = true; btnTest.Text = "Тест";
                MessageBox.Show(dlg, err == null ? "OK — подключение успешно" : $"Ошибка: {err}", "FTP тест", MessageBoxButtons.OK, err == null ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            };
            var btnNow = FtpButton("Выгрузить сейчас", 130, bgOverride: Color.FromArgb(0xF0, 0xA0, 0x50));
            btnNow.Location = new Point(96, 57);
            btnNow.Click += async (a, b) =>
            {
                var tmp = BuildSettings(txtHost, numPort, cmbProto, cmbData, txtDir, chkEvents, chkZabbix, txtEntry, cmbSftpAuth, txtKey, cmbSched, numInterval, dtDaily, clbDays, chkStartup, chkEnabled);
                FtpUploadStore.Save(tmp);
                btnNow.Enabled = false; btnNow.Text = "...";
                var (ok, report) = await FtpUploadService.UploadWithReportAsync();
                btnNow.Enabled = true; btnNow.Text = "Выгрузить сейчас";
                ShowReport(dlg, report, ok);
            };
            var lblLast = new Label
            {
                Text = GetLastReportText(),
                AutoSize = false,
                Size = new Size(360, 36),
                Location = new Point(240, 56),
                Font = new Font("Consolas", 7.5f),
                ForeColor = UiTheme.MutedForeground,
                BackColor = Color.Transparent
            };

            // ——— 3. Footer (Сохранить / Отмена) ———
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = UiTheme.DialogCard };
            footer.Paint += (s, e) => { using var pen = new Pen(UiTheme.Border, 1); e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };
            var btnOk = FtpButton("Сохранить", 100);
            btnOk.Location = new Point(ClientW - 233, 15);
            btnOk.Click += (a, b) => { dlg.DialogResult = DialogResult.OK; dlg.Close(); };
            var btnCancel = FtpButton("Отмена", 100, bgOverride: UiTheme.MutedForeground);
            btnCancel.Location = new Point(ClientW - 125, 15);
            btnCancel.Click += (a, b) => { dlg.DialogResult = DialogResult.Cancel; dlg.Close(); };
            var hiddenOk = new Button { DialogResult = DialogResult.OK, Visible = false, Location = new Point(-100, -100), Size = new Size(1, 1) };
            var hiddenCancel = new Button { DialogResult = DialogResult.Cancel, Visible = false, Location = new Point(-100, -100), Size = new Size(1, 1) };
            footer.Controls.Add(btnOk);
            footer.Controls.Add(btnCancel);
            footer.Controls.Add(hiddenOk);
            footer.Controls.Add(hiddenCancel);
            dlg.AcceptButton = hiddenOk;
            dlg.CancelButton = hiddenCancel;

            // Picker buttons (KeePass entry / key file)
            btnPick.Click += (a, b) =>
            {
                try
                {
                    if (!KeePassVault.IsOpen)
                    {
                        var cfg = KeePassVault.LoadConfig();
                        var res = KeePassUnlockDialog.Show(dlg, cfg);
                        if (res == null) return;
                        var err = KeePassVault.Open(res.DbPath, res.KeyFilePath, res.Password);
                        if (err != null) { MessageBox.Show(dlg, err, "KeePass", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                        KeePassVault.SaveConfig(new Services.KeePassVault.Config { DbPath = res.DbPath, KeyFilePath = res.KeyFilePath, AutoUnlockAtStartup = res.AutoUnlockAtStartup });
                    }
                    var list = KeePassVault.ListEntries(KeePassVault.FtpGroupPath);
                    if (list == null || list.Count == 0) { MessageBox.Show(dlg, "В группе PupSeekRDP/FTP нет записей. Создай через Пароли → FTP.", "KeePass", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                    using var pick = new Form { Text = "Выбери запись FTP", Size = new Size(360, 320), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog };
                    var lb = new ScrollHiddenListBox { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 9f) };
                    foreach (var e in list) lb.Items.Add($"{e.Name} — {e.Login}");
                    var btnOkPick = new Button { Text = "OK", DialogResult = DialogResult.OK, Dock = DockStyle.Bottom, Height = 30 };
                    pick.Controls.Add(lb); pick.Controls.Add(btnOkPick); pick.AcceptButton = btnOkPick;
                    // v4.4.0 — своя тонкая полоса вместо системной.
                    ShadowScroll.AttachListBox(lb);
                    pick.KeyPreview = true;
                    pick.KeyDown += (s2, e2) => { if (e2.KeyCode == Keys.Escape) { e2.Handled = true; pick.DialogResult = DialogResult.Cancel; pick.Close(); } };
                    if (pick.ShowDialog(dlg) == DialogResult.OK && lb.SelectedIndex >= 0) txtEntry.Text = list[lb.SelectedIndex].Name;
                }
                catch (Exception ex) { MessageBox.Show(dlg, ex.Message, "KeePass", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            };
            btnKeyBrowse.Click += (a, b) => { using var ofd = new OpenFileDialog { Filter = "Key (*.ppk;*.pem;*.key)|*.ppk;*.pem;*.key|All|*.*", Title = "Приватный ключ" }; if (ofd.ShowDialog(dlg) == DialogResult.OK) txtKey.Text = ofd.FileName; };

            void UpdateVis()
            {
                bool isSftp = cmbProto.SelectedIndex == 2;
                bool isPk = cmbSftpAuth.SelectedIndex == 1;
                lData.Visible = wData.Visible = !isSftp;
                lAuth.Visible = wAuth.Visible = isSftp;
                lKey.Visible = wKey.Visible = btnKeyBrowse.Visible = isSftp && isPk;
                // schedule rows
                bool isInterval = cmbSched.SelectedIndex == 0;
                bool isDaily = cmbSched.SelectedIndex == 1;
                bool isWeekly = cmbSched.SelectedIndex == 2;
                lInterval.Visible = wInterval.Visible = isInterval;
                lDaily.Visible = wDaily.Visible = isDaily;
                lWeekly.Visible = clbWrap.Visible = clbDays.Visible = isWeekly;
            }
            cmbProto.SelectedIndexChanged += (a, b) =>
            {
                if (cmbProto.SelectedIndex == 2 && numPort.Value == 21) numPort.Value = 22;
                else if (cmbProto.SelectedIndex != 2 && numPort.Value == 22) numPort.Value = 21;
                UpdateVis();
            };
            cmbSftpAuth.SelectedIndexChanged += (a, b) => UpdateVis();
            cmbSched.SelectedIndexChanged += (a, b) => UpdateVis();
            // DateTimePicker не покрыт UiModal.Field (там нет case) — шрифт вручную,
            // иначе редактор мельче остальных. Затем центрируем все невысокие
            // редакторы в 30px-обёртке (ComboBox/UpDown/DateTime игнорируют Height=22).
            ((DateTimePicker)wDaily.Controls[0]).Font = new Font("Segoe UI", 9.5f);
            foreach (var w in new[] { wProto, wData, wPort, wAuth, wSchedMode, wInterval, wDaily })
                CenterEditor(w);
            // v3.9.3Y — обёртки UiModal.Field гладкие сами (Transparent +
            // белый фил в Paint): BackColor/Region поверх только давали лесенку.
            // v3.9.3AA — clbDays в гладкой белой обёртке (см. выше), Region снят.
            try { clbDays.Region?.Dispose(); clbDays.Region = null; } catch { }
            foreach (var cd in new[] { cardConn, cardKp, cardSched, cardAct }) { try { cd.Region?.Dispose(); cd.Region = null; } catch { } }
            UpdateVis();

            // ——— assemble: hero (top) + footer (bottom) + scrollable content ———
            // Widgets are CHILDREN of their card (relative coordinates), so the
            // card paints its rounded fill first and the widgets render on top
            // — no z-order games, no manual offsets for card position.
                        var content = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = UiTheme.DialogBackground };
            cardConn.Controls.AddRange(new Control[] {                chkEnabled, lHost, wHost, lPort, wPort, lProto, wProto, lData, wData, lDir, wDir, chkEvents, chkZabbix
            });
            cardKp.Controls.AddRange(new Control[] {
                lEntry, wEntry, btnPick, lAuth, wAuth, lKey, wKey, btnKeyBrowse, hintKp
            });
            cardSched.Controls.AddRange(new Control[] {
                lSchedMode, wSchedMode, lInterval, wInterval, lDaily, wDaily, lWeekly, clbWrap, chkStartup
            });
            cardAct.Controls.AddRange(new Control[] { btnTest, btnNow, lblLast });
            content.Controls.AddRange(new Control[] { cardConn, cardKp, cardSched, cardAct });
            // v4.4.0 — своя тонкая полоса вместо системной.
            ShadowScroll.AttachPanel(content);
            dlg.Controls.Add(content);
            dlg.Controls.Add(hero);
            dlg.Controls.Add(footer);

            var dpiScale = UiModal.ApplyDpiScale(dlg);
            if (dpiScale > 1.001f)
                dlg.ClientSize = new Size((int)Math.Round(dlg.ClientSize.Width * dpiScale), (int)Math.Round(dlg.ClientSize.Height * dpiScale));

            // Якоря, выставленные до финального layout, запоминают мусорную
            // дистанцию на промежуточных проходах и увозят кнопки за экран
            // (факт по дампу: 903/1011/1096 при ширине 668) — финальные
            // координаты ставим по факту, когда у панелей реальный размер.
            // Форма FixedDialog — дальше ресайзов нет, позиции стоят мёртво.
            dlg.Shown += (a, b) =>
            {
                btnOk.Location = new Point(footer.Width - 233, 15);
                btnCancel.Location = new Point(footer.Width - 125, 15);
                // Финальная чистовая перерисовка в конце очереди сообщений —
                // после активации/фокуса/раскладки. Стирает артефакты переходных
                // отрисовок при показе (линии-призраки у тоглов).
                try { dlg.BeginInvoke(new Action(() => { try { dlg.Refresh(); } catch { } })); } catch { }
            };

            if (UiModal.ShowDimmed(owner, dlg) == DialogResult.OK)
            {
                var ns = BuildSettings(txtHost, numPort, cmbProto, cmbData, txtDir, chkEvents, chkZabbix, txtEntry, cmbSftpAuth, txtKey, cmbSched, numInterval, dtDaily, clbDays, chkStartup, chkEnabled);
                FtpUploadStore.Save(ns);
                FtpUploadService.Restart();
                return true;
            }
            return false;
        }

        // Локальная кнопка ТОЛЬКО для окна FTP (общий UiButtons не трогаем):
        // тот же вид, что MakeCompactFilled, но текст рисуется из lbl.Text
        // с SingleLine|NoPadding|EndEllipsis — не вылезает на соседей,
        // а смена Text ("..." при Тест/Выгрузке) реально меняет картинку.
        private static Label FtpButton(string text, int width, Color? bgOverride = null)
        {
            Color bg = bgOverride ?? UiTheme.HeaderBar;
            Color hover = ControlPaint.Dark(bg, 0.08f);
            var fill = new SolidBrush(bg);
            var lbl = new Label
            {
                Text = text,
                AutoSize = false,
                Size = new Size(width, 22),
                Font = new Font("Segoe UI Semibold", 9F),
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                BackColor = Color.Transparent
            };
            lbl.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                var r = new Rectangle(0, 0, lbl.Width, lbl.Height);
                using var path = UiButtons.RoundedRect(r, 1);
                if (lbl.Enabled) e.Graphics.FillPath(fill, path);
                else { using var dis = new SolidBrush(Color.FromArgb(0xE2, 0xE4, 0xE8)); e.Graphics.FillPath(dis, path); }
                var fg = lbl.Enabled ? Color.White : Color.FromArgb(0x8A, 0x8E, 0x94);
                TextRenderer.DrawText(e.Graphics, lbl.Text, lbl.Font, r, fg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            };
            lbl.MouseEnter += (s, e) => { if (lbl.Enabled) { fill.Color = hover; lbl.Invalidate(); } };
            lbl.MouseLeave += (s, e) => { if (lbl.Enabled) { fill.Color = bg; lbl.Invalidate(); } };
            lbl.EnabledChanged += (s, e) => lbl.Invalidate();
            return lbl;
        }

        // ComboBox/NumericUpDown/DateTimePicker игнорируют заданный Height
        // (высота от шрифта) — центрируем редактор в 30px-обёртке.
        private static void CenterEditor(Panel wrap)
        {
            if (wrap.Controls.Count == 0) return;
            var ed = wrap.Controls[0];
            int pref = 0;
            try
            {
                if (ed is ComboBox cb) pref = cb.PreferredHeight;
                else if (ed is DateTimePicker dt) pref = dt.PreferredHeight;
                else if (ed is NumericUpDown nud) pref = nud.PreferredHeight;
                else return;
            }
            catch { return; }
            if (pref < 1 || pref > wrap.Height - 4) return;
            ed.Height = pref;
            ed.Location = new Point(wrap.Padding.Left, (wrap.Height - pref) / 2);
        }

        // Скругление углов только для FTP (общее не трогаем): режем Region
        // контрола по скруглённому rect, чтобы прямоугольный белый фон полей
        // и списка не торчал за радиус обводки.
        private static void RoundCorners(Control c, int radius)
        {
            void Update()
            {
                if (c.IsDisposed || c.Width <= 0 || c.Height <= 0) return;
                c.Region?.Dispose();
                using var path = new System.Drawing.Drawing2D.GraphicsPath();
                var r = new Rectangle(0, 0, c.Width, c.Height);
                int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
                path.AddArc(r.X, r.Y, d, d, 180, 90);
                path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                c.Region = new Region(path);
            }
            c.Resize += (s, e) => Update();
            Update();
        }

        private static ComboBox ProtoCombo(FtpProtocol protocol)
        {
            var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            c.Items.AddRange(new object[] { "FTP plain 21", "FTPS Explicit 21", "SFTP 22" });
            c.SelectedIndex = protocol == FtpProtocol.Sftp ? 2 : protocol == FtpProtocol.FtpsExplicit ? 1 : 0;
            Helpers.UiCombo.Apply(c);
            return c;
        }

        private static ComboBox DataCombo(string data)
        {
            var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            c.Items.AddRange(new object[] { "Passive", "Active" });
            c.SelectedItem = data;
            if (c.SelectedIndex < 0) c.SelectedIndex = 0;
            Helpers.UiCombo.Apply(c);
            return c;
        }

        private static ComboBox AuthCombo(SftpAuthMode mode)
        {
            var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            c.Items.AddRange(new object[] { "Пароль", "Ключ (PK)" });
            c.SelectedIndex = mode == SftpAuthMode.PrivateKey ? 1 : 0;
            Helpers.UiCombo.Apply(c);
            return c;
        }

        private static ComboBox SchedCombo(FtpScheduleMode mode)
        {
            var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            c.Items.AddRange(new object[] { "Каждые N мин", "Ежедневно", "Еженедельно" });
            c.SelectedIndex = mode == FtpScheduleMode.Daily ? 1 : mode == FtpScheduleMode.Weekly ? 2 : 0;
            Helpers.UiCombo.Apply(c);
            return c;
        }

        private static FtpUploadSettings BuildSettings(TextBox txtHost, NumericUpDown numPort, ComboBox cmbProto, ComboBox cmbData, TextBox txtDir, UiToggle chkEvents, UiToggle chkZabbix, TextBox txtEntry, ComboBox cmbSftpAuth, TextBox txtKey, ComboBox cmbSched, NumericUpDown numInterval, DateTimePicker dtDaily, CheckedListBox clbDays, UiToggle chkStartup, UiToggle chkEnabled)
        {
            var dayVals = new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday };
            var days = new System.Collections.Generic.List<DayOfWeek>();
            for (int i = 0; i < dayVals.Length; i++) if (clbDays.GetItemChecked(i)) days.Add(dayVals[i]);
            if (days.Count == 0) days.Add(DayOfWeek.Monday);
            return new FtpUploadSettings
            {
                Enabled = chkEnabled.Checked,
                Host = txtHost.Text.Trim(),
                Port = (int)numPort.Value,
                Protocol = cmbProto.SelectedIndex == 2 ? FtpProtocol.Sftp : cmbProto.SelectedIndex == 1 ? FtpProtocol.FtpsExplicit : FtpProtocol.Ftp,
                DataConnection = cmbData.SelectedItem?.ToString() ?? "Passive",
                RemoteDir = txtDir.Text.Trim(),
                UploadEvents = chkEvents.Checked,
                UploadZabbix = chkZabbix.Checked,
                KeepassEntry = txtEntry.Text.Trim(),
                SftpAuthMode = cmbSftpAuth.SelectedIndex == 1 ? SftpAuthMode.PrivateKey : SftpAuthMode.Password,
                PrivateKeyPath = txtKey.Text.Trim(),
                ScheduleMode = cmbSched.SelectedIndex == 1 ? FtpScheduleMode.Daily : cmbSched.SelectedIndex == 2 ? FtpScheduleMode.Weekly : FtpScheduleMode.Interval,
                IntervalMinutes = (int)numInterval.Value,
                DailyTime = dtDaily.Value.ToString("HH:mm"),
                WeeklyDays = days,
                RunOnStartup = chkStartup.Checked
            };
        }

        private static string GetLastReportText()
        {
            try
            {
                var s = FtpUploadStore.Load();
                if (s.LastRun == null) return "Последняя выгрузка: ещё не было";
                var proto = s.Protocol == FtpProtocol.Sftp ? "SFTP" : s.Protocol == FtpProtocol.FtpsExplicit ? "FTPS" : "FTP";
                return $"Последняя: {s.LastRun:dd.MM HH:mm} — {proto} {s.Host}:{s.Port}/{s.RemoteDir?.Trim('/')}";
            }
            catch { return "Последняя выгрузка: —"; }
        }

        // Report window — uses the same sample-modal recipe (hero + card + footer).
        private static void ShowReport(Form owner, string report, bool ok)
        {
            using var f = new Form
            {
                Text = ok ? "FTP — успешно" : "FTP — ошибка",
                Size = new Size(620, 440),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                BackColor = UiTheme.DialogBackground
            };
            f.KeyPreview = true;
            f.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { e.Handled = true; f.Close(); } };

            var hero = UiModal.Hero(ok ? "Выгрузка завершена" : "Выгрузка не удалась",
                ok ? "Файлы событий и отчёты Zabbix отправлены." : "Исправь ошибки и повтори выгрузку.", "exchange", () => f.Close());

            var content = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = UiTheme.DialogBackground };
            var card = UiModal.Card(14, 14, f.ClientSize.Width - 28, f.ClientSize.Height - 80 - 52 - 28, ok ? "Отчёт" : "Ошибка", ok ? "exchange" : "delete");
            // v3.9.3AA — поле отчёта в гладкой белой обёртке (квадратные углы
            // текстбокса торчали из скругления карточки).
            var tbWrap = new Panel
            {
                Location = new Point(16, 52),
                Size = new Size(card.Width - 32, card.Height - 66),
                BorderStyle = BorderStyle.None
            };
            UiButtons.SmoothPanel(tbWrap, 7, Color.White, UiTheme.Border);
            var tb = new ScrollHiddenTextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Location = new Point(8, 8),
                Size = new Size(tbWrap.Width - 16, tbWrap.Height - 16),
                Font = new Font("Consolas", 9f),
                Text = report,
                BackColor = Color.White,
                BorderStyle = BorderStyle.None,
                ForeColor = UiTheme.Foreground
            };
            tbWrap.Controls.Add(tb);
            card.Controls.Add(tbWrap);
            // v4.4.0 — своя тонкая полоса вместо системной.
            ShadowScroll.AttachTextBox(tb);
            // v3.9.3Y — UiModal.Card гладкая сама, Region давал лесенку.
            try { card.Region?.Dispose(); card.Region = null; } catch { }

            var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = UiTheme.DialogCard };
            footer.Paint += (s, e) => { using var pen = new Pen(UiTheme.Border, 1); e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };
            var btnCopy = FtpButton("Копировать", 100);
            btnCopy.Location = new Point(12, 15);
            btnCopy.Click += (a, b) => { try { Clipboard.SetText(report); } catch { } };
            var btnClose = FtpButton("Закрыть", 100);
            btnClose.Location = new Point(492, 15); // клиент отчёта ~604 при outer 620
            btnClose.Click += (a, b) => f.Close();
            footer.Controls.Add(btnCopy);
            footer.Controls.Add(btnClose);

            content.Controls.Add(card);
            // v4.4.0 — своя тонкая полоса вместо системной.
            ShadowScroll.AttachPanel(content);
            f.Controls.Add(content);
            f.Controls.Add(hero);
            f.Controls.Add(footer);

            var dpiScale = UiModal.ApplyDpiScale(f);
            if (dpiScale > 1.001f)
                f.ClientSize = new Size((int)Math.Round(f.ClientSize.Width * dpiScale), (int)Math.Round(f.ClientSize.Height * dpiScale));

            f.Shown += (a, b) => { btnClose.Location = new Point(footer.Width - 112, 15); };
            tb.Select(); tb.SelectAll();
            UiModal.ShowDimmed(owner, f);
        }
    }
}