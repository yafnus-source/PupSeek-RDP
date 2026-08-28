using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShadowRDP.Services
{
    // Persists the list of open RDP Manager sessions so they can be
    // restored when the user reopens the manager. Saved whenever a
    // session is added or removed, and on RdpManagerForm.FormClosing.
    //
    // Path: %PROGRAMDATA%\ShadowRDP\rdp_sessions.json (via AppPaths).
    public static class RdpSessionStore
    {
        public class Entry
        {
            public string Computer { get; set; } = "";
            public string? Ip { get; set; }
            // v2.1.49 — user-defined group name for the session. Empty =
            // "Без группы". Used by RdpManagerForm to render grouped sidebar.
            public string? Group { get; set; }
            // v3.0.7 — when false, RestoreSessions adds the session as a
            // sidebar entry WITHOUT connecting mstsc (pending until the user
            // clicks the tab). Lets the user opt out of "open everything on
            // startup" per-session. Default true for backwards compat (old
            // JSON without this field deserialises to default(bool)=false,
            // but we explicitly set true in RestoreSessions if missing).
            public bool AutoOpen { get; set; } = true;
            // v3.8.x — drag&drop sidebar ordering. Explicit position within the
            // session's group (RDP + custom sessions share the numbering).
            public int SortOrder { get; set; }
        }

        private static readonly object _lock = new object();
        private static readonly string _dir = AppPaths.DataDir;
        private static readonly string _path = AppPaths.RdpSessionsPath;

        public static List<Entry> Load()
        {
            lock (_lock)
            {
                try
                {
                    if (!File.Exists(_path)) return new List<Entry>();
                    var json = File.ReadAllText(_path, Encoding.UTF8);
                    if (string.IsNullOrWhiteSpace(json)) return new List<Entry>();
                    var list = JsonSerializer.Deserialize<List<Entry>>(json) ?? new List<Entry>();
                    return list
                        .Where(e => !string.IsNullOrWhiteSpace(e.Computer))
                        .ToList();
                }
                catch (Exception ex)
                {
                    Logger.Warn($"RdpSessionStore.Load failed: {ex.Message}");
                    return new List<Entry>();
                }
            }
        }

        public static void Save(IEnumerable<Entry> entries)
        {
            lock (_lock)
            {
                try
                {
                    Directory.CreateDirectory(_dir);
                    var list = entries
                        .Where(e => !string.IsNullOrWhiteSpace(e.Computer))
                        .Select(e => new Entry {
                            Computer = e.Computer.Trim(),
                            Ip = string.IsNullOrWhiteSpace(e.Ip) ? null : e.Ip.Trim(),
                            Group = string.IsNullOrWhiteSpace(e.Group) ? null : e.Group.Trim(),
                            AutoOpen = e.AutoOpen,
                            SortOrder = e.SortOrder
                        })
                        .ToList();
                    var options = new JsonSerializerOptions
                    {
                        WriteIndented = false,
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                    };
                    var json = JsonSerializer.Serialize(list, options);
                    File.WriteAllText(_path, json, new UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    Logger.Warn($"RdpSessionStore.Save failed: {ex.Message}");
                }
            }
        }
    }
}
