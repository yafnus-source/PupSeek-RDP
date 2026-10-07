using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;

namespace ShadowRDP
{
    // Append-only file logger. Mirror of Write-Log from the PowerShell version.
    // Path: %PROGRAMDATA%\ShadowRDP\debug.log (via AppPaths) — shared between
    // the non-elevated and elevated instances of the app. Per-user
    // %LOCALAPPDATA% wouldn't work because UAC switches the user (ivanov →
    // IvanovAD), and each profile has its own %LOCALAPPDATA%.
    //
    // Severity levels: INFO (default), WARN, ERROR. Each line is prefixed with
    // [timestamp] [LEVEL] for grep-ability.
    //
    // v3.9.3 — Фаза 1 оптимизации: запись в фоне. Раньше каждый вызов делал
    // lock + File.AppendAllText в потоке вызвавшего (часто UI-поток) при
    // файле 20МБ/380тыс.строк — UI регулярно ждал диск. Теперь вызов только
    // кладёт строку в очередь (наносекунды); пишет фоновый поток пачками
    // раз в 500мс. Ротация 5МБ × 3 файла. Сброс очереди при выходе.
    public static class Logger
    {
        private static readonly string _logDir = AppPaths.DataDir;
        private static readonly string _logPath = AppPaths.DebugLogPath;
        private static readonly ConcurrentQueue<string> _queue = new ConcurrentQueue<string>();
        private static readonly AutoResetEvent _signal = new AutoResetEvent(false);
        private static readonly object _fileLock = new object();
        private const long MaxBytes = 5L * 1024 * 1024;

        static Logger()
        {
            try
            {
                var writer = new Thread(WriterLoop) { IsBackground = true, Name = "Logger" };
                writer.Start();
                AppDomain.CurrentDomain.ProcessExit += (s, e) => { try { Drain(); } catch { } };
            }
            catch { }
        }

        public static void Info(string message) => Enqueue("INFO", message);
        public static void Warn(string message) => Enqueue("WARN", message);
        public static void Error(string message) => Enqueue("ERROR", message);

        private static void Enqueue(string level, string message)
        {
            try
            {
                _queue.Enqueue($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}{Environment.NewLine}");
                _signal.Set();
            }
            catch
            {
                // Logging is best-effort. Never throw from here.
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

        // Сброс очереди на диск одним Append. Вызывается фоном и при выходе.
        private static void Drain()
        {
            try
            {
                if (_queue.IsEmpty) return;
                var sb = new StringBuilder();
                while (_queue.TryDequeue(out var line)) sb.Append(line);
                if (sb.Length == 0) return;
                lock (_fileLock)
                {
                    Directory.CreateDirectory(_logDir);
                    RotateIfNeeded(sb.Length);
                    // UTF-8 without BOM — C# doesn't have the PS 5.1 BOM quirk.
                    File.AppendAllText(_logPath, sb.ToString(), new UTF8Encoding(false));
                }
            }
            catch
            {
                // Logging is best-effort. Never throw from here — would crash
                // the app for what's essentially diagnostic data.
            }
        }

        private static void RotateIfNeeded(int incoming)
        {
            try
            {
                var fi = new FileInfo(_logPath);
                if (!fi.Exists || fi.Length + incoming <= MaxBytes) return;
                string p1 = _logPath + ".1";
                string p2 = _logPath + ".2";
                try { if (File.Exists(p2)) File.Delete(p2); } catch { }
                try { if (File.Exists(p1)) File.Move(p1, p2); } catch { }
                try { File.Move(_logPath, p1); } catch { }
            }
            catch { }
        }
    }
}
