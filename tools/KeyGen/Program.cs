// Генератор ключей PupSeekRDP v4. Только для вендора.
// Платные ключи — ECDSA P-256 (приватный ключ в PEM-файле, никогда не в git).
// Пробные — HMAC тем же TrialSecret, что в продукте (честно слабый: триал
// правится реверсом; платные без приватного ключа не минтятся никак).
// Использование:
//   KeyGen --key Lic\license-private.pem --modules zabbix,events --days 30
//   KeyGen --key Lic\license-private.pem --trial
//   KeyGen --key Lic\license-private.pem --modules todo --days 0   (бессрочно)
//   KeyGen --genkey --out Lic\license-private.pem                  (новая пара; публичный SPKI — в код)
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

string? keyPath = null, modulesArg = null, verifyKey = null;
int days = 0;
bool trial = false, genkey = false;
string? outPath = null;
for (int i = 0; i < args.Length; i++)
{
    string a = args[i];
    if (a == "--key" && i + 1 < args.Length) keyPath = args[++i];
    else if (a == "--modules" && i + 1 < args.Length) modulesArg = args[++i];
    else if (a == "--days" && i + 1 < args.Length) int.TryParse(args[++i], out days);
    else if (a == "--trial") trial = true;
    else if (a == "--genkey") genkey = true;
    else if (a == "--out" && i + 1 < args.Length) outPath = args[++i];
    else if (a == "--verify" && i + 1 < args.Length) verifyKey = args[++i];
    else if (a is "--help" or "-h" or "/?")
    {
        Console.WriteLine("KeyGen --key <pem> --modules <id,id> --days <N|0=бессрочно> [--trial]");
        Console.WriteLine("KeyGen --genkey --out <pem>   (новая пара ключей)");
        return 0;
    }
}

if (genkey)
{
    if (string.IsNullOrEmpty(outPath)) { Console.Error.WriteLine("Нужен --out <pem>"); return 2; }
    using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    File.WriteAllText(outPath, ec.ExportECPrivateKeyPem());
    Console.WriteLine("PUB_SPKI_B64 (в код LicenseStore):");
    Console.WriteLine(Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo()));
    return 0;
}

if (!string.IsNullOrEmpty(verifyKey))
{
    // Независимая проверка ключа публичным ключом (тот же SPKI, что в продукте).
    const string pubB64 = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE/r8q6Hc002AMQkQkX+Jo56Cs2rlZ4g026uTRq6rhB/qH8mkmX9Rr5Nix1kEJXt5MUIJxijViZg9decXyvc3YIQ==";
    try
    {
        string k = verifyKey.Trim();
        string b64 = k.StartsWith("PSR1T-") ? k.Substring(6) : k.StartsWith("PSR1-") ? k.Substring(5) : "";
        if (b64 == "") { Console.WriteLine("VERIFY: BAD-FORMAT"); return 1; }
        var parts = b64.Split('.');
        if (parts.Length != 2) { Console.WriteLine("VERIFY: BAD-FORMAT"); return 1; }
        static byte[] Du(string s)
        {
            string t = s.Replace('-', '+').Replace('_', '/');
            switch (t.Length % 4) { case 2: t += "=="; break; case 3: t += "="; break; }
            return Convert.FromBase64String(t);
        }
        byte[] vpb = Du(parts[0]);
        byte[] vsg = Du(parts[1]);
        using var pub = ECDsa.Create();
        pub.ImportSubjectPublicKeyInfo(Convert.FromBase64String(pubB64), out _);
        bool ok = pub.VerifyData(vpb, vsg, HashAlgorithmName.SHA256);
        Console.WriteLine("VERIFY: " + (ok ? "OK" : "BAD-SIGNATURE"));
        Console.WriteLine("PAYLOAD: " + Encoding.UTF8.GetString(vpb));
        return ok ? 0 : 1;
    }
    catch (Exception ex) { Console.WriteLine("VERIFY: ERROR " + ex.Message); return 1; }
}

if (string.IsNullOrEmpty(keyPath) || !File.Exists(keyPath))
{
    Console.Error.WriteLine("Нужен --key <pem> с приватным ключом.");
    return 2;
}

string[] ids = Array.Empty<string>();
if (trial)
{
    ids = new[] { "events", "zabbix", "webmon", "inventory", "todo", "ftp-upload", "ai", "domain" };
}
else if (!string.IsNullOrEmpty(modulesArg))
{
    ids = modulesArg.Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
}
if (ids.Length == 0) { Console.Error.WriteLine("Нет модулей (--modules или --trial)."); return 2; }

var sl = ids.Select(s => s.Trim().ToLowerInvariant()).Where(s => s.Length > 0)
    .Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();
long exp = trial ? DateTimeOffset.UtcNow.AddDays(10).ToUnixTimeSeconds()
    : days != 0 ? DateTimeOffset.UtcNow.AddDays(days).ToUnixTimeSeconds() : 0;

string payload = JsonSerializer.Serialize(new { m = sl, exp = exp, trial = trial });
byte[] pb = Encoding.UTF8.GetBytes(payload);
byte[] sig;
if (trial)
{
    using var h = new HMACSHA256(Encoding.UTF8.GetBytes(KeyGenTrial.Secret));
    sig = h.ComputeHash(pb);
}
else
{
    using var ec = ECDsa.Create();
    ec.ImportFromPem(File.ReadAllText(keyPath));
    sig = ec.SignData(pb, HashAlgorithmName.SHA256);
}
string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
string prefix = trial ? "PSR1T" : "PSR1";
Console.WriteLine("M Modules : " + string.Join(", ", sl));
Console.WriteLine("Exp     : " + (exp > 0
    ? DateTimeOffset.FromUnixTimeSeconds(exp).ToLocalTime().ToString("dd.MM.yyyy") + (trial ? " (пробный)" : $" ({days} дн.)")
    : "бессрочно"));
Console.WriteLine();
Console.WriteLine(prefix + "-" + B64(pb) + "." + B64(sig));
return 0;

static class KeyGenTrial
{
    // Тот же секрет, что LicenseStore.TrialSecret в продукте.
    public const string Secret = "T3r1aL9s8eC7r4eT5k6E1y0fOrHeLp4U2sErX8wQ0pL";
}
