using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using ShadowRDP.Models;

namespace ShadowRDP.Services
{
    // v3.9.4 — хранилище сниппетов SSH-команд (data/ssh_snippets.json).
    // Ключ — Name (case-insensitive): повторный Upsert обновляет запись.
    public static class SshSnippetStore
    {
        private static readonly object _lock = new();
        private static List<SshSnippet>? _cache;
        private static string PathFile => Path.Combine(AppPaths.DataDir, "ssh_snippets.json");

        public static List<SshSnippet> All()
        {
            lock (_lock)
            {
                var list = new List<SshSnippet>(Load());
                list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                return list;
            }
        }

        public static SshSnippet? Find(string name)
        {
            lock (_lock)
            {
                foreach (var s in Load())
                    if (string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                        return s;
                return null;
            }
        }

        public static void Upsert(SshSnippet snippet)
        {
            if (string.IsNullOrWhiteSpace(snippet.Name)) return;
            lock (_lock)
            {
                var list = Load();
                var s = list.Find(x => string.Equals(x.Name, snippet.Name, StringComparison.OrdinalIgnoreCase));
                if (s == null)
                {
                    list.Add(snippet);
                }
                else
                {
                    s.Name = snippet.Name;
                    s.Group = snippet.Group;
                    s.Command = snippet.Command;
                }
                Save(list);
            }
        }

        public static bool Remove(string name)
        {
            lock (_lock)
            {
                var list = Load();
                if (list.RemoveAll(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)) > 0)
                {
                    Save(list);
                    return true;
                }
                return false;
            }
        }

        private static List<SshSnippet> Load()
        {
            if (_cache != null) return _cache;
            try
            {
                if (File.Exists(PathFile))
                    _cache = JsonSerializer.Deserialize<List<SshSnippet>>(File.ReadAllText(PathFile)) ?? new List<SshSnippet>();
                else
                    _cache = new List<SshSnippet>();
            }
            catch (Exception ex)
            {
                Logger.Warn($"SshSnippetStore: load failed: {ex.Message}");
                _cache = new List<SshSnippet>();
            }
            return _cache;
        }

        private static void Save(List<SshSnippet> list)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                File.WriteAllText(PathFile, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
                _cache = list;
            }
            catch (Exception ex)
            {
                Logger.Warn($"SshSnippetStore: save failed: {ex.Message}");
            }
        }
    }
}
