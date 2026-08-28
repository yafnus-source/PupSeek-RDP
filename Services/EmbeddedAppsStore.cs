using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShadowRDP.Services
{
    // v3.6.8 — Persists the list of MMC consoles the user embedded via the
    // RDP Manager "Встроить ММС" button.
    //
    // Path: %PROGRAMDATA%\PupSeekRDP\embedded_apps.json (same dir as the
    // other stores).
    //
    // Schema: a flat JSON array of EmbeddedAppEntry, most-recently-used
    // first (Load sorts by LastUsedUtc descending). Only .msc consoles are
    // kept (arbitrary programs are no longer embedded).
    public static class EmbeddedAppsStore
    {
        private static readonly object _lock = new object();
        private static readonly string _dir = AppPaths.DataDir;
        private static readonly string _path = AppPaths.EmbeddedAppsPath;

        // v3.6.8 — the store is MMC-only: entries that aren't console files
        // (left over from the old "Встроить программу" era) are dropped.
        private static bool IsConsole(Models.EmbeddedAppEntry e) =>
            !string.IsNullOrWhiteSpace(e.Path)
            && string.Equals(Path.GetExtension(e.Path), ".msc", StringComparison.OrdinalIgnoreCase);

        private static readonly JsonSerializerOptions _jsonOpts = new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public static List<Models.EmbeddedAppEntry> Load()
        {
            lock (_lock)
            {
                try
                {
                    if (!File.Exists(_path)) return new List<Models.EmbeddedAppEntry>();
                    var json = File.ReadAllText(_path, Encoding.UTF8);
                    if (string.IsNullOrWhiteSpace(json)) return new List<Models.EmbeddedAppEntry>();
                    var list = JsonSerializer.Deserialize<List<Models.EmbeddedAppEntry>>(json)
                               ?? new List<Models.EmbeddedAppEntry>();
                    return list
                        .Where(IsConsole)
                        .OrderByDescending(e => e.LastUsedUtc)
                        .ToList();
                }
                catch (Exception ex)
                {
                    Logger.Warn($"EmbeddedAppsStore.Load failed: {ex.Message}");
                    return new List<Models.EmbeddedAppEntry>();
                }
            }
        }

        public static void Save(IEnumerable<Models.EmbeddedAppEntry> entries)
        {
            lock (_lock)
            {
                try
                {
                    Directory.CreateDirectory(_dir);
                    var list = entries
                        .Where(IsConsole)
                        .OrderByDescending(e => e.LastUsedUtc)
                        .ToList();
                    var json = JsonSerializer.Serialize(list, _jsonOpts);
                    File.WriteAllText(_path, json, new UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    Logger.Warn($"EmbeddedAppsStore.Save failed: {ex.Message}");
                }
            }
        }
    }
}
