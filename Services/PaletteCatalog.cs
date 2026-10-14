using System.Collections.Generic;

namespace ShadowRDP.Services
{
    // v3.9.5 — единый каталог статичных строк палитры команд (Ctrl+K).
    // Один источник правды для RdpManagerForm.OpenPalette (строит Action
    // по ключу) и PaletteSettingsForm (показывает ВЕСЬ каталог с галочками
    // «показывать», скрытые лежат в palette.json через PaletteStore).
    // IconKey — та же иконка, что у пункта в программе (MenuIcons).
    // Открытые сессии/консоли и недавние ММС — динамические (Key пустой),
    // настройкой не скрываются и в каталог не входят.
    public sealed record PaletteDef(string Key, string Title, string Category, string IconKey);

    public static class PaletteCatalog
    {
        // v3.9.5 — ключ скрытия отдельной недавней ММС (история
        // «Встроить ММС»): стабилен, регистр пути гасится.
        public const string RecentPrefix = "mmc-recent:";
        public static string RecentKey(string? path) =>
            RecentPrefix + (path ?? "").Trim().ToLowerInvariant();

        public static readonly IReadOnlyList<PaletteDef> All =
            new List<PaletteDef>
            {
                new("add-session", "Добавить сессию…", "Сессии", "add"),
                new("add-book", "Добавить в книгу…", "Сессии", "book"),
                new("portscan", "Проверка портов…", "Инструменты", "ping"),
                new("snippets", "Управление сниппетами…", "Инструменты", "terminal"),
                new("audit", "Журнал действий…", "Инструменты", "list"),
                new("remote-console", "Удалённый терминал…", "Инструменты", "terminal"),
                new("tunnels", "Туннели SSH…", "Инструменты", "exchange"),
                new("backup", "Backup…", "Инструменты", "exchange"),
                new("dash-events", "Дашборд: События", "Дашборд", "bell"),
                new("dash-monitor", "Дашборд: Монитор", "Дашборд", "computer"),
                new("dash-web", "Дашборд: Web", "Дашборд", "globe"),
                new("book", "Книга", "Книга", "list"),
                new("openfiles", "Открытые файлы", "Книга", "folder"),
                new("inv-soft", "Инвентаризация SOFT", "Книга", "list"),
                new("inv-hard", "Сводная таблица HARD", "Книга", "computer"),
                new("msgall", "Сообщение всем", "Книга", "msg"),
                new("passwords", "Пароли…", "Пароли", "shield"),
                new("todo", "TODO/шня", "TODO/шня", "tasks"),
                new("mmc-open", "Открыть консоль MMC…", "ММС", "gear"),
                new("mmc-add", "Добавить MMC", "ММС", "gear"),
                new("cmd", "CMD (вкладка консоли)", "Консоли", "terminal"),
                new("cmd-window", "CMD (отдельное окно)", "Консоли", "terminal"),
                new("color-settings", "Настройки цвета…", "Настройки", "info"),
                new("modules", "Модули…", "Настройки", "tasks"),
                new("change-pwd", "Сменить пароль", "Настройки", "key"),
                new("switch-theme", "Сменить тему", "Настройки", "sun"),
                new("help", "Справка по программе…", "Справка", "book"),
            }.AsReadOnly();
    }
}
