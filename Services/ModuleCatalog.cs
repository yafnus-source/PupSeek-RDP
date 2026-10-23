using System.Collections.Generic;

namespace ShadowRDP.Services
{
    // v4.0 — реестр платных модулей. Ядро программы бесплатно всегда;
    // модули открываются ключом (LicenseStore) или пробным периодом.
    // Пакеты («Мониторинг», «Про») — просто предзаполненные списки id.
    public sealed record ModuleDef(string Id, string Title, string Blurb, string Price);

    public static class ModuleCatalog
    {
        public static readonly IReadOnlyList<ModuleDef> All = new List<ModuleDef>
        {
            new("events", "События",
                "Мониторинг журналов Windows по WMI: Critical/Error, баллуны, история.",
                "500 ₽"),
            new("zabbix", "Монитор",
                "Живые метрики по Zabbix-агенту (10050) и SNMP: CPU, RAM, диски, службы, графики.",
                "500 ₽"),
            new("webmon", "Web-мониторинг",
                "Проверки сайтов: коды ответов, время отклика, баллун «Ресурс недоступен».",
                "500 ₽"),
            new("inventory", "Инвентаризации",
                "Сводная таблица HARD (железо по WMI) и SOFT (программы через GPO-скрипт).",
                "500 ₽"),
            new("todo", "TODO и календарь",
                "Задачи с напоминаниями, импорт .ics, автосинхронизация.",
                "500 ₽"),
            new("ftp-upload", "FTP-выгрузка",
                "Выгрузка дашбордов на FTP/SFTP по расписанию.",
                "500 ₽"),
            new("ai", "ИИ-расшифровка",
                "«Спросить ИИ»: разбор событий Windows человеческим языком (локальная Ollama); ИИ-анализ журнала компьютера — ПКМ в Книге и кнопка в окне «События».",
                "500 ₽"),
            new("domain", "Доменные инструменты",
                "Открытые файлы, поиск где залогинен, GPO Results.",
                "500 ₽"),
        }.AsReadOnly();

        public static string TitleOf(string id)
        {
            foreach (var m in All)
                if (m.Id == id) return m.Title;
            return id;
        }
    }
}
