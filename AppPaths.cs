using System;
using System.Collections.Generic;
using System.IO;

namespace ShadowRDP
{
    // Centralized path constants for all data files.
    //
    // v3.0.18: storage is back to %PROGRAMDATA%\PupSeekRDP\ (shared, machine-
    // wide). v3.0.16/17 experimented with running non-elevated + per-user
    // LocalAppData, but the app MUST run elevated for shadow RDP (qwinsta
    // /server:<remote> requires admin). With auto-elevation (v3.0.18, no
    // dialog), the process is always elevated and ProgramData is writable.
    //
    // v3.0.17 stored data in %LOCALAPPDATA%\PupSeekRDP\ per user. On upgrade
    // to v3.0.18, MigrateFromLocalAppData() copies any newer files back to
    // ProgramData so the user doesn't lose recent edits.
    //
    // Legacy ShadowRDP → PupSeekRDP rename migration also preserved.
    public static class AppPaths
    {
        private static readonly string _legacyDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ShadowRDP");

        // v3.0.17 location — used only for one-shot back-migration.
        private static readonly string _localAppDataDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PupSeekRDP");

        public static string DataDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PupSeekRDP");

        public static string DebugLogPath => Path.Combine(DataDir, "debug.log");
        public static string HistoryPath => Path.Combine(DataDir, "history.json");
        public static string AddressBookPath => Path.Combine(DataDir, "addressbook.json");
        public static string WindowStatePath => Path.Combine(DataDir, "windows.json");
        // Per-form DataGridView column widths. Keyed by form name → { column: width }.
        public static string ColumnWidthsPath => Path.Combine(DataDir, "column_widths.json");
        // Per-form DataGridView column ORDER (drag-to-reorder). Separate file
        // on purpose: column_widths.json is deserialized as a strict
        // Dictionary<string, Dictionary<string,int>>, so an extra array key
        // there would break the existing width persistence.
        public static string ColumnOrderPath => Path.Combine(DataDir, "column_order.json");
        // Open RDP Manager sessions — restored when the user reopens the manager.
        public static string RdpSessionsPath => Path.Combine(DataDir, "rdp_sessions.json");
        // DPAPI-encrypted shared RDP password blob. CurrentUser scope.
        public static string RdpPasswordPath => Path.Combine(DataDir, "rdp_password.bin");
        // History of MMC consoles embedded via the RDP Manager "Встроить ММС" button.
        public static string EmbeddedAppsPath => Path.Combine(DataDir, "embedded_apps.json");
        // App-owned MMC console file (v3.6.8): snap-ins the user adds to the
        // console (File → Add/Remove Snap-in) persist here after File → Save,
        // so the console can be reopened configured from the recent list.
        public static string MmcDirPath => Path.Combine(DataDir, "mmc");
        public static string MmcConsolePath => Path.Combine(MmcDirPath, "MMC.msc");
        // v3.6.8 — KeePass integration config: .kdbx path + optional keyfile
        // path. The master password is NEVER persisted — it's asked each time
        // the vault is unlocked.
        public static string KeePassConfigPath => Path.Combine(DataDir, "keepass.json");
        // v3.7.1 — one-shot marker: the first-run "create a KeePass store?"
        // offer was shown (and answered). Written on both Yes and No so the
        // question never reappears after the user has seen it once.
        public static string KeePassOfferDonePath => Path.Combine(DataDir, "keepass_offer_done.json");
        // v3.8.x — user-defined address-book groups (Создать/Редактировать/
        // Удалить группу from the Book filter pills). Persisted so groups
        // survive restarts.
        public static string GroupsPath => Path.Combine(DataDir, "groups.json");
        // v3.8.2 — user-customised active filter-pill colours (role → hex).
        public static string ColorsPath => Path.Combine(DataDir, "colors.json");
        // v4.0 — Event Dashboard (Zabbix-like): monitored hosts, cached events, settings.
        public static string DashboardEventsPath => Path.Combine(DataDir, "dashboard_events.json");
        public static string DashboardSettingsPath => Path.Combine(DataDir, "dashboard_settings.json");
        public static string MonitoredComputersPath => Path.Combine(DataDir, "monitored_computers.json");
        // v4.1 — Zabbix Agent direct (без сервера): история опроса агентов
        public static string ZabbixAgentHistoryPath => Path.Combine(DataDir, "zabbix_agent_history.json");
        public static string ZabbixAgentSettingsPath => Path.Combine(DataDir, "zabbix_agent_settings.json");
        // v4.3.x — ручные записи мониторинга (даш Z «Добавить запись»): хосты,
        // которые пользователь добавил в мониторинг прямо из даша, без книги.
        public static string ZabbixManualHostsPath => Path.Combine(DataDir, "zabbix_manual_hosts.json");
        // v4.6 — мониторинг web-ресурсов (даш Z, ряд WEB): URL для проверок,
        // история точек (код + время ответа), настройки опроса.
        public static string WebMonitorUrlsPath => Path.Combine(DataDir, "webmon_urls.json");
        public static string WebMonitorHistoryPath => Path.Combine(DataDir, "webmon_history.json");
        public static string WebMonitorSettingsPath => Path.Combine(DataDir, "webmon_settings.json");
        // v4.2 — FTP выгрузка (общий для W и Z)
        public static string FtpSettingsPath => Path.Combine(DataDir, "ftp_upload_settings.json");
        // v4.7 — ICMP-пинг хостов даша Z (колонка «Пинг», опрос раз в час).
        public static string HostPingPath => Path.Combine(DataDir, "hostping.json");
        // v4.3 — TODO.шная (изолированный модуль, удаление todo.json = откат фичи)
        public static string TodoPath => Path.Combine(DataDir, "todo.json");
        public static string TodoSettingsPath => Path.Combine(DataDir, "todo_settings.json");
        // v4.7 — реестр встраиваемых модулей: id → включён (modules.json).
        // Отсутствие записи = выключен по умолчанию («не установлен»).
        public static string ModulesSettingsPath => Path.Combine(DataDir, "modules.json");
        // v3.9.3BD — кэш сводки железа (Сводная таблица HARD).
        public static string HardwareInventoryPath => Path.Combine(DataDir, "hardware_inventory.json");

        // Путь к data/ директории сайта монитора (для синхронизации JSON)
        private static string? _monitorDataDir;
        public static string MonitorDataDir
        {
            get
            {
                if (_monitorDataDir != null) return _monitorDataDir;
                // 1) Рядом с exe: monitor_data
                var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
                var c1 = Path.Combine(exeDir, "monitor_data");
                if (Directory.Exists(c1)) { _monitorDataDir = c1; return _monitorDataDir; }
                // 2) %PROGRAMDATA%\PupSeekRDP\monitor_data
                var c2 = Path.Combine(DataDir, "monitor_data");
                if (Directory.Exists(c2)) { _monitorDataDir = c2; return _monitorDataDir; }
                // 3) Dev: относительно исходников — C:\temp\Clo\cpro\monitor\data
                var c3 = @"C:\temp\Clo\cpro\monitor\data";
                if (Directory.Exists(c3)) { _monitorDataDir = c3; return _monitorDataDir; }
                // 4) Создаём рядом с exe
                Directory.CreateDirectory(c1);
                _monitorDataDir = c1;
                return _monitorDataDir;
            }
        }

        public static void SyncToMonitor(string srcPath, string fileName)
        {
            // Пишем во все возможные папки — чтобы сайт точно увидел
            string[] targets = {
                MonitorDataDir,                                     // определённый путь
                @"C:\temp\Clo\cpro\monitor\data",                  // dev-сайт
                Path.Combine(DataDir, "monitor_data")              // %PROGRAMDATA%
            };
            var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in targets)
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                var dst = Path.Combine(dir, fileName);
                var normDir = Path.GetFullPath(dir);
                if (written.Contains(normDir)) continue;
                try { File.Copy(srcPath, dst, true); written.Add(normDir); } catch { }
            }
        }

        public static void EnsureDataDir()
        {
            try
            {
                MigrateLegacyDir();
                MigrateFromLocalAppData();
                var dir = DataDir;
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
            }
            catch { /* best-effort */ }
        }

        // v3.0.18 — one-shot back-migration from %LOCALAPPDATA%\PupSeekRDP\
        // (where v3.0.17 briefly stored data) to %PROGRAMDATA%\PupSeekRDP\.
        // Copies any file that's missing in ProgramData OR newer in
        // LocalAppData. No-op if the LocalAppData folder doesn't exist.
        //
        // v3.8.1 — guarded by a marker file: the lite build also wrote its
        // address book into the same %LOCALAPPDATA%\PupSeekRDP\ folder, and
        // this migration was silently overwriting the full version's
        // ProgramData book with the lite one on every launch. It now runs
        // at most once (marker), and the lite build has its own folder.
        public static void MigrateFromLocalAppData()
        {
            try
            {
                var marker = Path.Combine(DataDir, ".migrated_from_localappdata");
                if (File.Exists(marker)) return;
                if (!Directory.Exists(_localAppDataDir)) { File.WriteAllText(marker, ""); return; }
                Directory.CreateDirectory(DataDir);
                foreach (var src in Directory.GetFiles(_localAppDataDir))
                {
                    var name = Path.GetFileName(src);
                    if (string.Equals(name, "debug.log", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var dest = Path.Combine(DataDir, name);
                    // Copy if dest missing OR source is newer (user edited in v3.0.17).
                    if (!File.Exists(dest) ||
                        File.GetLastWriteTimeUtc(src) > File.GetLastWriteTimeUtc(dest))
                    {
                        File.Copy(src, dest, overwrite: true);
                    }
                }
                try { Logger.Info($"MigrateFromLocalAppData: merged files from '{_localAppDataDir}' into '{DataDir}'"); } catch { }
                try { File.WriteAllText(marker, ""); } catch { }
            }
            catch (Exception ex)
            {
                try { Logger.Warn($"MigrateFromLocalAppData failed: {ex.Message}"); } catch { }
            }
        }

        // Legacy ShadowRDP folder migration (v2.1.44 rename).
        public static void MigrateLegacyDir()
        {
            try
            {
                if (!Directory.Exists(_legacyDir)) return;
                Directory.CreateDirectory(DataDir);
                foreach (var src in Directory.GetFiles(_legacyDir))
                {
                    var name = Path.GetFileName(src);
                    var dest = Path.Combine(DataDir, name);
                    if (string.Equals(name, "debug.log", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!File.Exists(dest) || new FileInfo(dest).Length < new FileInfo(src).Length)
                    {
                        File.Copy(src, dest, overwrite: true);
                    }
                }
            }
            catch (Exception ex)
            {
                try { Logger.Warn($"MigrateLegacyDir failed: {ex.Message}"); } catch { }
            }
        }
    }
}
