using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace ShadowRDP.Controls
{
    public class ConsoleSessionControl : UserControl
    {
        private Process? _process;
        private readonly string _exePath;
        private readonly string _arguments;
        private readonly Encoding _outputEncoding;
        private readonly Color _accentColor;
        private bool _closing;
        private bool _started;
        private readonly ConsoleOutputBox _output;
        private readonly ThemedVScrollBar _scrollBar;
        private readonly TerminalWheelFilter _termWheelFilter;
        private int _lineHeight = 24;
        private readonly TextBox _input;
        private readonly Panel _statusPanel;
        private Rectangle _statusCloseRect;
        private bool _statusCloseHover;
        private readonly string _statusTitle;
        private readonly string _statusVersion;
        private readonly string _statusKind;

        // Command history for Up/Down navigation (persisted per shell kind).
        private readonly List<string> _history = new List<string>();
        private int _historyIndex = -1;
        private readonly string _historyKey;

        // Windows Terminal-style colors.
        private static readonly Color OutputColor = Color.FromArgb(0xEE, 0xEE, 0xEE);
        private static readonly Color PromptColor = Color.FromArgb(0x7F, 0xD8, 0x8F);
        private static readonly Color ErrorColor = Color.FromArgb(0xFF, 0x55, 0x55);

        // === Right hints panel: offline command dictionary (per shell). ===
        private sealed class HintExample
        {
            public string Text = "";
            public string Note = "";
        }

        private sealed class HintCmd
        {
            public string Name = "";
            public string Desc = "";
            public HintExample[] Examples = Array.Empty<HintExample>();
        }

        private static HintCmd H(string name, string desc, params (string Text, string Note)[] examples)
        {
            return new HintCmd
            {
                Name = name,
                Desc = desc,
                Examples = Array.ConvertAll(examples,
                    t => new HintExample { Text = t.Text, Note = t.Note })
            };
        }

        private static readonly Font _hintNameFont = new Font("Consolas", 11F, FontStyle.Bold);
        private static readonly Font _hintDescFont = new Font("Segoe UI", 10F);
        private static readonly Font _hintExampleFont = new Font("Consolas", 10F);
        private static readonly Font _hintExampleSelFont = new Font("Consolas", 10F, FontStyle.Bold);
        private static readonly Font _hintNoteFont = new Font("Segoe UI", 9F);
        private const int _hintCardSide = 8;
        private const int _hintCardPadTop = 10;
        private const int _hintCardPadBottom = 15;
        private const int _hintGap = 7;
        private const int _hintAccentBar = 3;
        private readonly int _hintNameH;
        private readonly int _hintDescH;
        private readonly int _hintExampleH;
        private readonly int _hintNoteH;

        private static readonly Dictionary<string, HintCmd[]> _hintDictionary = new()
        {
            ["CMD"] = new[]
            {
                // === Сеть ===
                H("ping", "Проверка сетевой доступности хоста",
                    ("ping 10.0.0.10", "4 пакета по умолчанию — базовый тест доступности"),
                    ("ping -n 100 8.8.8.8", "100 пакетов для оценки потерь и стабильности"),
                    ("ping -t 10.0.0.10", "Пинг до остановки Ctrl+C, удобно ловить обрыв линии"),
                    ("ping -a 10.0.0.10", "Разрешить имя хоста по IP — проверить DNS"),
                    ("ping -l 1400 10.0.0.10", "Пакеты 1400 байт — поиск проблем с MTU и фрагментацией")),
                H("ipconfig", "Сетевые настройки",
                    ("ipconfig", "IP-адрес и шлюз текущих адаптеров"),
                    ("ipconfig /all", "Полная информация: MAC, DNS, DHCP, физический адрес"),
                    ("ipconfig /flushdns", "Очистить DNS-кэш после смены настроек"),
                    ("ipconfig /release && ipconfig /renew", "Получить новый IP от DHCP"),
                    ("ipconfig /displaydns", "Показать содержимое DNS-кэша"),
                    ("ipconfig /registerdns", "Перерегистрировать записи ПК в DNS (после смены имени)")),
                H("netstat", "Активные соединения и порты",
                    ("netstat -ano", "Все соединения с PID процессов"),
                    ("netstat -ano | findstr 3389", "Только соединения на RDP-порту"),
                    ("netstat -rn", "Таблица маршрутизации (как route print)"),
                    ("netstat -anb", "Программа, создавшая соединение (нужны права админа)"),
                    ("netstat -e", "Счётчики Ethernet: отправлено/получено, ошибки")),
                H("tracert", "Трассировка маршрута до хоста",
                    ("tracert 8.8.8.8", "Показывает все хопы до цели"),
                    ("tracert -h 15 10.0.0.1", "Ограничить 15 хопами — быстрее"),
                    ("tracert -d 8.8.8.8", "Без разрешения DNS-имён хопов — заметно быстрее"),
                    ("tracert -w 500 10.0.0.1", "Таймаут ответа 500 мс — поймать медленные хопы")),
                H("pathping", "Трассировка с анализом потерь на каждом хопе",
                    ("pathping 10.0.0.10", "Долгий анализ, затем таблица потерь по каждому хопу"),
                    ("pathping -n 10.0.0.10", "Без разрешения DNS — сокращает время анализа"),
                    ("pathping -q 50 10.0.0.10", "50 запросов на хоп — точнее статистика потерь")),
                H("nslookup", "DNS-запрос",
                    ("nslookup server01", "IP-адрес по имени хоста"),
                    ("nslookup -type=any domain.local", "Все DNS-записи домена"),
                    ("nslookup 10.0.0.15", "Обратный запрос: имя хоста по IP"),
                    ("nslookup -type=mx domain.local", "MX-записи почтового домена")),
                H("arp -a", "Таблица ARP (MAC соседей)",
                    ("arp -a", "IP и MAC всех хостов на L2-сегменте"),
                    ("arp -a -N 10.0.0.15", "Таблица ARP для конкретного интерфейса (по IP)"),
                    ("arp -d *", "Очистить таблицу ARP (после смены подсети)")),
                H("route print", "Таблица маршрутов",
                    ("route print", "Все маршруты"),
                    ("route print -4", "Только IPv4 — короче вывод"),
                    ("route print -6", "Только IPv6-маршруты"),
                    ("route add 10.0.5.0 mask 255.255.255.0 10.0.0.1", "Добавить маршрут до подсети через шлюз")),
                H("getmac", "MAC-адреса компьютера",
                    ("getmac /v", "MAC всех адаптеров и их имена"),
                    ("getmac /fo csv", "Вывод в CSV — легко вставить в отчёт"),
                    ("getmac /s server01 /u corp\\ivanov", "MAC-адреса удалённого ПК")),
                H("netsh", "Настройка сети и Wi-Fi",
                    ("netsh interface ip show config", "Конфигурация всех интерфейсов"),
                    ("netsh wlan show profiles", "Сохранённые Wi-Fi сети"),
                    ("netsh wlan show profile \"MyWiFi\" key=clear", "Пароль сохранённой Wi-Fi сети"),
                    ("netsh wlan show drivers", "Драйвер Wi-Fi и поддерживаемые стандарты"),
                    ("netsh interface ip set dns \"Ethernet\" static 8.8.8.8", "Задать статический DNS для адаптера")),

                // === Процессы и службы ===
                H("tasklist", "Список процессов",
                    ("tasklist", "Имена и PID всех процессов"),
                    ("tasklist /svc", "Какие службы запущены внутри процесса"),
                    ("tasklist /m", "Загруженные DLL-модули процесса, полезно для поиска вирусов"),
                    ("tasklist /v", "Подробно: имя окна, сессия, состояние"),
                    ("tasklist /fi \"memusage gt 100000\"", "Процессы с памятью больше 100 МБ")),
                H("taskkill", "Завершить процесс",
                    ("taskkill /PID 8420 /F", "Завершить процесс по PID (из tasklist)"),
                    ("taskkill /IM notepad.exe /F", "Завершить все экземпляры программы по имени"),
                    ("taskkill /PID 8420 /F /T", "Завершить процесс вместе со всем деревом потомков"),
                    ("taskkill /FI \"IMAGENAME eq cmd.exe\" /F", "Завершить по фильтру — например все cmd.exe")),
                H("sc", "Управление службами",
                    ("sc query", "Состояние всех служб"),
                    ("sc query | findstr /i spooler", "Состояние конкретной службы"),
                    ("sc start Spooler", "Запустить службу"),
                    ("sc config Spooler start= auto", "Автозапуск службы при старте"),
                    ("sc config Spooler start= disabled", "Отключить автозапуск службы"),
                    ("sc queryex Spooler", "PID службы и дополнительные данные"),
                    ("sc delete OldService", "Удалить службу")),
                H("net start / net stop", "Быстрый запуск/остановка служб",
                    ("net start Spooler", "Запустить службу (альтернатива sc)"),
                    ("net stop Spooler", "Остановить службу"),
                    ("net start", "Список всех запущенных служб"),
                    ("net stop Spooler /y", "Остановить, подтверждая остановку зависимых служб")),
                H("wmic process", "Запросы WMI по процессам",
                    ("wmic process list brief", "Процессы кратко: имя, PID, память"),
                    ("wmic process list full", "Все свойства процессов"),
                    ("wmic process where name='explorer.exe' get processid,parentprocessid", "PID конкретного процесса"),
                    ("wmic process where name='explorer.exe' call terminate", "Завершить процесс через WMI"),
                    ("wmic process get name,processid,workingsetsize /format:csv", "Вывод в CSV для экспорта")),

                // === Система ===
                H("systeminfo", "Информация о системе",
                    ("systeminfo", "Всё: ОС, память, сетевая карта, последние обновления"),
                    ("systeminfo /fo csv > info.csv", "Сохранить отчёт в CSV-файл"),
                    ("systeminfo | findstr /i \"os name memory\"", "Только нужные строки отчёта")),
                H("whoami", "Текущий пользователь",
                    ("whoami", "Имя пользователя"),
                    ("whoami /groups", "Группы, в т.ч. доменные"),
                    ("whoami /priv", "Назначенные привилегии"),
                    ("whoami /all", "Полный вывод: SID, группы, привилегии")),
                H("set", "Переменные окружения",
                    ("set", "Все переменные окружения"),
                    ("set | findstr /i user", "Только нужные (например, user*)"),
                    ("set PATH", "Показать переменную PATH"),
                    ("set MYVAR=hello", "Задать переменную на время текущей сессии")),
                H("echo", "Вывод текста и переменных",
                    ("echo %USERPROFILE%", "Путь к профилю пользователя"),
                    ("echo %COMPUTERNAME%", "Имя компьютера из переменной"),
                    ("echo %DATE% %TIME%", "Текущие дата и время"),
                    ("echo Hello > file.txt", "Записать текст в новый файл"),
                    ("echo %ERRORLEVEL%", "Код возврата последней команды")),
                H("sfc", "Проверка системных файлов",
                    ("sfc /scannow", "Проверить и восстановить системные файлы"),
                    ("sfc /verifyonly", "Только проверка, без восстановления"),
                    ("sfc /scanfile=C:\\Windows\\System32\\kernel32.dll", "Проверить один конкретный файл")),
                H("chkdsk", "Проверка диска",
                    ("chkdsk C:", "Проверка в режиме чтения"),
                    ("chkdsk C: /f /r", "Исправить ошибки и найти битые сектора (может требовать перезагрузку)"),
                    ("chkdsk D: /scan", "Онлайн-проверка без перезагрузки (NTFS)")),
                H("shutdown", "Перезагрузка и выключение",
                    ("shutdown /r /t 0", "Мгновенная перезагрузка"),
                    ("shutdown /s /t 60 /c \"Сохраните работу\"", "Выключение через 60 сек с сообщением"),
                    ("shutdown /r /t 60 /f", "Перезагрузка с принудительным закрытием приложений"),
                    ("shutdown /a", "Отменить запланированное выключение"),
                    ("shutdown /l", "Выход из текущего сеанса"),
                    ("shutdown /m \\\\server01 /r /t 0", "Перезагрузить удалённый ПК"),
                    ("shutdown /s /t 0", "Выключить компьютер сейчас"),
                    ("shutdown /g /t 0", "Перезагрузка с перезапуском зарегистрированных приложений")),
                H("powercfg", "Питание и энергопотребление",
                    ("powercfg /energy", "Отчёт об энергопотреблении (генерирует HTML)"),
                    ("powercfg /batteryreport", "Отчёт о батарее ноутбука"),
                    ("powercfg /a", "Доступные режимы сна"),
                    ("powercfg /hibernate on", "Включить режим гибернации"),
                    ("powercfg /sleepstudy", "Отчёт о проблемах пробуждения из сна")),
                H("msinfo32", "Сведения о системе (окно)",
                    ("msinfo32", "GUI-обзор железа и ПО, экспорт в .nfo"),
                    ("msinfo32 /report C:\\sysinfo.txt", "Сохранить текстовый отчёт сразу в файл")),
                H("dxdiag", "Диагностика DirectX",
                    ("dxdiag", "Видеокарта, звук, версии DirectX"),
                    ("dxdiag /x C:\\dx.xml", "Экспорт отчёта в XML для отправки в поддержку")),
                H("ver / hostname", "Версия и имя системы",
                    ("ver", "Версия Windows"),
                    ("hostname", "Имя компьютера"),
                    ("ver && hostname", "Версия и имя сразу одной строкой")),

                // === Пользователи и домен ===
                H("net user", "Учётные записи пользователей",
                    ("net user", "Список локальных пользователей"),
                    ("net user Ivanov", "Детали: когда сменит пароль, членство в группах"),
                    ("net user Ivanov /add Pass123", "Создать пользователя с паролем"),
                    ("net user Ivanov /active:no", "Заблокировать учётную запись"),
                    ("net user Ivanov /domain", "Данные доменного пользователя"),
                    ("net user Ivanov /expires:never", "Отключить истечение срока действия пароля")),
                H("net localgroup", "Локальные группы",
                    ("net localgroup administrators", "Кто входит в администраторы"),
                    ("net localgroup administrators Ivanov /add", "Дать пользователю права администратора"),
                    ("net localgroup", "Список всех локальных групп"),
                    ("net localgroup \"Remote Desktop Users\" Ivanov /add", "Разрешить пользователю подключаться по RDP")),
                H("net group", "Группы домена",
                    ("net group /domain", "Все группы домена"),
                    ("net group \"Domain Admins\" /domain", "Члены доменной группы"),
                    ("net group \"Domain Computers\" /domain", "Компьютерные учётные записи домена"),
                    ("net group /domain | findstr /i admins", "Быстрый поиск групп с 'admins' в имени")),
                H("net view", "Компьютеры сети и общие ресурсы",
                    ("net view", "Компьютеры рабочей группы или домена"),
                    ("net view \\\\server01", "Общие папки и принтеры хоста"),
                    ("net view /all", "Показывать и скрытые общие ресурсы ($)"),
                    ("net view /domain", "Список доменов в сети")),
                H("net use", "Сетевые диски",
                    ("net use", "Текущие сетевые подключения"),
                    ("net use Z: \\\\server01\\docs", "Подключить общую папку как диск Z:"),
                    ("net use Z: /delete", "Отключить сетевой диск"),
                    ("net use Z: \\\\server01\\docs /persistent:no", "Подключить без сохранения после перезагрузки"),
                    ("net use * \\\\server01\\docs", "Буква диска выберется автоматически"),
                    ("net use Z: \\\\server01\\docs /user:corp\\ivanov Pass123", "Подключение с явными учётными данными")),
                H("net session", "Активные сеансы на этом ПК",
                    ("net session", "Кто подключён к компьютеру по сети/RDP"),
                    ("net session | findstr /i tcp", "Только TCP-сессии (активные соединения)"),
                    ("net session \\\\client01", "Детали конкретной сессии с клиента")),
                H("gpresult", "Результат групповых политик",
                    ("gpresult /r", "Применённые политики в консоли"),
                    ("gpresult /h C:\\report.html", "Подробный HTML-отчёт для анализа"),
                    ("gpresult /r /scope:user", "Только пользовательские политики"),
                    ("gpresult /r /user Ivanov", "Политики для другого пользователя")),
                H("qwinsta / query user", "RDP-сессии",
                    ("qwinsta", "Сессии на этой машине: ID, имя, состояние"),
                    ("qwinsta /server:server01", "Сессии на удалённом ПК"),
                    ("query user /server:server01", "Кто залогинен на удалённом ПК"),
                    ("query session", "Все сессии, включая console и services")),
                H("tskill / logoff / tscon", "Управление сессиями",
                    ("tskill 2", "Завершить процессы сеанса по ID"),
                    ("logoff 1", "Разлогинить сеанс по ID"),
                    ("tscon 2 /dest:console", "Перенести сеанс ID 2 в консоль"),
                    ("tskill explorer.exe", "Завершить процессы по имени (например explorer)")),

                // === Файлы и каталоги ===
                H("cd / dir", "Навигация и список файлов",
                    ("cd C:\\Windows\\System32", "Перейти в каталог"),
                    ("cd ..", "На уровень вверх"),
                    ("dir", "Список файлов текущего каталога"),
                    ("dir /s /b *.log", "Все .log в подпапках полными путями"),
                    ("dir /a:h", "Только скрытые файлы"),
                    ("cd /d D:\\tools", "Сменить каталог вместе с диском"),
                    ("dir /w", "Широкий формат — больше имён в одну строку")),
                H("tree", "Дерево каталогов",
                    ("tree /f", "Дерево с файлами, наглядно для структуры"),
                    ("tree /a", "Дерево ASCII-символами — сохраняется в файл без искажений"),
                    ("tree C:\\temp /f > tree.txt", "Сохранить структуру папки в файл")),
                H("copy / xcopy / robocopy", "Копирование файлов и папок",
                    ("copy report.txt backup.txt", "Скопировать один файл"),
                    ("xcopy C:\\data D:\\backup /E /I", "Копия папки со всем содержимым"),
                    ("robocopy C:\\data D:\\backup /E /R:1 /W:1", "Надёжное копирование с повторными попытками"),
                    ("robocopy C:\\data D:\\backup /MIR", "Зеркалирование: удаляет лишнее на приёмнике"),
                    ("xcopy C:\\docs\\*.doc D:\\backup /S", "Копировать только .doc-файлы по всем подпапкам"),
                    ("robocopy C:\\data D:\\backup /LOG:backup.log", "Копирование с записью журнала в файл")),
                H("move", "Перемещение файлов",
                    ("move report.txt C:\\archive", "Переместить файл в другой каталог"),
                    ("move *.log C:\\logs", "Переместить все .log-файлы"),
                    ("move C:\\data D:\\archive", "Переместить целую папку")),
                H("ren", "Переименование",
                    ("ren report.txt report2026.txt", "Переименовать файл"),
                    ("ren *.tmp *.txt", "Массовая смена расширения у группы файлов")),
                H("del", "Удаление файлов",
                    ("del report.txt", "Удалить файл"),
                    ("del /s /q C:\\temp\\*.*", "Удалить всё в папке и подпапках без вопросов"),
                    ("del /f report.txt", "Удалить файл с атрибутом только для чтения"),
                    ("del /s *.tmp", "Удалить все .tmp в текущей папке и подпапках")),
                H("rmdir", "Удаление папок",
                    ("rmdir /s C:\\temp\\old", "Удалить папку со всем содержимым"),
                    ("rmdir empty_folder", "Удалить пустую папку (без /s)"),
                    ("rmdir /s /q C:\\temp\\old", "Удалить без запроса подтверждения")),
                H("mkdir", "Создание папок",
                    ("mkdir C:\\temp\\new", "Создать папку"),
                    ("mkdir a\\b\\c", "Создать вложенные папки одной командой"),
                    ("md C:\\temp\\new", "Короткий алиас той же команды")),
                H("type", "Просмотр содержимого файла",
                    ("type config.ini", "Вывести содержимое файла в консоль"),
                    ("type log.txt | more", "Постраничный просмотр длинного файла"),
                    ("type log.txt | findstr error", "Просмотр с фильтрацией по слову")),
                H("findstr", "Поиск текста в файлах",
                    ("findstr /i \"error\" log.txt", "Искать слово в файле без учёта регистра"),
                    ("findstr /s /i \"password\" *.config", "Искать по всем файлам в подпапках"),
                    ("findstr /v error log.txt", "Строки, в которых НЕТ слова error"),
                    ("findstr /i /r \"error.*timeout\" log.txt", "Поиск по регулярному выражению")),
                H("attrib", "Атрибуты файлов",
                    ("attrib +r report.txt", "Сделать файл только для чтения"),
                    ("attrib -h -s autorun.inf", "Снять скрытый и системный атрибуты"),
                    ("attrib +a file.txt", "Установить архивный атрибут"),
                    ("attrib /s +r C:\\docs\\*.doc", "Применить атрибут рекурсивно ко всем .doc")),
                H("where / start", "Поиск программы и запуск",
                    ("where notepad", "Полный путь к notepad.exe"),
                    ("start notepad", "Запустить программу, не блокируя терминал"),
                    ("start http://google.com", "Открыть URL в браузере по умолчанию"),
                    ("where /r C:\\ *.exe | findstr /i notepad", "Рекурсивный поиск exe-файла по диску"),
                    ("start /d C:\\Windows notepad", "Запустить программу из указанной папки")),
                H("| more / | sort / > file", "Пайпы и перенаправление",
                    ("dir /s /b | more", "Постраничный вывод длинного списка"),
                    ("dir /b | sort /r", "Сортировка вывода"),
                    ("ping 10.0.0.10 > ping.txt", "Сохранить вывод команды в файл"),
                    ("ping 10.0.0.10 >> ping.log", "Дописать вывод в конец файла"),
                    ("netstat -ano > netstat.txt", "Сохранить снимок соединений для анализа")),

                // === Диски и ресурсы ===
                H("wmic diskdrive", "Физические диски",
                    ("wmic diskdrive get model,size", "Модель и размер каждого диска"),
                    ("wmic diskdrive get serialnumber", "Серийные номера дисков"),
                    ("wmic diskdrive where \"mediaType='Fixed hard disk media'\" get size", "Размеры только жёстких дисков")),
                H("wmic logicaldisk", "Логические диски и место",
                    ("wmic logicaldisk get name,freespace,size", "Занято/свободно по разделам"),
                    ("wmic logicaldisk get caption,volumename", "Буквы и метки томов"),
                    ("wmic logicaldisk where 'freespace<10485760' get caption", "Диски со свободным местом меньше 10 МБ")),
                H("fsutil", "Свободное место на диске",
                    ("fsutil volume diskfree C:", "Быстрый отчёт по месту на C:"),
                    ("fsutil fsinfo volumeinfo C:", "Информация о томе: версия FS, флаги"),
                    ("fsutil fsinfo ntfsinfo C:", "Детали файловой системы NTFS (кластеры, MFT)")),
                H("defrag", "Дефрагментация",
                    ("defrag C: /U /V", "Дефрагментация с подробным отчётом"),
                    ("defrag D: /O", "Оптимизация (дефрагментация или trim для SSD)"),
                    ("defrag C: /U", "Дефрагментация с кратким отчётом о результате")),
                H("cleanmgr", "Очистка диска",
                    ("cleanmgr", "Мастер очистки диска"),
                    ("cleanmgr /sagerun:1", "Тихая очистка с ранее сохранёнными настройками"),
                    ("cleanmgr /lowdisk", "Сразу открыть проверку на освобождение места"),
                    ("cleanmgr /sageset:1", "Настроить профиль очистки (что удалять)")),
                H("reg", "Работа с реестром",
                    ("reg query \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\"", "Прочитать ветку реестра"),
                    ("reg add \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run\" /v App /t REG_SZ /d \"C:\\app.exe\"", "Добавить программу в автозагрузку"),
                    ("reg delete \"HKCU\\SOFTWARE\\Test\" /f", "Удалить ветку реестра без подтверждения"),
                    ("reg export \"HKCU\\SOFTWARE\\Test\" C:\\backup.reg", "Экспортировать ветку в файл"),
                    ("reg import C:\\backup.reg", "Импортировать ветку из файла"),
                    ("reg query \"HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\" /v ProductName", "Прочитать одно конкретное значение")),

                // === Оснастки и системные окна ===
                H("compmgmt.msc", "Управление компьютером (окно)",
                    ("compmgmt.msc", "Диски, службы, планировщик, общие папки"),
                    ("compmgmt.msc /computer:server01", "Управление удалённым ПК (если есть права)")),
                H("diskmgmt.msc", "Управление дисками (окно)",
                    ("diskmgmt.msc", "Разделы, буквы дисков, том"),
                    ("runas /user:Administrator \"diskmgmt.msc\"", "Открыть с правами администратора")),
                H("devmgmt.msc", "Диспетчер устройств (окно)",
                    ("devmgmt.msc", "Устройства и драйверы"),
                    ("devmgmt.msc /d", "Сортировка устройств по типу"),
                    ("devmgmt.msc /s", "Сортировка устройств по подключению")),
                H("services.msc", "Службы Windows (окно)",
                    ("services.msc", "Полный список служб со статусом")),
                H("eventvwr.msc", "Просмотр событий (окно)",
                    ("eventvwr.msc", "Журналы Windows: системный, приложения, безопасности"),
                    ("eventvwr.msc /c:application", "Сразу открыть журнал приложений")),
                H("secpol.msc", "Локальная политика безопасности (окно)",
                    ("secpol.msc", "Политики паролей, блокировки, аудита")),
                H("regedit", "Редактор реестра (окно)",
                    ("regedit", "Визуальная работа с реестром"),
                    ("regedit /e C:\\backup.reg \"HKCU\\SOFTWARE\\Test\"", "Экспорт ветки через редактор реестра")),

                // === Печать и драйверы ===
                H("print", "Печать файла",
                    ("print /d:LPT1 report.txt", "Печать текстового файла на LPT1"),
                    ("print /d:USB001 report.txt", "Печать на USB-принтер")),
                H("wmic printer", "Установленные принтеры",
                    ("wmic printer list brief", "Принтеры и их статус"),
                    ("wmic printer get name,default", "Какой принтер используется по умолчанию"),
                    ("wmic printer where name='HP LaserJet' call setdefaultprinter", "Сделать принтером по умолчанию")),
                H("rundll32 printui", "Окно со списком принтеров",
                    ("rundll32 printui.dll,PrintUIEntry /il", "Открыть список принтеров"),
                    ("rundll32 printui.dll,PrintUIEntry /e", "Открыть окно управления принтерами")),
                H("driverquery", "Список драйверов",
                    ("driverquery /v", "Все драйверы подробно"),
                    ("driverquery /fo csv", "Вывод в CSV для отчёта"),
                    ("driverquery /si", "Список неподписанных драйверов")),
                H("pnputil", "Пакеты драйверов",
                    ("pnputil /enum-drivers", "Установленные пакеты драйверов"),
                    ("pnputil /add-driver C:\\drv.inf /install", "Установить драйвер из INF-файла"),
                    ("pnputil /delete-driver oem0.inf", "Удалить пакет драйвера")),

                // === Диагностика и мониторинг ===
                H("perfmon / resmon", "Мониторинг производительности",
                    ("perfmon", "Монитор производительности (окно)"),
                    ("resmon", "Монитор ресурсов: сеть, диск, память (окно)"),
                    ("perfmon /report", "Полный отчёт о производительности системы")),
                H("taskmgr", "Диспетчер задач",
                    ("taskmgr", "Запустить диспетчер задач"),
                    ("taskmgr /startup", "Сразу открыть вкладку автозагрузки")),
                H("control / sysdm.cpl / winver", "Панель управления и свойства системы",
                    ("control", "Панель управления"),
                    ("sysdm.cpl", "Свойства системы: имя ПК, домен, удалённый доступ"),
                    ("winver", "Версия Windows в окне"),
                    ("sysdm.cpl,4", "Сразу вкладка удалённого доступа (RDP)")),
                H("net statistics / net config", "Статистика и конфигурация станции",
                    ("net statistics workstation", "Статистика трафика рабочей станции"),
                    ("net config workstation", "Конфигурация: домен, имя пользователя, кэш"),
                    ("net statistics server", "Статистика серверной части (если роль установлена)")),
                H("tcpview", "Мониторинг соединений (если установлен)",
                    ("tcpview", "Живой список TCP/UDP соединений")),
            },
            ["POWERSHELL"] = new[]
            {
                // === Процессы и службы ===
                H("Get-Process", "Список процессов",
                    ("Get-Process", "Список всех процессов с CPU и памятью"),
                    ("Get-Process | Sort-Object CPU -Descending | Select -First 10", "Топ-10 процессов по нагрузке CPU"),
                    ("Get-Process notepad", "Информация по конкретному процессу"),
                    ("Get-Process | Where-Object CPU -gt 100", "Процессы с CPU больше 100 сек"),
                    ("Get-Process | Select-Object Name, Id, WorkingSet -First 5", "Выбор нужных колонок для первых 5")),
                H("Stop-Process", "Завершить процесс",
                    ("Stop-Process -Name chrome -Force", "Завершить все процессы chrome"),
                    ("Get-Process explorer | Stop-Process", "Завершить через конвейер"),
                    ("Stop-Process -Id 8420 -Force", "Завершить по ID процесса"),
                    ("Stop-Process -Name notepad -WhatIf", "Проверить, что было бы остановлено")),
                H("Get-Service", "Список служб",
                    ("Get-Service", "Все службы и их статус"),
                    ("Get-Service | Where Status -eq 'Running'", "Только запущенные службы"),
                    ("Get-Service | Where DisplayName -match 'print'", "Найти службы по слову в имени"),
                    ("Get-Service Spooler | Select Status, StartType", "Статус и тип запуска службы"),
                    ("Get-Service | Where StartType -eq 'Manual'", "Службы с ручным запуском")),
                H("Start/Stop/Restart-Service", "Управление службами",
                    ("Restart-Service Spooler", "Перезапустить службу печати"),
                    ("Start-Service Spooler", "Запустить службу"),
                    ("Set-Service Spooler -StartupType Automatic", "Служба при старте — авто"),
                    ("Get-Service Spooler | Restart-Service", "Перезапуск через конвейер"),
                    ("Restart-Service Spooler -Force", "Перезапуск без учёта зависимостей"),
                    ("Stop-Service Spooler", "Остановить службу")),

                // === Сеть ===
                H("Test-Connection", "Проверка доступности (ping)",
                    ("Test-Connection 10.0.0.10", "Пинг хоста, 4 пакета"),
                    ("Test-Connection google.com -Count 10", "10 пакетов"),
                    ("Test-Connection server01 -Quiet", "Возвращает True/False — удобно в скриптах"),
                    ("Test-Connection 10.0.0.10 -Count 4 -Delay 1", "4 пакета с интервалом 1 сек"),
                    ("Test-Connection 10.0.0.10 -Source server01", "Пинг с другого источника (если настроено)")),
                H("Test-NetConnection", "Проверка портов",
                    ("Test-NetConnection 10.0.0.10 -Port 3389", "Проверить, открыт ли RDP-порт"),
                    ("Test-NetConnection google.com -Port 443", "Проверить доступность HTTPS"),
                    ("Test-NetConnection google.com", "Полная диагностика: DNS, маршрут, TCP"),
                    ("Test-NetConnection 10.0.0.10 -Port 445 -InformationLevel Detailed", "Подробная проверка SMB-порта")),
                H("Get-NetIPAddress", "IP-адреса интерфейсов",
                    ("Get-NetIPAddress | Select IPAddress, InterfaceAlias", "IP всех интерфейсов"),
                    ("Get-NetIPAddress -AddressFamily IPv4", "Только IPv4-адреса"),
                    ("Get-NetIPAddress -InterfaceAlias Ethernet", "IP только сетевой карты Ethernet"),
                    ("Get-NetIPAddress 10.0.0.15 | Format-List", "Все свойства одного адреса")),
                H("Get-NetAdapter", "Сетевые адаптеры",
                    ("Get-NetAdapter", "Адаптеры и их состояние (up/down)"),
                    ("Get-NetAdapter | Where Status -eq 'Up'", "Только активные адаптеры"),
                    ("Get-NetAdapter | Select Name, MacAddress, LinkSpeed", "MAC и скорость соединения")),
                H("Resolve-DnsName", "DNS-запрос (nslookup)",
                    ("Resolve-DnsName server01", "IP-адрес по имени хоста"),
                    ("Resolve-DnsName domain.local -Type MX", "MX-записи почтового домена"),
                    ("Resolve-DnsName -Name server01 -Server 10.0.0.1", "Запрос на конкретный DNS-сервер"),
                    ("Resolve-DnsName server01 -Type A", "Только A-записи")),
                H("Get-NetTCPConnection", "TCP-соединения",
                    ("Get-NetTCPConnection -State Established", "Активные установленные соединения"),
                    ("Get-NetTCPConnection -LocalPort 3389", "Кто подключён к RDP сейчас"),
                    ("Get-NetTCPConnection -OwningProcess 8420", "Соединения конкретного процесса по PID"),
                    ("Get-NetTCPConnection | Where RemoteAddress -notin @('0.0.0.0','::')", "Внешние соединения")),
                H("Get/New-NetFirewallRule", "Брандмауэр Windows",
                    ("Get-NetFirewallRule | Select DisplayName", "Все правила брандмауэра"),
                    ("New-NetFirewallRule -DisplayName 'RDP' -Direction Inbound -Protocol TCP -RemotePort 3389 -Action Allow", "Открыть порт 3389 входящим"),
                    ("New-NetFirewallRule -DisplayName 'Allow 8080 out' -Direction Outbound -Action Allow -Protocol TCP -RemotePort 8080", "Разрешить исходящие на порт 8080"),
                    ("Remove-NetFirewallRule -DisplayName 'RDP'", "Удалить созданное правило")),
                H("Get/Clear-DnsClientCache", "DNS-кэш клиента",
                    ("Get-DnsClientCache", "Показать содержимое DNS-кэша"),
                    ("Clear-DnsClientCache", "Очистить DNS-кэш"),
                    ("Get-DnsClientCache | Where Entry -match 'corp'", "Записи кэша по корпоративному домену")),
                H("Get-NetRoute", "Таблица маршрутов",
                    ("Get-NetRoute", "Все маршруты системы"),
                    ("Get-NetRoute -DestinationPrefix 10.0.0.0/16", "Маршруты до конкретной подсети"),
                    ("Get-NetRoute | Sort RouteMetric", "Маршруты, отсортированные по метрике")),

                // === Система ===
                H("Get-ComputerInfo", "Информация о системе",
                    ("Get-ComputerInfo", "Все данные: ОС, память, железо"),
                    ("Get-ComputerInfo | Select WindowsProductName, WindowsVersion", "Название и версия Windows"),
                    ("Get-ComputerInfo | Select TotalPhysicalMemory, CsProcessors", "Память и процессоры")),
                H("Get-CimInstance Win32_*", "Запросы WMI (CIM)",
                    ("Get-CimInstance Win32_OperatingSystem", "Версия и билд ОС"),
                    ("Get-CimInstance Win32_ComputerSystem | Select UserName", "Кто залогинен на ПК"),
                    ("Get-CimInstance Win32_Processor", "Процессор и его частота"),
                    ("Get-CimInstance Win32_PhysicalMemory", "Планки памяти"),
                    ("Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3'", "Локальные диски и место"),
                    ("Get-CimInstance Win32_VideoController | Select Name", "Видеокарта"),
                    ("Get-CimInstance Win32_ComputerSystem -ComputerName server01", "Данные удалённого ПК (WinRM)"),
                    ("Get-CimInstance Win32_NetworkAdapterConfiguration | Where IPEnabled | Select IPAddress, MACAddress", "IP и MAC всех активных адаптеров")),
                H("Get-HotFix", "Установленные обновления",
                    ("Get-HotFix | Sort InstalledOn -Descending | Select -First 10", "Последние 10 обновлений"),
                    ("Get-HotFix -Id KB5000000", "Информация по конкретному обновлению"),
                    ("Get-HotFix | Select HotFixID, InstalledOn", "Короткий список: KB и дата установки")),
                H("Restart/Stop-Computer", "Перезагрузка и выключение",
                    ("Restart-Computer -Force", "Перезагрузка с принудительным закрытием"),
                    ("Restart-Computer -ComputerName server01 -Force", "Перезагрузить удалённый ПК (WinRM)"),
                    ("Stop-Computer -Force", "Выключить компьютер"),
                    ("Restart-Computer -ComputerName (Get-Content servers.txt) -Force", "Перезагрузить все ПК из списка"),
                    ("Stop-Computer -ComputerName server01 -Credential $cred", "Выключить удалённый ПК с учётными данными")),
                H("Get-Uptime / Get-Date", "Время работы и дата",
                    ("Get-Uptime", "Сколько работает система без перезагрузки"),
                    ("Get-Date", "Текущие дата и время"),
                    ("Get-Uptime | Select Days, Hours, Minutes", "Время работы по компонентам"),
                    ("Get-Date -Format 'yyyy-MM-dd HH:mm'", "Дата и время в нужном формате")),
                H("Get/Set-Clipboard", "Буфер обмена",
                    ("Get-Clipboard", "Показать содержимое буфера обмена"),
                    ("Set-Clipboard -Value 'Hello'", "Записать текст в буфер обмена"),
                    ("Get-Clipboard | Set-Content clip.txt", "Сохранить буфер обмена в файл"),
                    ("Get-Content file.txt | Set-Clipboard", "Скопировать содержимое файла в буфер")),
                H("Get-Volume / Get-PSDrive", "Диски и тома",
                    ("Get-Volume", "Все тома и свободное место"),
                    ("Get-PSDrive -PSProvider FileSystem", "Диски как их видит PowerShell"),
                    ("Get-Volume | Where DriveLetter", "Тома с буквами дисков (без служебных)")),

                // === Пользователи и домен ===
                H("Get-LocalUser", "Локальные пользователи",
                    ("Get-LocalUser", "Все локальные учётные записи"),
                    ("Get-LocalUser | Where Enabled", "Только активные аккаунты"),
                    ("Get-LocalUser | Select Name, LastLogon, PasswordLastSet", "Детали по последним входам"),
                    ("Get-LocalUser Administrator | Select Enabled, UserMayChangePassword", "Проверка аккаунта Administrator")),
                H("Get-LocalGroupMember", "Члены локальных групп",
                    ("Get-LocalGroupMember Administrators", "Кто входит в администраторы"),
                    ("Get-LocalGroupMember Administrators | Select Name, ObjectClass", "Члены группы и их тип (User/Group)"),
                    ("Get-LocalGroupMember 'Remote Desktop Users'", "Кому разрешён RDP")),
                H("Get-ADUser / Get-ADComputer / Get-ADGroup", "Active Directory",
                    ("Get-ADUser Ivanov -Properties *", "Данные доменного пользователя"),
                    ("Get-ADUser -Filter 'Enabled -eq true' -SearchBase 'OU=Users,DC=corp,DC=local'", "Активные пользователи из конкретной OU"),
                    ("Get-ADComputer server01", "Компьютерная учётная запись"),
                    ("Get-ADGroup 'RDP Users' -Properties Members", "Члены AD-группы"),
                    ("Get-ADUser Ivanov -Properties LastLogonDate | Select Name, LastLogonDate", "Когда пользователь входил последний раз"),
                    ("Get-ADGroupMember 'Domain Admins'", "Члены доменной группы админов")),
                H("Enter-PSSession / Invoke-Command", "Удалённое управление (WinRM)",
                    ("Enter-PSSession server01", "Интерактивная PS-сессия на хосте"),
                    ("Invoke-Command -ComputerName server01 { Get-Service }", "Выполнить команду на хосте"),
                    ("Invoke-Command -ComputerName server01 { Restart-Service Spooler }", "Перезапустить службу удалённо"),
                    ("qwinsta /server:server01", "RDP-сессии на удалённом хосте"),
                    ("Invoke-Command -ComputerName (Get-Content servers.txt) { Get-HotFix | Select -First 3 }", "Выполнить команду на группе хостов"),
                    ("Invoke-Command -ComputerName server01 -Credential $cred { Get-Process }", "Подключение к хосту с явными правами")),

                // === Файлы и каталоги ===
                H("Get-ChildItem", "Список файлов (dir)",
                    ("Get-ChildItem", "Список файлов текущего каталога"),
                    ("Get-ChildItem C:\\temp -Recurse", "Все файлы в подпапках"),
                    ("Get-ChildItem C:\\temp\\*.txt", "Только txt-файлы"),
                    ("Get-ChildItem -Force", "Включая скрытые файлы"),
                    ("Get-ChildItem | Where Length -gt 1MB", "Файлы размером больше 1 МБ"),
                    ("Get-ChildItem C:\\temp -Directory", "Только папки, без файлов"),
                    ("Get-ChildItem -Recurse -Filter *.log | Select FullName, Length", "Все .log-файлы с путями и размером")),
                H("Set-Location", "Сменить каталог",
                    ("Set-Location C:\\Windows\\System32", "Перейти в каталог"),
                    ("Set-Location ..", "На уровень вверх"),
                    ("Set-Location $env:USERPROFILE", "Перейти в домашнюю папку"),
                    ("cd D:\\tools", "Алиас cd — короткая запись")),
                H("Copy-Item", "Копирование",
                    ("Copy-Item report.txt backup.txt", "Скопировать файл"),
                    ("Copy-Item C:\\data D:\\backup -Recurse", "Скопировать папку со всем содержимым"),
                    ("Copy-Item *.txt C:\\backup", "Скопировать группу файлов по маске"),
                    ("Copy-Item report.txt backup.txt -Verbose", "Копирование с подробным выводом")),
                H("Move-Item / Rename-Item", "Перемещение и переименование",
                    ("Move-Item report.txt C:\\archive", "Переместить файл"),
                    ("Rename-Item report.txt report2026.txt", "Переименовать файл"),
                    ("Move-Item *.log C:\\logs", "Переместить все .log-файлы"),
                    ("Get-ChildItem *.tmp | Rename-Item -NewName { $_.Name -replace '.tmp','.txt' }", "Массовое переименование через регулярное выражение")),
                H("Remove-Item", "Удаление",
                    ("Remove-Item report.txt", "Удалить файл"),
                    ("Remove-Item C:\\temp\\old -Recurse -Force", "Удалить папку целиком без вопросов"),
                    ("Remove-Item *.log", "Удалить группу файлов по маске"),
                    ("Remove-Item C:\\temp -Recurse -Confirm:$false", "Удалить без запроса подтверждения")),
                H("New-Item", "Создание папок и файлов",
                    ("New-Item -ItemType Directory -Path C:\\temp\\new", "Создать папку"),
                    ("New-Item -ItemType File -Path C:\\temp\\a.txt", "Создать пустой файл"),
                    ("New-Item -ItemType File -Name config.ini -Value 'setting=1'", "Создать файл сразу с содержимым"),
                    ("New-Item -ItemType Directory -Path C:\\backup\\2026", "Создать вложенные папки")),
                H("Get-Content", "Просмотр файла",
                    ("Get-Content config.ini", "Вывести содержимое файла"),
                    ("Get-Content log.txt -Tail 50", "Последние 50 строк лога"),
                    ("Get-Content log.txt -Wait", "Следить за логом в реальном времени"),
                    ("Get-Content log.txt -TotalCount 10", "Первые 10 строк файла"),
                    ("Get-Content log.txt -Encoding UTF8", "Чтение файла в нужной кодировке")),
                H("Select-String", "Поиск текста в файлах",
                    ("Select-String -Path *.log -Pattern 'error'", "Найти 'error' во всех логах"),
                    ("Select-String -Path log.txt -Pattern 'ERROR' -Context 2", "Поиск с 2 строками контекста до и после"),
                    ("Get-Content log.txt | Select-String 'timeout'", "Поиск через конвейер")),
                H("Get-FileHash", "Хеширование файлов",
                    ("Get-FileHash install.exe", "SHA256-хеш файла"),
                    ("Get-FileHash install.exe -Algorithm MD5", "MD5-хеш для сверки"),
                    ("Get-ChildItem *.exe | Get-FileHash", "Хеши всех exe-файлов в папке"),
                    ("Get-FileHash file.exe -Algorithm SHA1", "SHA1-хеш файла")),
                H("Get-Item", "Свойства файла",
                    ("Get-Item report.txt | Select Length, LastWriteTime", "Размер и дата изменения"),
                    ("Get-Item report.txt | Format-List *", "Все свойства файла"),
                    ("Get-Item C:\\temp", "Свойства папки")),

                // === Вывод и форматирование ===
                H("Get-History / Clear-History", "История команд",
                    ("Get-History", "История команд текущей сессии"),
                    ("Clear-History", "Очистить историю команд"),
                    ("Get-History -Count 5", "Последние 5 команд"),
                    ("Get-History | Select CommandLine | Out-File hist.txt", "Сохранить историю в файл")),
                H("Get-Command", "Поиск команд",
                    ("Get-Command -Name *service*", "Найти все команды со словом service"),
                    ("Get-Command Get-*", "Все команды, начинающиеся с Get-"),
                    ("Get-Command -ParameterName ComputerName", "Команды, поддерживающие параметр ComputerName")),
                H("Get-Help", "Справка по командам",
                    ("Get-Help Get-Process", "Справка по команде"),
                    ("Get-Help Get-Process -Examples", "Только примеры использования"),
                    ("Get-Help Get-Process -Full", "Полная справка со всеми параметрами"),
                    ("Get-Help about_Arrays", "Справка по концепциям (about_*)")),
                H("Get-Alias / Clear-Host", "Алиасы и очистка",
                    ("Get-Alias", "Все алиасы (gps = Get-Process, dir = Get-ChildItem)"),
                    ("Clear-Host", "Очистить экран"),
                    ("Get-Alias dir", "Что скрывается за алиасом dir"),
                    ("Get-Alias | Where Name -like 'g*'", "Алиасы, начинающиеся на 'g'")),
                H("Format/Select/Export", "Форматирование вывода",
                    ("Get-Process | Format-Table -AutoSize", "Таблица по ширине окна"),
                    ("Get-Process | Select-Object Name, CPU -First 10", "Первые 10 записей по нужным колонкам"),
                    ("Get-Service | Export-Csv services.csv -NoTypeInformation", "Экспорт списка служб в CSV"),
                    ("Get-Process | Out-GridView", "Результат в отдельном окне-таблице"),
                    ("Get-Process > processes.txt", "Перенаправить вывод в файл"),
                    ("Get-Service * 2>&1 | Out-File err.txt", "Записать в файл вместе с ошибками"),
                    ("Get-Service | ConvertTo-Json | Out-File services.json", "Экспорт списка служб в JSON"),
                    ("Get-Process | Export-Clixml procs.xml", "Экспорт с сохранением структуры (Clixml)")),
                H("Get-WinEvent / Get-EventLog", "Журналы событий",
                    ("Get-WinEvent -LogName Security -MaxEvents 20", "Последние события безопасности"),
                    ("Get-WinEvent -FilterHashtable @{LogName='System';Level=2} -MaxEvents 20", "Критические ошибки системы"),
                    ("Get-EventLog System -Newest 20", "Последние 20 событий журнала"),
                    ("Get-WinEvent -LogName System -MaxEvents 100 | Where TimeCreated -gt (Get-Date).AddDays(-1)", "События за последние сутки"),
                    ("Get-WinEvent -FilterHashtable @{LogName='Application';Level=3} -MaxEvents 50", "Предупреждения и ошибки приложений")),

                // === Пакеты, политики и задачи ===
                H("winget", "Управление приложениями",
                    ("winget list", "Установленные приложения"),
                    ("winget search chrome", "Поиск приложения в репозитории"),
                    ("winget install Google.Chrome", "Установить приложение по ID"),
                    ("winget upgrade --all", "Обновить все установленные приложения"),
                    ("winget show Google.Chrome", "Информация о приложении перед установкой"),
                    ("winget uninstall Google.Chrome", "Удалить приложение")),
                H("Get/Set-ExecutionPolicy", "Политика выполнения скриптов",
                    ("Get-ExecutionPolicy", "Текущая политика (обычно Restricted)"),
                    ("Set-ExecutionPolicy RemoteSigned -Scope CurrentUser", "Разрешить локальные скрипты"),
                    ("Get-ExecutionPolicy -List", "Политики по всем областям действия"),
                    ("Set-ExecutionPolicy Bypass -Scope Process", "Разрешить скрипты только на текущую сессию")),
                H("Get-Credential", "Запрос учётных данных",
                    ("$cred = Get-Credential", "Сохранить логин/пароль в переменную"),
                    ("$cred = Get-Credential; Get-CimInstance Win32_ComputerSystem -ComputerName server01 -Credential $cred", "Запросить данные и подключиться к удалённому ПК"),
                    ("Get-Credential -UserName corp\\ivanov", "Запрос пароля с заранее подставленным логином")),
                H("Get-ScheduledTask", "Задания по расписанию",
                    ("Get-ScheduledTask | Where State -eq 'Running'", "Сейчас запущенные задания"),
                    ("Get-ScheduledTask | Where TaskName -match 'update'", "Задания с 'update' в имени"),
                    ("Get-ScheduledTask | Select TaskName, State", "Список заданий и их состояния"),
                    ("Start-ScheduledTask -TaskName 'Defrag'", "Запустить задание вручную")),
                H("Get-ItemProperty Uninstall", "Установленные программы",
                    ("Get-ItemProperty 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\*' | Select DisplayName", "Список установленных программ из реестра"),
                    ("Get-ItemProperty 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\*' | Select DisplayName, DisplayVersion, InstallDate", "Список программ с версиями и датами"),
                    ("Get-ItemProperty 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\*' | Where DisplayName -match 'Office'", "Найти программы по слову в имени")),
                H("Get-CimInstance Win32_Process/Service", "Пути процессов и служб",
                    ("Get-CimInstance Win32_Process | Select Name, ExecutablePath", "Процессы и пути к их exe"),
                    ("Get-CimInstance Win32_Service | Select Name, PathName", "Службы и пути к их exe"),
                    ("Get-CimInstance Win32_Process | Where Name -eq 'explorer.exe'", "Процессы с именем explorer.exe"),
                    ("Get-CimInstance Win32_Service -Filter 'State=\"Running\"'", "Только запущенные службы")),
                H("Get-Process explorer | Select Path", "Путь к процессу",
                    ("Get-Process explorer | Select Path", "Полный путь к explorer.exe"),
                    ("Get-Process | Where Name -eq 'explorer' | Select Path", "Тот же поиск через Where-Object"),
                    ("(Get-Process -Id $pid).Path", "Путь к текущему процессу PowerShell")),
            }
        };

        private readonly ScrolllessListBox _hintsList;
        private readonly Panel _hintsPanel;
        private readonly Label _showHintsBtn;
        private int _hintExampleSel = -1;
        private int _hintExampleSelItem = -1;
        private readonly HintWheelFilter _wheelFilter;

        public string SessionTitle { get; }
        public string? WelcomeText { get; set; }
        public event EventHandler? SessionClosed;

        public ConsoleSessionControl(string exePath, string arguments, string title,
            Encoding? outputEncoding = null, Color? accentColor = null)
        {
            _exePath = exePath;
            _arguments = arguments;
            SessionTitle = title;
            _outputEncoding = outputEncoding ?? Encoding.UTF8;
            _accentColor = accentColor ?? Color.FromArgb(0x3C, 0x3C, 0x3C);
            SuspendLayout();

            BackColor = Color.FromArgb(0x1E, 0x1E, 0x1E);
            Dock = DockStyle.Fill;

            // === Layout: dark output on top, floating input BOX at bottom ===
            // Matches reference term.png: dark #0A0A0A surround, a lighter
            // #1E1E1E input box with a thick accent left border, margins on
            // all sides. Box is ~60% width, aligned left.
            // DOCK RULE: the LAST-added control docks FIRST, so add the
            // Fill control first, then the edge strips after it.

            // Right content panel (output + bottom surround area).
            var contentPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(0x0A, 0x0A, 0x0A)
            };

            // Output — RichTextBox (Dock.Fill, darker bg like the reference).
            // Native scrollbar is hidden (ConsoleOutputBox) — the themed
            // ThemedVScrollBar on the right drives the scrolling instead.
            _output = new ConsoleOutputBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(0x0A, 0x0A, 0x0A),
                ForeColor = Color.FromArgb(0xEE, 0xEE, 0xEE),
                Font = new Font("Consolas", 12F),
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                WordWrap = false,
                DetectUrls = false,
                ShortcutsEnabled = true,
                AcceptsTab = false
            };
            _output.KeyDown += OutputKeyDown;

            // Output host — pads the left edge: 90px aligned with the old input
            // text position; moved left 20px (90→70) to sit near the box border.
            var outputHost = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(0x0A, 0x0A, 0x0A),
                Padding = new Padding(60, 15, 0, 0)
            };
            outputHost.Controls.Add(_output);

            _lineHeight = _output.Font.Height + 2;
            _scrollBar = new ThemedVScrollBar(
                OutputFirstLine,
                OutputTotalLines,
                OutputVisibleLines,
                ScrollOutputTo);
            _output.Scrolled += (s, e) => RefreshScrollBar();
            outputHost.Controls.Add(_scrollBar);
            outputHost.Resize += (s, e) => RefreshScrollBar();
            contentPanel.Controls.Add(outputHost);

            // Wheel over the output scrolls it even when the input box has
            // focus (Windows routes the wheel to the focused window, not the
            // control under the cursor).
            _termWheelFilter = new TerminalWheelFilter(_output, ScrollOutputLines);
            Application.AddMessageFilter(_termWheelFilter);
            Disposed += (s, e) => Application.RemoveMessageFilter(_termWheelFilter);
            // === Bottom dark surround area that hosts the floating box. ===
            var bottomArea = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 162,
                BackColor = Color.FromArgb(0x0A, 0x0A, 0x0A)
            };

            // The input box — a distinct lighter field, positioned manually.
            var boxPanel = new Panel
            {
                BackColor = Color.FromArgb(0x1E, 0x1E, 0x1E)
            };
            bottomArea.Controls.Add(boxPanel);

            // Left accent border — 6px (double the previous 3px), spans box height.
            var accentStrip = new Panel
            {
                Dock = DockStyle.Left,
                Width = 6,
                BackColor = _accentColor
            };

            // Status bar (bottom of the box), all text gray.
            string kind = title.Contains("Power", StringComparison.OrdinalIgnoreCase) ? "POWERSHELL" : "CMD";
            _statusTitle = "Pup Seek RDP";
            _statusVersion = " v3.5.8";
            _statusKind = kind;
            _historyKey = kind;
            LoadHistory();
            _statusPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 18,
                BackColor = Color.FromArgb(0x1E, 0x1E, 0x1E)
            };
            _statusPanel.Paint += StatusPanel_Paint;
            _statusPanel.MouseDown += StatusPanel_MouseDown;
            _statusPanel.MouseMove += StatusPanel_MouseMove;
            _statusPanel.MouseLeave += (s, e) =>
            {
                _statusCloseHover = false;
                _statusPanel.Cursor = Cursors.Default;
                _statusPanel.Invalidate();
            };

            // 10px spacer below the status text — raises it from the box bottom.
            var spacerBottom = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 10,
                BackColor = Color.FromArgb(0x1E, 0x1E, 0x1E)
            };

            // 20px spacer above the input — pushes the (vertically centered)
            // input line down by 10px.
            var spacerTop = new Panel
            {
                Dock = DockStyle.Top,
                Height = 20,
                BackColor = Color.FromArgb(0x1E, 0x1E, 0x1E)
            };

            // Input TextBox — fills the box; internal padding keeps text off
            // the edges (26px left = 16 + 10px caret shift, 8px top).
            _input = new CaretTextBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(0x1E, 0x1E, 0x1E),
                ForeColor = Color.FromArgb(0xEE, 0xEE, 0xEE),
                Font = new Font("Consolas", 14F),
                BorderStyle = BorderStyle.None,
                Padding = new Padding(26, 8, 16, 4),
                CaretWidth = 2,
                Cursor = Cursors.IBeam
            };
            _input.KeyDown += InputKeyDown;

            // ADD ORDER inside the box (Fill first, edge strips after).
            // Last added docks first: accentStrip (Left) -> spacerTop -> spacerBottom
            // -> statusPanel -> input (Fill).
            boxPanel.Controls.Add(_input);       // Fill
            boxPanel.Controls.Add(_statusPanel); // Bottom
            boxPanel.Controls.Add(spacerBottom); // Bottom — docks above status
            boxPanel.Controls.Add(spacerTop);    // Top    — docks above bottom, below border
            boxPanel.Controls.Add(accentStrip);  // Left   — docks first, spans box height

            // Position the floating box: 48px left margin (x3), 12px top margin,
            // 30px bottom margin (x3), stretched to 30px before the right hints
            // panel, aligned left.
            // Box height = bottomArea(162) - top(12) - bottomGap(30) = 120px (x2).
            void LayoutBox()
            {
                if (bottomArea.ClientSize.Width <= 0) return;
                int left = 48;
                int top = 12;
                int bottomGap = 30;
                int rightGap = 30;
                int w = bottomArea.ClientSize.Width - left - rightGap;
                int h = bottomArea.ClientSize.Height - top - bottomGap;
                boxPanel.SetBounds(left, top, w, h);
            }
            bottomArea.Resize += (s, e) => LayoutBox();
            bottomArea.Layout += (s, e) => LayoutBox();
            LayoutBox();

            // ADD ORDER on content panel (Fill first, bottom box after).
            contentPanel.Controls.Add(bottomArea);

            // === Right hints panel: offline command dictionary. ===
            // Same bg as the input box (#1E1E1E); muted gray text so it doesn't
            // distract from the terminal. Filters by what's typed in the input.
            _hintsPanel = new Panel
            {
                Dock = DockStyle.Right,
                Width = 600,
                BackColor = Color.FromArgb(0x14, 0x14, 0x14),
                Padding = new Padding(20, 20, 20, 30)
            };
            var hintsTitle = new Label
            {
                Dock = DockStyle.Top,
                Height = 34,
                Text = "Подсказки",
                ForeColor = Color.FromArgb(0x80, 0x80, 0x80),
                BackColor = Color.FromArgb(0x14, 0x14, 0x14),
                Font = new Font("Segoe UI Semibold", 12F),
                TextAlign = ContentAlignment.MiddleLeft
            };
            _hintNameH = TextRenderer.MeasureText("Ag", _hintNameFont).Height;
            _hintDescH = TextRenderer.MeasureText("Ag", _hintDescFont).Height;
            _hintExampleH = TextRenderer.MeasureText("Ag", _hintExampleFont).Height;
            _hintNoteH = TextRenderer.MeasureText("Ag", _hintNoteFont).Height;
            _hintsList = new ScrolllessListBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(0x14, 0x14, 0x14),
                ForeColor = Color.FromArgb(0xA0, 0xA0, 0xA0),
                Font = new Font("Consolas", 11.5F),
                BorderStyle = BorderStyle.None,
                DrawMode = DrawMode.OwnerDrawVariable,
                ItemHeight = _hintNameH + _hintGap + _hintDescH + _hintCardPadTop + _hintCardPadBottom,
                IntegralHeight = false,
                HorizontalScrollbar = false
            };
            _hintsList.DrawItem += HintsListDrawItem;
            _hintsList.MeasureItem += HintsListMeasureItem;
            _hintsList.MouseDown += HintsListMouseDown;
            _hintsList.MouseDoubleClick += HintsListDoubleClick;
            _hintsList.KeyDown += HintsListKeyDown;
            _hintsList.SelectedIndexChanged += (s, e) =>
            {
                _hintExampleSel = -1;
                _hintExampleSelItem = -1;
            };
            _hintsPanel.Controls.Add(_hintsList);
            _hintsPanel.Controls.Add(hintsTitle);

            // Left border separating hints from the terminal (subtle).
            _hintsPanel.Paint += (s, e) =>
            {
                using var pen = new Pen(Color.FromArgb(0x2A, 0x2A, 0x2A));
                e.Graphics.DrawLine(pen, 0, 0, 0, _hintsPanel.Height);
            };
            contentPanel.Controls.Add(_hintsPanel);

            // Toggle for the hints panel: "Скрыть" in its title row (level with
            // the "Подсказки" title) hides it; a left arrow at the right edge,
            // vertically centered, restores it when hidden.
            var hideHintsBtn = new Label
            {
                Text = "Скрыть",
                AutoSize = true,
                MinimumSize = new Size(56, 26),
                Location = new Point(_hintsPanel.Width - 20 - 80, 24),
                Padding = new Padding(10, 2, 10, 2),
                ForeColor = Color.FromArgb(0x60, 0x60, 0x60),
                BackColor = Color.FromArgb(0x14, 0x14, 0x14),
                Font = new Font("Segoe UI", 10F),
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand
            };
            hideHintsBtn.Click += (s, e) => SetHintsVisible(false);
            hideHintsBtn.MouseEnter += (s, e) => hideHintsBtn.ForeColor = Color.White;
            hideHintsBtn.MouseLeave += (s, e) => hideHintsBtn.ForeColor = Color.FromArgb(0x60, 0x60, 0x60);
            _hintsPanel.Controls.Add(hideHintsBtn);
            hideHintsBtn.BringToFront();
            _hintsPanel.Resize += (s, e) =>
            {
                if (hideHintsBtn.Width <= 0) return;
                hideHintsBtn.Left = _hintsPanel.ClientSize.Width - hideHintsBtn.Width - 20;
                hideHintsBtn.Top = 24;
            };

            _showHintsBtn = new Label
            {
                Text = "\u276E",
                AutoSize = false,
                Size = new Size(28, 60),
                ForeColor = Color.FromArgb(0x60, 0x60, 0x60),
                BackColor = Color.FromArgb(0x0A, 0x0A, 0x0A),
                Font = new Font("Segoe UI", 14F),
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand,
                Visible = false
            };
            _showHintsBtn.Click += (s, e) => SetHintsVisible(true);
            _showHintsBtn.MouseEnter += (s, e) => _showHintsBtn.ForeColor = Color.White;
            _showHintsBtn.MouseLeave += (s, e) => _showHintsBtn.ForeColor = Color.FromArgb(0x60, 0x60, 0x60);
            contentPanel.Controls.Add(_showHintsBtn);
            _showHintsBtn.BringToFront();
            contentPanel.Resize += (s, e) =>
            {
                _showHintsBtn.Left = contentPanel.ClientSize.Width - _showHintsBtn.Width;
                _showHintsBtn.Top = Math.Max(0, (contentPanel.ClientSize.Height - _showHintsBtn.Height) / 2);
            };

            _input.TextChanged += (s, e) => UpdateHints();
            UpdateHints();

            // Wheel over the hints list scrolls it even when the input box has
            // focus (Windows routes the wheel to the focused window).
            _wheelFilter = new HintWheelFilter(_hintsList);
            Application.AddMessageFilter(_wheelFilter);
            Disposed += (s, e) => Application.RemoveMessageFilter(_wheelFilter);

            Controls.Add(contentPanel);

            ResumeLayout(false);
        }

        private void HintsListMeasureItem(object? sender, MeasureItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= _hintsList.Items.Count) return;
            var cmd = (HintCmd)_hintsList.Items[e.Index];
            int h = _hintCardPadTop + _hintNameH + _hintGap + _hintDescH + _hintCardPadBottom;
            foreach (var ex in cmd.Examples)
                h += _hintGap + _hintExampleH + _hintNoteH + 4;
            e.ItemHeight = h;
        }

        private void HintsListDrawItem(object? sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= _hintsList.Items.Count) return;
            var cmd = (HintCmd)_hintsList.Items[e.Index];
            bool selected = (e.State & DrawItemState.Selected) != 0;
            bool exSel = e.Index == _hintExampleSelItem && _hintExampleSel >= 0;

            var card = new Rectangle(
                e.Bounds.X + _hintCardSide,
                e.Bounds.Y + 2,
                e.Bounds.Width - _hintCardSide * 2,
                e.Bounds.Height - 4);
            using (var bg = new SolidBrush(selected ? Color.FromArgb(0x2E, 0x2E, 0x2E) : Color.FromArgb(0x24, 0x24, 0x24)))
                e.Graphics.FillRectangle(bg, card);
            Color borderColor = selected ? Color.FromArgb(0x7F, 0xD8, 0x8F) : _accentColor;
            using (var accent = new SolidBrush(borderColor))
                e.Graphics.FillRectangle(accent, card.X, card.Y, _hintAccentBar, card.Height);

            int textX = card.X + _hintCardSide * 2;
            int textW = card.Width - _hintCardSide * 2 - _hintAccentBar - _hintCardSide;
            int y = card.Y + _hintCardPadTop;

            Color nameColor = selected ? Color.FromArgb(0x7F, 0xD8, 0x8F) : Color.FromArgb(0xE3, 0xE3, 0xE3);
            Color descColor = Color.FromArgb(0x8C, 0x8C, 0x8C);
            TextRenderer.DrawText(e.Graphics, cmd.Name, _hintNameFont,
                new Rectangle(textX, y, textW, _hintNameH), nameColor,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            y += _hintNameH + _hintGap;
            TextRenderer.DrawText(e.Graphics, cmd.Desc, _hintDescFont,
                new Rectangle(textX, y, textW, _hintDescH), descColor,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);

            if (cmd.Examples.Length > 0)
            {
                y += _hintDescH + _hintGap;
                for (int i = 0; i < cmd.Examples.Length; i++)
                {
                    bool thisSel = exSel && i == _hintExampleSel;
                    int chipH = _hintExampleH + _hintNoteH + 4;
                    var chip = new Rectangle(textX, y, textW, chipH);
                    using (var chipBg = new SolidBrush(Color.FromArgb(0x18, 0x18, 0x18)))
                        e.Graphics.FillRectangle(chipBg, chip);
                    using (var chipBorder = new Pen(thisSel ? Color.FromArgb(0x7F, 0xD8, 0x8F) : Color.FromArgb(0x2A, 0x2A, 0x2A)))
                        e.Graphics.DrawRectangle(chipBorder, chip.X, chip.Y, chip.Width - 1, chip.Height - 1);
                    TextRenderer.DrawText(e.Graphics, "❯ " + cmd.Examples[i].Text,
                        thisSel ? _hintExampleSelFont : _hintExampleFont,
                        new Rectangle(chip.X + 8, chip.Y + 2, chip.Width - 16, _hintExampleH),
                        thisSel ? Color.FromArgb(0xFF, 0xFF, 0xFF) : Color.FromArgb(0x60, 0xC7, 0xFF),
                        TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
                    TextRenderer.DrawText(e.Graphics, cmd.Examples[i].Note, _hintNoteFont,
                        new Rectangle(chip.X + 8, chip.Y + 2 + _hintExampleH, chip.Width - 16, _hintNoteH),
                        Color.FromArgb(0x8C, 0x8C, 0x8C),
                        TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
                    y += _hintGap + chipH;
                }
            }
        }

        private List<Rectangle> HintChipRects(HintCmd cmd, Rectangle itemBounds)
        {
            var card = new Rectangle(
                itemBounds.X + _hintCardSide,
                itemBounds.Y + 2,
                itemBounds.Width - _hintCardSide * 2,
                itemBounds.Height - 4);
            int textX = card.X + _hintCardSide * 2;
            int textW = card.Width - _hintCardSide * 2 - _hintAccentBar - _hintCardSide;
            int y = card.Y + _hintCardPadTop + _hintNameH + _hintGap + _hintDescH + _hintGap;
            var chips = new List<Rectangle>(cmd.Examples.Length);
            foreach (var ex in cmd.Examples)
            {
                int chipH = _hintExampleH + _hintNoteH + 4;
                chips.Add(new Rectangle(textX, y, textW, chipH));
                y += _hintGap + chipH;
            }
            return chips;
        }

        private static int FindChipIndex(List<Rectangle> chips, Point p)
        {
            for (int i = 0; i < chips.Count; i++)
            {
                if (chips[i].Contains(p)) return i;
            }
            return -1;
        }

        private void HintsListMouseDown(object? sender, MouseEventArgs e)
        {
            int idx = _hintsList.IndexFromPoint(e.Location);
            if (idx < 0 || idx >= _hintsList.Items.Count) return;
            var cmd = (HintCmd)_hintsList.Items[idx];
            _hintsList.SelectedIndex = idx;
            var chips = HintChipRects(cmd, _hintsList.GetItemRectangle(idx));
            _hintExampleSel = FindChipIndex(chips, e.Location);
            _hintExampleSelItem = idx;
            _hintsList.Invalidate();
        }

        private void HintsListDoubleClick(object? sender, MouseEventArgs e)
        {
            int idx = _hintsList.IndexFromPoint(e.Location);
            if (idx < 0 || idx >= _hintsList.Items.Count) return;
            var cmd = (HintCmd)_hintsList.Items[idx];
            var chips = HintChipRects(cmd, _hintsList.GetItemRectangle(idx));
            int ci = FindChipIndex(chips, e.Location);
            ApplyHint(ci >= 0 ? cmd.Examples[ci].Text : cmd.Name);
        }

        private void HintsListKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && _hintsList.SelectedIndex >= 0)
            {
                e.SuppressKeyPress = true;
                var cmd = (HintCmd)_hintsList.Items[_hintsList.SelectedIndex];
                if (_hintExampleSel >= 0 &&
                    _hintExampleSelItem == _hintsList.SelectedIndex &&
                    _hintExampleSel < cmd.Examples.Length)
                {
                    ApplyHint(cmd.Examples[_hintExampleSel].Text);
                }
                else
                {
                    ApplyHint(cmd.Name);
                }
            }
        }

        private void ApplyHint(string text)
        {
            _input.Text = text;
            _input.SelectionStart = _input.TextLength;
            _input.Focus();
        }

        private void UpdateHints()
        {
            if (_hintsList == null) return;
            var query = _input.Text.Trim();
            _hintsList.BeginUpdate();
            _hintsList.Items.Clear();
            if (_hintDictionary.TryGetValue(_historyKey, out var all))
            {
                foreach (var cmd in all)
                {
                    bool match = query.Length == 0 ||
                        cmd.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        cmd.Desc.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!match)
                    {
                        foreach (var ex in cmd.Examples)
                        {
                            if (ex.Text.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                ex.Note.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                match = true;
                                break;
                            }
                        }
                    }
                    if (match)
                    {
                        _hintsList.Items.Add(cmd);
                    }
                }
            }
            _hintsList.EndUpdate();
        }

        private void SetHintsVisible(bool visible)
        {
            _hintsPanel.Visible = visible;
            _showHintsBtn.Visible = !visible;
            if (_input.CanFocus) _input.Focus();
        }

        private void StatusPanel_Paint(object? sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            var font = new Font("Segoe UI", 7.5F);
            float x = 16;
            float y = 4;
            Color titleColor = Color.FromArgb(0x5C, 0x9C, 0xF5);
            Color versionColor = Color.FromArgb(0xEE, 0xEE, 0xEE);
            Color gray = Color.FromArgb(0x80, 0x80, 0x80);
            // "Pup Seek RDP" — blue accent, like the reference terminal.
            TextRenderer.DrawText(g, _statusTitle, font, new Point((int)x, (int)y), titleColor);
            x += TextRenderer.MeasureText(_statusTitle, font).Width;
            // " v3.5.7" — bright white.
            TextRenderer.DrawText(g, _statusVersion, font, new Point((int)x, (int)y), versionColor);
            x += TextRenderer.MeasureText(_statusVersion, font).Width;
            // Dot separator — gray.
            string dot = " \u00B7 ";
            TextRenderer.DrawText(g, dot, font, new Point((int)x, (int)y), gray);
            x += TextRenderer.MeasureText(dot, font).Width;
            // "CMD" / "POWERSHELL" — gray.
            TextRenderer.DrawText(g, _statusKind, font, new Point((int)x, (int)y), gray);

            // "✕ Закрыть сеанс" — right side of the same status line.
            string closeText = "\u2715  Закрыть сеанс";
            int closeTextW = TextRenderer.MeasureText(closeText, font).Width;
            var textPt = new Point(_statusPanel.ClientSize.Width - 16 - closeTextW, 4);
            _statusCloseRect = new Rectangle(textPt.X - 8, 0, closeTextW + 16, _statusPanel.Height);
            TextRenderer.DrawText(g, closeText, font, textPt, _statusCloseHover ? Color.White : gray);
        }

        private void StatusPanel_MouseDown(object? sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && _statusCloseRect.Contains(e.Location))
                SessionClosed?.Invoke(this, EventArgs.Empty);
        }

        private void StatusPanel_MouseMove(object? sender, MouseEventArgs e)
        {
            bool hover = _statusCloseRect.Contains(e.Location);
            if (hover != _statusCloseHover)
            {
                _statusCloseHover = hover;
                _statusPanel.Cursor = hover ? Cursors.Hand : Cursors.Default;
                _statusPanel.Invalidate();
            }
        }

        private void OutputKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.C && e.Control)
            {
                if (_output.SelectionLength > 0)
                    Clipboard.SetText(_output.SelectedText);
                else
                    SendInterrupt();
                e.SuppressKeyPress = true;
                return;
            }
            if (e.KeyCode == Keys.V && e.Control)
            {
                _input.Focus();
                e.SuppressKeyPress = true;
                return;
            }
            if (e.KeyCode == Keys.Prior) // PageUp
            {
                ScrollOutputLines(-OutputVisibleLines());
                e.SuppressKeyPress = true;
                return;
            }
            if (e.KeyCode == Keys.Next) // PageDown
            {
                ScrollOutputLines(OutputVisibleLines());
                e.SuppressKeyPress = true;
                return;
            }
            if (e.KeyCode == Keys.Home)
            {
                ScrollOutputTo(0);
                e.SuppressKeyPress = true;
                return;
            }
            if (e.KeyCode == Keys.End)
            {
                ScrollOutputTo(Math.Max(0, OutputTotalLines() - OutputVisibleLines()));
                e.SuppressKeyPress = true;
                return;
            }
            if (!e.Control && !e.Alt && e.KeyCode != Keys.ShiftKey)
            {
                _input.Focus();
                e.SuppressKeyPress = true;
            }
        }

        private void InputKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                SendInput();
            }
            else if (e.KeyCode == Keys.Up)
            {
                if (_history.Count > 0)
                {
                    e.SuppressKeyPress = true;
                    if (_historyIndex < 0) _historyIndex = _history.Count; // save current draft
                    if (_historyIndex > 0) _historyIndex--;
                    _input.Text = _history[_historyIndex];
                    _input.SelectionStart = _input.TextLength;
                }
            }
            else if (e.KeyCode == Keys.Down)
            {
                if (_historyIndex >= 0)
                {
                    e.SuppressKeyPress = true;
                    _historyIndex++;
                    if (_historyIndex >= _history.Count)
                    {
                        _historyIndex = -1;
                        _input.Clear();
                    }
                    else
                    {
                        _input.Text = _history[_historyIndex];
                        _input.SelectionStart = _input.TextLength;
                    }
                }
            }
            else if (e.KeyCode == Keys.C && e.Control)
            {
                if (_input.SelectionLength > 0)
                    Clipboard.SetText(_input.SelectedText);
                else
                    SendInterrupt();
                e.SuppressKeyPress = true;
            }
        }

        public void StartProcess()
        {
            if (_started) return;
            _started = true;
            try
            {
                var psi = new ProcessStartInfo(_exePath, _arguments)
                {
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = _outputEncoding,
                    StandardErrorEncoding = _outputEncoding
                };
                _process = Process.Start(psi);
                if (_process == null) return;

                _process.OutputDataReceived += (s, e) =>
                {
                    if (e.Data != null) AppendLine(e.Data, IsPromptLine(e.Data));
                };
                _process.BeginOutputReadLine();

                _process.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data != null) AppendErrorLine(e.Data);
                };
                _process.BeginErrorReadLine();

                if (!string.IsNullOrEmpty(WelcomeText))
                    BeginInvoke(new Action(() => AppendLine(WelcomeText, false)));
                BeginInvoke(new Action(() =>
                {
                    _input.Focus();
                    _input.SelectionStart = 0;
                }));
            }
            catch (Exception ex)
            {
                AppendLine($"[Ошибка: {ex.Message}]", false);
            }
        }

        private void SendInput()
        {
            if (_process == null || _process.HasExited) return;
            var text = _input.Text;
            if (!string.IsNullOrEmpty(text))
            {
                _history.Add(text);
                _historyIndex = -1;
                SaveHistory();
            }
            _input.Clear();
            try
            {
                _process.StandardInput.WriteLine(text);
                _process.StandardInput.Flush();
            }
            catch { }
        }

        private void SendInterrupt()
        {
            if (_process == null || _process.HasExited) return;
            try
            {
                // Redirected stdin means writing \x03 does NOT interrupt a running
                // command. Send a real console Ctrl+C event to the child console:
                // detach, attach to the child's console, generate the event, detach.
                SetConsoleCtrlHandler(IntPtr.Zero, true); // ignore Ctrl+C for ourselves
                FreeConsole();
                if (AttachConsole((uint)_process.Id))
                {
                    GenerateConsoleCtrlEvent(0 /*CTRL_C_EVENT*/, 0 /*all in console*/);
                    System.Threading.Thread.Sleep(100);
                    FreeConsole();
                    SetConsoleCtrlHandler(IntPtr.Zero, false);
                    return;
                }
                SetConsoleCtrlHandler(IntPtr.Zero, false);
                _process.StandardInput.Write("\x03");
                _process.StandardInput.Flush();
            }
            catch { }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeConsole();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);

        private void LoadHistory()
        {
            try
            {
                if (!File.Exists(ShadowRDP.AppPaths.HistoryPath)) return;
                using var doc = JsonDocument.Parse(File.ReadAllText(ShadowRDP.AppPaths.HistoryPath));
                if (doc.RootElement.TryGetProperty(_historyKey, out var arr))
                {
                    foreach (var item in arr.EnumerateArray())
                    {
                        var s = item.GetString();
                        if (!string.IsNullOrEmpty(s)) _history.Add(s);
                    }
                }
            }
            catch { }
        }

        private void SaveHistory()
        {
            try
            {
                Dictionary<string, List<string>> all;
                if (File.Exists(ShadowRDP.AppPaths.HistoryPath))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(ShadowRDP.AppPaths.HistoryPath));
                        all = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(doc.RootElement.GetRawText())
                            ?? new Dictionary<string, List<string>>();
                    }
                    catch { all = new Dictionary<string, List<string>>(); }
                }
                else
                {
                    all = new Dictionary<string, List<string>>();
                }
                all[_historyKey] = _history;
                Directory.CreateDirectory(Path.GetDirectoryName(ShadowRDP.AppPaths.HistoryPath)!);
                File.WriteAllText(ShadowRDP.AppPaths.HistoryPath,
                    JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        private void AppendLine(string text, bool isPrompt)
        {
            if (_closing || IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(() => AppendLine(text, isPrompt))); } catch { }
                return;
            }
            try
            {
                _output.SelectionStart = _output.TextLength;
                _output.SelectionLength = 0;
                _output.SelectionColor = isPrompt ? PromptColor : OutputColor;
                _output.AppendText(text + "\n");
                _output.SelectionStart = _output.TextLength;
                _output.SelectionColor = OutputColor;
                _output.ScrollToCaret();
                RefreshScrollBar();
            }
            catch { }
        }

        private void AppendErrorLine(string text)
        {
            if (_closing || IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(() => AppendErrorLine(text))); } catch { }
                return;
            }
            try
            {
                _output.SelectionStart = _output.TextLength;
                _output.SelectionLength = 0;
                _output.SelectionColor = ErrorColor;
                _output.AppendText(text + "\n");
                _output.SelectionStart = _output.TextLength;
                _output.SelectionColor = OutputColor;
                _output.ScrollToCaret();
                RefreshScrollBar();
            }
            catch { }
        }

        // === Custom scrollbar integration ===
        // The native RichTextBox scrollbar is hidden; ThemedVScrollBar on the
        // right reports the real scroll position (via RichEdit messages) and
        // scrolls the output with EM_SCROLL (page + line steps — EM_LINESCROLL
        // is a no-op in msftedit.dll and must not be used).

        private const int EM_GETLINECOUNT = 0x00BA;
        private const int EM_GETFIRSTVISIBLELINE = 0x00CE;
        private const int EM_SCROLL = 0x00B5;
        private const int SB_LINEUP = 0;
        private const int SB_LINEDOWN = 1;
        private const int SB_PAGEUP = 2;
        private const int SB_PAGEDOWN = 3;
        private const int SB_BOTTOM = 7;

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private int OutputFirstLine()
        {
            if (!_output.IsHandleCreated) return 0;
            return SendMessage(_output.Handle, EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero).ToInt32();
        }

        private int OutputTotalLines()
        {
            if (!_output.IsHandleCreated) return 1;
            int n = SendMessage(_output.Handle, EM_GETLINECOUNT, IntPtr.Zero, IntPtr.Zero).ToInt32();
            return Math.Max(1, n);
        }

        private int OutputVisibleLines()
        {
            if (_lineHeight <= 0 || _output.ClientSize.Height <= 0) return 1;
            return Math.Max(1, _output.ClientSize.Height / _lineHeight);
        }

        private void ScrollOutputTo(int firstLine)
        {
            if (!_output.IsHandleCreated) return;
            ScrollOutputLines(firstLine - OutputFirstLine());
        }

        private void ScrollOutputLines(int delta)
        {
            if (!_output.IsHandleCreated || delta == 0) return;
            int page = Math.Max(1, OutputVisibleLines());
            int n = Math.Abs(delta);
            int pages = n / page;
            int rest = n % page;
            int pageCmd = delta > 0 ? SB_PAGEDOWN : SB_PAGEUP;
            int lineCmd = delta > 0 ? SB_LINEDOWN : SB_LINEUP;
            var h = _output.Handle;
            for (int i = 0; i < pages; i++)
                SendMessage(h, EM_SCROLL, (IntPtr)pageCmd, IntPtr.Zero);
            for (int i = 0; i < rest; i++)
                SendMessage(h, EM_SCROLL, (IntPtr)lineCmd, IntPtr.Zero);
            RefreshScrollBar();
        }

        /// <summary>
        /// Shows the themed scrollbar only when the output actually overflows;
        /// hides it (and the output takes the full width) otherwise.
        /// </summary>
        private void RefreshScrollBar()
        {
            if (_scrollBar == null || _scrollBar.IsDisposed) return;
            bool needed = OutputTotalLines() > OutputVisibleLines();
            if (_scrollBar.Visible != needed)
                _scrollBar.Visible = needed;
            _scrollBar.Invalidate();
        }

        private static bool IsPromptLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return false;
            var t = line.TrimStart();
            return t.StartsWith("PS ", StringComparison.Ordinal) ||
                   (t.Length >= 3 && t[1] == ':' && t[2] == '\\');
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            _closing = true;
            try
            {
                if (_process != null && !_process.HasExited)
                {
                    _process.Kill();
                    _process.Dispose();
                }
            }
            catch { }
            base.OnHandleDestroyed(e);
        }

        public void KillProcess()
        {
            try
            {
                if (_process != null && !_process.HasExited)
                {
                    _process.Kill();
                    _process.Dispose();
                    _process = null;
                }
            }
            catch { }
        }

        public void Activate()
        {
            if (_input.CanFocus) _input.Focus();
        }
    }

    /// <summary>
    /// RichTextBox whose native vertical scrollbar is hidden (the themed
    /// ThemedVScrollBar replaces it). The scroll RANGE is kept intact, so
    /// wheel scrolling, ScrollToCaret and EM_* messages still work — the bar
    /// is just never drawn. Raises Scrolled after any wheel/scroll activity
    /// so the custom scrollbar can redraw its thumb.
    /// </summary>
    internal sealed class ConsoleOutputBox : RichTextBox
    {
        private const int SB_VERT = 1;
        private const int GWL_STYLE = -16;
        private const int WS_VSCROLL = 0x00200000;
        private const int WM_PAINT = 0x000F;
        private const int WM_NCPAINT = 0x0085;
        private const int WM_SIZE = 0x0005;
        private const int WM_MOUSEWHEEL = 0x020A;
        private const int WM_VSCROLL = 0x0115;
        private const int WM_HSCROLL = 0x0114;

        [DllImport("user32.dll")]
        private static extern bool ShowScrollBar(IntPtr hWnd, int wBar, bool bShow);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        /// <summary>Raised after the view scrolled (wheel, scrollbar, ...).</summary>
        public event EventHandler? Scrolled;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            HideScrollBar();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            HideScrollBar();
        }

        protected override void WndProc(ref Message m)
        {
            // Strip the vertical-scrollbar style before any paint, otherwise
            // the control may re-add it and the bar flashes on the frame.
            if (m.Msg == WM_PAINT || m.Msg == WM_NCPAINT || m.Msg == WM_SIZE)
                ClearVScroll();
            base.WndProc(ref m);
            HideScrollBar();
            if (m.Msg == WM_MOUSEWHEEL || m.Msg == WM_VSCROLL || m.Msg == WM_HSCROLL)
                Scrolled?.Invoke(this, EventArgs.Empty);
        }

        private void ClearVScroll()
        {
            try
            {
                if (!IsHandleCreated) return;
                int style = GetWindowLong(Handle, GWL_STYLE);
                if ((style & WS_VSCROLL) != 0)
                    SetWindowLong(Handle, GWL_STYLE, style & ~WS_VSCROLL);
            }
            catch { }
        }

        private void HideScrollBar()
        {
            try
            {
                if (!IsHandleCreated) return;
                ShowScrollBar(Handle, SB_VERT, false);
                ClearVScroll();
            }
            catch { }
        }
    }

    /// <summary>
    /// ListBox that hides the vertical scrollbar but keeps wheel scrolling.
    /// </summary>
    internal sealed class ScrolllessListBox : ListBox
    {
        private const int SB_VERT = 1;
        private const int GWL_STYLE = -16;
        private const int WS_VSCROLL = 0x00200000;
        private const int WM_PAINT = 0x000F;
        private const int WM_NCPAINT = 0x0085;
        private const int WM_SIZE = 0x0005;

        [DllImport("user32.dll")]
        private static extern bool ShowScrollBar(IntPtr hWnd, int wBar, bool bShow);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            HideScrollBar();
        }

        protected override void WndProc(ref Message m)
        {
            // Strip the vertical-scrollbar style before any paint, otherwise the
            // ListBox may re-add it during its own message handling and the bar
            // flashes on the frame being drawn right now.
            if (m.Msg == WM_PAINT || m.Msg == WM_NCPAINT || m.Msg == WM_SIZE)
                ClearVScroll();
            base.WndProc(ref m);
            HideScrollBar();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            HideScrollBar();
        }

        public void WheelScroll(short delta)
        {
            if (Items.Count == 0) return;
            int step = delta < 0 ? 1 : -1;
            int ti = Math.Max(0, Math.Min(Items.Count - 1, TopIndex + step));
            if (ti != TopIndex)
            {
                TopIndex = ti;
                HideScrollBar();
            }
        }

        private void ClearVScroll()
        {
            try
            {
                if (!IsHandleCreated) return;
                int style = GetWindowLong(Handle, GWL_STYLE);
                if ((style & WS_VSCROLL) != 0)
                    SetWindowLong(Handle, GWL_STYLE, style & ~WS_VSCROLL);
            }
            catch { }
        }

        private void HideScrollBar()
        {
            try
            {
                if (!IsHandleCreated) return;
                ShowScrollBar(Handle, SB_VERT, false);
                ClearVScroll();
            }
            catch { }
        }
    }

    /// <summary>
    /// Routes the mouse wheel to the hints list even when another control
    /// (the input box) has focus and Windows sends the wheel to it instead.
    /// </summary>
    internal sealed class HintWheelFilter : IMessageFilter
    {
        private const int WM_MOUSEWHEEL = 0x020A;
        private readonly ScrolllessListBox _list;

        public HintWheelFilter(ScrolllessListBox list)
        {
            _list = list;
        }

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != WM_MOUSEWHEEL) return false;
            if (!_list.IsHandleCreated || !_list.Visible) return false;
            if (!_list.RectangleToScreen(_list.ClientRectangle).Contains(Cursor.Position)) return false;
            short delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
            _list.WheelScroll(delta);
            return true;
        }
    }

    /// <summary>
    /// Routes the mouse wheel over the terminal output to a manual scroll
    /// (EM_SCROLL), even when another control (the input box) has focus.
    /// The custom scrollbar updates itself through the same path.
    /// </summary>
    internal sealed class TerminalWheelFilter : IMessageFilter
    {
        private const int WM_MOUSEWHEEL = 0x020A;
        private readonly ConsoleOutputBox _output;
        private readonly Action<int> _scrollLines;

        public TerminalWheelFilter(ConsoleOutputBox output, Action<int> scrollLines)
        {
            _output = output;
            _scrollLines = scrollLines;
        }

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != WM_MOUSEWHEEL) return false;
            if (!_output.IsHandleCreated || !_output.Visible) return false;
            if (!_output.RectangleToScreen(_output.ClientRectangle).Contains(Cursor.Position)) return false;
            short delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
            int lines = delta > 0 ? -3 : 3;
            _scrollLines(lines);
            return true;
        }
    }

    /// <summary>
    /// TextBox with a thicker, blinking caret (via Win32 CreateCaret).
    /// </summary>
    internal sealed class CaretTextBox : TextBox
    {
        private const int WM_SETFOCUS = 0x0007;
        private const int WM_KILLFOCUS = 0x0008;
        private const int EM_SETMARGINS = 0x00D3;
        private const int EC_LEFTMARGIN = 0x0001;
        private const int EC_RIGHTMARGIN = 0x0002;
        private const int EC_USEFONTINFO = 0xFFFF;

        /// <summary>Caret width in pixels (default system = 1).</summary>
        public int CaretWidth { get; set; } = 1;

        [DllImport("user32.dll")]
        private static extern bool CreateCaret(IntPtr hWnd, IntPtr hBitmap, int nWidth, int nHeight);

        [DllImport("user32.dll")]
        private static extern bool DestroyCaret();

        [DllImport("user32.dll")]
        private static extern bool ShowCaret(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool SetCaretBlinkTime(uint uMSeconds);

        [DllImport("user32.dll")]
        private static extern bool GetCaretPos(out Point lpPoint);

        [DllImport("user32.dll")]
        private static extern bool SetCaretPos(int x, int y);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int wm = EC_LEFTMARGIN | EC_RIGHTMARGIN;
            int lm = EC_USEFONTINFO;
            int rm = EC_USEFONTINFO;
            if (Padding.Left > 0) lm = Padding.Left;
            if (Padding.Right > 0) rm = Padding.Right;
            int margins = lm | (rm << 16);
            SendMessage(Handle, EM_SETMARGINS, (IntPtr)wm, (IntPtr)margins);
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);

            if (m.Msg == WM_SETFOCUS)
            {
                SetCaretBlinkTime(500);
                RecreateCaret();
            }
            else if (m.Msg == WM_KILLFOCUS)
            {
                DestroyCaret();
            }
        }

        private void RecreateCaret()
        {
            if (!Focused || !IsHandleCreated || CaretWidth < 2) return;

            Point pos = Point.Empty;
            GetCaretPos(out pos);

            DestroyCaret();
            int height = (int)(Font.GetHeight()) + 2;
            CreateCaret(Handle, IntPtr.Zero, CaretWidth, height);
            SetCaretPos(pos.X, pos.Y);
            ShowCaret(Handle);
        }
    }
}
