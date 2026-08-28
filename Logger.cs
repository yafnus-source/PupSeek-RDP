using System;
using System.IO;
using System.Text;

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
    public static class Logger
    {
        private static readonly object _lock = new object();
        private static readonly string _logDir = AppPaths.DataDir;
        private static readonly string _logPath = AppPaths.DebugLogPath;

        public static void Info(string message) => Write("INFO", message);
        public static void Warn(string message) => Write("WARN", message);
        public static void Error(string message) => Write("ERROR", message);

        private static void Write(string level, string message)
        {
            try
            {
                lock (_lock)
                {
                    Directory.CreateDirectory(_logDir);
                    // UTF-8 without BOM — C# doesn't have the PS 5.1 BOM quirk.
                    var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}{Environment.NewLine}";
                    File.AppendAllText(_logPath, line, new UTF8Encoding(false));
                }
            }
            catch
            {
                // Logging is best-effort. Never throw from here — would crash
                // the app for what's essentially diagnostic data.
            }
        }
    }
}
