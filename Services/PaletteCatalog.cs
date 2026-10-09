using System.Collections.Generic;

namespace ShadowRDP.Services
{
    // v3.9.5 — единый каталог статичных строк палитры команд (Ctrl+K).
    // Один источник правды для RdpManagerForm.OpenPalette (строит Action
    // по ключу) и PaletteSettingsForm (показывает ВЕСЬ каталог с галочками
    // «показывать», скрытые лежат в palette.json через PaletteStore).
    // Открытые сессии/консоли и недавние ММС — динамические (Key пустой),
    // настройкой не скрываются и в каталог не входят.
    public sealed record PaletteDef(string Key, string Title, string Category);

    public static class PaletteCatalog
    {
        public static readonly IReadOnlyList<PaletteDef> All =
            new List<PaletteDef>
            {
                new("add-session", "Добавить сессию…", "Сессии"),
                new("add-book", "Добавить в книгу…", "Сессии"),
                new("portscan", "Проверка портов…", "Инструменты"),
                new("snippets", "Управление сниппетами…", "Инструменты"),
                new("audit", "Журнал действий…", "Инструменты"),
                new("remote-console", "Удалённая консоль…", "Инструменты"),
                new("tunnels", "Туннели SSH…", "Инструменты"),
                new("dash-events", "Дашборд: События", "Дашборд"),
                new("dash-monitor", "Дашборд: Монитор", "Дашборд"),
                new("dash-web", "Дашборд: Web", "Дашборд"),
                new("book", "Книга", "Книга"),
                new("openfiles", "Открытые файлы", "Книга"),
                new("inv-soft", "Инвентаризация SOFT", "Книга"),
                new("inv-hard", "Сводная таблица HARD", "Книга"),
                new("msgall", "Сообщение всем", "Книга"),
                new("passwords", "Пароли…", "Пароли"),
                new("todo", "TODO/шня", "TODO/шня"),
                new("mmc-open", "Открыть консоль MMC…", "ММС"),
                new("mmc-add", "Добавить MMC", "ММС"),
                new("cmd", "CMD (вкладка консоли)", "Консоли"),
                new("cmd-window", "CMD (отдельное окно)", "Консоли"),
                new("color-settings", "Настройки цвета…", "Настройки"),
                new("modules", "Модули…", "Настройки"),
                new("change-pwd", "Сменить пароль", "Настройки"),
                new("switch-theme", "Сменить тему", "Настройки"),
            }.AsReadOnly();
    }
}
