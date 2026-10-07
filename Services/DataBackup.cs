using System;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace ShadowRDP.Services
{
    // v3.9.4 — ежедневный бэкап ключевых JSON в DataDir\backup\
    // (data_ГГГГ-ММ-ДД.zip), ротация: остаются последние BackupsToKeep.
    // Ошибка бэкапа никогда не валит старт приложения.
    public static class DataBackup
    {
        public static int BackupsToKeep = 7;

        private static readonly string[] Files =
        {
            "addressbook.json", "custom_sessions.json", "rdp_sessions.json",
            "todo.json", "colors.json", "known_hosts.json",
            "ssh_snippets.json", "port_forwards.json"
        };

        public static string BackupDir => Path.Combine(AppPaths.DataDir, "backup");

        public static string[] TrackedFiles => Files;

        public static void RunDailyIfDue()
        {
            try
            {
                Directory.CreateDirectory(BackupDir);
                var marker = Path.Combine(BackupDir, ".last_backup");
                if (File.Exists(marker) && File.GetLastWriteTime(marker).Date == DateTime.Today)
                    return;

                var zip = Path.Combine(BackupDir, $"data_{DateTime.Now:yyyy-MM-dd}.zip");
                if (File.Exists(zip)) File.Delete(zip);
                using (var zs = File.Create(zip))
                using (var ar = new ZipArchive(zs, ZipArchiveMode.Create))
                {
                    foreach (var f in Files)
                    {
                        var p = Path.Combine(AppPaths.DataDir, f);
                        if (File.Exists(p))
                            ar.CreateEntryFromFile(p, f, CompressionLevel.Optimal);
                    }
                }
                File.WriteAllText(marker, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                Rotate();
                Logger.Info($"DataBackup: ежедневный бэкап → {zip}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"DataBackup: {ex.Message}");
            }
        }

        private static void Rotate()
        {
            try
            {
                var zips = Directory.GetFiles(BackupDir, "data_*.zip")
                    .OrderBy(File.GetLastWriteTimeUtc).ToList();
                while (zips.Count > BackupsToKeep)
                {
                    try { File.Delete(zips[0]); } catch { }
                    zips.RemoveAt(0);
                }
            }
            catch { }
        }
    }
}
