using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text.Json;

namespace ShadowRDP.Helpers
{
    // v3.8.2 — selectable theme system.
    //
    // Two selectable themes:
    //   - Light  ("Синяя")    : original warm cream / indigo redesign (default).
    //   - Olive  ("Оливковая"): earthy olive design (web version's 2nd theme).
    //
    // Every form/control references UiTheme.Background, UiTheme.Accent, … which
    // resolve to the CURRENT palette. The chosen theme is persisted to
    // <CommonApplicationData>/PupSeekRDP/theme.json and restored on startup
    // (before any form is constructed) by the static constructor.

    /// <summary>Immutable colour/metric set for one theme.</summary>
    public sealed class ThemePalette
    {
        // Surfaces
        public Color Background { get; init; }
        public Color Card { get; init; }
        public Color Muted { get; init; }
        public Color HeaderBg { get; init; }
        public Color HoverRow { get; init; }
        public Color MutedHover { get; init; }
        // Text
        public Color Foreground { get; init; }
        public Color MutedForeground { get; init; }
        // Accent
        public Color Accent { get; init; }
        public Color AccentHover { get; init; }
        public Color AccentFg { get; init; }
        public Color AccentSoft { get; init; }
        public Color AccentSoftBorder { get; init; }
        public Color HoverBg { get; init; }
        public Color HoverText { get; init; }
        // Status
        public Color Danger { get; init; }
        public Color DangerSoft { get; init; }
        public Color DangerBorder { get; init; }
        // Borders
        public Color Border { get; init; }
        public Color BorderSubtle { get; init; }
        // Status dots
        public Color StatusOnline { get; init; }
        public Color StatusActive { get; init; }
        public Color StatusOffline { get; init; }
        // Header bar colour (UiHeader + dialog headers + OK buttons + language indicator)
        public Color HeaderBar { get; init; }
        // Warm icon colour (menu icons)
        public Color MenuIconBrown { get; init; }
        // Sidebar (RDP Manager left panel + embedded-program title bar)
        public Color Sidebar { get; init; }
        public Color SidebarItemHover { get; init; }
        public Color SidebarGroupBg { get; init; }
        public Color SidebarSubFg { get; init; }
        // Table row alternating backgrounds
        public Color RowBase { get; init; }
        public Color RowAlt { get; init; }
        // Sidebar section tab background (Книга / Пароли buttons)
        public Color SidebarTab { get; init; }
        // Primary action button background (Обновить ФИО / Импорт из AD / Обновить всё …)
        public Color ButtonPrimary { get; init; }
        // Context-menu background
        public Color MenuBackground { get; init; }
        // Menu icon colours (Olive: all monochrome icons → #A2AD9E)
        public Color MenuIcon { get; init; }
        public Color MenuIconNeutral { get; init; }
        // Active filter-pill colours (Book)
        public Color PillActiveBg { get; init; }
        public Color PillActiveFg { get; init; }
        public Color PillActiveBorder { get; init; }
        // Book grid column-header colours
        public Color GridHeaderBg { get; init; }
        public Color GridHeaderFg { get; init; }
        // Outlined header buttons (+ Сессия / Экспорт-Импорт): border + text colour
        public Color OutlineBorder { get; init; }
        public Color OutlineFg { get; init; }
        // Grid selected-row text colour (so active row text doesn't turn accent)
        public Color GridSelectionFore { get; init; }
        // Outlined header buttons (+ Сессия / Экспорт-Импорт) background fill
        public Color ButtonBg { get; init; }
    }

    public static class UiTheme
    {
        // === Palette: Light (original cream/indigo) ===
        public static readonly ThemePalette Light = new ThemePalette
        {
            Background       = Color.FromArgb(245, 240, 235),
            Card            = Color.FromArgb(250, 247, 244),
            Muted           = Color.FromArgb(237, 232, 226),
            HeaderBg        = Color.FromArgb(220, 215, 208),
            HoverRow        = Color.FromArgb(240, 236, 230),
            MutedHover      = Color.FromArgb(226, 221, 215),
            Foreground      = Color.FromArgb(28, 25, 23),
            MutedForeground = Color.FromArgb(120, 113, 108),
            Accent          = Color.FromArgb(91, 95, 240),
            AccentHover     = Color.FromArgb(129, 140, 248),
            AccentFg        = Color.White,
            AccentSoft      = Color.FromArgb(233, 234, 252),
            AccentSoftBorder= Color.FromArgb(199, 201, 248),
            HoverBg         = Color.FromArgb(0x4A, 0x6E, 0x87),
            HoverText       = Color.White,
            Danger          = Color.FromArgb(220, 38, 38),
            DangerSoft      = Color.FromArgb(254, 226, 226),
            DangerBorder    = Color.FromArgb(254, 202, 202),
            Border          = Color.FromArgb(225, 220, 215),
            BorderSubtle    = Color.FromArgb(235, 230, 225),
            StatusOnline    = Color.FromArgb(16, 185, 129),
            StatusActive    = Color.FromArgb(99, 102, 241),
            StatusOffline   = Color.FromArgb(214, 211, 209),
            HeaderBar       = Color.FromArgb(41, 106, 160),
            MenuIconBrown   = Color.FromArgb(146, 64, 14),
            Sidebar         = Color.FromArgb(30, 58, 95),
            SidebarItemHover= Color.FromArgb(42, 79, 122),
            SidebarGroupBg  = Color.FromArgb(10, 26, 46),
            SidebarSubFg     = Color.FromArgb(176, 196, 222),
            RowBase          = Color.FromArgb(245, 240, 235),
            RowAlt           = Color.FromArgb(250, 247, 244),
            SidebarTab       = Color.FromArgb(41, 106, 160),
            ButtonPrimary    = Color.FromArgb(41, 106, 160),
            MenuBackground   = Color.FromArgb(250, 247, 244),
            MenuIcon         = Color.FromArgb(41, 106, 160),
            MenuIconNeutral  = Color.FromArgb(120, 113, 108),
            PillActiveBg     = Color.FromArgb(233, 234, 252),
            PillActiveFg     = Color.FromArgb(91, 95, 240),
            PillActiveBorder = Color.FromArgb(91, 95, 240),
            GridHeaderBg     = Color.FromArgb(237, 232, 226),
            GridHeaderFg     = Color.FromArgb(120, 113, 108),
            OutlineBorder    = Color.FromArgb(0x41, 0x6A, 0xA0),
            OutlineFg        = Color.FromArgb(41, 106, 160),
            GridSelectionFore= Color.FromArgb(91, 95, 240),
            ButtonBg         = Color.FromArgb(0xF6, 0xF8, 0xFC),
        };

        // === Palette: Olive (earthy olive; sidebar = web version's 2nd theme) ===
        public static readonly ThemePalette Olive = new ThemePalette
        {
            Background       = Color.FromArgb(0xE8, 0xE4, 0xD2),
            Card            = Color.FromArgb(0xF2, 0xEF, 0xE0),
            Muted           = Color.FromArgb(0xDB, 0xD6, 0xC0),
            HeaderBg        = Color.FromArgb(0xCD, 0xC6, 0xAC),
            HoverRow        = Color.FromArgb(0xE2, 0xDD, 0xC9),
            MutedHover      = Color.FromArgb(0xD2, 0xCC, 0xB2),
            Foreground      = Color.FromArgb(0x2B, 0x2A, 0x20),
            MutedForeground = Color.FromArgb(0x7E, 0x7A, 0x5E),
            Accent          = Color.FromArgb(0xA2, 0xAD, 0x9E),
            AccentHover     = Color.FromArgb(0x8E, 0x9A, 0x8A),
            AccentFg        = Color.White,
            AccentSoft      = Color.FromArgb(0xE8, 0xEC, 0xE8),
            AccentSoftBorder= Color.FromArgb(0xCD, 0xD3, 0xCC),
            HoverBg         = Color.FromArgb(0x56, 0x5C, 0x30),
            HoverText       = Color.White,
            Danger          = Color.FromArgb(0xA8, 0x5A, 0x4A),
            DangerSoft      = Color.FromArgb(0xEF, 0xDC, 0xDC),
            DangerBorder    = Color.FromArgb(0xE0, 0xC2, 0xB8),
            Border          = Color.FromArgb(0xD4, 0xCF, 0xBB),
            BorderSubtle    = Color.FromArgb(0xE2, 0xDD, 0xC9),
            StatusOnline    = Color.FromArgb(0x5F, 0x9E, 0x6E),
            StatusActive    = Color.FromArgb(0xA2, 0xAD, 0x9E),
            StatusOffline   = Color.FromArgb(0xC9, 0xC3, 0xAE),
            HeaderBar       = Color.FromArgb(0xA2, 0xAD, 0x9E),
            MenuIconBrown   = Color.FromArgb(0xA2, 0xAD, 0x9E),
            Sidebar         = Color.FromArgb(0x10, 0x0E, 0x0C),
            SidebarItemHover= Color.FromArgb(0x4A, 0x45, 0x3E),
            SidebarGroupBg  = Color.FromArgb(0x0B, 0x0A, 0x08),
            SidebarSubFg     = Color.FromArgb(0xC8, 0xBC, 0xA8),
            RowBase          = Color.FromArgb(0xFB, 0xF7, 0xEE),
            RowAlt           = Color.FromArgb(0xFF, 0xFD, 0xF8),
            SidebarTab       = Color.FromArgb(0x76, 0x4E, 0x4C),
            ButtonPrimary    = Color.FromArgb(0x76, 0x4E, 0x4C),
            MenuBackground   = Color.FromArgb(0xFF, 0xFF, 0xFF),
            MenuIcon         = Color.FromArgb(0xA2, 0xAD, 0x9E),
            MenuIconNeutral  = Color.FromArgb(0xA2, 0xAD, 0x9E),
            PillActiveBg     = Color.FromArgb(0xEA, 0xE4, 0xF0),
            PillActiveFg     = Color.FromArgb(0x7B, 0x6F, 0xA8),
            PillActiveBorder = Color.FromArgb(0x7B, 0x6F, 0xA8),
            GridHeaderBg     = Color.FromArgb(0xD7, 0xDC, 0xD5),
            GridHeaderFg     = Color.FromArgb(0x3A, 0x3A, 0x39),
            OutlineBorder    = Color.FromArgb(0x72, 0x6D, 0x61),
            OutlineFg        = Color.FromArgb(0x72, 0x6D, 0x61),
            GridSelectionFore= Color.FromArgb(0x2B, 0x2A, 0x20),
            ButtonBg         = Color.FromArgb(0xE8, 0xEC, 0xE8),
        };

        private static ThemePalette _current = Light;
        public static ThemePalette Current => _current;
        public static string CurrentName { get; private set; } = "Light";

        // v3.8.2 — user-customised active filter-pill colours (persisted
        // separately from the theme; null = use the current palette value).
        private static Color? _pillBg;
        private static Color? _pillFg;
        private static Color? _pillBorder;

        private static string ThemeFilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "PupSeekRDP", "theme.json");

        static UiTheme()
        {
            LoadState();
        }

        public static void LoadState()
        {
            try
            {
                var path = ThemeFilePath;
                if (File.Exists(path))
                {
                    var txt = File.ReadAllText(path).Trim();
                    CurrentName = txt == "Olive" ? "Olive" : "Light";
                }
            }
            catch {             }
            _current = (CurrentName == "Olive") ? Olive : Light;
            LoadPillColors();
        }

        private static void LoadPillColors()
        {
            try
            {
                var cpath = AppPaths.ColorsPath;
                if (File.Exists(cpath))
                {
                    var txt = File.ReadAllText(cpath).Trim();
                    if (!string.IsNullOrWhiteSpace(txt))
                    {
                        var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(txt);
                        if (dict != null)
                        {
                            if (dict.TryGetValue("PillActiveBg", out var b) && !string.IsNullOrWhiteSpace(b)) _pillBg = HexToColor(b);
                            if (dict.TryGetValue("PillActiveFg", out var f) && !string.IsNullOrWhiteSpace(f)) _pillFg = HexToColor(f);
                            if (dict.TryGetValue("PillActiveBorder", out var bo) && !string.IsNullOrWhiteSpace(bo)) _pillBorder = HexToColor(bo);
                        }
                    }
                }
            }
            catch { }
        }

        // Persist here only the pill colours (no other surface customisation).
        public static void SetPillColors(Color bg, Color fg, Color border)
        {
            _pillBg = bg;
            _pillFg = fg;
            _pillBorder = border;
            try
            {
                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (_pillBg.HasValue) dict["PillActiveBg"] = ColorToHex(_pillBg.Value);
                if (_pillFg.HasValue) dict["PillActiveFg"] = ColorToHex(_pillFg.Value);
                if (_pillBorder.HasValue) dict["PillActiveBorder"] = ColorToHex(_pillBorder.Value);
                File.WriteAllText(AppPaths.ColorsPath,
                    JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        public static string ColorToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        public static Color HexToColor(string h)
        {
            try
            {
                var hex = h.TrimStart('#');
                if (hex.Length == 6) hex = "FF" + hex;
                return Color.FromArgb(Convert.ToInt32(hex, 16));
            }
            catch { return Color.Empty; }
        }

        // Swap the active palette and persist the choice. Callers typically
        // restart the app afterwards so every surface picks up the new theme.
        public static void SetTheme(string name)
        {
            if (name != "Light" && name != "Olive") name = "Light";
            CurrentName = name;
            _current = (name == "Olive") ? Olive : Light;
            try { File.WriteAllText(ThemeFilePath, CurrentName); } catch { }
        }

        // === Current-palette accessors (existing call sites keep working) ===
        public static Color Background        => Current.Background;
        public static Color Card             => Current.Card;
        public static Color Muted            => Current.Muted;
        public static Color HeaderBg         => Current.HeaderBg;
        public static Color HoverRow         => Current.HoverRow;
        public static Color MutedHover       => Current.MutedHover;
        public static Color Foreground       => Current.Foreground;
        public static Color MutedForeground  => Current.MutedForeground;
        public static Color Accent           => Current.Accent;
        public static Color AccentHover      => Current.AccentHover;
        public static Color AccentFg         => Current.AccentFg;
        public static Color AccentSoft       => Current.AccentSoft;
        public static Color AccentSoftBorder => Current.AccentSoftBorder;
        public static Color HoverBg          => Current.HoverBg;
        public static Color HoverText        => Current.HoverText;
        public static Color Danger           => Current.Danger;
        public static Color DangerSoft       => Current.DangerSoft;
        public static Color DangerBorder     => Current.DangerBorder;
        public static Color Border           => Current.Border;
        public static Color BorderSubtle     => Current.BorderSubtle;
        public static Color StatusOnline     => Current.StatusOnline;
        public static Color StatusActive      => Current.StatusActive;
        public static Color StatusOffline    => Current.StatusOffline;
        public static Color HeaderBar        => Current.HeaderBar;
        public static Color MenuIconBrown    => Current.MenuIconBrown;
        public static Color Sidebar          => Current.Sidebar;
        public static Color SidebarItemHover => Current.SidebarItemHover;
        public static Color SidebarGroupBg   => Current.SidebarGroupBg;
        public static Color SidebarSubFg     => Current.SidebarSubFg;
        public static Color RowBase          => Current.RowBase;
        public static Color RowAlt           => Current.RowAlt;
        public static Color SidebarTab       => Current.SidebarTab;
        public static Color ButtonPrimary    => Current.ButtonPrimary;
        public static Color MenuBackground   => Current.MenuBackground;
        public static Color MenuIcon         => Current.MenuIcon;
        public static Color MenuIconNeutral  => Current.MenuIconNeutral;
        public static Color PillActiveBg     => _pillBg ?? Current.PillActiveBg;
        public static Color PillActiveFg     => _pillFg ?? Current.PillActiveFg;
        public static Color PillActiveBorder => _pillBorder ?? Current.PillActiveBorder;
        public static Color GridHeaderBg     => Current.GridHeaderBg;
        public static Color GridHeaderFg     => Current.GridHeaderFg;
        public static Color OutlineBorder    => Current.OutlineBorder;
        public static Color OutlineFg        => Current.OutlineFg;
        public static Color GridSelectionFore=> Current.GridSelectionFore;
        public static Color ButtonBg        => Current.ButtonBg;

        // === Metrics (layout constants, shared across themes) ===
        public const int RadiusMd = 6;
        public const int RadiusLg = 8;
        public const int HeaderHeight = 56;
        public const int TabsHeight = 44;
        public const int FooterHeight = 90;
    }
}
