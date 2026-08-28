using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShadowRDP.Services
{
    // v2.1.83 — Persists the user's custom (non-RDP) session shortcuts:
    // SSH hosts and Web URLs that live in the RDP Manager sidebar.
    //
    // Path: %PROGRAMDATA%\ShadowRDP\custom_sessions.json (shared across
    // user accounts during UAC elevation, same dir as the other stores).
    //
    // Schema: a flat JSON array of CustomSession objects.
    public static class CustomSessionStore
    {
        private static readonly object _lock = new object();
        private static readonly string _dir = AppPaths.DataDir;
        private static readonly string _path = Path.Combine(_dir, "custom_sessions.json");

        private static readonly JsonSerializerOptions _jsonOpts = new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public static List<Models.CustomSession> Load()
        {
            lock (_lock)
            {
                try
                {
                    if (!File.Exists(_path)) return new List<Models.CustomSession>();
                    var json = File.ReadAllText(_path, Encoding.UTF8);
                    if (string.IsNullOrWhiteSpace(json)) return new List<Models.CustomSession>();
                    return JsonSerializer.Deserialize<List<Models.CustomSession>>(json)
                           ?? new List<Models.CustomSession>();
                }
                catch (Exception ex)
                {
                    Logger.Warn($"CustomSessionStore.Load failed: {ex.Message}");
                    return new List<Models.CustomSession>();
                }
            }
        }

        public static void Save(IEnumerable<Models.CustomSession> sessions)
        {
            lock (_lock)
            {
                try
                {
                    Directory.CreateDirectory(_dir);
                    // Filter out entries with empty Name OR Target — they'd
                    // render as empty rows in the sidebar.
                    var list = sessions
                        .Where(s => !string.IsNullOrWhiteSpace(s.Name) && !string.IsNullOrWhiteSpace(s.Target))
                        .ToList();
                    var json = JsonSerializer.Serialize(list, _jsonOpts);
                    File.WriteAllText(_path, json, new UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    Logger.Warn($"CustomSessionStore.Save failed: {ex.Message}");
                }
            }
        }
    }
}
