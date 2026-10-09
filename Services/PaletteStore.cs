using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ShadowRDP.Services
{
    // v3.9.5 — какие строки каталога палитры (Ctrl+K) скрыты настройкой.
    // Файл palette.json: { "hidden": ["ключ", ...] }. Нет файла = всё видно.
    // Паттерн как у TodoStore: System.Text.Json + UTF-8 без BOM.
    public static class PaletteStore
    {
        private sealed class Dto
        {
            public List<string>? hidden { get; set; }
        }

        private static readonly JsonSerializerOptions _opts = new()
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        private static readonly JsonSerializerOptions _readOpts = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        public static HashSet<string> LoadHidden()
        {
            try
            {
                var path = AppPaths.PalettePath;
                if (!File.Exists(path)) return new HashSet<string>(StringComparer.Ordinal);
                var json = File.ReadAllText(path, Encoding.UTF8);
                var dto = JsonSerializer.Deserialize<Dto>(json, _readOpts);
                return dto?.hidden == null
                    ? new HashSet<string>(StringComparer.Ordinal)
                    : new HashSet<string>(dto.hidden, StringComparer.Ordinal);
            }
            catch (Exception ex)
            {
                try { Logger.Warn($"PaletteStore Load failed: {ex.Message}"); } catch { }
                return new HashSet<string>(StringComparer.Ordinal);
            }
        }

        public static void SaveHidden(IEnumerable<string> keys)
        {
            try
            {
                AppPaths.EnsureDataDir();
                var dto = new Dto { hidden = new List<string>(keys) };
                File.WriteAllText(AppPaths.PalettePath,
                    JsonSerializer.Serialize(dto, _opts), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                try { Logger.Warn($"PaletteStore Save failed: {ex.Message}"); } catch { }
            }
        }
    }
}
