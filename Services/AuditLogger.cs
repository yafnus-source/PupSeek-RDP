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
