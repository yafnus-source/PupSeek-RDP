using System;
using System.IO;

namespace ShadowRDP.Services
{
    // v3.9.4 — импорт .rdp (mstsc). Формат: строки "key:type:value"
    // (type: s=string, i=int), '#' — комментарий, значения могут
    // содержать ':' (host:port) — поэтому Split с count=3.
    // Берём: full address (обязателен), username, administrative session.
    public static class RdpFileParser
    {
        public sealed class Parsed
        {
            public string Address { get; set; } = "";
            public string Username { get; set; } = "";
            public bool AdminSession { get; set; }
        }

        public static Parsed ParseFile(string path) => Parse(File.ReadAllText(path));

        public static Parsed Parse(string content)
        {
            var r = new Parsed();
            foreach (var raw in content.Split('\n'))
            {
                var line = raw.Trim('\r', ' ', '\t');
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                var parts = line.Split(':', 3);
                if (parts.Length < 3) continue;
                var key = parts[0].Trim().ToLowerInvariant();
                var type = parts[1].Trim().ToLowerInvariant();
                var value = parts[2].Trim();
                if (key == "full address" && type == "s") r.Address = value;
                else if (key == "username" && type == "s") r.Username = value;
                else if (key == "administrative session" && type == "i") r.AdminSession = value == "1";
            }
            return r;
        }
    }
}
