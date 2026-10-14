using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

[assembly: InternalsVisibleTo("Harness")]

namespace ShadowRDP.Services
{
    // v4.0 — лицензии платных модулей (гибрид: ключ всегда на список
    // модулей, пакеты — предзаполненные списки).
    //
    // ПЛАТНЫЕ ключи: PSR1-<payload>.<signature>, подпись ECDSA P-256.
    // В коде только ПУБЛИЧНЫЙ ключ — сминтить платный без приватного
    // (он лишь в tools/KeyGen вызовах и файле Lic\license-private.pem,
    // которого нет в git) нельзя. Приватный в репозиторий не класть!
    //
    // ПРОБНЫЕ ключи: PSR1T-<payload>.<signature>, HMAC на TrialSecret.
    // Честно слабый: секрет в бинаре извлекается, триал правится
    // реверсом. Платные от этого не страдают. Пробный обязан иметь
    // флаг trial (PSR1T без trial не принимается).
    public static class LicenseStore
    {
        private const string Prefix = "PSR1";
        private const string TrialPrefix = "PSR1T";

        // Публичный ключ ECDSA P-256 (SPKI DER, base64). Проверка тут,
        // подпись — только приватным у вендора.
        private const string PublicSpkiB64 =
            "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE/r8q6Hc002AMQkQkX+Jo56Cs2rlZ4g026uTRq6rhB/qH8mkmX9Rr5Nix1kEJXt5MUIJxijViZg9decXyvc3YIQ==";

        // Секрет пробных ключей (см. шапку: слабый by design).
        // Дублируется в tools/KeyGen/Program.cs — менять в обоих местах.
        private const string TrialSecret = "T3r1aL9s8eC7r4eT5k6E1y0fOrHeLp4U2sErX8wQ0pL";

        public const int TrialDays = 10;
        // Напоминание из трея, когда до конца триала осталось дней.
        public const int TrialRemindDaysLeft = 3;

        // Переопределяется в харнессе, чтобы не трогать живой стор.
        public static string Path { get; set; } = AppPaths.LicensePath;

        // Тест-режим харнесса (internal: в публичном API его нет).
        internal static bool UnlockAllForTests { get; set; }

        public sealed class LicenseData
        {
            public string Key { get; set; } = "";
            public List<string> Modules { get; set; } = new();
            public long Exp { get; set; }
            public bool Trial { get; set; }
            public bool TrialReminded { get; set; }
        }

        private static readonly JsonSerializerOptions _opts = new() { WriteIndented = true };

        private static ECDsa? _pub;
        private static ECDsa? PubKey()
        {
            try
            {
                if (_pub != null) return _pub;
                var ec = ECDsa.Create();
                ec.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PublicSpkiB64), out _);
                _pub = ec;
                return _pub;
            }
            catch { return null; }
        }

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

        private static byte[] TrialSign(byte[] payload)
        {
            using var h = new HMACSHA256(Encoding.UTF8.GetBytes(TrialSecret));
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
                bool trialOnly;
                string b64;
                if (key.StartsWith(TrialPrefix + "-", StringComparison.Ordinal))
                {
                    trialOnly = true;
                    b64 = key.Substring(TrialPrefix.Length + 1);
                }
                else if (key.StartsWith(Prefix + "-", StringComparison.Ordinal))
                {
                    trialOnly = false;
                    b64 = key.Substring(Prefix.Length + 1);
                }
                else return (null, "Не тот формат ключа (нужен PSR1-…).");
                var parts = b64.Split('.');
                if (parts.Length != 2)
                    return (null, "Ключ повреждён (не читается).");
                byte[]? payload = B64UrlDecode(parts[0]);
                byte[]? sig = B64UrlDecode(parts[1]);
                if (payload == null || sig == null)
                    return (null, "Ключ повреждён (не читается).");
                if (trialOnly)
                {
                    byte[] expect = TrialSign(payload);
                    if (!CryptographicOperations.FixedTimeEquals(expect, sig))
                        return (null, "Подпись не сошлась — ключ чужой или опечатка.");
                }
                else
                {
                    var pub = PubKey();
                    if (pub == null)
                        return (null, "Проверка недоступна.");
                    bool ok;
                    try { ok = pub.VerifyData(payload, sig, HashAlgorithmName.SHA256); }
                    catch { ok = false; }
                    if (!ok)
                        return (null, "Подпись не сошлась — ключ чужой или опечатка.");
                }
                Payload? p;
                try { p = JsonSerializer.Deserialize<Payload>(Encoding.UTF8.GetString(payload)); }
                catch { return (null, "Ключ повреждён (не читается)."); }
                if (trialOnly && (p == null || !p.trial))
                    return (null, "Не тот ключ.");
                if (p?.m == null || p.m.Count == 0)
                    return (null, "В ключе нет модулей.");
                var mods = p.m
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s.Trim().ToLowerInvariant())
                    .Distinct()
                    .ToList();
                // Чужие id отбрасываем (вперёд-совместимость).
                var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var m in ModuleCatalog.All) known.Add(m.Id);
                mods = mods.Where(m => known.Contains(m)).ToList();
                if (mods.Count == 0)
                    return (null, "В ключе нет известных модулей.");
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

        // Внутренний минт пробного (триал создаётся на машине
        // пользователя — приватного ключа там нет, поэтому HMAC).
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
            return TrialPrefix + "-" + B64UrlEncode(pb) + "." + B64UrlEncode(TrialSign(pb));
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
