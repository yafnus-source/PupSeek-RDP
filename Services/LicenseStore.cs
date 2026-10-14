using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ShadowRDP.Services
{
    // v4.0 — лицензии платных модулей (гибрид: ключ всегда на список
    // модулей, пакеты — предзаполненные списки).
    //
    // Формат ключа: PSR1-<payload>.<signature>, где payload = base64url
    // от {"m":["events","zabbix"],"exp":1799459200,"trial":false},
    // signature = base64url(HMAC-SHA256(payload, Secret)). Без exp —
    // бессрочно. Подпись сверяется FixedTimeEquals при каждом старте,
    // файл license.json можно править руками — не поможет.
    public static class LicenseStore
    {
        private const string Prefix = "PSR1";

        // Секрет подписи. Скомпилирован в бинарь: от школьника защищает,
        // от реверса — нет (осознанно: честность + удобство).
        private const string Secret = "V9x4Q7mZ2pL8sT5wK3nR6yU0aB1cD4eF7gH2jJ5kK8";

        public const int TrialDays = 10;
        // Напоминание из трея, когда до конца триала осталось дней.
        public const int TrialRemindDaysLeft = 3;

        // Переопределяется в харнессе, чтобы не трогать живой стор.
        public static string Path { get; set; } = AppPaths.LicensePath;

        // Тест-режим харнесса: все модули разрешены (реальные проверки
        // ключей идут отдельными тестами до его включения).
        public static bool UnlockAllForTests { get; set; }

        public sealed class LicenseData
        {
            public string Key { get; set; } = "";
            public List<string> Modules { get; set; } = new();
            public long Exp { get; set; }
            public bool Trial { get; set; }
            public bool TrialReminded { get; set; }
        }

        private static readonly JsonSerializerOptions _opts = new() { WriteIndented = true };

        private static string B64UrlEncode(byte[] b) =>
            Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static byte[]? B64UrlDecode(string s)
        {
            try
            {
                string t = s.Replace('-', '+').Replace('_', '/');
                switch (t.Length % 4) { case 2: t += "=="; break; case 3: t += "="; break; }
                return Convert.FromBase64String(t);
            }
            catch { return null; }
        }

        private static byte[] SignBytes(byte[] payload)
        {
            using var h = new HMACSHA256(Encoding.UTF8.GetBytes(Secret));
            return h.ComputeHash(payload);
        }

        private sealed class Payload
        {
            public List<string>? m { get; set; }
            public long exp { get; set; }
            public bool trial { get; set; }
        }

        // Проверка строки ключа без сохранения. Возвращает данные или
        // текст ошибки для показа пользователю.
        public static (LicenseData? data, string? error) Validate(string key)
        {
            try
            {
                key = (key ?? "").Trim();
                var parts = key.Split('.');
                if (parts.Length != 2 || parts[0].Length <= Prefix.Length + 1)
                    return (null, "Не тот формат ключа (нужен PSR1-… . …).");
                if (!parts[0].StartsWith(Prefix + "-", StringComparison.Ordinal))
                    return (null, "Не тот формат ключа (нужен PSR1-…).");
                byte[]? payload = B64UrlDecode(parts[0].Substring(Prefix.Length + 1));
                byte[]? sig = B64UrlDecode(parts[1]);
                if (payload == null || sig == null)
                    return (null, "Ключ повреждён (не читается).");
                byte[] expect = SignBytes(payload);
                if (!CryptographicOperations.FixedTimeEquals(expect, sig))
                    return (null, "Подпись не сошлась — ключ чужой или опечатка.");
                Payload? p;
                try { p = JsonSerializer.Deserialize<Payload>(Encoding.UTF8.GetString(payload)); }
                catch { return (null, "Ключ повреждён (не читается)."); }
                if (p?.m == null || p.m.Count == 0)
                    return (null, "В ключе нет модулей.");
                var mods = p.m
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s.Trim().ToLowerInvariant())
                    .Distinct()
                    .ToList();
                if (mods.Count == 0)
                    return (null, "В ключе нет модулей.");
                if (p.exp > 0 && DateTimeOffset.UtcNow.ToUnixTimeSeconds() > p.exp)
                    return (null, "Срок ключа вышел.");
                return (new LicenseData { Key = key, Modules = mods, Exp = p.exp, Trial = p.trial }, null);
            }
            catch (Exception ex) { return (null, $"Не проверяется: {ex.Message}"); }
        }

        public static void Save(LicenseData d)
        {
            try
            {
                File.WriteAllText(Path, JsonSerializer.Serialize(d ?? new LicenseData(), _opts),
                    new UTF8Encoding(false));
            }
            catch { }
        }

        // Загрузка + перепроверка подписи и срока. Битый/просроченный =
        // пустой (free-режим), файл не удаляем.
        public static LicenseData Load()
        {
            try
            {
                if (!File.Exists(Path)) return new LicenseData();
                var json = File.ReadAllText(Path, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json)) return new LicenseData();
                var d = JsonSerializer.Deserialize<LicenseData>(json);
                if (d == null || string.IsNullOrWhiteSpace(d.Key)) return new LicenseData();
                var (v, _) = Validate(d.Key);
                if (v == null) return new LicenseData();
                v.TrialReminded = d.TrialReminded;
                return v;
            }
            catch { return new LicenseData(); }
        }

        // Внутренний минт (триал): тот же подписанный формат, флаг trial.
        // Правка файла руками подпись не переживёт.
        private static string MintTrialKey()
        {
            var mods = new List<string>();
            foreach (var m in ModuleCatalog.All) mods.Add(m.Id);
            mods.Sort(StringComparer.Ordinal);
            var payload = JsonSerializer.Serialize(new Payload
            {
                m = mods,
                exp = DateTimeOffset.UtcNow.AddDays(TrialDays).ToUnixTimeSeconds(),
                trial = true
            });
            byte[] pb = Encoding.UTF8.GetBytes(payload);
            return Prefix + "-" + B64UrlEncode(pb) + "." + B64UrlEncode(SignBytes(pb));
        }

        // Первый старт 4.x: всем модулям триал 10 дней.
        public static LicenseData EnsureTrial()
        {
            try
            {
                if (File.Exists(Path)) return Load();
                var d = new LicenseData { Key = MintTrialKey(), TrialReminded = false };
                var (v, err) = Validate(d.Key);
                if (v == null) return new LicenseData();
                v.TrialReminded = false;
                Save(v);
                return v;
            }
            catch { return new LicenseData(); }
        }

        public static HashSet<string> ActiveModules()
        {
            try
            {
                if (UnlockAllForTests)
                {
                    var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var m in ModuleCatalog.All) all.Add(m.Id);
                    return all;
                }
                return new HashSet<string>(Load().Modules, StringComparer.OrdinalIgnoreCase);
            }
            catch { return new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
        }

        // Остаток пробных дней (>0) или -1 (не триал).
        public static int TrialDaysLeft()
        {
            try
            {
                var d = Load();
                if (!d.Trial || d.Exp <= 0) return -1;
                long left = d.Exp - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (left <= 0) return -1;
                return (int)Math.Ceiling(left / 86400.0);
            }
            catch { return -1; }
        }

        public static void MarkTrialReminded()
        {
            try
            {
                var d = Load();
                if (!d.Trial) return;
                d.TrialReminded = true;
                Save(d);
            }
            catch { }
        }
    }
}
