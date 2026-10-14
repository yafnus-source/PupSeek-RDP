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
            // v3.9.3CF — синяя тема: фон белый (был крем #F5F0EB).
            Background       = Color.FromArgb(0xFF, 0xFF, 0xFF),
            // v3.9.3CD — синяя тема: Card стальной светлый (был крем #FAF7F4):
            // фон книги, хостов/статуса Событий, Web-секции.
            Card            = Color.FromArgb(0xEC, 0xF1, 0xF7),
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
            StatusOnline    = Color.FromArgb(240, 160, 80),
            StatusActive    = Color.FromArgb(99, 102, 241),
            StatusOffline   = Color.FromArgb(214, 211, 209),
            HeaderBar       = Color.FromArgb(41, 106, 160),
            MenuIconBrown   = Color.FromArgb(146, 64, 14),
            Sidebar         = Color.FromArgb(0x16, 0x2B, 0x41),
            SidebarItemHover= Color.FromArgb(0x24, 0x46, 0x6B),
            SidebarGroupBg  = Color.FromArgb(0x0E, 0x1E, 0x30),
            SidebarSubFg     = Color.FromArgb(0xB9, 0xC9, 0xDE),
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
            GridHeaderBg     = Color.White,
            GridHeaderFg     = Color.FromArgb(28, 25, 23),
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
            // v3.9.3BG — ховер #E8ECE8 во всех таблицах.
            HoverRow        = Color.FromArgb(0xE8, 0xE8, 0xE8),
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
            PillActiveBg     = Color.FromArgb(0xF0, 0xA0, 0x50),
            PillActiveFg     = Color.White,
            PillActiveBorder = Color.FromArgb(0xF0, 0xA0, 0x50),
            GridHeaderBg     = Color.White,
            GridHeaderFg     = Color.FromArgb(43, 42, 32),
            OutlineBorder    = Color.FromArgb(0x72, 0x6D, 0x61),
            OutlineFg        = Color.FromArgb(0x72, 0x6D, 0x61),
            GridSelectionFore= Color.FromArgb(0x2B, 0x2A, 0x20),
            ButtonBg         = Color.FromArgb(0xE8, 0xEC, 0xE8),
        };

        private static ThemePalette _current = Light;
        public static ThemePalette Current => _current;
        public static string CurrentName { get; private set; } = "Light";

        // v3.9.5 — цвета поте́мно: у синей (Light) и оливковой (Olive)
        // свои настройки. Слои: секция темы в colors.json → гамма темы
        // (theme_gamma.json, кнопка «Сохранить гамму») → встроенная палитра.
        // Старый плоский colors.json мигрирует в ОБЕ секции без потерь.
        private sealed class ThemeColors
        {
            public Color? PillBg, PillFg, PillBorder, HeroBg, RdpBg, DialogBg;
        }
        private static readonly Dictionary<string, ThemeColors> _overrides = new(StringComparer.OrdinalIgnoreCase)
        { ["Light"] = new ThemeColors(), ["Olive"] = new ThemeColors() };
        private static readonly Dictionary<string, ThemeColors> _gamma = new(StringComparer.OrdinalIgnoreCase)
        { ["Light"] = new ThemeColors(), ["Olive"] = new ThemeColors() };
        private static ThemeColors Ov => _overrides.TryGetValue(CurrentName, out var c) ? c : _overrides["Light"];
        private static ThemeColors Gm => _gamma.TryGetValue(CurrentName, out var c) ? c : _gamma["Light"];

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
            LoadGamma();
        }

        private static void ReadSection(Dictionary<string, string> dict, ThemeColors dst)
        {
            Color? Get(string k)
            {
                try
                {
                    if (!dict.TryGetValue(k, out var v) || string.IsNullOrWhiteSpace(v)) return null;
                    var c = HexToColor(v);
                    return c.IsEmpty ? null : (Color?)c;
                }
                catch { return null; }
            }
            dst.PillBg = Get("PillActiveBg");
            dst.PillFg = Get("PillActiveFg");
            dst.PillBorder = Get("PillActiveBorder");
            dst.HeroBg = Get("HeroBg");
            dst.RdpBg = Get("RdpHeaderBg");
            dst.DialogBg = Get("DialogHeaderBg");
        }

        private static Dictionary<string, string> DumpSection(ThemeColors s)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (s.PillBg.HasValue) dict["PillActiveBg"] = ColorToHex(s.PillBg.Value);
            if (s.PillFg.HasValue) dict["PillActiveFg"] = ColorToHex(s.PillFg.Value);
            if (s.PillBorder.HasValue) dict["PillActiveBorder"] = ColorToHex(s.PillBorder.Value);
            if (s.HeroBg.HasValue) dict["HeroBg"] = ColorToHex(s.HeroBg.Value);
            if (s.RdpBg.HasValue) dict["RdpHeaderBg"] = ColorToHex(s.RdpBg.Value);
            if (s.DialogBg.HasValue) dict["DialogHeaderBg"] = ColorToHex(s.DialogBg.Value);
            return dict;
        }

        private static readonly string[] _themeNames = new[] { "Light", "Olive" };

        private static void LoadPillColors()
        {
            try
            {
                foreach (var s in _overrides.Values)
                    s.PillBg = s.PillFg = s.PillBorder = s.HeroBg = s.RdpBg = s.DialogBg = null;
                var cpath = AppPaths.ColorsPath;
                if (!File.Exists(cpath)) return;
                var txt = File.ReadAllText(cpath).Trim();
                if (string.IsNullOrWhiteSpace(txt)) return;
                var root = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(txt);
                if (root == null) return;
                bool perTheme = false;
                foreach (var tn in _themeNames)
                    if (root.TryGetValue(tn, out var el) && el.ValueKind == JsonValueKind.Object) perTheme = true;
                if (perTheme)
                {
                    foreach (var tn in _themeNames)
                        if (root.TryGetValue(tn, out var el) && el.ValueKind == JsonValueKind.Object)
                            try { ReadSection(el.Deserialize<Dictionary<string, string>>()!, _overrides[tn]); } catch { }
                }
                else
                {
                    // Legacy плоский формат — в обе секции без потерь.
                    try
                    {
                        var flat = JsonSerializer.Deserialize<Dictionary<string, string>>(txt);
                        if (flat != null)
                        {
                            ReadSection(flat, _overrides["Light"]);
                            ReadSection(flat, _overrides["Olive"]);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        // Persist per-theme custom colours (null = use gamma/built-in).
        private static void SaveCustomColors()
        {
            try
            {
                var root = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var tn in _themeNames)
                    root[tn] = DumpSection(_overrides[tn]);
                File.WriteAllText(AppPaths.ColorsPath,
                    JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        // v3.9.5 — гамма: снимок всех текущих цветов ТЕКУЩЕЙ темы как её
        // умолчаний (theme_gamma.json, секции Light/Olive). Новый слой
        // между секцией colors.json и встроенной палитрой.
        private static void LoadGamma()
        {
            try
            {
                foreach (var s in _gamma.Values)
                    s.PillBg = s.PillFg = s.PillBorder = s.HeroBg = s.RdpBg = s.DialogBg = null;
                var path = AppPaths.ThemeGammaPath;
                if (!File.Exists(path)) return;
                var txt = File.ReadAllText(path).Trim();
                if (string.IsNullOrWhiteSpace(txt)) return;
                var root = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(txt);
                if (root == null) return;
                bool perTheme = false;
                foreach (var tn in _themeNames)
                    if (root.TryGetValue(tn, out var el) && el.ValueKind == JsonValueKind.Object) perTheme = true;
                if (perTheme)
                {
                    foreach (var tn in _themeNames)
                        if (root.TryGetValue(tn, out var el) && el.ValueKind == JsonValueKind.Object)
                            try { ReadSection(el.Deserialize<Dictionary<string, string>>()!, _gamma[tn]); } catch { }
                }
                else
                {
                    try
                    {
                        var flat = JsonSerializer.Deserialize<Dictionary<string, string>>(txt);
                        if (flat != null)
                        {
                            ReadSection(flat, _gamma["Light"]);
                            ReadSection(flat, _gamma["Olive"]);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        // v3.9.5 — кнопка «Сохранить гамму» («Настройки цвета»): текущие
        // эффективные цвета становятся умолчаниями ТЕКУЩЕЙ темы.
        public static void SaveGamma()
        {
            try
            {
                AppPaths.EnsureDataDir();
                var g = _gamma[CurrentName];
                g.HeroBg = HeroBg;
                g.RdpBg = RdpHeaderBg;
                g.DialogBg = DialogHeaderBg;
                g.PillBg = PillActiveBg;
                g.PillFg = PillActiveFg;
                g.PillBorder = PillActiveBorder;
                var root = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var tn in _themeNames)
                    root[tn] = DumpSection(_gamma[tn]);
                File.WriteAllText(AppPaths.ThemeGammaPath,
                    JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        public static void SetPillColors(Color bg, Color fg, Color border)
        {
            Ov.PillBg = bg;
            Ov.PillFg = fg;
            Ov.PillBorder = border;
            SaveCustomColors();
            try { PillColorsChanged?.Invoke(); } catch { }
        }
        // v3.9.3CY — сброс кастомных цветов пил (null = гамма/палитра темы).
        public static void ResetPillColors()
        {
            Ov.PillBg = null;
            Ov.PillFg = null;
            Ov.PillBorder = null;
            SaveCustomColors();
            try { PillColorsChanged?.Invoke(); } catch { }
        }
        public static event Action? PillColorsChanged;

        public static string ColorToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        // v3.9.3CY — тёмный/светлый текст на произвольном фоне (пилы, свотчи).
        public static Color ContrastText(Color c)
        {
            var lum = c.R * 0.299 + c.G * 0.587 + c.B * 0.114;
            return lum > 150 ? Color.FromArgb(0x2B, 0x2A, 0x20) : Color.White;
        }        public static Color HexToColor(string h)
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
        // v3.9.3BH — HeroBg: пользовательский цвет hero (как пилы через
        // colors.json); по умолчанию HeaderBar текущей темы. HeroFg —
        // авоконтраст текста (тёмный на светлом фоне).
        public static Color HeroBg => Ov.HeroBg ?? Gm.HeroBg ?? Current.HeaderBar;
        public static Color HeroFg
        {
            get
            {
                var bg = HeroBg;
                double lum = (0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B) / 255.0;
                return lum > 0.6 ? Current.Foreground : Color.White;
            }
        }
        public static event Action? HeroColorChanged;
        public static void SetHeroColor(Color bg)
        {
            Ov.HeroBg = bg;
            SaveCustomColors();
            try { HeroColorChanged?.Invoke(); } catch { }
        }
        public static void ResetHeroColor()
        {
            Ov.HeroBg = null;
            SaveCustomColors();
            try { HeroColorChanged?.Invoke(); } catch { }
        }
        // v3.9.3CM — эксперимент: цвет области шапки RDP (как hero в Даше).
        // По умолчанию Card (как было); текст — авоконтраст; персист в colors.json.
        public static Color RdpHeaderBg => Ov.RdpBg ?? Gm.RdpBg ?? Card;
        public static Color RdpHeaderFg
        {
            get
            {
                var bg = RdpHeaderBg;
                double lum = (0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B) / 255.0;
                return lum > 0.6 ? Current.Foreground : Color.White;
            }
        }
        public static event Action? RdpHeaderColorChanged;
        public static void SetRdpHeaderColor(Color bg)
        {
            Ov.RdpBg = bg;
            SaveCustomColors();
            try { RdpHeaderColorChanged?.Invoke(); } catch { }
        }
        public static void ResetRdpHeaderColor()
        {
            Ov.RdpBg = null;
            SaveCustomColors();
            try { RdpHeaderColorChanged?.Invoke(); } catch { }
        }
        // v3.9.3CY — хедер всплывающих окон: НЕ зависит от hero. По умолчанию
        // синий HeaderBar темы (решение пользователя). Авоконтраст текста.
        public static Color DialogHeaderBg => Ov.DialogBg ?? Gm.DialogBg ?? Current.HeaderBar;
        public static Color DialogHeaderFg
        {
            get
            {
                var bg = DialogHeaderBg;
                double lum = (0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B) / 255.0;
                return lum > 0.6 ? Current.Foreground : Color.White;
            }
        }
        public static event Action? DialogHeaderColorChanged;
        public static void SetDialogHeaderColor(Color bg)
        {
            Ov.DialogBg = bg;
            SaveCustomColors();
            try { DialogHeaderColorChanged?.Invoke(); } catch { }
        }
        public static void ResetDialogHeaderColor()
        {
            Ov.DialogBg = null;
            SaveCustomColors();
            try { DialogHeaderColorChanged?.Invoke(); } catch { }
        }
        public static Color Background        => Current.Background;
        // v3.9.3CJ — диалоги в синей теме: фон #ECF1F7, карточки белые.
        public static Color DialogBackground => CurrentName != "Olive" ? Color.FromArgb(0xEC, 0xF1, 0xF7) : Background;
        public static Color DialogCard       => CurrentName != "Olive" ? Color.White : Card;
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
        public static Color PillActiveBg     => Ov.PillBg ?? Gm.PillBg ?? Current.PillActiveBg;
        public static Color PillActiveFg     => Ov.PillFg ?? Gm.PillFg ?? Current.PillActiveFg;
        public static Color PillActiveBorder => Ov.PillBorder ?? Gm.PillBorder ?? Current.PillActiveBorder;
        // v3.9.3 — пользователь задал свои цвета пил (через «Настройки цвета»):
        // пилы видов Даша тогда следуют за настройкой, иначе остаются рыжими.
        public static bool PillColorsCustomized => Ov.PillBg.HasValue || Ov.PillFg.HasValue || Ov.PillBorder.HasValue
            || Gm.PillBg.HasValue;
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
