using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace ShadowRDP.Services
{
    // v3.9.4 — хранилище доверенных SSH-хостов (аналог ~/.ssh/known_hosts).
    // Отпечаток: SHA256 от сырого host key blob в Base64 ("SHA256:…").
    // Своя стабильная метрика: сравнивается байт-в-байт, формат OpenSSH
    // не обещаем (мы не пишем файл в формате known_hosts).
    public static class KnownHostsStore
    {
        public sealed class Entry
        {
            public string Host { get; set; } = "";
            public int Port { get; set; } = 22;
            public string KeyName { get; set; } = "";
            public string Sha256 { get; set; } = "";
            public string TrustedAt { get; set; } = "";
        }

        private static readonly object _lock = new();
        private static List<Entry>? _cache;
        private static string PathFile => Path.Combine(AppPaths.DataDir, "known_hosts.json");

        public static string Fingerprint(byte[] hostKey)
        {
            return "SHA256:" + Convert.ToBase64String(SHA256.HashData(hostKey));
        }

        public static Entry? Find(string host, int port)
        {
            lock (_lock)
            {
                foreach (var e in Load())
                    if (string.Equals(e.Host, host, StringComparison.OrdinalIgnoreCase) && e.Port == port)
                        return e;
                return null;
            }
        }

        public static void Upsert(string host, int port, string keyName, string sha256)
        {
            lock (_lock)
            {
                var list = Load();
                var e = list.Find(x => string.Equals(x.Host, host, StringComparison.OrdinalIgnoreCase) && x.Port == port);
                if (e == null)
                {
                    e = new Entry { Host = host, Port = port };
                    list.Add(e);
                }
                e.KeyName = keyName;
                e.Sha256 = sha256;
                e.TrustedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                Save(list);
            }
        }

        public static void Remove(string host, int port)
        {
            lock (_lock)
            {
                var list = Load();
                if (list.RemoveAll(x => string.Equals(x.Host, host, StringComparison.OrdinalIgnoreCase) && x.Port == port) > 0)
                    Save(list);
            }
        }

        private static List<Entry> Load()
        {
            if (_cache != null) return _cache;
            try
            {
                if (File.Exists(PathFile))
                    _cache = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(PathFile)) ?? new List<Entry>();
                else
                    _cache = new List<Entry>();
            }
            catch (Exception ex)
            {
                Logger.Warn($"KnownHostsStore: load failed: {ex.Message}");
                _cache = new List<Entry>();
            }
            return _cache;
        }

        private static void Save(List<Entry> list)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                File.WriteAllText(PathFile, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
                _cache = list;
            }
            catch (Exception ex)
            {
                Logger.Warn($"KnownHostsStore: save failed: {ex.Message}");
            }
        }
    }
}
