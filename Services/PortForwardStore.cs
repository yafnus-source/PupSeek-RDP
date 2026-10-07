using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using ShadowRDP.Models;

namespace ShadowRDP.Services
{
    // v3.9.4 — хранилище SSH-туннелей (data/port_forwards.json).
    // Ключ — Id. Запущенные состояния живут только в ForwardManager.
    public static class PortForwardStore
    {
        private static readonly object _lock = new();
        private static List<PortForward>? _cache;
        private static string PathFile => Path.Combine(AppPaths.DataDir, "port_forwards.json");

        public static List<PortForward> All()
        {
            lock (_lock)
            {
                var list = new List<PortForward>(Load());
                list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                return list;
            }
        }

        public static PortForward? Find(string id)
        {
            lock (_lock)
            {
                foreach (var t in Load())
                    if (string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase))
                        return t;
                return null;
            }
        }

        public static void Upsert(PortForward t)
        {
            if (string.IsNullOrWhiteSpace(t.Id)) t.Id = Guid.NewGuid().ToString("N");
            lock (_lock)
            {
                var list = Load();
                var e = list.Find(x => string.Equals(x.Id, t.Id, StringComparison.OrdinalIgnoreCase));
                if (e == null) list.Add(t);
                else
                {
                    e.Name = t.Name;
                    e.SessionId = t.SessionId;
                    e.Kind = t.Kind;
                    e.BindPort = t.BindPort;
                    e.TargetHost = t.TargetHost;
                    e.TargetPort = t.TargetPort;
                }
                Save(list);
            }
        }

        public static bool Remove(string id)
        {
            lock (_lock)
            {
                var list = Load();
                if (list.RemoveAll(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)) > 0)
                {
                    Save(list);
                    return true;
                }
                return false;
            }
        }

        private static List<PortForward> Load()
        {
            if (_cache != null) return _cache;
            try
            {
                if (File.Exists(PathFile))
                    _cache = JsonSerializer.Deserialize<List<PortForward>>(File.ReadAllText(PathFile)) ?? new List<PortForward>();
                else
                    _cache = new List<PortForward>();
            }
            catch (Exception ex)
            {
                Logger.Warn($"PortForwardStore: load failed: {ex.Message}");
                _cache = new List<PortForward>();
            }
            return _cache;
        }

        private static void Save(List<PortForward> list)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                File.WriteAllText(PathFile, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
                _cache = list;
            }
            catch (Exception ex)
            {
                Logger.Warn($"PortForwardStore: save failed: {ex.Message}");
            }
        }
    }
}
