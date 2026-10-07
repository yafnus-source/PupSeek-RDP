using System;
using System.IO;
using System.Text;
using System.Text.Json;
using ShadowRDP.Models;

namespace ShadowRDP.Services
{
    public static class FtpUploadStore
    {
        private static readonly object _lock = new();
        private static readonly JsonSerializerOptions _opts = new()
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static FtpUploadSettings Load()
        {
            lock (_lock)
            {
                try
                {
                    var p = AppPaths.FtpSettingsPath;
                    if (!File.Exists(p)) return new FtpUploadSettings();
                    var j = File.ReadAllText(p, Encoding.UTF8);
                    if (string.IsNullOrWhiteSpace(j)) return new FtpUploadSettings();
                    return JsonSerializer.Deserialize<FtpUploadSettings>(j) ?? new FtpUploadSettings();
                }
                catch { return new FtpUploadSettings(); }
            }
        }

        public static void Save(FtpUploadSettings s)
        {
            lock (_lock)
            {
                try
                {
                    Directory.CreateDirectory(AppPaths.DataDir);
                    File.WriteAllText(AppPaths.FtpSettingsPath, JsonSerializer.Serialize(s ?? new FtpUploadSettings(), _opts), new UTF8Encoding(false));
                }
                catch (Exception ex) { try { Logger.Warn($"FtpUploadStore Save: {ex.Message}"); } catch { } }
            }
        }
    }
}
