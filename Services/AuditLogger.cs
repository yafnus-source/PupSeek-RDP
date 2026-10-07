using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace ShadowRDP.Services
{
    // Аудит-журнал действий администратора: кто/когда/что сделал.
    // Отдельно от debug.log (тот шумный и ротируется с потерей истории).
    // Формат — JSONL по месяцам: %PROGRAMDATA%\PupSeekRDP\audit_2026-10.jsonl.
    // Файлы за прошлые месяцы не трогаются — история сохраняется.
    //
    // Пишутся только ФАКТЫ действий (подключился, перезагрузил, сменил
    // пароль, удалил записи). Пароли, ключи и содержимое сессий — НИКОГДА.
    public static class AuditLogger
    {
        private static readonly ConcurrentQueue<string> _queue = new ConcurrentQueue<string>();
        private static readonly AutoResetEvent _signal = new AutoResetEvent(false);
        private static readonly object _fileLock = new object();

        static AuditLogger()
        {
            try
            {
                var writer = new Thread(WriterLoop) { IsBackground = true, Name = "Audit" };
                writer.Start();
                AppDomain.CurrentDomain.ProcessExit += (s, e) => { try { Drain(); } catch { } };
            }
            catch { }
        }

        // action: "rdp.connect", "shadow", "host.restart", "session.logoff",
        // "password.change", "ssh.connect", "sftp.connect", "book.save", ...
        // result: "ok" / "fail". details — без секретов.
        public static void Write(string action, string target, string result, string details = "")
        {
            try
            {
                var rec = new
                {
                    ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    user = Environment.UserName,
                    action,
                    target = target ?? "",
                    result = result ?? "",
                    details = details ?? ""
                };
                _queue.Enqueue(JsonSerializer.Serialize(rec));
                _signal.Set();
            }
            catch
            {
                // Аудит не должен ронять приложение. Никогда.
            }
        }

        // v3.9.4 — ретеншн истории. Месяцы сравниваются с точностью до
        // месяца (день не учитывает: файл за октябрь 2025 жив, пока не
        // наступит «12 полных месяцев назад»). public — харнесс гоняет
        // чистую функцию PlanAction без файловой системы.
        public static int RetentionMonths = 12;
        public static int ArchiveSizeMb = 20;

        public enum RetentionAction { Keep, Delete, Gzip }

        public static RetentionAction PlanAction(string fileMonth, long sizeBytes,
            DateTime now, int retentionMonths, int archiveSizeMb)
        {
            if (!DateTime.TryParseExact(fileMonth, "yyyy-MM",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var month))
                return RetentionAction.Keep;
            var cutoff = new DateTime(now.AddMonths(-retentionMonths).Year,
                now.AddMonths(-retentionMonths).Month, 1);
            if (month < cutoff) return RetentionAction.Delete;
            if (sizeBytes > (long)archiveSizeMb * 1024L * 1024L)
                return RetentionAction.Gzip;
            return RetentionAction.Keep;
        }

        // Разовый запуск при старте: прошлые месяцы старше лимита —
        // удалить; слишком большие прошлые месяцы — сжать в .gz.
        // Текущий месяц не трогаем (в него пишет WriterLoop).
        public static void RunRetention()
        {
            try
            {
                var dir = Path.GetDirectoryName(AppPaths.AuditLogPath);
                if (dir == null || !Directory.Exists(dir)) return;
                var current = $"audit_{DateTime.Now:yyyy-MM}";
                foreach (var f in Directory.GetFiles(dir, "audit_*.jsonl"))
                {
                    var name = Path.GetFileName(f);
                    if (name.StartsWith(current, StringComparison.OrdinalIgnoreCase)) continue;
                    var stamp = name.Substring(6, 7);
                    var size = new FileInfo(f).Length;
                    switch (PlanAction(stamp, size, DateTime.Now, RetentionMonths, ArchiveSizeMb))
                    {
                        case RetentionAction.Delete:
                            try { File.Delete(f); } catch { }
                            break;
                        case RetentionAction.Gzip:
                            try
                            {
                                using (var inFs = File.OpenRead(f))
                                using (var outFs = File.Create(f + ".gz"))
                                using (var gz = new System.IO.Compression.GZipStream(
                                    outFs, System.IO.Compression.CompressionMode.Compress))
                                    inFs.CopyTo(gz);
                                File.Delete(f);
                            }
                            catch { }
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"AuditLogger retention: {ex.Message}");
            }
        }

        private static void WriterLoop()
        {
            try
            {
                while (true)
                {
                    _signal.WaitOne(500);
                    Drain();
                }
            }
            catch { }
        }

        private static void Drain()
        {
            try
            {
                if (_queue.IsEmpty) return;
                var sb = new StringBuilder();
                while (_queue.TryDequeue(out var line)) sb.AppendLine(line);
                if (sb.Length == 0) return;
                lock (_fileLock)
                {
                    var path = AppPaths.AuditLogPath;
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.AppendAllText(path, sb.ToString(), new UTF8Encoding(false));
                }
            }
            catch { }
        }
    }
}
