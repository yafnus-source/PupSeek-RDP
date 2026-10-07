using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ShadowRDP.Services;

namespace ShadowRDP.Forms
{
    // v3.9.4 — TCP-порт-сканер одного хоста поверх PingService.IsPortOpenAsync.
    // Параллельность 40, таймаут 1с на порт, пресеты-кнопки, отмена «Стоп».
    // Вызов: PortScanForm.Show(owner, host) — из «Инструменты» и ПКМ книги.
    public class PortScanForm : Form
    {
        private readonly TextBox _hostBox;
        private readonly Panel _hostWrap;
        private readonly TextBox _portsBox;
        private readonly Panel _portsWrap;
        private readonly Label _statusLabel;
        private readonly ListView _list;
        private readonly Label _scanBtn;
        private readonly Label _closeBtn;
        private CancellationTokenSource? _cts;

        private const string PresetCore = "21,22,23,25,80,110,135,139,143,389,443,445,636,993,995,3389,5985";
        private const string PresetWeb = "80,81,443,8080,8443,8000,8888,3000,5000,9090";
        private const string PresetDb = "1433,1521,3306,5432,27017,6379,5984,9200";
        private const string PresetAll = "21,22,23,25,53,80,110,135,139,143,389,443,445,4899,5900,636,993,995,1433,1521,3306,3389,5432,5985,6379,8080,8443,9200,10050,27017";

        public static void Show(IWin32Window owner, string? host = null)
        {
            using var f = new PortScanForm(host);
            Helpers.UiModal.ShowDimmed(owner, f);
        }

        private PortScanForm(string? host)
        {
            Text = "Проверка портов";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Font = new Font("Segoe UI", 9F);
            BackColor = Helpers.UiTheme.DialogBackground;
            ClientSize = new Size(440, 640);

            var hero = Helpers.UiModal.Hero("Проверка портов",
                "TCP-сканер: открыт ли порт на хосте", "ping");
            hero.Dock = DockStyle.Top;
            Controls.Add(hero);

            var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = Helpers.UiTheme.DialogCard };
            footer.Paint += (s, e) =>
            {
                using var pen = new Pen(Helpers.UiTheme.Border, 1);
                e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
            };
            _scanBtn = Helpers.UiButtons.MakeCompactFilled("Сканировать", 130);
            _closeBtn = Helpers.UiButtons.MakeCompactFilled("Закрыть", 100,
                bgOverride: Helpers.UiTheme.MutedForeground);
            footer.Controls.Add(_scanBtn);
            footer.Controls.Add(_closeBtn);
            footer.Resize += (s, e) =>
            {
                try
                {
                    _scanBtn.Location = new Point(footer.ClientSize.Width - 246, 15);
                    _closeBtn.Location = new Point(footer.ClientSize.Width - 108, 15);
                }
                catch { }
            };
            Controls.Add(footer);

            var card = Helpers.UiModal.Card(12, 90, 416, 216, "Сканер", "ping");
            Controls.Add(card);

            var lblHost = Helpers.UiModal.FieldLabel("Хост", 16, 48);
            card.Controls.Add(lblHost);
            _hostBox = new TextBox { Text = host ?? "" };
            _hostWrap = Helpers.UiModal.Field(_hostBox, 384);
            _hostWrap.Location = new Point(16, 64);
            card.Controls.Add(_hostWrap);

            var lblPorts = Helpers.UiModal.FieldLabel("Порты через запятую", 16, 112);
            card.Controls.Add(lblPorts);
            _portsBox = new TextBox { Text = PresetCore };
            _portsWrap = Helpers.UiModal.Field(_portsBox, 384);
            _portsWrap.Location = new Point(16, 128);
            card.Controls.Add(_portsWrap);

            // Пресеты — под полем портов: 4 кнопки одинаковой ширины с
            // одинаковым зазором (v3.9.5: раньше «БД» перекрывала «Все»).
            var presets = new (string Text, string Ports)[]
            {
                ("Сетевые", PresetCore),
                ("Web", PresetWeb),
                ("БД", PresetDb),
                ("Все", PresetAll)
            };
            const int presetWidth = 90, presetStep = 98; // 90 + зазор 8
            for (int i = 0; i < presets.Length; i++)
                AddPreset(card, presets[i].Text, presets[i].Ports, 16 + i * presetStep);

            _statusLabel = new Label
            {
                Location = new Point(14, 316),
                Size = new Size(412, 20),
                Font = new Font("Segoe UI", 8.5F),
                ForeColor = Helpers.UiTheme.MutedForeground,
                BackColor = Color.Transparent,
                Text = "Укажите хост и порты."
            };
            Controls.Add(_statusLabel);

            _list = new ListView
            {
                Location = new Point(12, 340),
                Size = new Size(416, 236),
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                HideSelection = false,
                BackColor = Helpers.UiTheme.DialogCard,
                ForeColor = Helpers.UiTheme.Foreground,
                BorderStyle = BorderStyle.FixedSingle
            };
            _list.Columns.Add("Порт", 80, HorizontalAlignment.Right);
            _list.Columns.Add("Состояние", 200, HorizontalAlignment.Left);
            _list.Columns.Add("мс", 95, HorizontalAlignment.Right);
            Controls.Add(_list);

            var hiddenClose = new Button
            {
                Visible = true, TabStop = false,
                Location = new Point(-200, -200), Size = new Size(1, 1),
                DialogResult = DialogResult.Cancel
            };
            Controls.Add(hiddenClose);
            CancelButton = hiddenClose;

            _scanBtn.Click += async (_, _) => await ScanOrStopAsync();
            _closeBtn.Click += (_, _) => Close();
            Shown += (_, _) => _hostBox.Focus();
        }

        private void AddPreset(Control card, string text, string ports, int x)
        {
            var b = Helpers.UiButtons.MakeCompactFilled(text, 90,
                bgOverride: Helpers.UiTheme.MutedForeground);
            b.Location = new Point(x, 170);
            card.Controls.Add(b);
            b.Click += (_, _) => _portsBox.Text = ports;
        }

        private List<int> ParsePorts(out string? error)
        {
            error = null;
            var set = new SortedSet<int>();
            foreach (var part in _portsBox.Text.Split(
                new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(part.Trim(), out var p) || p < 1 || p > 65535)
                {
                    error = $"Некорректный порт: '{part.Trim()}'.";
                    return new List<int>();
                }
                set.Add(p);
            }
            if (set.Count == 0) error = "Список портов пуст.";
            else if (set.Count > 300) error = "Слишком много портов (максимум 300 за прогон).";
            return set.ToList();
        }

        private async Task ScanOrStopAsync()
        {
            // Второй клик во время скана — «Стоп».
            if (_cts != null)
            {
                try { _cts.Cancel(); } catch { }
                _scanBtn.Text = "Останавливаем…";
                return;
            }

            var host = _hostBox.Text.Trim();
            if (string.IsNullOrEmpty(host))
            {
                MessageBox.Show(this, "Укажите хост.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _hostBox.Focus();
                return;
            }
            var ports = ParsePorts(out var parseError);
            if (parseError != null)
            {
                MessageBox.Show(this, parseError, Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _list.Items.Clear();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _scanBtn.Text = "Стоп";
            int done = 0;
            _statusLabel.Text = $"Сканирование {host}: 0 из {ports.Count}…";

            try
            {
                using var sem = new SemaphoreSlim(40);
                var tasks = ports.Select(async p =>
                {
                    await sem.WaitAsync(ct);
                    try
                    {
                        if (ct.IsCancellationRequested) return;
                        var sw = Stopwatch.StartNew();
                        bool open = false;
                        string state;
                        try
                        {
                            open = await PingService.IsPortOpenAsync(host, p, 1000);
                            sw.Stop();
                            state = open ? "открыт" : "закрыт / фильтруется";
                        }
                        catch
                        {
                            sw.Stop();
                            state = "ошибка";
                        }
                        int n = Interlocked.Increment(ref done);
                        try
                        {
                            if (ct.IsCancellationRequested) return;
                            var item = new ListViewItem(new[] { p.ToString(), state, sw.ElapsedMilliseconds.ToString() });
                            if (open) item.ForeColor = Color.FromArgb(56, 161, 105);
                            else if (state == "ошибка") item.ForeColor = Helpers.UiTheme.MutedForeground;
                            else item.ForeColor = Color.FromArgb(214, 73, 73);
                            _list.Items.Add(item);
                            _statusLabel.Text = ct.IsCancellationRequested
                                ? $"Остановлено: проверено {n} из {ports.Count}."
                                : $"Сканирование {host}: {n} из {ports.Count}…";
                        }
                        catch { }
                    }
                    finally
                    {
                        try { sem.Release(); } catch { }
                    }
                }).ToArray();

                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException) { }
            finally
            {
                try { _cts?.Dispose(); } catch { }
                _cts = null;
                try
                {
                    _scanBtn.Text = "Сканировать";
                    SortByPort();
                    int openCount = _list.Items.Cast<ListViewItem>()
                        .Count(i => i.Text.Length > 0 && i.ForeColor == Color.FromArgb(56, 161, 105));
                    _statusLabel.Text = $"Готово: проверено {done} из {ports.Count}, открыто {openCount}.";
                }
                catch { }
            }
        }

        private void SortByPort()
        {
            try
            {
                _list.ListViewItemSorter = new ByPortComparer();
                _list.Sort();
                _list.ListViewItemSorter = null;
            }
            catch { }
        }

        private sealed class ByPortComparer : System.Collections.IComparer
        {
            public int Compare(object? x, object? y)
            {
                int a = int.TryParse((x as ListViewItem)?.Text, out var pa) ? pa : 0;
                int b = int.TryParse((y as ListViewItem)?.Text, out var pb) ? pb : 0;
                return a.CompareTo(b);
            }
        }
    }
}
