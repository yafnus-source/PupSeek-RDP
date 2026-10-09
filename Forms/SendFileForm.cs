using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ShadowRDP.Services;

namespace ShadowRDP.Forms
{
    // v3.9.5 — «Передать файл…» из меню книги: копия на Windows-хост
    // через админ-шару SMB. Текущие доменные права — без пароля;
    // иначе явный логин/пароль (WNetAddConnection2 на время копии).
    public sealed class SendFileForm : Form
    {
        private readonly TextBox _fileBox;
        private readonly TextBox _compBox;
        private readonly TextBox _pathBox;
        private readonly RadioButton _radioCurrent;
        private readonly RadioButton _radioExplicit;
        private readonly TextBox _loginBox;
        private readonly TextBox _passBox;
        private readonly ProgressBar _progress;
        private readonly Label _status;
        private readonly Label _sendBtn;
        private readonly string _computer;

        public static void Show(IWin32Window owner, string computer)
        {
            using var f = new SendFileForm(computer);
            Helpers.UiModal.ShowDimmed(owner, f);
        }

        private SendFileForm(string computer)
        {
            _computer = SmbSendService.CleanHost(computer);
            Text = "Передать файл";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Font = new Font("Segoe UI", 9F);
            BackColor = Helpers.UiTheme.DialogBackground;
            ClientSize = new Size(560, 602);

            var hero = Helpers.UiModal.Hero("Передать файл",
                "копия через админ-шару SMB", "computer");
            hero.Dock = DockStyle.Top;
            Controls.Add(hero);

            var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = Helpers.UiTheme.DialogCard };
            footer.Paint += (s, e) =>
            {
                using var pen = new Pen(Helpers.UiTheme.Border, 1);
                e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
            };
            // v3.9.5 — ширина 240: шире налезал прозрачным краем на
            // кнопку «Отправить» (x = w−280) — выглядело откусанным углом.
            _progress = new ProgressBar
            {
                Location = new Point(16, 8),
                Size = new Size(240, 16),
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Visible = false
            };
            footer.Controls.Add(_progress);
            _status = new Label
            {
                Location = new Point(16, 26),
                Size = new Size(240, 20),
                Font = new Font("Segoe UI", 8F),
                ForeColor = Helpers.UiTheme.MutedForeground,
                BackColor = Color.Transparent,
                Text = "Готов."
            };
            footer.Controls.Add(_status);
            _sendBtn = Helpers.UiButtons.MakeCompactFilled("Отправить", 150);
            var closeBtn = Helpers.UiButtons.MakeCompactFilled("Закрыть", 100,
                bgOverride: Helpers.UiTheme.MutedForeground);
            footer.Controls.Add(_sendBtn);
            footer.Controls.Add(closeBtn);
            footer.Resize += (s, e) =>
            {
                try
                {
                    int w = footer.ClientSize.Width;
                    closeBtn.Location = new Point(w - 120, 15);
                    _sendBtn.Location = new Point(w - 280, 15);
                }
                catch { }
            };
            Controls.Add(footer);

            // --- Файл ---
            var cardFile = Helpers.UiModal.Card(12, 90, 536, 120, "Файл", "folder");
            Controls.Add(cardFile);
            cardFile.Controls.Add(Helpers.UiModal.FieldLabel("Локальный файл", 16, 48));
            _fileBox = new TextBox();
            var fileWrap = Helpers.UiModal.Field(_fileBox, 400);
            fileWrap.Location = new Point(16, 64);
            cardFile.Controls.Add(fileWrap);
            var browseBtn = Helpers.UiButtons.MakeCompactFilled("Обзор…", 100);
            browseBtn.Location = new Point(424, 64);
            browseBtn.Click += (_, _) => BrowseFile();
            cardFile.Controls.Add(browseBtn);

            // --- Назначение ---
            var cardDest = Helpers.UiModal.Card(12, 222, 536, 164, "Назначение", "computer");
            Controls.Add(cardDest);
            cardDest.Controls.Add(Helpers.UiModal.FieldLabel("Компьютер", 16, 48));
            _compBox = new TextBox { Text = _computer, ReadOnly = true };
            var compWrap = Helpers.UiModal.Field(_compBox, 504);
            compWrap.Location = new Point(16, 64);
            cardDest.Controls.Add(compWrap);
            cardDest.Controls.Add(Helpers.UiModal.FieldLabel("Папка на удалённом (шара\\путь)", 16, 104));
            _pathBox = new TextBox { Text = SmbSendService.DefaultRemoteDir };
            var pathWrap = Helpers.UiModal.Field(_pathBox, 504);
            pathWrap.Location = new Point(16, 120);
            cardDest.Controls.Add(pathWrap);

            // --- Доступ ---
            var cardCreds = Helpers.UiModal.Card(12, 398, 536, 140, "Доступ", "key");
            Controls.Add(cardCreds);
            _radioCurrent = new RadioButton
            {
                Location = new Point(16, 48),
                Size = new Size(504, 22),
                Font = new Font("Segoe UI", 9F),
                ForeColor = Helpers.UiTheme.Foreground,
                BackColor = Color.Transparent,
                Text = "Текущий пользователь Windows (без пароля)",
                Checked = true
            };
            _radioCurrent.CheckedChanged += (_, _) => UpdateCreds();
            cardCreds.Controls.Add(_radioCurrent);
            _radioExplicit = new RadioButton
            {
                Location = new Point(16, 74),
                Size = new Size(504, 22),
                Font = new Font("Segoe UI", 9F),
                ForeColor = Helpers.UiTheme.Foreground,
                BackColor = Color.Transparent,
                Text = "Указать логин и пароль"
            };
            _radioExplicit.CheckedChanged += (_, _) => UpdateCreds();
            cardCreds.Controls.Add(_radioExplicit);
            _loginBox = new TextBox { Text = Environment.UserDomainName + "\\" + Environment.UserName };
            var loginWrap = Helpers.UiModal.Field(_loginBox, 240);
            loginWrap.Location = new Point(32, 100);
            cardCreds.Controls.Add(loginWrap);
            _passBox = new TextBox { UseSystemPasswordChar = true };
            var passWrap = Helpers.UiModal.Field(_passBox, 240);
            passWrap.Location = new Point(288, 100);
            cardCreds.Controls.Add(passWrap);
            UpdateCreds();

            var hiddenClose = new Button
            {
                Visible = true, TabStop = false,
                Location = new Point(-200, -200), Size = new Size(1, 1),
                DialogResult = DialogResult.Cancel
            };
            Controls.Add(hiddenClose);
            CancelButton = hiddenClose;

            _sendBtn.Click += async (_, _) => await SendAsync();
            closeBtn.Click += (_, _) => Close();
            Shown += (_, _) => _fileBox.Focus();
        }

        private void UpdateCreds()
        {
            try
            {
                bool expl = _radioExplicit.Checked;
                _loginBox.Enabled = expl;
                _passBox.Enabled = expl;
            }
            catch { }
        }

        private void BrowseFile()
        {
            try
            {
                using var d = new OpenFileDialog { Title = "Выберите файл" };
                if (Helpers.UiModal.ShowDimmed(this, d) == DialogResult.OK
                    && !string.IsNullOrWhiteSpace(d.FileName))
                    _fileBox.Text = d.FileName;
            }
            catch (Exception ex) { Logger.Warn($"Передать файл: обзор: {ex.Message}"); }
        }

        private void SetBusy(bool busy, string status)
        {
            try
            {
                _sendBtn.Enabled = !busy;
                _progress.Visible = busy;
                if (!busy) _progress.Value = 0;
                _status.Text = status;
            }
            catch { }
        }

        private static string Mb(long bytes) =>
            bytes < 1048576 ? $"{bytes / 1024} КБ" : $"{bytes / 1048576.0:F1} МБ";

        private async Task SendAsync()
        {
            string local = (_fileBox.Text ?? "").Trim();
            string comp = (_compBox.Text ?? "").Trim();
            string rdir = (_pathBox.Text ?? "").Trim();
            bool explicitCreds = _radioExplicit.Checked;
            string login = (_loginBox.Text ?? "").Trim();
            string pass = _passBox.Text ?? "";

            var verr = SmbSendService.Validate(local, comp, rdir, explicitCreds, login);
            if (verr != null)
            {
                MessageBox.Show(this, verr, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!File.Exists(local))
            {
                MessageBox.Show(this, "Локальный файл не найден.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _fileBox.Focus();
                return;
            }
            string fileName = Path.GetFileName(local);
            string unc = SmbSendService.BuildUnc(comp, rdir, fileName);
            string uncDir = Path.GetDirectoryName(unc) ?? unc;
            string remote = comp + ":" + SmbSendService.NormalizeRemoteDir(rdir).Replace('/', '\\');

            SetBusy(true, "Подключение…");
            bool connected = false;
            string shareUnc = "";
            try
            {
                if (explicitCreds)
                {
                    shareUnc = SmbSendService.BuildShareUnc(comp, rdir);
                    SetBusy(true, "Подключение к шаре…");
                    await Task.Run(() => SmbSendService.ConnectShare(shareUnc, login, pass))
                        .ConfigureAwait(true);
                    connected = true;
                }
                SetBusy(true, "Копирование… 0%");
                var prog = new Progress<(long Done, long Total)>(t =>
                {
                    try
                    {
                        int pct = t.Total == 0 ? 0 : (int)(t.Done * 100 / t.Total);
                        if (pct < 0) pct = 0; if (pct > 100) pct = 100;
                        _progress.Value = pct;
                        _status.Text = $"Копирование… {Mb(t.Done)} / {Mb(t.Total)}";
                    }
                    catch { }
                });
                await Task.Run(async () =>
                {
                    Directory.CreateDirectory(uncDir);
                    await SmbSendService.CopyWithProgressAsync(
                        local, unc, prog, CancellationToken.None).ConfigureAwait(false);
                }).ConfigureAwait(true);

                SetBusy(false, "Готово.");
                try { Services.AuditLogger.Write("file.send", remote, "ok", fileName); } catch { }
                MessageBox.Show(this, $"Файл отправлен:\n{unc}", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                Close();
            }
            catch (UnauthorizedAccessException)
            {
                SetBusy(false, "Нет прав.");
                try { Services.AuditLogger.Write("file.send", remote, "denied", fileName); } catch { }
                MessageBox.Show(this,
                    "Нет прав на запись в шару.\n\nПроверьте права на компьютере или укажите логин и пароль.",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                SetBusy(false, "Ошибка.");
                try { Services.AuditLogger.Write("file.send", remote, "fail", ex.Message); } catch { }
                MessageBox.Show(this, $"Не удалось отправить:\n{ex.Message}", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                if (connected)
                    try { SmbSendService.DisconnectShare(shareUnc); } catch { }
            }
        }
    }
}
