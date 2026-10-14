using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ShadowRDP.Services
{
    // v3.9.5 — закладки окна справки («?»): выделенный текст + место,
    // чтобы вернуться. Файл help_bookmarks.json.
    public sealed class HelpBookmark
    {
        public int Section { get; set; }
        public string SectionTitle { get; set; } = "";
        public int Offset { get; set; }
        public string Text { get; set; } = "";
        public DateTime CreatedAt { get; set; }
    }

    public static class HelpBookmarkStore
    {
        // Переопределяется в харнессе, чтобы не трогать живой стор.
        public static string Path { get; set; } = AppPaths.HelpBookmarksPath;

        private static readonly JsonSerializerOptions _opts = new() { WriteIndented = true };

        public static List<HelpBookmark> Load()
        {
            try
            {
                if (!File.Exists(Path)) return new List<HelpBookmark>();
                var json = File.ReadAllText(Path, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json)) return new List<HelpBookmark>();
                return JsonSerializer.Deserialize<List<HelpBookmark>>(json) ?? new List<HelpBookmark>();
            }
            catch { return new List<HelpBookmark>(); }
        }

        public static void Save(List<HelpBookmark> items)
        {
            try
            {
                File.WriteAllText(Path,
                    JsonSerializer.Serialize(items ?? new List<HelpBookmark>(), _opts),
                    new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
