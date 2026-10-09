using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ShadowRDP.Controls;
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
        private readonly DataGridView _grid;
        private readonly Label _scanBtn;
        private readonly Label _closeBtn;
        private CancellationTokenSource? _cts;

        private const string PresetCore = "21,22,23,25,80,110,135,139,143,389,443,445,636,993,995,3389,5985";
        private const string PresetWeb = "80,81,443,8080,8443,8000,8888,3000,5000,9090";
        private const string PresetDb = "1433,1521,3306,5432,27017,6379,5984,9200";
        private const string PresetAll = "21,22,23,25,53,80,110,135,139,143,389,443,445,4899,5900,636,993,995,1433,1521,3306,3389,5432,5985,6379,8080,8443,9200,10050,27017";
        private const string Preset1C = "1540,1541,1560-1591,1545,1542,1550,1947,80,443,1433,1434,5432";
        private const string PresetMail = "25,53,80,110,143,389,443,465,587,636,993,995,1023+,3268,3269,5075-5077,5061,6005-65535,691";

        public static void Show(IWin32Window owner, string? host = null)
        {
            using var f = new PortScanForm(host);
            Helpers.UiModal.ShowDimmed(owner, f);
        }

        private PortScanForm(string? host)
        {
            Text = "Проверка портов";
            StartPosition = FormStartPosition.CenterParent;
            // v3.9.5 — регулировка мышкой + запоминание размера (Sizable +
            // WindowStateStore); минимум под пресеты-кнопки (4×190+зазоры).
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = false;
            MinimumSize = new Size(860, 560);
            Font = new Font("Segoe UI", 9F);
            BackColor = Helpers.UiTheme.DialogBackground;
            // v3.9.5 — окно ×2 по ширине и +360 по высоте (440×640 → 880×1000;
            // влезает в экран 1920×1080), сетка колонок под широкую карточку.
            ClientSize = new Size(880, 1000);

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

            var card = Helpers.UiModal.Card(12, 90, 832, 246, "Сканер", "ping");
            Controls.Add(card);

            var lblHost = Helpers.UiModal.FieldLabel("Хост", 16, 48);
            card.Controls.Add(lblHost);
            _hostBox = new TextBox { Text = host ?? "" };
            _hostWrap = Helpers.UiModal.Field(_hostBox, 800);
            _hostWrap.Location = new Point(16, 64);
            card.Controls.Add(_hostWrap);

            var lblPorts = Helpers.UiModal.FieldLabel("Порты и диапазоны (a-b, n+) через запятую", 16, 112);
            card.Controls.Add(lblPorts);
            _portsBox = new TextBox { Text = PresetCore };
            _portsWrap = Helpers.UiModal.Field(_portsBox, 800);
            _portsWrap.Location = new Point(16, 128);
            card.Controls.Add(_portsWrap);

            // Пресеты — под полем портов: два ряда по три-четыре кнопки
            // одинаковой ширины с одинаковым зазором (v3.9.5: раньше «БД»
            // перекрывала «Все»; добавлены 1С и Mail).
            var presets = new (string Text, string Ports)[]
            {
                ("Сетевые", PresetCore),
                ("Web", PresetWeb),
                ("БД", PresetDb),
                ("Все", PresetAll),
                ("1С", Preset1C),
                ("Mail", PresetMail)
            };
            const int presetWidth = 190, presetStep = 198; // 190 + зазор 8
            const int presetRowY = 170, presetRowStep = 30; // 22 + зазор 8
            for (int i = 0; i < presets.Length; i++)
            {
                int row = i < 4 ? 0 : 1;
                int col = row == 0 ? i : i - 4;
                AddPreset(card, presets[i].Text, presets[i].Ports,
                    16 + col * presetStep, presetWidth, presetRowY + row * presetRowStep);
            }

            _statusLabel = new Label
            {
                Location = new Point(14, 346),
                Size = new Size(812, 20),
                Font = new Font("Segoe UI", 8.5F),
                ForeColor = Helpers.UiTheme.MutedForeground,
                BackColor = Color.Transparent,
                Text = "Укажите хост и порты."
            };
            Controls.Add(_statusLabel);

            // v3.9.5 — список результатов в стиле «Открытых файлов»:
            // headerless-карточка + грид ApplyCardGrid.
            var cardList = Helpers.UiModal.Card(12, 370, 832, 566, "", "", withHeader: false);
            try { cardList.Region?.Dispose(); cardList.Region = null; } catch { }
            Controls.Add(cardList);

            _grid = new DoubleBufferedDataGridView
            {
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                MultiSelect = false
            };
            var colPort = new DataGridViewTextBoxColumn
            {
                Name = "Port", HeaderText = "Порт",
                FillWeight = 20, MinimumWidth = 60
            };
            colPort.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            colPort.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleRight;
            _grid.Columns.Add(colPort);
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "State", HeaderText = "Состояние",
                FillWeight = 55, MinimumWidth = 140
            });
            var colMs = new DataGridViewTextBoxColumn
            {
                Name = "Ms", HeaderText = "мс",
                FillWeight = 25, MinimumWidth = 48
            };
            colMs.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            colMs.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleRight;
            _grid.Columns.Add(colMs);
            Helpers.UiGridStyle.ApplyCardGrid(_grid, cardList);

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

            // v3.9.5 — ресайз: ширина карточек/полей по окну, список
            // результатов тянется по высоте (футер и статус Dock/абсолют).
            Resize += (_, _) => LayoutContent();
            LayoutContent();
            Services.WindowStateStore.Register(this, "portScan");

            void LayoutContent()
            {
                try
                {
                    int w = ClientSize.Width, h = ClientSize.Height;
                    int cw = Math.Max(400, w - 24);
                    card.Width = cw;
                    _hostWrap.Width = card.ClientSize.Width - 32;
                    _portsWrap.Width = card.ClientSize.Width - 32;
                    _statusLabel.Width = Math.Max(200, w - 28);
                    cardList.Width = cw;
                    cardList.Height = Math.Max(120, h - 370 - 52 - 12);
                }
                catch { }
            }
        }

        private void AddPreset(Control card, string text, string ports, int x, int width, int y)
        {
            // v3.9.5 — песочные кнопки (#D9BC8C), текст белый по умолчанию.
            var b = Helpers.UiButtons.MakeCompactFilled(text, width,
                bgOverride: Color.FromArgb(0xD9, 0xBC, 0x8C));
            b.Location = new Point(x, y);
            card.Controls.Add(b);
            b.Click += (_, _) => _portsBox.Text = ports;
        }

        private List<int> ParsePorts(out string? error)
        {
            error = null;
            var set = new SortedSet<int>();
            // v3.9.5 — диапазоны «a-b» и открытые «n+» (= n..65535):
            // пресеты 1С/Mail без них не задать.
            string? AddRange(int a, int b, string token)
            {
                if (a < 1 || b > 65535 || a > b)
                    return $"Некорректный диапазон: '{token}'.";
                for (int p = a; p <= b; p++) set.Add(p);
                return null;
            }
            foreach (var part in _portsBox.Text.Split(
                new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = part.Trim();
                if (t.EndsWith("+", StringComparison.Ordinal)
                    && int.TryParse(t.Substring(0, t.Length - 1), out var fromPlus))
                {
                    error = AddRange(fromPlus, 65535, t);
                    if (error != null) return new List<int>();
                }
                else if (t.Contains('-'))
                {
                    var ab = t.Split('-');
                    if (ab.Length != 2
                        || !int.TryParse(ab[0].Trim(), out var ra)
                        || !int.TryParse(ab[1].Trim(), out var rb))
                    {
                        error = $"Некорректный диапазон: '{t}'.";
                        return new List<int>();
                    }
                    error = AddRange(ra, rb, t);
                    if (error != null) return new List<int>();
                }
                else if (!int.TryParse(t, out var p) || p < 1 || p > 65535)
                {
                    error = $"Некорректный порт: '{t}'.";
                    return new List<int>();
                }
                else set.Add(p);
            }
            if (set.Count == 0) error = "Список портов пуст.";
            else if (set.Count > 65535) error = "Слишком много портов (максимум 65535 за прогон).";
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

            _grid.Rows.Clear();
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
                            int ri = _grid.Rows.Add(p, state, sw.ElapsedMilliseconds.ToString());
                            var row = _grid.Rows[ri];
                            if (open) row.DefaultCellStyle.ForeColor = Color.FromArgb(56, 161, 105);
                            else if (state == "ошибка") row.DefaultCellStyle.ForeColor = Helpers.UiTheme.MutedForeground;
                            else row.DefaultCellStyle.ForeColor = Color.FromArgb(214, 73, 73);
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
                    int openCount = _grid.Rows.Cast<DataGridViewRow>()
                        .Count(r => "открыт".Equals(r.Cells[1].Value?.ToString()));
                    _statusLabel.Text = $"Готово: проверено {done} из {ports.Count}, открыто {openCount}.";
                }
                catch { }
            }
        }

        private void SortByPort()
        {
            // Порт лежит в ячейке как int — сортировка числовая, не лексикографическая.
            try
            {
                _grid.Sort(_grid.Columns[0], ListSortDirection.Ascending);
            }
            catch { }
        }
    }
}
