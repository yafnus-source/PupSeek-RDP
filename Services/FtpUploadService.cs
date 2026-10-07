using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentFTP;
using Renci.SshNet;
using ShadowRDP.Models;

namespace ShadowRDP.Services
{
    public static class FtpUploadService
    {
        private static Timer? _timer;
        private static readonly object _lock = new();
        private static bool _running;
        private static CancellationTokenSource? _cts;
        private static DateTime _lastRun = DateTime.MinValue;

        public static event Action<string, int, int>? OnProgress; // file, cur, total
        public static event Action<bool, string>? OnResult; // ok, msg

        public static void Start()
        {
            lock (_lock)
            {
                if (_timer != null) return;
                var s = FtpUploadStore.Load();
                if (!s.Enabled) { Logger.Info("FtpUploadService: disabled"); return; }
                _cts = new CancellationTokenSource();
                TimeSpan due = GetDue(s);
                TimeSpan period = GetPeriod(s);
                _timer = new Timer(async _ => await TickAsync(), null, due, period);
                Logger.Info($"FtpUploadService: started proto={s.Protocol} host={s.Host}:{s.Port} mode={s.ScheduleMode} due={due} period={period}");
            }
        }

        public static void Stop()
        {
            lock (_lock)
            {
                try { _cts?.Cancel(); } catch { }
                _cts?.Dispose(); _cts = null;
                _timer?.Dispose(); _timer = null;
                Logger.Info("FtpUploadService: stopped");
            }
        }

        public static void Restart() { Stop(); Start(); }

        private static TimeSpan GetPeriod(FtpUploadSettings s)
        {
            if (s.ScheduleMode == FtpScheduleMode.Interval) return TimeSpan.FromMinutes(Math.Max(5, s.IntervalMinutes));
            return TimeSpan.FromMinutes(1); // tick every minute for Daily/Weekly
        }

        private static TimeSpan GetDue(FtpUploadSettings s)
        {
            if (s.RunOnStartup) return TimeSpan.FromSeconds(15);
            if (s.ScheduleMode == FtpScheduleMode.Interval) return TimeSpan.FromMinutes(Math.Max(5, s.IntervalMinutes));
            var next = GetNextOccurrence(s);
            var d = next - DateTime.Now;
            return d < TimeSpan.Zero ? TimeSpan.FromSeconds(10) : d;
        }

        private static DateTime GetNextOccurrence(FtpUploadSettings s)
        {
            if (!TimeSpan.TryParse(s.DailyTime, out var ts)) ts = new TimeSpan(2, 0, 0);
            var now = DateTime.Now;
            var today = now.Date + ts;
            if (s.ScheduleMode == FtpScheduleMode.Daily)
                return today > now ? today : today.AddDays(1);
            if (s.ScheduleMode == FtpScheduleMode.Weekly)
            {
                var days = s.WeeklyDays.Count > 0 ? s.WeeklyDays : new System.Collections.Generic.List<DayOfWeek> { DayOfWeek.Monday };
                for (int i = 0; i < 8; i++)
                {
                    var cand = today.AddDays(i);
                    if (days.Contains(cand.DayOfWeek) && cand > now) return cand;
                }
                return today.AddDays(7);
            }
            return now.AddMinutes(Math.Max(5, s.IntervalMinutes));
        }

        private static async Task TickAsync()
        {
            var s = FtpUploadStore.Load();
            if (!s.Enabled) return;
            if (s.ScheduleMode != FtpScheduleMode.Interval)
            {
                var next = GetNextOccurrence(s);
                // allow 60s window after scheduled time
                if ((DateTime.Now - next).TotalSeconds > 60 || (next - DateTime.Now).TotalSeconds > 60) return;
                if ((DateTime.Now - _lastRun).TotalMinutes < 5) return; // debounce
            }
            await UploadAsync();
        }

        public static async Task UploadAsync(CancellationToken ct = default)
        {
            var (_, report) = await UploadWithReportAsync(ct);
            // UploadWithReport already fires OnResult
        }

        public static async Task<(bool ok, string report)> UploadWithReportAsync(CancellationToken ct = default)
        {
            if (_running) return (false, "Уже выполняется выгрузка — подожди");
            _running = true;
            var sb = new System.Text.StringBuilder();
            try
            {
                var s = FtpUploadStore.Load();
                sb.AppendLine($"Хост: {s.Host}:{s.Port}  Протокол: {s.Protocol}  Папка: {s.RemoteDir}");
                sb.AppendLine($"Режим: {(s.Protocol == FtpProtocol.Sftp ? (s.SftpAuthMode == SftpAuthMode.PrivateKey ? "SFTP PK" : "SFTP") : s.Protocol == FtpProtocol.FtpsExplicit ? "FTPS Explicit" : "FTP")}  Пассив: {s.DataConnection}");
                if (!s.Enabled) { var msg = "FTP выгрузка отключена (чекбокс)"; OnResult?.Invoke(false, msg); sb.AppendLine("❌ " + msg); return (false, sb.ToString()); }
                if (string.IsNullOrWhiteSpace(s.Host)) { var msg = "FTP host не задан"; OnResult?.Invoke(false, msg); sb.AppendLine("❌ " + msg); return (false, sb.ToString()); }

                var cred = ResolveCreds(s);
                if (cred == null) { var msg = "Нет кредов в KeePass — открой Креды FTP (PupSeekRDP/FTP)"; OnResult?.Invoke(false, msg); sb.AppendLine("❌ " + msg); return (false, sb.ToString()); }
                sb.AppendLine($"Креды: {cred.Value.Login} (из KeePass: {s.KeepassEntry})");

                var files = new System.Collections.Generic.List<(string local, string remote)>();
                if (s.UploadEvents && File.Exists(AppPaths.DashboardEventsPath)) files.Add((AppPaths.DashboardEventsPath, "dashboard_events.json"));
                if (s.UploadZabbix && File.Exists(AppPaths.ZabbixAgentHistoryPath)) files.Add((AppPaths.ZabbixAgentHistoryPath, "zabbix_agent_history.json"));
                if (files.Count == 0) { var msg = "Нет файлов для выгрузки (файлы не найдены локально)"; OnResult?.Invoke(false, msg); sb.AppendLine("❌ " + msg); sb.AppendLine($"Проверь: {AppPaths.DashboardEventsPath} / {AppPaths.ZabbixAgentHistoryPath}"); return (false, sb.ToString()); }

                foreach (var (local, remote) in files)
                {
                    var fi = new FileInfo(local);
                    sb.AppendLine($"• {remote} — {fi.Length} байт ({fi.Length / 1024.0:0.0} КБ)  локально: {local}");
                }

                int cur = 0;
                foreach (var (local, remote) in files)
                {
                    cur++;
                    OnProgress?.Invoke(remote, cur, files.Count);
                    sb.AppendLine($"→ Загрузка {cur}/{files.Count} {remote} …");
                    await UploadSingleAsync(s, cred.Value, local, remote, ct);
                    sb.AppendLine($"  ✓ {remote} загружен");
                }

                // Строгая проверка: файлы реально лежат и размер совпадает
                sb.AppendLine($"Проверка папки {s.RemoteDir} на сервере…");
                var (verifyOk, verifyMsg) = await VerifyRemoteDetailedAsync(s, cred.Value, files, ct);
                sb.AppendLine(verifyMsg);
                if (!verifyOk)
                {
                    var failMsg = $"❌ Проверка не пройдена — файлы не видны в {s.RemoteDir} или размер не совпал. Папка указана неверно (попробуй /{s.RemoteDir.Trim('/')}) или нет прав.";
                    sb.AppendLine(failMsg);
                    OnResult?.Invoke(false, failMsg);
                    Logger.Warn($"FtpUploadService: verify failed for {s.Host}:{s.Port}/{s.RemoteDir}");
                    return (false, sb.ToString());
                }

                _lastRun = DateTime.Now;
                s.LastRun = _lastRun;
                FtpUploadStore.Save(s);
                OnProgress?.Invoke("", files.Count, files.Count);
                var okMsg = $"✓ Выгружено {files.Count} файл(ов) на {s.Host}:{s.Port}/{s.RemoteDir.Trim('/')}";
                sb.AppendLine(okMsg);
                sb.AppendLine($"Время: {_lastRun:dd.MM HH:mm:ss}");
                OnResult?.Invoke(true, okMsg);
                Logger.Info($"FtpUploadService: uploaded {files.Count} files to {s.Host}:{s.Port}/{s.RemoteDir} proto={s.Protocol}");
                return (true, sb.ToString());
            }
            catch (Exception ex)
            {
                Logger.Warn($"FtpUploadService UploadWithReport: {ex.Message}");
                sb.AppendLine($"❌ Ошибка: {ex.Message}");
                if (ex.InnerException != null) sb.AppendLine($"   {ex.InnerException.Message}");
                sb.AppendLine("Проверь: хост/порт, папка (создастся автоматом), креды в KeePass, брандмауэр.");
                OnResult?.Invoke(false, ex.Message);
                return (false, sb.ToString());
            }
            finally { _running = false; }
        }

        private static async Task<string> VerifyRemoteAsync(FtpUploadSettings s, (string Login, string Password) cred, System.Collections.Generic.List<string> fileNames, CancellationToken ct)
        {
            try
            {
                if (s.Protocol == FtpProtocol.Sftp)
                {
                    return await Task.Run(() =>
                    {
                        using var client = CreateSftpClient(s, cred);
                        client.Connect();
                        var dir = (s.RemoteDir ?? "/").Trim().TrimEnd('/');
                        if (string.IsNullOrWhiteSpace(dir)) dir = "/";
                        if (!client.Exists(dir)) return $"⚠ Папка {dir} не найдена (создание не удалось)";
                        var list = client.ListDirectory(dir).Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        var ok = fileNames.Count(f => list.Contains(f));
                        client.Disconnect();
                        return $"Проверка: {ok}/{fileNames.Count} файл(ов) найдены в {dir} — {(ok == fileNames.Count ? "✓ папка верная" : "⚠ не все файлы видны")}";
                    }, ct);
                }
                else
                {
                    using var client = CreateFtpClient(s, cred);
                    await client.AutoConnect(ct);
                    var dir = (s.RemoteDir ?? "/").Trim().TrimEnd('/');
                    if (string.IsNullOrWhiteSpace(dir)) dir = "/";
                    bool exists = await client.DirectoryExists(dir, ct);
                    if (!exists) return $"⚠ Папка {dir} не найдена";
                    var list = await client.GetListing(dir, ct);
                    var names = list.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var ok = fileNames.Count(f => names.Contains(f));
                    await client.Disconnect(ct);
                    return $"Проверка: {ok}/{fileNames.Count} файл(ов) найдены в {dir} — {(ok == fileNames.Count ? "✓ папка верная" : "⚠ не все файлы видны, проверь права")}";
                }
            }
            catch (Exception ex) { return $"Проверка папки не удалась: {ex.Message}"; }
        }

        private static async Task<(bool ok, string msg)> VerifyRemoteDetailedAsync(FtpUploadSettings s, (string Login, string Password) cred, System.Collections.Generic.List<(string local, string remote)> files, CancellationToken ct)
        {
            var sb = new System.Text.StringBuilder();
            try
            {
                string dir = (s.RemoteDir ?? "/").Trim().TrimEnd('/');
                if (string.IsNullOrWhiteSpace(dir)) dir = "/";
                long totalLocal = files.Sum(f => new FileInfo(f.local).Length);
                sb.AppendLine($"Локально: {files.Count} файл(ов), {totalLocal} байт");

                if (s.Protocol == FtpProtocol.Sftp)
                {
                    var result = await Task.Run(() =>
                    {
                        using var client = CreateSftpClient(s, cred);
                        client.Connect();
                        if (!client.Exists(dir)) return (false, $"❌ Папка {dir} не найдена на SFTP");
                        var sb2 = new System.Text.StringBuilder();
                        bool allOk = true;
                        foreach (var (local, remote) in files)
                        {
                            string full = dir == "/" ? "/" + remote : dir + "/" + remote;
                            if (!client.Exists(full)) { sb2.AppendLine($"❌ {remote} — не найден в {dir}"); allOk = false; continue; }
                            var attrs = client.GetAttributes(full);
                            long localSize = new FileInfo(local).Length;
                            if (attrs.Size != localSize) { sb2.AppendLine($"❌ {remote} — размер на сервере {attrs.Size} != локально {localSize}"); allOk = false; }
                            else sb2.AppendLine($"✓ {remote} — {attrs.Size} байт в {dir} — OK");
                        }
                        try { var list = client.ListDirectory(dir).Where(f => !f.IsDirectory).Select(f => f.Name).ToList(); sb2.AppendLine($"На сервере в {dir}: {string.Join(", ", list.Take(10))}{(list.Count > 10 ? $" +{list.Count - 10}" : "")}"); } catch { }
                        client.Disconnect();
                        return (allOk, sb2.ToString());
                    }, ct);
                    sb.Append(result.Item2);
                    return (result.Item1, sb.ToString());
                }
                else
                {
                    using var client = CreateFtpClient(s, cred);
                    await client.AutoConnect(ct);
                    if (!await client.DirectoryExists(dir, ct)) { await client.Disconnect(ct); return (false, sb.ToString() + $"❌ Папка {dir} не найдена на FTP"); }
                    bool allOk = true;
                    var sb2 = new System.Text.StringBuilder();
                    foreach (var (local, remote) in files)
                    {
                        string full = dir == "/" ? "/" + remote : dir + "/" + remote;
                        if (!await client.FileExists(full, ct)) { sb2.AppendLine($"❌ {remote} — не найден в {dir}"); allOk = false; continue; }
                        long remoteSize = await client.GetFileSize(full, -1, ct);
                        long localSize = new FileInfo(local).Length;
                        if (remoteSize != localSize) { sb2.AppendLine($"❌ {remote} — размер на сервере {remoteSize} != локально {localSize}"); allOk = false; }
                        else sb2.AppendLine($"✓ {remote} — {remoteSize} байт в {dir} — OK");
                    }
                    try { var list = await client.GetListing(dir, ct); sb2.AppendLine($"На сервере в {dir}: {string.Join(", ", list.Where(f => f.Type == FluentFTP.FtpObjectType.File).Select(f => f.Name).Take(10))}"); } catch { }
                    await client.Disconnect(ct);
                    sb.Append(sb2.ToString());
                    return (allOk, sb.ToString());
                }
            }
            catch (Exception ex) { return (false, sb.ToString() + $"❌ Проверка не удалась: {ex.Message}"); }
        }

        public static async Task<string?> TestConnectionAsync(FtpUploadSettings s, CancellationToken ct = default)
        {
            var cred = ResolveCreds(s);
            if (cred == null) return "Нет кредов в KeePass — создай запись в PupSeekRDP/FTP";
            if (string.IsNullOrWhiteSpace(s.Host)) return "Host пустой";
            string testName = $".pupseek_test_{Guid.NewGuid().ToString("N")[..8]}.tmp";
            string testContent = $"pupseek test {DateTime.Now:O}";
            string tmpLocal = Path.Combine(Path.GetTempPath(), testName);
            try
            {
                File.WriteAllText(tmpLocal, testContent);
                if (s.Protocol == FtpProtocol.Sftp)
                {
                    var err = await Task.Run(() =>
                    {
                        try
                        {
                            using var client = CreateSftpClient(s, cred.Value);
                            client.Connect();
                            string dir = (s.RemoteDir ?? "/").Trim().TrimEnd('/');
                            if (string.IsNullOrWhiteSpace(dir)) dir = "/";
                            if (!client.Exists(dir))
                            {
                                try { client.CreateDirectory(dir); } catch (Exception ex) { return $"Папка {dir} не создалась: {ex.Message}"; }
                            }
                            string remote = dir == "/" ? "/" + testName : dir + "/" + testName;
                            using var fs = File.OpenRead(tmpLocal);
                            client.UploadFile(fs, remote, true);
                            if (!client.Exists(remote)) return $"Подключились к {s.Host}:{s.Port} OK, но файл {testName} не появился в {dir} — папка неверная (попробуй /{s.RemoteDir?.Trim('/')}) или нет прав на запись";
                            var attrs = client.GetAttributes(remote);
                            if (attrs.Size != new FileInfo(tmpLocal).Length) return $"Файл загрузился, но размер не совпал ({attrs.Size} != {new FileInfo(tmpLocal).Length})";
                            try { client.DeleteFile(remote); } catch { }
                            client.Disconnect();
                            return null;
                        }
                        catch (Exception ex) { return ex.Message; }
                    }, ct);
                    return err;
                }
                else
                {
                    using var client = CreateFtpClient(s, cred.Value);
                    await client.AutoConnect(ct);
                    string dir = (s.RemoteDir ?? "/").Trim().TrimEnd('/');
                    if (string.IsNullOrWhiteSpace(dir)) dir = "/";
                    if (!await client.DirectoryExists(dir, ct))
                    {
                        try { await client.CreateDirectory(dir, ct); } catch (Exception ex) { await client.Disconnect(ct); return $"Папка {dir} не создалась: {ex.Message}"; }
                    }
                    string remote = dir == "/" ? "/" + testName : dir + "/" + testName;
                    var status = await client.UploadFile(tmpLocal, remote, FtpRemoteExists.Overwrite, true, FtpVerify.Retry, token: ct);
                    if (status == FtpStatus.Failed) { await client.Disconnect(ct); return $"Подключились к {s.Host}:{s.Port} OK, но upload вернул Failed для {remote} — нет прав на запись в {dir}"; }
                    if (!await client.FileExists(remote, ct)) { await client.Disconnect(ct); return $"Подключились OK, но файл {testName} не найден в {dir} после загрузки — папка указана неверно (сейчас: {dir})"; }
                    long rs = await client.GetFileSize(remote, -1, ct);
                    if (rs != new FileInfo(tmpLocal).Length) { await client.Disconnect(ct); return $"Размер на сервере {rs} != локально {new FileInfo(tmpLocal).Length}"; }
                    try { await client.DeleteFile(remote, ct); } catch { }
                    await client.Disconnect(ct);
                    return null;
                }
            }
            catch (Exception ex) { return ex.Message; }
            finally { try { if (File.Exists(tmpLocal)) File.Delete(tmpLocal); } catch { } }
        }

        private static (string Login, string Password)? ResolveCreds(FtpUploadSettings s)
        {
            try
            {
                if (!KeePassVault.IsOpen) KeePassVault.EnsureReopened();
                var entryName = string.IsNullOrWhiteSpace(s.KeepassEntry) ? s.Host : s.KeepassEntry;
                var found = KeePassVault.FindLogin(KeePassVault.FtpGroupPath, entryName);
                if (found == null && !string.Equals(entryName, s.Host, StringComparison.OrdinalIgnoreCase))
                    found = KeePassVault.FindLogin(KeePassVault.FtpGroupPath, s.Host);
                if (found != null) return found;
                var hp = $"{s.Host}:{s.Port}";
                found = KeePassVault.FindLogin(KeePassVault.FtpGroupPath, hp);
                if (found != null) return found;
                return null;
            }
            catch { return null; }
        }

        private static AsyncFtpClient CreateFtpClient(FtpUploadSettings s, (string Login, string Password) cred)
        {
            var cfg = new FtpConfig
            {
                EncryptionMode = s.Protocol == FtpProtocol.FtpsExplicit ? FtpEncryptionMode.Explicit : FtpEncryptionMode.None,
                ValidateAnyCertificate = true,
                DataConnectionType = s.DataConnection == "Active" ? FtpDataConnectionType.PORT : FtpDataConnectionType.AutoPassive,
                ConnectTimeout = 10000,
                DataConnectionConnectTimeout = 30000,
                InternetProtocolVersions = FtpIpVersion.ANY
            };
            var client = new AsyncFtpClient(s.Host, cred.Login, cred.Password, s.Port, cfg);
            return client;
        }

        private static SftpClient CreateSftpClient(FtpUploadSettings s, (string Login, string Password) cred)
        {
            string keyPath = s.SftpAuthMode == SftpAuthMode.PrivateKey ? (s.PrivateKeyPath ?? "") : "";
            if (s.SftpAuthMode == SftpAuthMode.PrivateKey && !string.IsNullOrWhiteSpace(keyPath) && File.Exists(keyPath))
            {
                var pk = new PrivateKeyFile(keyPath, string.IsNullOrWhiteSpace(cred.Password) ? null : cred.Password);
                return new SftpClient(s.Host, s.Port, cred.Login, new[] { pk });
            }
            return new SftpClient(s.Host, s.Port, cred.Login, cred.Password);
        }

        private static async Task UploadSingleAsync(FtpUploadSettings s, (string Login, string Password) cred, string localPath, string remoteName, CancellationToken ct)
        {
            string remote = (s.RemoteDir ?? "/").Trim().TrimEnd('/') + "/" + remoteName;
            if (s.Protocol == FtpProtocol.Sftp)
            {
                await Task.Run(() =>
                {
                    using var client = CreateSftpClient(s, cred);
                    client.Connect();
                    var dir = Path.GetDirectoryName(remote)?.Replace('\\', '/');
                    if (!string.IsNullOrWhiteSpace(dir))
                    {
                        try { if (!client.Exists(dir)) client.CreateDirectory(dir); } catch (Exception ex) { throw new IOException($"Не удалось создать папку {dir} на SFTP: {ex.Message}. Проверь путь и права.", ex); }
                    }
                    using var fs = File.OpenRead(localPath);
                    long localSize = fs.Length;
                    fs.Position = 0;
                    client.UploadFile(fs, remote, true);
                    if (!client.Exists(remote)) throw new IOException($"Файл {remote} не найден сразу после SFTP загрузки — неверная папка или нет прав.");
                    var attrs = client.GetAttributes(remote);
                    if (attrs.Size != localSize) throw new IOException($"Размер на сервере {attrs.Size} != локально {localSize} для {remote} — загрузка оборвалась.");
                    client.Disconnect();
                }, ct);
            }
            else
            {
                using var client = CreateFtpClient(s, cred);
                await client.AutoConnect(ct);
                var dir = remote.Contains('/') ? remote.Substring(0, remote.LastIndexOf('/')) : "";
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    try { if (!await client.DirectoryExists(dir, ct)) await client.CreateDirectory(dir, ct); } catch (Exception ex) { throw new IOException($"Не удалось создать папку {dir} на FTP: {ex.Message}. Проверь путь (должен начинаться с /) и права.", ex); }
                }
                var status = await client.UploadFile(localPath, remote, FtpRemoteExists.Overwrite, true, FtpVerify.None, token: ct);
                if (status == FtpStatus.Failed) throw new IOException($"FTP upload вернул Failed для {remoteName} в {dir} — файл не записался (диск переполнен или нет прав).");
                // строгая проверка: файл должен существовать и размер совпадать
                try
                {
                    if (!await client.FileExists(remote, ct)) throw new IOException($"Файл {remote} не найден сразу после загрузки — папка неверная или сервер отклонил запись.");
                    long remoteSize = await client.GetFileSize(remote, -1, ct);
                    long localSize = new FileInfo(localPath).Length;
                    if (remoteSize != localSize) throw new IOException($"Размер на сервере {remoteSize} != локально {localSize} для {remote} — загрузка оборвалась.");
                }
                catch (IOException) { throw; }
                catch (Exception ex) { throw new IOException($"Не удалось проверить файл {remote} после загрузки: {ex.Message}", ex); }
                await client.Disconnect(ct);
            }
        }
    }
}
