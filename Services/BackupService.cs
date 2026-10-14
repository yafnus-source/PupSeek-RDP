using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace ShadowRDP.Services
{
    // v3.9.5 — полный бэкап по выбору пользователя + расписание.
    // Окно: Forms.BackupForm (кнопка «Backup…» в Инструментах).
    // Состав — максимально полный: сессии+группы сайдбара, книга,
    // сниппеты, задачи, дашборды, оформление, FTP, модули, KeePass
    // (настройки + .kdbx), аудит, MMC, данные веб-монитора.
    // Не входят: debug.log (диагностика), rdp_password.bin (DPAPI-привязка
    // к машине, на другой не расшифруется), crashdumps.
    public static class BackupService
    {
        public sealed class Item
        {
            public string Id = "";
            public string Title = "";
        }

        private static readonly (string Id, string Title, string[] Files)[] StaticGroups =
        {
            ("sessions", "Сессии и группы сайдбара",
                new[] { "rdp_sessions.json", "custom_sessions.json", "groups.json" }),
            ("book", "Адресная книга",
                new[] { "addressbook.json" }),
            ("snippets", "Сниппеты SSH",
                new[] { "ssh_snippets.json" }),
            ("todo", "Задачи",
                new[] { "todo.json", "todo_settings.json" }),
            ("dash", "Дашборды и мониторинг",
                new[] { "dashboard_events.json", "dashboard_settings.json",
                    "monitored_computers.json", "zabbix_agent_history.json",
                    "zabbix_agent_settings.json", "zabbix_manual_hosts.json",
                    "webmon_urls.json", "webmon_history.json",
                    "webmon_settings.json", "hostping.json",
                    "hardware_inventory.json" }),
            ("ui", "Оформление и окна",
                new[] { "colors.json", "theme.json", "windows.json",
                    "column_widths.json", "column_order.json", "history.json" }),
            ("ftp", "FTP выгрузка",
                new[] { "ftp_upload_settings.json" }),
            ("modules", "Модули",
                new[] { "modules.json", "embedded_apps.json" }),
            ("keepass", "KeePass (настройки + база)",
                new[] { "keepass.json", "keepass_offer_done.json" }),
            ("mmc", "Консоль MMC",
                new string[0]),
            ("license", "Лицензия",
                new[] { "license.json" }),
        };

        public static IReadOnlyList<Item> Catalog =>
            StaticGroups.Select(g => new Item { Id = g.Id, Title = g.Title })
                .Concat(new[]
                {
                    new Item { Id = "audit", Title = "Журнал действий" },
                    new Item { Id = "monitor", Title = "Данные веб-монитора" },
                }).ToList();

        private static string SettingsPath =>
            Path.Combine(AppPaths.DataDir, "backup_settings.json");

        // Все файлы категории: (имя в zip → полный путь). Папок mmc/ и
        // monitor_data/ — с префиксом, остальное плоско (имена уникальны).
        // audit_*.jsonl — glob; .kdbx — путь из конфига KeePass.
        public static List<(string Rel, string Abs)> ResolveFiles(IEnumerable<string> ids)
        {
            var want = new HashSet<string>(ids ?? Enumerable.Empty<string>());
            var out_ = new List<(string Rel, string Abs)>();
            string dd = AppPaths.DataDir;
            void AddFile(string abs, string rel)
            {
                try
                {
                    if (!File.Exists(abs)) return;
                    if (out_.Any(x => string.Equals(x.Rel, rel, StringComparison.OrdinalIgnoreCase))) return;
                    out_.Add((rel, abs));
                }
                catch { }
            }
            foreach (var g in StaticGroups)
            {
                if (!want.Contains(g.Id)) continue;
                foreach (var f in g.Files) AddFile(Path.Combine(dd, f), f);
                if (g.Id == "keepass")
                {
                    foreach (var vault in VaultCandidates())
                        AddFile(vault, Path.GetFileName(vault));
                }
                if (g.Id == "mmc")
                {
                    var dir = AppPaths.MmcDirPath;
                    if (Directory.Exists(dir))
                        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                        {
                            var rel = "mmc/" + GetRel(dir, f).Replace('\\', '/');
                            AddFile(f, rel);
                        }
                }
            }
            if (want.Contains("audit"))
            {
                try
                {
                    foreach (var f in Directory.EnumerateFiles(dd, "audit_*.jsonl"))
                        AddFile(f, Path.GetFileName(f));
                }
                catch { }
            }
            if (want.Contains("monitor"))
            {
                string dir = "";
                try { dir = AppPaths.MonitorDataDir; } catch { }
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        var rel = "monitor_data/" + GetRel(dir, f).Replace('\\', '/');
                        AddFile(f, rel);
                    }
            }
            return out_;
        }

        private static string GetRel(string dir, string file)
        {
            try
            {
                var d = Path.GetFullPath(dir);
                var f = Path.GetFullPath(file);
                if (f.StartsWith(d, StringComparison.OrdinalIgnoreCase))
                    return f.Substring(d.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch { }
            return Path.GetFileName(file);
        }

        private static IEnumerable<string> VaultCandidates()
        {
            string? cfg = null, cur = null;
            try { cfg = KeePassVault.LoadConfig()?.DbPath; } catch { }
            try { cur = KeePassVault.CurrentDbPath; } catch { }
            if (!string.IsNullOrWhiteSpace(cfg)) yield return cfg;
            if (!string.IsNullOrWhiteSpace(cur) && !string.Equals(cur, cfg, StringComparison.OrdinalIgnoreCase))
                yield return cur;
        }

        // Ручной/плановый прогон: zip в destDir. password=null/пусто —
        // обычный .zip; иначе AES-256-GCM (.zip.aes). Пароль нигде не
        // хранится. Возвращает (путь, файлов, ошибка).
        public static (string? Zip, int Files, string? Error) RunBackup(
            string destDir, IEnumerable<string> ids, string? password = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(destDir))
                    return (null, 0, "Укажите папку для бэкапа.");
                Directory.CreateDirectory(destDir);
                var files = ResolveFiles(ids);
                if (files.Count == 0)
                    return (null, 0, "Нечего бекапить: файлы не найдены (галочки пусты?).");
                var zip = Path.Combine(destDir,
                    $"PupSeekRDP-backup-{DateTime.Now:yyyy-MM-dd-HHmmss}.zip");
                using (var zs = File.Create(zip))
                using (var ar = new ZipArchive(zs, ZipArchiveMode.Create))
                {
                    foreach (var (rel, abs) in files)
                    {
                        try { ar.CreateEntryFromFile(abs, rel, CompressionLevel.Optimal); }
                        catch { }
                    }
                }
                if (!string.IsNullOrEmpty(password))
                {
                    string aes = zip + ".aes";
                    try
                    {
                        EncryptFile(zip, aes, password);
                        try { File.Delete(zip); } catch { }
                        zip = aes;
                    }
                    catch (Exception ex)
                    {
                        try { File.Delete(aes); } catch { }
                        return (null, 0, "Шифрование: " + ex.Message);
                    }
                }
                try { Logger.Info($"Backup: {files.Count} файлов → {zip}"); } catch { }
                return (zip, files.Count, null);
            }
            catch (Exception ex)
            {
                try { Logger.Warn($"Backup failed: {ex.Message}"); } catch { }
                return (null, 0, ex.Message);
            }
        }

        public static void RotateBackups(string destDir, int keep)
        {
            try
            {
                if (keep < 1) keep = 1;
                var zips = Directory.GetFiles(destDir, "PupSeekRDP-backup-*.zip*")
                    .Where(f => f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".zip.aes", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(File.GetLastWriteTimeUtc).ToList();
                while (zips.Count > keep)
                {
                    try { File.Delete(zips[0]); } catch { }
                    zips.RemoveAt(0);
                }
            }
            catch { }
        }

        // === шифрование архива (AES-256-GCM, пароль → PBKDF2-SHA256) ===
        // Формат: magic(8) | salt(16) | nonce(12) | ciphertext | tag(16).

        private static readonly byte[] AesMagic =
            System.Text.Encoding.ASCII.GetBytes("PSRBAES1");

        private static void EncryptFile(string src, string dst, string password)
        {
            byte[] salt = new byte[16], nonce = new byte[12];
            System.Security.Cryptography.RandomNumberGenerator.Fill(salt);
            System.Security.Cryptography.RandomNumberGenerator.Fill(nonce);
            byte[] key;
            using (var kdf = new System.Security.Cryptography.Rfc2898DeriveBytes(
                password, salt, 100_000, System.Security.Cryptography.HashAlgorithmName.SHA256))
                key = kdf.GetBytes(32);
            byte[] plain = File.ReadAllBytes(src);
            byte[] cipher = new byte[plain.Length];
            byte[] tag = new byte[16];
            using (var aes = new System.Security.Cryptography.AesGcm(key, 16))
                aes.Encrypt(nonce, plain, cipher, tag);
            try { Array.Clear(key, 0, key.Length); } catch { }
            try { Array.Clear(plain, 0, plain.Length); } catch { }
            using var fs = File.Create(dst);
            fs.Write(AesMagic, 0, AesMagic.Length);
            fs.Write(salt, 0, salt.Length);
            fs.Write(nonce, 0, nonce.Length);
            fs.Write(cipher, 0, cipher.Length);
            fs.Write(tag, 0, tag.Length);
        }

        private static byte[] DecryptBytes(byte[] blob, string password)
        {
            int head = AesMagic.Length + 16 + 12;
            if (blob.Length < head + 16)
                throw new InvalidDataException("Файл повреждён.");
            for (int i = 0; i < AesMagic.Length; i++)
                if (blob[i] != AesMagic[i])
                    throw new InvalidDataException("Не бэкап PupSeekRDP.");
            byte[] salt = new byte[16], nonce = new byte[12], tag = new byte[16];
            Buffer.BlockCopy(blob, AesMagic.Length, salt, 0, 16);
            Buffer.BlockCopy(blob, AesMagic.Length + 16, nonce, 0, 12);
            int clen = blob.Length - head - 16;
            byte[] cipher = new byte[clen], plain = new byte[clen];
            Buffer.BlockCopy(blob, head, cipher, 0, clen);
            Buffer.BlockCopy(blob, head + clen, tag, 0, 16);
            byte[] key;
            using (var kdf = new System.Security.Cryptography.Rfc2898DeriveBytes(
                password, salt, 100_000, System.Security.Cryptography.HashAlgorithmName.SHA256))
                key = kdf.GetBytes(32);
            try
            {
                using var aes = new System.Security.Cryptography.AesGcm(key, 16);
                aes.Decrypt(nonce, cipher, tag, plain);
                return plain;
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                throw new UnauthorizedAccessException(
                    "Неверный пароль или файл повреждён.");
            }
            finally { try { Array.Clear(key, 0, key.Length); } catch { } }
        }

        private static bool HasAesMagic(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                if (fs.Length < AesMagic.Length) return false;
                byte[] buf = new byte[AesMagic.Length];
                if (fs.Read(buf, 0, buf.Length) != buf.Length) return false;
                for (int i = 0; i < buf.Length; i++)
                    if (buf[i] != AesMagic[i]) return false;
                return true;
            }
            catch { return false; }
        }

        // === восстановление ===

        // Распаковка zip обратно. targetDir=null → DataDir (прод),
        // иначе указанная папка (тесты). Шифрованный (.zip.aes) требует
        // password, расшифровывается во временный файл. Возвращает
        // (восстановлено, пропущено, ошибка). Zip-slip режется, чужой zip
        // отклоняется.
        // .kdbx в проде кладётся по пути из восстановленного keepass.json
        // (иначе конфиг будет указывать в никуда), в тестах — в targetDir.
        public static (int Restored, int Skipped, string? Error) RestoreBackup(
            string zipPath, string? targetDir = null, string? password = null)
        {
            string target = string.IsNullOrWhiteSpace(targetDir)
                ? AppPaths.DataDir : targetDir!;
            bool prod = string.IsNullOrWhiteSpace(targetDir);
            string? tmpPlain = null;
            try
            {
                if (!File.Exists(zipPath)) return (0, 0, "Файл не найден.");
                string work = zipPath;
                if (HasAesMagic(zipPath))
                {
                    if (string.IsNullOrEmpty(password))
                        return (0, 0, "Архив запаролен — укажите пароль.");
                    tmpPlain = Path.Combine(Path.GetTempPath(),
                        "psrbp-" + Guid.NewGuid().ToString("N") + ".zip");
                    try
                    {
                        var plain = DecryptBytes(File.ReadAllBytes(zipPath), password);
                        File.WriteAllBytes(tmpPlain, plain);
                        try { Array.Clear(plain, 0, plain.Length); } catch { }
                    }
                    catch (UnauthorizedAccessException ex) { return (0, 0, ex.Message); }
                    catch (Exception ex) { return (0, 0, "Расшифровка: " + ex.Message); }
                    work = tmpPlain;
                }
                Directory.CreateDirectory(target);
                var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var g in StaticGroups)
                    foreach (var f in g.Files) known.Add(f);
                using var ar = ZipFile.OpenRead(work);
                var entries = ar.Entries
                    .Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
                if (entries.Count == 0) return (0, 0, "Пустой zip.");
                bool LooksOurs(ZipArchiveEntry e)
                {
                    if (known.Contains(e.Name)) return true;
                    var full = e.FullName.Replace('\\', '/');
                    if (full.StartsWith("mmc/", StringComparison.OrdinalIgnoreCase)) return true;
                    if (full.StartsWith("monitor_data/", StringComparison.OrdinalIgnoreCase)) return true;
                    if (e.Name.StartsWith("audit_", StringComparison.OrdinalIgnoreCase)
                        && e.Name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) return true;
                    if (e.Name.EndsWith(".kdbx", StringComparison.OrdinalIgnoreCase)) return true;
                    return false;
                }
                if (!Path.GetFileName(zipPath).StartsWith("PupSeekRDP-backup-",
                        StringComparison.OrdinalIgnoreCase)
                    && !HasAesMagic(work)
                    && !entries.Any(LooksOurs))
                    return (0, 0, "Не похож на бэкап PupSeekRDP.");
                string? vaultDbPath = null;
                if (prod)
                {
                    try { vaultDbPath = ReadDbPathFromZip(ar); } catch { }
                }
                int ok = 0, skip = 0;
                foreach (var e in entries)
                {
                    try
                    {
                        var dest = MapEntry(e, target, prod, vaultDbPath);
                        if (dest == null) { skip++; continue; }
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        e.ExtractToFile(dest, overwrite: true);
                        ok++;
                    }
                    catch { skip++; }
                }
                try { Logger.Info($"Backup: восстановлено {ok}, пропущено {skip} из {zipPath}"); }
                catch { }
                return (ok, skip, null);
            }
            catch (Exception ex)
            {
                try { Logger.Warn($"Backup restore failed: {ex.Message}"); } catch { }
                return (0, 0, ex.Message);
            }
            finally
            {
                if (tmpPlain != null)
                    try { File.Delete(tmpPlain); } catch { }
            }
        }

        private static string? MapEntry(ZipArchiveEntry e, string target,
            bool prod, string? vaultDbPath)
        {
            string full = e.FullName.Replace('\\', '/');
            // Zip-slip: выход наверх, абсолютные пути, диски.
            if (full.Contains("..") || Path.IsPathRooted(full)
                || full.StartsWith("/") || full.IndexOf(':') >= 0) return null;
            if (full.StartsWith("mmc/", StringComparison.OrdinalIgnoreCase))
            {
                string baseDir = prod ? AppPaths.MmcDirPath
                    : Path.Combine(target, "mmc");
                return Path.Combine(baseDir, full.Substring(4));
            }
            if (full.StartsWith("monitor_data/", StringComparison.OrdinalIgnoreCase))
            {
                string baseDir;
                if (prod)
                {
                    try { baseDir = AppPaths.MonitorDataDir; }
                    catch { baseDir = Path.Combine(AppPaths.DataDir, "monitor_data"); }
                }
                else baseDir = Path.Combine(target, "monitor_data");
                return Path.Combine(baseDir, full.Substring(13));
            }
            if (full.Contains("/")) return null; // чужие подпапки не разбрасываем
            string name = e.Name;
            if (name.EndsWith(".kdbx", StringComparison.OrdinalIgnoreCase))
            {
                if (prod && !string.IsNullOrWhiteSpace(vaultDbPath))
                {
                    try
                    {
                        Directory.CreateDirectory(
                            Path.GetDirectoryName(vaultDbPath)!);
                        return vaultDbPath;
                    }
                    catch { }
                }
                return Path.Combine(target, name);
            }
            return Path.Combine(target, name);
        }

        private static string? ReadDbPathFromZip(ZipArchive ar)
        {
            try
            {
                var ke = ar.Entries.FirstOrDefault(x =>
                    string.Equals(x.Name, "keepass.json",
                        StringComparison.OrdinalIgnoreCase));
                if (ke == null) return null;
                using var sr = new StreamReader(ke.Open());
                using var doc = JsonDocument.Parse(sr.ReadToEnd());
                foreach (var p in doc.RootElement.EnumerateObject())
                    if (string.Equals(p.Name, "DbPath",
                            StringComparison.OrdinalIgnoreCase))
                        return p.Value.GetString();
            }
            catch { }
            return null;
        }

        // === настройки + расписание ===

        public sealed class Schedule
        {
            public bool Enabled;
            public string Period = "daily";   // daily | weekly
            public string Time = "03:00";     // HH:mm
            public int Keep = 7;
            public string LastRun = "";       // yyyy-MM-dd HH:mm
            public string DestDir = "";
            public string ZipPath = "";       // последний zip восстановления
            public List<string> SelectedIds = new();
        }

        public static Schedule LoadSettings(string? path = null)
        {
            var def = new Schedule
            {
                DestDir = "",
                SelectedIds = StaticGroups.Select(g => g.Id)
                    .Concat(new[] { "audit", "monitor" }).ToList()
            };
            try
            {
                var p = path ?? SettingsPath;
                if (!File.Exists(p)) return def;
                // v3.9.5 — IncludeFields: Schedule на полях, без этого
                // System.Text.Json молча писал/читал {}.
                var s = JsonSerializer.Deserialize<Schedule>(
                    File.ReadAllText(p), JsonOpts) ?? def;
                if (s.SelectedIds == null || s.SelectedIds.Count == 0)
                    s.SelectedIds = def.SelectedIds;
                if (s.Keep < 1) s.Keep = 7;
                if (s.Period != "weekly") s.Period = "daily";
                return s;
            }
            catch { return def; }
        }

        public static void SaveSettings(Schedule s, string? path = null)
        {
            try
            {
                var p = path ?? SettingsPath;
                Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                File.WriteAllText(p, JsonSerializer.Serialize(s, JsonOpts));
            }
            catch (Exception ex)
            {
                try { Logger.Warn($"Backup settings save failed: {ex.Message}"); } catch { }
            }
        }

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            IncludeFields = true
        };

        // Чистая логика «пора ли»: тестируется харнессом.
        public static bool IsDue(Schedule s, DateTime now)
        {
            try
            {
                if (s == null || !s.Enabled) return false;
                if (string.IsNullOrWhiteSpace(s.DestDir)) return false;
                if (!TimeSpan.TryParse(s.Time, out var tod)) tod = new TimeSpan(3, 0, 0);
                TimeSpan step = s.Period == "weekly"
                    ? TimeSpan.FromDays(7) : TimeSpan.FromDays(1);
                if (string.IsNullOrWhiteSpace(s.LastRun)
                    || !DateTime.TryParseExact(s.LastRun, "yyyy-MM-dd HH:mm",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var last))
                    return now.TimeOfDay >= tod; // ни разу: сегодня после HH:mm
                if (now < last.Add(step)) return false;
                return now.TimeOfDay >= tod;
            }
            catch { return false; }
        }

        private static System.Threading.Timer? _timer;
        private static int _running;

        // Проверка раз в минуту + догон просроченного на старте.
        // Прогон на фоновом потоке (только IO, без UI).
        public static void StartScheduler()
        {
            try
            {
                StopScheduler();
                Tick(true);
                _timer = new System.Threading.Timer(_ => Tick(false), null, 60_000, 60_000);
                Logger.Info("Backup: планировщик запущен");
            }
            catch (Exception ex)
            {
                try { Logger.Warn($"Backup scheduler failed: {ex.Message}"); } catch { }
            }
        }

        public static void StopScheduler()
        {
            try { _timer?.Dispose(); } catch { }
            _timer = null;
        }

        private static void Tick(bool startup)
        {
            if (Interlocked.Exchange(ref _running, 1) == 1) return;
            try
            {
                var s = LoadSettings();
                if (!IsDue(s, DateTime.Now)) return;
                var (zip, n, err) = RunBackup(s.DestDir, s.SelectedIds);
                if (zip != null)
                {
                    RotateBackups(s.DestDir, s.Keep);
                    s.LastRun = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                    SaveSettings(s);
                }
                else if (startup)
                {
                    try { Logger.Warn($"Backup (план): {err}"); } catch { }
                }
            }
            catch { }
            finally { Interlocked.Exchange(ref _running, 0); }
        }
    }
}
