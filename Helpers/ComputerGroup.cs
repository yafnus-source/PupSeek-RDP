using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Drawing;
using ShadowRDP.Services;

namespace ShadowRDP.Helpers
{
    // Address book computer groups. Each group has:
    //   - Stable string key (stored in AddressBookEntry.Group, JSON-friendly)
    //   - Russian display label
    //   - Badge label (short text drawn inside the coloured Group badge)
    //   - Accent colour (used as the colour-picker seed + derivation base)
    //   - Badge colours (Background / Foreground / Border), saturated enough
    //     that the badge "pops" against the light row tint.
    //
    // v3.8.x — groups are now dynamic and persisted to groups.json (via
    // AppPaths) so the user can add / rename / recolor / delete them from the
    // Book's filter-pill context menu. The four built-ins below are seeded on
    // first run; everything else is user-defined.
    public static class ComputerGroup
    {
        public const string None = "";
        public const string Users = "Users";
        public const string Servers = "Servers";
        public const string Network = "Network";
        public const string Mfu = "Mfu";

        public sealed class Info
        {
            public string Key { get; }
            public string Label { get; }
            public string BadgeLabel { get; }
            public Color Accent { get; }
            public BadgeColors Badge { get; }
            // Compatibility shim — some callers read the row tint.
            public Color Tint => Badge.Background;

            public Info(string key, string label, string badgeLabel, Color accent, BadgeColors badge)
            {
                Key = key;
                Label = label;
                BadgeLabel = badgeLabel;
                Accent = accent;
                Badge = badge;
            }
        }

        // Figma-accurate badge convention: a tinted pill with a matching
        // outline (shadcn/ui BadgeColors), not a saturated block.
        public sealed class BadgeColors
        {
            public Color Background { get; }
            public Color Foreground { get; }
            public Color Border { get; }
            public BadgeColors(Color bg, Color fg, Color border)
            {
                Background = bg; Foreground = fg; Border = border;
            }
        }

        private static readonly object _lock = new object();
        private static List<Info> _all = new List<Info>();
        private static bool _loaded = false;

        // Lazily loaded. First access (from the Book form or the group-edit
        // combo) reads groups.json, seeds the built-ins if missing, and merges
        // any group keys already present in the address book.
        public static IReadOnlyList<Info> All
        {
            get { EnsureLoaded(); return _all; }
        }

        public static void EnsureLoaded()
        {
            if (_loaded) return;
            lock (_lock)
            {
                if (_loaded) return;
                Load();
            }
        }

        private static void Load()
        {
            var list = new List<Info>();
            bool seeded = false;
            try
            {
                var path = AppPaths.GroupsPath;
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path, Encoding.UTF8);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        var dtos = JsonSerializer.Deserialize<List<GroupDto>>(json);
                        if (dtos != null)
                            foreach (var d in dtos) list.Add(FromDto(d));
                    }
                }
                if (list.Count == 0)
                {
                    list.AddRange(DefaultGroups());
                    seeded = true;
                }
            }
            catch (Exception ex)
            {
                try { Logger.Warn($"ComputerGroup.Load failed: {ex.Message}"); } catch { }
                if (list.Count == 0) list.AddRange(DefaultGroups());
            }

            // Merge any groups referenced by existing address-book entries but
            // not yet known (legacy / externally-authored groups). They get a
            // neutral badge so they don't vanish.
            try
            {
                var entries = AddressBookStore.Load();
                foreach (var g in entries
                    .Select(e => (e.Group ?? "").Trim())
                    .Where(g => g.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (list.Any(i => string.Equals(i.Key, g, StringComparison.OrdinalIgnoreCase))) continue;
                    list.Add(new Info(g, g, g, Color.FromArgb(150, 150, 150),
                        new BadgeColors(Color.FromArgb(235, 235, 235), Color.FromArgb(90, 90, 90), Color.FromArgb(210, 210, 210))));
                }
            }
            catch { }

            _all = list;
            _loaded = true;
            if (seeded) Save();
        }

        public static void Save()
        {
            lock (_lock)
            {
                try
                {
                    Directory.CreateDirectory(AppPaths.DataDir);
                    var dtos = _all.Select(ToDto).ToList();
                    var json = JsonSerializer.Serialize(dtos, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(AppPaths.GroupsPath, json, new UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    try { Logger.Warn($"ComputerGroup.Save failed: {ex.Message}"); } catch { }
                }
            }
        }

        // Returns the Info for a group key, or null if None / unknown.
        public static Info? ByKey(string? key)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(key)) return null;
            lock (_lock)
                return _all.FirstOrDefault(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase));
        }

        // Badge colours for a group key, or null if unknown (caller then
        // skips the badge and just shows the raw text).
        public static BadgeColors? BadgeFor(string? key)
        {
            return ByKey(key)?.Badge;
        }

        public static void AddGroup(string key, string label, Color accent)
        {
            EnsureLoaded();
            lock (_lock)
                _all.Add(new Info(key, label, label, accent, MakeBadge(accent)));
            Save();
        }

        public static void UpdateGroup(string key, string label, Color accent)
        {
            EnsureLoaded();
            lock (_lock)
            {
                var idx = _all.FindIndex(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase));
                if (idx < 0) return;
                _all[idx] = new Info(key, label, label, accent, MakeBadge(accent));
            }
            Save();
        }

        public static void RemoveGroup(string key)
        {
            EnsureLoaded();
            lock (_lock)
                _all.RemoveAll(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase));
            Save();
        }

        // Builds a stable, unique key from a display label (letters/digits
        // only; numeric/empty labels fall back to "Group"; collisions get a
        // numeric suffix).
        public static string GenerateKey(string label)
        {
            EnsureLoaded();
            var slug = new string(label.Where(char.IsLetterOrDigit).ToArray());
            if (string.IsNullOrEmpty(slug)) slug = "Group";
            string key = slug;
            int i = 2;
            while (_all.Any(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase)))
                key = slug + i++;
            return key;
        }

        // Derives badge colours from a single accent colour:
        //   Background = very light tint of the accent
        //   Foreground = darkened accent (readable text)
        //   Border     = light accent outline
        private static BadgeColors MakeBadge(Color a)
        {
            Color Mix(Color c, double t) => Color.FromArgb(
                (int)(a.R + (c.R - a.R) * t),
                (int)(a.G + (c.G - a.G) * t),
                (int)(a.B + (c.B - a.B) * t));
            return new BadgeColors(Mix(Color.White, 0.82), Mix(Color.Black, 0.45), Mix(Color.White, 0.35));
        }

        private static IEnumerable<Info> DefaultGroups()
        {
            yield return new Info(Users, "Пользователи", "Пользователь",
                Color.FromArgb(180, 170, 60),
                new BadgeColors(Color.FromArgb(254, 252, 232), Color.FromArgb(113, 63, 18), Color.FromArgb(253, 230, 138)));
            yield return new Info(Servers, "Сервера", "Сервер",
                Color.FromArgb(222, 139, 139),
                new BadgeColors(Color.FromArgb(254, 226, 226), Color.FromArgb(159, 18, 57), Color.FromArgb(254, 202, 202)));
            yield return new Info(Network, "Сетевое", "Сетевое",
                Color.FromArgb(139, 186, 139),
                new BadgeColors(Color.FromArgb(204, 251, 241), Color.FromArgb(15, 118, 110), Color.FromArgb(167, 243, 208)));
            yield return new Info(Mfu, "МФУ", "МФУ",
                Color.FromArgb(210, 180, 103),
                new BadgeColors(Color.FromArgb(254, 243, 199), Color.FromArgb(146, 64, 14), Color.FromArgb(253, 230, 138)));
        }

        // === JSON DTO (groups.json) ===
        private class GroupDto
        {
            public string Key { get; set; } = "";
            public string Label { get; set; } = "";
            public string BadgeLabel { get; set; } = "";
            public string Accent { get; set; } = "#000000";
            public string Bg { get; set; } = "#000000";
            public string Fg { get; set; } = "#000000";
            public string Border { get; set; } = "#000000";
        }

        private static Info FromDto(GroupDto d)
        {
            var accent = HexToColor(d.Accent);
            var badge = new BadgeColors(HexToColor(d.Bg), HexToColor(d.Fg), HexToColor(d.Border));
            return new Info(d.Key, d.Label, string.IsNullOrEmpty(d.BadgeLabel) ? d.Label : d.BadgeLabel, accent, badge);
        }

        private static GroupDto ToDto(Info i) => new GroupDto
        {
            Key = i.Key,
            Label = i.Label,
            BadgeLabel = i.BadgeLabel,
            Accent = ColorToHex(i.Accent),
            Bg = ColorToHex(i.Badge.Background),
            Fg = ColorToHex(i.Badge.Foreground),
            Border = ColorToHex(i.Badge.Border)
        };

        private static string ColorToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        private static Color HexToColor(string h)
        {
            try
            {
                var hex = h.TrimStart('#');
                if (hex.Length == 6) hex = "FF" + hex;
                return Color.FromArgb(Convert.ToInt32(hex, 16));
            }
            catch
            {
                return Color.Gray;
            }
        }
    }
}
