using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using ShadowRDP.Controls;
using ShadowRDP.Helpers;
using ShadowRDP.Services;

namespace ShadowRDP.Forms
{
    // v3.9.5 — помощь в настройке инвентаризации (кнопка «Помощь
    // в настройке» в хедере HARD и SOFT): как всё устроено + готовый
    // скрипт (скопировать / сохранить в файл) + проверка + типовые
    // проблемы. Чтобы человек с нуля понял что и как настраивать.
    public enum InventoryHelpMode { Hard, Soft, Monitor }

    // Тексты скриптов — отдельно от формы, чтобы харнесс проверял
    // содержимое без UI.
    public static class InventoryHelpScripts
    {
        // Подготовка ПК к опросу HARD (запуск от администратора
        // на каждом ПК или через GPO → автозагрузка).
        public const string HardSetupScript =
            "#Requires -RunAsAdministrator\n" +
            "# ============================================================\n" +
            "# PupSeekRDP — «Сводная таблица HARD»: подготовка компьютера\n" +
            "# к опросу. Отдельный агент НЕ нужен: программа опрашивает ПК\n" +
            "# напрямую по WMI от имени администратора.\n" +
            "#\n" +
            "# Запуск: от имени администратора на каждом ПК\n" +
            "#   или один раз через GPO: Конфигурация компьютера →\n" +
            "#   Политики → Конфигурация Windows → Сценарии → Автозагрузка.\n" +
            "# Перезагрузка НЕ требуется.\n" +
            "# ============================================================\n" +
            "\n" +
            "# 1. Брандмауэр: входящие WMI-запросы (DCOM).\n" +
            "Enable-NetFirewallRule -DisplayGroup 'Windows Management Instrumentation (WMI)' -ErrorAction SilentlyContinue\n" +
            "netsh advfirewall firewall set rule group=\"windows management instrumentation (wmi)\" new enable=yes | Out-Null\n" +
            "\n" +
            "# 2. Ping: таблица сначала быстро проверяет доступность ПК.\n" +
            "Enable-NetFirewallRule -DisplayName 'File and Printer Sharing (Echo Request - ICMPv4-In)' -ErrorAction SilentlyContinue\n" +
            "\n" +
            "# 3. Служба WMI должна работать.\n" +
            "Set-Service -Name Winmgmt -StartupType Automatic -ErrorAction SilentlyContinue\n" +
            "Start-Service -Name Winmgmt -ErrorAction SilentlyContinue\n" +
            "\n" +
            "# 4. Только для рабочей группы (компьютер НЕ в домене):\n" +
            "#    разрешить удалённое администрирование локальным админом.\n" +
            "if ((Get-CimInstance Win32_ComputerSystem).PartOfDomain -eq $false) {\n" +
            "    Set-ItemProperty -Path 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\System' `\n" +
            "        -Name LocalAccountTokenFilterPolicy -Value 1 -Type DWord -Force\n" +
            "}\n" +
            "\n" +
            "Write-Host 'Готово. Проверка с компьютера оператора:' -ForegroundColor Green\n" +
            "Write-Host ('  Get-CimInstance -ComputerName ' + $env:COMPUTERNAME + ' -ClassName Win32_Processor | Select-Object -ExpandProperty Name')\n";

        // Сбор установленного ПО для SOFT (GPO → автозагрузка).
        // Схема JSON обязана совпадать с SoftwareInventoryService:
        // { Computer, OS, Timestamp, Programs: [{ Name, Version }] }.
        public const string SoftGpoScript =
            "# ============================================================\n" +
            "# PupSeekRDP — «Инвентаризация SOFT»: сбор установленного ПО.\n" +
            "# Куда: GPO → Конфигурация компьютера → Политики →\n" +
            "#   Конфигурация Windows → Сценарии → Автозагрузка → этот файл.\n" +
            "# Почему автозагрузка, а не вход в систему: срабатывает и на\n" +
            "# серверах, где никто не логинится.\n" +
            "# ВАЖНО: ниже замените FILESERVER на имя файлового сервера.\n" +
            "# ============================================================\n" +
            "$Share = '\\\\FILESERVER\\SoftwareInventory$'\n" +
            "\n" +
            "$programs = @()\n" +
            "foreach ($path in @(\n" +
            "    'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall',\n" +
            "    'HKLM:\\SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall'\n" +
            ")) {\n" +
            "    Get-ItemProperty $path -ErrorAction SilentlyContinue |\n" +
            "        Where-Object { $_.DisplayName } |\n" +
            "        ForEach-Object {\n" +
            "            $programs += [PSCustomObject]@{\n" +
            "                Name    = [string]$_.DisplayName\n" +
            "                Version = [string]$_.DisplayVersion\n" +
            "            }\n" +
            "        }\n" +
            "}\n" +
            "\n" +
            "[PSCustomObject]@{\n" +
            "    Computer  = $env:COMPUTERNAME\n" +
            "    OS        = (Get-CimInstance Win32_OperatingSystem).Caption\n" +
            "    Timestamp = (Get-Date).ToString('o')\n" +
            "    Programs  = $programs\n" +
            "} | ConvertTo-Json -Depth 3 |\n" +
            "    Set-Content -Path (Join-Path $Share ($env:COMPUTERNAME + '.json')) -Encoding UTF8\n";
    }

    // v3.9.5 — общий дизайн диалогов: hero + секции UiModal.Card
    // + футер. Вёрстка детерминированная (все координаты считаются
    // в LayoutCards через TextRenderer) — текст физически не может
    // вылезти за карточку: ширина подписей = ширина карточки − 32.
    public class InventoryHelpDialog : Form
    {
        private readonly InventoryHelpMode _mode;
        private readonly Panel _body;
        private readonly BufferedPanel _scroll;
        private readonly BufferedPanel _inner;
        private ShadowScrollBar _vbar = null!;
        private int _offset;
        private TextBox _scriptBox = null!;
        private TextBox _commandBox = null!;
        private readonly Label _statusLabel;
        private readonly Font _bodyFont = new Font("Segoe UI", 9F);
        private readonly List<Section> _sections = new();
        // Все переносимые подписи — для харнесс-проверки «не вылезают».
        private readonly List<Label> _bodyLabels = new();

        // Контейнер скролла с двойной буферизацией: без неё прокрутка
        // кастомно красящихся карточек дёргается и мигает.
        private sealed class BufferedPanel : Panel
        {
            public BufferedPanel()
            {
                DoubleBuffered = true;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                    | ControlStyles.OptimizedDoubleBuffer, true);
                UpdateStyles();
            }
        }

        private static void EnableBuffer(Control c)
        {
            try
            {
                typeof(Control).GetProperty("DoubleBuffered",
                    BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(c, true);
            }
            catch { }
        }

        private sealed class Section
        {
            public Panel Card = null!;
            public readonly List<Control> Flow = new();
        }

        public static void Show(IWin32Window owner, InventoryHelpMode mode)
        {
            using var f = new InventoryHelpDialog(mode);
            UiModal.ShowDimmed(owner, f);
        }

        public InventoryHelpDialog(InventoryHelpMode mode)
        {
            _mode = mode;
            bool hard = mode == InventoryHelpMode.Hard;
            bool monitor = mode == InventoryHelpMode.Monitor;
            Text = hard ? "Помощь в настройке — Сводная таблица HARD"
                : monitor ? "Помощь в настройке — Дашборд Монитор"
                : "Помощь в настройке — Инвентаризация SOFT";
            ClientSize = new Size(740, 660);
            MinimumSize = new Size(620, 480);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = false;
            Font = new Font("Segoe UI", 9F);
            BackColor = UiTheme.DialogBackground;
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { e.Handled = true; Close(); } };

            var hero = UiModal.Hero(
                hard ? "Помощь в настройке — HARD"
                    : monitor ? "Помощь в настройке — Монитор"
                    : "Помощь в настройке — SOFT",
                hard ? "Сводная таблица HARD — что нужно для опроса по WMI"
                    : monitor ? "Дашборд Монитор — откуда берутся данные"
                    : "Инвентаризация SOFT — скрипт сбора и общая папка",
                "info");
            hero.Dock = DockStyle.Top;
            Controls.Add(hero);

            // ——— Футер (как в остальных диалогах) ———
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = UiTheme.DialogCard };
            footer.Paint += (s, e) => { using var pen = new Pen(UiTheme.Border, 1); e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };
            var closeBtn = UiButtons.MakeCompactFilled("Закрыть", 120);
            closeBtn.Click += (s, e) => Close();
            footer.Controls.Add(closeBtn);
            footer.Resize += (s, e) =>
            {
                try
                {
                    closeBtn.Location = new Point(footer.ClientSize.Width - 16 - closeBtn.Width, 15);
                    _statusLabel.Width = Math.Max(100, footer.ClientSize.Width - 32 - closeBtn.Width - 16);
                }
                catch { }
            };
            _statusLabel = new Label
            {
                Location = new Point(16, 15),
                Size = new Size(400, 22),
                Font = new Font("Segoe UI", 8.5F),
                ForeColor = UiTheme.MutedForeground,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft,
                Text = ""
            };
            footer.Controls.Add(_statusLabel);
            Controls.Add(footer);

            // ——— Тело с явными границами (паттерн PaletteSettingsForm) ———
            _body = new Panel
            {
                Location = new Point(0, 80),
                Size = new Size(740, 528),
                BackColor = UiTheme.DialogBackground
            };
            _scroll = new BufferedPanel
            {
                Dock = DockStyle.Fill,
                // Ручной скролл (см. ScrollTo): с включённым AutoScroll
                // фреймворк на каждый сдвиг показывает обратно спрятанные
                // через ShowScrollBar нативные полосы — мигание + прыжки
                // ClientSize + пересчёт вёрстки = дёрганье при перетаскивании.
                AutoScroll = false,
                BackColor = UiTheme.DialogBackground
            };
            _inner = new BufferedPanel
            {
                Location = new Point(0, 0),
                Size = new Size(100, 100),
                BackColor = UiTheme.DialogBackground
            };
            _scroll.Controls.Add(_inner);
            _body.Controls.Add(_scroll);
            Controls.Add(_body);

            // Своя тонкая полоса (общий дизайн) поверх правого края —
            // вместо ShadowScroll.AttachPanel, которому нужен AutoScroll.
            _vbar = new ShadowScrollBar(ShadowScrollBar.BarOrientation.Vertical,
                () => _offset,
                () => _inner.Height,
                () => Math.Max(1, _scroll.ClientSize.Height),
                y => ScrollTo(y),
                () => 40);
            _body.Controls.Add(_vbar);
            bool CanV()
            {
                if (_inner.IsDisposed || _scroll.IsDisposed) return false;
                return _inner.Height > Math.Max(1, _scroll.ClientSize.Height);
            }
            bool ScrollV(int lines)
            {
                try
                {
                    if (!CanV()) return false;
                    // Строка панели ≈ 20px (как было в AttachPanel).
                    ScrollTo(_offset + lines * 20);
                    return true;
                }
                catch { return false; }
            }
            ShadowWheel.Register(_scroll, CanV, ScrollV);
            ShadowWheel.Register(_vbar, CanV, ScrollV);

            if (hard) BuildHard();
            else if (monitor) BuildMonitor();
            else BuildSoft();

            Resize += (s, e) => LayoutBody();
            _scroll.Resize += (s, e) => LayoutCards();
            LayoutBody();
            Shown += (s, e) =>
            {
                LayoutBody();
                try { BeginInvoke(new Action(() => { try { Refresh(); } catch { } })); } catch { }
            };
        }

        private void LayoutBody()
        {
            try
            {
                _body.Width = ClientSize.Width;
                _body.Height = Math.Max(200, ClientSize.Height - 80 - 52);
                LayoutCards();
            }
            catch { }
        }

        // Ручной скролл: едет только внутренний контейнер — никаких
        // нативных полос, layout-штормов и прыжков ClientSize.
        private void ScrollTo(int y)
        {
            try
            {
                if (_inner.IsDisposed || _scroll.IsDisposed) return;
                int page = Math.Max(1, _scroll.ClientSize.Height);
                int max = Math.Max(0, _inner.Height - page);
                int nv = Math.Max(0, Math.Min(max, y));
                _offset = nv;
                var want = new Point(0, -nv);
                if (_inner.Location != want) _inner.Location = want;
                try { _vbar.RefreshRange(); } catch { }
            }
            catch { }
        }

        // Все координаты считаются здесь — единственный источник правды.
        private void LayoutCards()
        {
            try
            {
                const int margin = 14;
                int contentW = Math.Max(240, _scroll.ClientSize.Width - margin * 2);
                int innerW = contentW - 32; // поля карточки по 16 с каждой стороны
                int y = 12;
                foreach (var s in _sections)
                {
                    if (s.Card.IsDisposed) continue;
                    int cy = 56; // под шапкой карточки (разделитель на y=44)
                    foreach (var c in s.Flow)
                    {
                        if (c.IsDisposed) continue;
                        if (c.Tag as string == "wrap" && c is Label lbl)
                        {
                            int h = MeasureWrap(lbl.Text, innerW);
                            var wantLoc = new Point(16, cy);
                            var wantSz = new Size(innerW, h);
                            // Лишние сеты геометрии = лишние перерисовки
                            // и дёрганье при скролле/ресайзе.
                            if (lbl.Location != wantLoc) lbl.Location = wantLoc;
                            if (lbl.Size != wantSz) lbl.Size = wantSz;
                            cy += h + 8;
                        }
                        else
                        {
                            if (c is Panel && c.Width != innerW) c.Width = innerW;
                            var wantLoc = new Point(16, cy);
                            if (c.Location != wantLoc) c.Location = wantLoc;
                            cy += c.Height + 10;
                        }
                    }
                    int cardH = cy - 10 + 14;
                    var wantCardLoc = new Point(margin, y);
                    var wantCardSz = new Size(contentW, cardH);
                    if (s.Card.Location != wantCardLoc) s.Card.Location = wantCardLoc;
                    if (s.Card.Size != wantCardSz) s.Card.Size = wantCardSz;
                    y += cardH + 12;
                }
                // Внутренний контейнер ровно по контенту + полоса справа.
                var wantInner = new Size(Math.Max(_scroll.ClientSize.Width, margin * 2 + contentW), y + 4);
                if (_inner.Size != wantInner) _inner.Size = wantInner;
                ScrollTo(_offset); // переклампить сдвиг под новую высоту
                try
                {
                    var wantBar = new Rectangle(_scroll.Right - 10, _scroll.Top, 10, Math.Max(1, _scroll.Height));
                    if (_vbar.Bounds != wantBar) _vbar.Bounds = wantBar;
                    _vbar.RefreshRange();
                    if (_vbar.Visible) _vbar.BringToFront();
                }
                catch { }
            }
            catch { }
        }

        private int MeasureWrap(string text, int maxW)
        {
            try
            {
                if (maxW < 60) maxW = 60;
                var sz = TextRenderer.MeasureText(text, _bodyFont, new Size(maxW, int.MaxValue),
                    TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix);
                return Math.Max(sz.Height, 16);
            }
            catch { return 32; }
        }

        private Section AddSection(string title, string iconKey)
        {
            var s = new Section
            {
                Card = UiModal.Card(0, 0, 100, 100, title, iconKey, withHeader: true)
            };
            EnableBuffer(s.Card);
            _inner.Controls.Add(s.Card);
            _sections.Add(s);
            return s;
        }

        private void AddWrap(Section s, string text)
        {
            var l = new Label
            {
                Text = text,
                AutoSize = false,
                Font = _bodyFont,
                ForeColor = UiTheme.Foreground,
                // Непрозрачная в цвет карточки: прозрачные подписи при
                // скролле дёргают перерисовку родителя (мигание).
                BackColor = UiTheme.DialogCard,
                Tag = "wrap"
            };
            s.Card.Controls.Add(l);
            s.Flow.Add(l);
            _bodyLabels.Add(l);
        }

        private void AddCaption(Section s, string text)
        {
            // Подпись тем же кеглем, что текст таблицы (9pt),
            // иерархия — цветом, а не мелким шрифтом.
            var cap = new Label
            {
                Text = text,
                AutoSize = false,
                Size = new Size(400, 18),
                Font = _bodyFont,
                ForeColor = UiTheme.MutedForeground,
                BackColor = UiTheme.DialogCard
            };
            s.Card.Controls.Add(cap);
            s.Flow.Add(cap);
        }

        private void AddScriptField(Section s, string script)
        {
            AddCaption(s, "Скрипт — скопируйте или сохраните в файл .ps1");
            var box = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                TabStop = false,
                BackColor = Color.White,
                ForeColor = UiTheme.Foreground,
                Text = script.Replace("\n", "\r\n")
            };
            var wrap = UiModal.Field(box, 400, 200);
            box.Font = new Font("Consolas", 9F); // Field ставит Segoe — возвращаем моноширинный тем же кеглем, что таблица
            EnableBuffer(wrap);
            s.Card.Controls.Add(wrap);
            s.Flow.Add(wrap);
            _scriptBox = box;
        }

        private void AddCommandField(Section s, string caption, string command)
        {
            AddCaption(s, caption);
            var box = new TextBox
            {
                Multiline = false,
                ReadOnly = true,
                TabStop = false,
                BackColor = Color.White,
                ForeColor = UiTheme.Foreground,
                Text = command
            };
            var wrap = UiModal.Field(box, 400, 30);
            box.Font = new Font("Consolas", 9F);
            EnableBuffer(wrap);
            s.Card.Controls.Add(wrap);
            s.Flow.Add(wrap);
            _commandBox = box;
        }

        private void AddScriptButtons(Section s, string fileName)
        {
            var row = new Panel
            {
                Size = new Size(400, 40),
                BackColor = Color.Transparent
            };
            EnableBuffer(row);
            var copyBtn = UiButtons.MakeCompactFilled("Скопировать скрипт", 170);
            copyBtn.Location = new Point(0, 9);
            copyBtn.Click += (st, e) =>
            {
                try
                {
                    Clipboard.SetText(_scriptBox.Text);
                    _statusLabel.Text = "Скрипт скопирован в буфер обмена.";
                }
                catch (Exception ex) { _statusLabel.Text = $"Не скопировалось: {ex.Message}"; }
            };
            var saveBtn = UiButtons.MakeCompactFlat("Сохранить в файл…", 170);
            saveBtn.Location = new Point(178, 9);
            saveBtn.Click += (st, e) =>
            {
                try
                {
                    using var sfd = new SaveFileDialog
                    {
                        Filter = "PowerShell (*.ps1)|*.ps1|Все файлы (*.*)|*.*",
                        FileName = fileName,
                        DefaultExt = "ps1"
                    };
                    if (UiModal.ShowDimmed(this, sfd) != DialogResult.OK) return;
                    File.WriteAllText(sfd.FileName, _scriptBox.Text, new UTF8Encoding(true));
                    _statusLabel.Text = $"Скрипт сохранён: {sfd.FileName}";
                    try { Logger.Info($"InventoryHelp: script saved to {sfd.FileName}"); } catch { }
                }
                catch (Exception ex) { _statusLabel.Text = $"Не сохранилось: {ex.Message}"; }
            };
            row.Controls.Add(copyBtn);
            row.Controls.Add(saveBtn);
            s.Card.Controls.Add(row);
            s.Flow.Add(row);
        }

        private void BuildHard()
        {
            var s1 = AddSection("Как это работает", "info");
            AddWrap(s1, "Отдельный агент или скрипт на компьютерах НЕ нужен. По кнопке «Сканировать» программа опрашивает каждый компьютер из адресной книги напрямую по WMI: процессор — Win32_Processor, память — Win32_ComputerSystem, диски — Win32_LogicalDisk. Опрос идёт от имени вашего пользователя, поэтому нужны права администратора на опрашиваемых ПК и разрешённый доступ по сети (см. ниже). Результат запоминается и показывается при следующем открытии окна.");

            var s2 = AddSection("Что должно быть на каждом ПК", "gear");
            AddWrap(s2, "1. ПК включён и пингуется.\n2. Ваша учётка — администратор на этом ПК (доменный админ подходит).\n3. Брандмауэр пускает входящие WMI-запросы.\n4. Если ПК НЕ в домене (рабочая группа) — дополнительно нужен пункт 4 скрипта ниже.\n5. Служба «Инструментарий управления Windows (WMI)» запущена (обычно да).");

            var s3 = AddSection("Скрипт подготовки — запуск от администратора", "powershell");
            AddWrap(s3, "На каждом ПК — или один раз через GPO: Конфигурация компьютера → Политики → Конфигурация Windows → Сценарии → Автозагрузка. Перезагрузка не нужна.");
            AddScriptField(s3, InventoryHelpScripts.HardSetupScript);
            AddScriptButtons(s3, "Setup-HardInventory.ps1");

            var s4 = AddSection("Проверка", "ping");
            AddWrap(s4, "Вернул название процессора — опрос пойдёт. Ошибка — смотрите раздел ниже.");
            AddCommandField(s4, "Выполнить с компьютера оператора:",
                "Get-CimInstance -ComputerName ИМЯ_ПК -ClassName Win32_Processor | Select-Object -ExpandProperty Name");

            var s5 = AddSection("Если не работает", "shield");
            AddWrap(s5, "«не в сети» — ПК выключен, нет ping (файрвол режет ICMP) или неверное имя.\n«Отказано в доступе» — нет прав админа на ПК, либо в рабочей группе не применён пункт 4 скрипта.\n«RPC-сервер недоступен» — закрыт файрвол (пункт 1 скрипта) или стоит служба WMI.");
        }

        private void BuildSoft()
        {
            var s1 = AddSection("Как это работает", "info");
            AddWrap(s1, "Таблица НЕ опрашивает компьютеры сама. Она читает готовые файлы <ИмяПК>.json из общей папки \\\\FILESERVER\\SoftwareInventory$. Файлы туда пишет скрипт ниже, который запускается на каждом ПК через групповые политики при старте. Без скрипта и папки таблица пустая — это нормально. Кнопка «Сканировать» просто перечитывает папку.");

            var s2 = AddSection("Шаг 1 — общая папка", "folder");
            AddWrap(s2, "На файловом сервере создайте папку (например, C:\\Shares\\SoftwareInventory) и расшарьте её под именем SoftwareInventory$ (знак $ прячет шару из обзора). Права: «Компьютеры домена» — запись (скрипт работает от имени SYSTEM), вам — чтение.");

            var s3 = AddSection("Шаг 2 — скрипт (положить в GPO)", "powershell");
            AddWrap(s3, "Сохраните скрипт в файл Collect-SoftInventory.ps1. В GPO: Конфигурация компьютера → Политики → Конфигурация Windows → Сценарии → Автозагрузка → добавить файл. Именно автозагрузка, а не вход в систему: тогда соберутся и серверы, где никто не логинится. Не забудьте заменить FILESERVER на имя вашего сервера.");
            AddScriptField(s3, InventoryHelpScripts.SoftGpoScript);
            AddScriptButtons(s3, "Collect-SoftInventory.ps1");

            var s4 = AddSection("Важно про имя сервера", "globe");
            AddWrap(s4, "Путь к папке зашит в программе: \\\\FILESERVER\\SoftwareInventory$. Если файловый сервер зовётся иначе — создайте в DNS алиас (CNAME) FILESERVER на него. Иначе таблица ничего не найдёт.");

            var s5 = AddSection("Проверка", "ping");
            AddWrap(s5, "После перезагрузки любого ПК в папке должен появиться файл <ИмяПК>.json. Откройте таблицу и нажмите «Сканировать».");

            var s6 = AddSection("Если не работает", "shield");
            AddWrap(s6, "Файлов нет — GPO не применилась (проверьте gpresult /r), скрипт упал или нет прав на запись в шару.\n«Доступ запрещён» к шаре — права из шага 1.\nФайл старый — ПК давно не перезагружался, либо скрипт висит во входе, а не в автозагрузке.");
        }

        private void BuildMonitor()
        {
            var s1 = AddSection("Как это работает", "info");
            AddWrap(s1, "Таблица показывает результат последнего опроса каждого хоста. Опрос идёт с вашего компьютера напрямую: сначала Zabbix-агент по TCP 10050 (CPU, RAM, диски, сеть, службы, температура, аптайм), если агент молчит — fallback на SNMP по UDP 161. Колонка «Пинг» опрашивается отдельно по ICMP. Первый опрос — примерно через 10 секунд после старта программы, дальше — по интервалу из настроек (по умолчанию 15 минут). История хранится 2 дня — она нужна для графиков в деталях. Кнопка «Обновить» в шапке опрашивает всё вручную.");

            var s2 = AddSection("Кто попадает в таблицу", "computer");
            AddWrap(s2, "1. ПК из адресной книги с включённым отслеживанием (ПКМ → тумблеры).\n2. Хосты, добавленные кнопкой «Добавить запись»: имя, IP через запятую, метод (Агент Zabbix или SNMP), community/порт/версия.\nКолонка «IP» берётся из книги — без IP опрос идёт только по имени (имена за VPN часто не резолвятся, пропишите IP).");

            var s3 = AddSection("Агент или SNMP", "gear");
            AddWrap(s3, "Режим агента: на ПК установлен Zabbix Agent, открыт TCP 10050, а IP вашего компьютера внесён в параметр Server= конфига агента. Кнопка «Настроить агентов» в шапке дописывает его автоматически через WMI (нужны права администратора на ПК).\nРежим SNMP: открыт UDP 161, Community и версия совпадают с настройками (карточка SNMP в окне настроек), на устройстве включены HOST-RESOURCES-MIB и IF-MIB. У ручных хостов community/порт/версию можно задать свои.");

            var s4 = AddSection("Опрос, пороги, обновление", "dashboard");
            AddWrap(s4, "Окно настроек (кнопка «Настройки» в шапке): интервал опроса 5–240 минут, таймаут, число потоков; пороги цвета CPU/RAM/диск/температура; уведомления (нет пинга, мало места, звук); параметры SNMP. Внизу таблицы видно время последнего обновления. Пинг опрашивается отдельно (первый — примерно через 30 секунд, дальше раз в час).");

            var s5 = AddSection("Проверка", "ping");
            AddWrap(s5, "Если порт открыт — агент на месте и опрос пойдёт. Для SNMP проверьте community тем же способом через любой SNMP-браузер на UDP 161. Подсказки пишет debug.log (%PROGRAMDATA%\\PupSeekRDP): строка «ZabbixAgent host …» показывает, что реально вернулся с хоста. Точечная проверка порта — «Инструменты → Проверка портов…» (введите 10050).");
            AddCommandField(s5, "Выполнить с вашего компьютера:",
                "Test-NetConnection -ComputerName ИМЯ_ПК -Port 10050");

            var s6 = AddSection("Если не работает", "shield");
            AddWrap(s6, "Красная строка, «Агент = ERR» — нет связи по 10050: ПК выключен, файрвол, нет вашего IP в Server= агента, неверный IP в книге.\n«SNMP: no SNMP response» — закрыт UDP 161, чужой community/версия.\nПрочерк «—» вместо значения — датчик не поддерживается (температуры нет на виртуалках, у SNMP нет температуры и версии) или истории меньше 2 опросов.\nДанные старые — большой интервал опроса или встал поллер (кнопка «Обновить», затем debug.log).");
        }
    }
}
