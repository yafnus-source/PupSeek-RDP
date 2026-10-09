using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ShadowRDP.Forms
{
    // v3.9.4 — удалённая консоль команд: одна команда на удалённый хост,
    // канал WinRM с фолбэком на WMI. Вывод stdout/stderr в моноширинное
    // поле, статус — канал/exit code/время. Аудит: remote.command.
    public class RemoteConsoleForm : Form
    {
        private readonly ComboBox _hostBox;
        private readonly TextBox _cmdBox;
        private readonly TextBox _outBox;
        private readonly Label _statusLabel;
        private readonly Label _runBtn;
        private readonly Label _closeBtn;
        private CancellationTokenSource? _cts;
        private bool _running;

        public static void Show(IWin32Window owner, string? host = null,
            IEnumerable<string>? hosts = null)
        {
            using var f = new RemoteConsoleForm(host, hosts);
            Helpers.UiModal.ShowDimmed(owner, f);
        }

        public RemoteConsoleForm(string? host, IEnumerable<string>? hosts)
        {
            Text = "Удалённая консоль команд";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            MinimumSize = new Size(640, 460);
            Font = new Font("Segoe UI", 9F);
            BackColor = Helpers.UiTheme.DialogBackground;
            ClientSize = new Size(740, 560);

            var hero = Helpers.UiModal.Hero("Удалённая консоль команд",
                "WinRM с фолбэком на WMI · аудит remote.command", "terminal");
            hero.Dock = DockStyle.Top;
            Controls.Add(hero);

            var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = Helpers.UiTheme.DialogCard };
            footer.Paint += (s, e) =>
            {
                using var pen = new Pen(Helpers.UiTheme.Border, 1);
                e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
            };
            _runBtn = Helpers.UiButtons.MakeCompactFilled("Выполнить", 120);
            _closeBtn = Helpers.UiButtons.MakeCompactFilled("Закрыть", 100,
                bgOverride: Helpers.UiTheme.MutedForeground);
            footer.Controls.Add(_runBtn);
            footer.Controls.Add(_closeBtn);
            footer.Resize += (s, e) =>
            {
                try
                {
                    int w = footer.ClientSize.Width;
                    _runBtn.Location = new Point(w - 236, 15);
                    _closeBtn.Location = new Point(w - 108, 15);
                }
                catch { }
            };
            Controls.Add(footer);

            var card = Helpers.UiModal.Card(12, 90, 716, 168, "Команда", "terminal");

            card.Controls.Add(Helpers.UiModal.FieldLabel("Хост", 16, 48));
            _hostBox = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDown,
                AutoCompleteMode = AutoCompleteMode.Suggest,
                AutoCompleteSource = AutoCompleteSource.ListItems,
                Location = new Point(16, 64),
                Size = new Size(300, 28),
                BackColor = Helpers.UiTheme.DialogCard,
                ForeColor = Helpers.UiTheme.Foreground,
                Text = host ?? ""
            };
            if (hosts != null)
                foreach (var h in hosts)
                    if (!string.IsNullOrWhiteSpace(h)) _hostBox.Items.Add(h);
            // v3.9.5 — общий дизайн: хост в белой рамке-поле как в других
            // окнах (UiModal.Field + стиль списка UiCombo).
            var hostWrap = Helpers.UiModal.Field(_hostBox, 300);
            hostWrap.Location = new Point(16, 64);
            card.Controls.Add(hostWrap);

            card.Controls.Add(Helpers.UiModal.FieldLabel("Команда", 16, 104));
            _cmdBox = new TextBox
            {
                Multiline = true,
                AcceptsReturn = true,
                ScrollBars = ScrollBars.Vertical,
                Location = new Point(16, 120),
                Size = new Size(684, 40),
                BorderStyle = BorderStyle.FixedSingle
            };
            _cmdBox.KeyDown += (_, e) =>
            {
                // Ctrl+Enter — выполнить (Enter в многострочном — перевод строки).
                if (e.Control && e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Execute(); }
            };
            card.Controls.Add(_cmdBox);
            Controls.Add(card);

            _statusLabel = new Label
            {
                Location = new Point(14, 266),
                Size = new Size(712, 20),
                Font = new Font("Segoe UI", 8.5F),
                ForeColor = Helpers.UiTheme.MutedForeground,
                BackColor = Color.Transparent,
                Text = "Готово. Enter — новая строка, Ctrl+Enter — выполнить."
            };
            Controls.Add(_statusLabel);

            // v3.9.5 — общий дизайн: вывод в headerless-карточке со
            // скруглением (как списки в других окнах), без голого
            // FixedSingle прямо по фону формы.
            var cardOut = Helpers.UiModal.Card(12, 290, 716, 206, "", "", withHeader: false);
            Controls.Add(cardOut);
            _outBox = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Font = new Font("Consolas", 9F),
                Location = new Point(14, 14),
                Size = new Size(688, 178),
                BorderStyle = BorderStyle.None,
                BackColor = Helpers.UiTheme.DialogCard,
                ForeColor = Helpers.UiTheme.Foreground
            };
            cardOut.Controls.Add(_outBox);
            cardOut.Resize += (_, _) =>
            {
                try
                {
                    _outBox.Bounds = new Rectangle(14, 14,
                        Math.Max(50, cardOut.ClientSize.Width - 28),
                        Math.Max(50, cardOut.ClientSize.Height - 28));
                }
                catch { }
            };

            var hiddenClose = new Button
            {
                Visible = true, TabStop = false,
                Location = new Point(-200, -200), Size = new Size(1, 1),
                DialogResult = DialogResult.Cancel
            };
            Controls.Add(hiddenClose);
            CancelButton = hiddenClose;

            _runBtn.Click += (_, _) => Execute();
            _closeBtn.Click += (_, _) =>
            {
                if (_running) { try { _cts?.Cancel(); } catch { } return; }
                Close();
            };
            Shown += (_, _) => { if (!string.IsNullOrEmpty(_hostBox.Text)) _cmdBox.Focus(); else _hostBox.Focus(); };

            // v3.9.5 — ресайз: карточка/статус/карточка вывода по окну,
            // вывод тянется по высоте (футер Dock.Bottom).
            Resize += (_, _) => LayoutContent();
            LayoutContent();
            Services.WindowStateStore.Register(this, "remoteConsole");

            void LayoutContent()
            {
                try
                {
                    int w = ClientSize.Width, h = ClientSize.Height;
                    int cw = Math.Max(400, w - 24);
                    card.Width = cw;
                    _cmdBox.Width = Math.Max(200, card.ClientSize.Width - 32);
                    _statusLabel.Width = Math.Max(200, w - 28);
                    cardOut.Width = cw;
                    cardOut.Height = Math.Max(80, h - 290 - 52 - 12);
                }
                catch { }
            }
        }

        private async void Execute()
        {
            if (_running)
            {
                try { _cts?.Cancel(); } catch { }
                _runBtn.Text = "Останавливаем…";
                return;
            }
            var host = _hostBox.Text.Trim();
            var command = _cmdBox.Text;
            if (string.IsNullOrEmpty(host))
            {
                MessageBox.Show(this, "Укажите хост.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _hostBox.Focus();
                return;
            }
            if (string.IsNullOrWhiteSpace(command))
            {
                MessageBox.Show(this, "Введите команду.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _cmdBox.Focus();
                return;
            }

            _running = true;
            _runBtn.Text = "Отмена";
            _statusLabel.Text = $"Выполняется на {host}…";
            _outBox.Text = "";
            _cts = new CancellationTokenSource();
            var sw = Stopwatch.StartNew();
            try
            {
                var res = await Task.Run(() => Services.RemoteCommand
                    .RunAsync(host, command.Trim(), 30000, _cts!.Token));
                sw.Stop();
                if (IsDisposed || !IsHandleCreated) return;
                _outBox.Text = string.IsNullOrWhiteSpace(res.Output)
                    ? (string.IsNullOrWhiteSpace(res.Error) ? "(нет вывода)" : res.Error)
                    : res.Output + (string.IsNullOrWhiteSpace(res.Error) ? "" : "\r\n[stderr] " + res.Error);
                var mark = res.Ok ? "ok" : "fail";
                _statusLabel.Text = $"[{res.Channel}] exit={res.ExitCode} · {sw.ElapsedMilliseconds} мс · {mark}";
                Services.AuditLogger.Write("remote.command", host, mark,
                    command.Length > 200 ? command.Substring(0, 200) : command);
            }
            catch (Exception ex)
            {
                sw.Stop();
                if (!IsDisposed && IsHandleCreated)
                    _statusLabel.Text = $"Ошибка: {ex.Message}";
                Logger.Warn($"Консоль команд: {ex.Message}");
                Services.AuditLogger.Write("remote.command", host, "fail", ex.Message);
            }
            finally
            {
                _running = false;
                try { _cts?.Dispose(); } catch { }
                _cts = null;
                if (!IsDisposed && IsHandleCreated) _runBtn.Text = "Выполнить";
            }
        }
    }
}
