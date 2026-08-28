using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ShadowRDP.Models;

namespace ShadowRDP.Services
{
    // JSON-backed address book. Mirror of Load-AddressBook / Save-AddressBook
    // from the PowerShell version.
    //
    // Canonical path: %PROGRAMDATA%\ShadowRDP\addressbook.json (via AppPaths)
    //   Shared between non-elevated and elevated instances. Per-user
    //   %LOCALAPPDATA% doesn't work because UAC switches the user (ivanov
    //   → IvanovAD), and each profile has its own %LOCALAPPDATA%.
    //
    // Migration: on first run, if the canonical file is missing, we look in
    // legacy locations (per-user profiles from before the path change, the
    // .ps1's $PSScriptRoot, etc.) and copy the first one we find.
    //
    // Schema: array of AddressBookEntry.
    // Filter rule: skip entries where BOTH Computer AND User are empty (junk).
    public static class AddressBookStore
    {
        private static readonly object _lock = new object();
        private static readonly string _dir = AppPaths.DataDir;
        private static readonly string _path = AppPaths.AddressBookPath;

        // Locations to probe on first-run migration, in priority order.
        private static readonly string[] _legacyPaths = new[]
        {
            // Per-user LOCALAPPDATA from the user who launched the .exe
            // (might be ivanov's or IvanovAD's profile, depending on
            // which one first opened the app):
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"ShadowRDP\addressbook.json"),
            // .ps1 dev location (next to the script):
            @"C:\temp\Clo\addressbook.json",
            // Old Inno Setup install location:
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Programs\Shadow RDP\addressbook.json"),
            // Next to the running .exe:
            Path.Combine(AppContext.BaseDirectory, "addressbook.json")
        };

        private static readonly JsonSerializerOptions _jsonOpts = new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public static string FilePath => _path;

        // On first run, migrate data from legacy locations if the canonical
        // file doesn't exist yet. Idempotent — once the canonical file is in
        // place, this is a no-op.
        private static void MigrateFromLegacyIfNeeded()
        {
            if (File.Exists(_path)) return;
            foreach (var legacy in _legacyPaths)
            {
                try
                {
                    if (!File.Exists(legacy)) continue;
                    Directory.CreateDirectory(_dir);
                    File.Copy(legacy, _path, overwrite: false);
                    Logger.Info($"AddressBookStore: migrated from legacy '{legacy}' -> '{_path}'");
                    return;
                }
                catch (Exception ex)
                {
                    Logger.Warn($"AddressBookStore: migration from '{legacy}' failed: {ex.Message}");
                }
            }
        }

        public static List<AddressBookEntry> Load()
        {
            lock (_lock)
            {
                try
                {
                    MigrateFromLegacyIfNeeded();
                    if (!File.Exists(_path)) return new List<AddressBookEntry>();
                    var json = File.ReadAllText(_path, Encoding.UTF8);
                    if (string.IsNullOrWhiteSpace(json)) return new List<AddressBookEntry>();

                    var parsed = JsonSerializer.Deserialize<List<AddressBookEntry>>(json);
                    if (parsed == null) return new List<AddressBookEntry>();

                    var filtered = parsed
                        .Where(e => e != null)
                        .Where(e => !string.IsNullOrWhiteSpace(e.Computer) ||
                                    !string.IsNullOrWhiteSpace(e.User))
                        .ToList();

                    Logger.Info($"AddressBookStore: loaded {filtered.Count} entries");
                    return filtered;
                }
                catch (Exception ex)
                {
                    Logger.Warn($"AddressBookStore.Load failed: {ex.Message}");
                    return new List<AddressBookEntry>();
                }
            }
        }

        public static void Save(IEnumerable<AddressBookEntry> entries)
        {
            lock (_lock)
            {
                try
                {
                    Directory.CreateDirectory(_dir);
                    var list = entries?
                        .Where(e => e != null)
                        .Where(e => !string.IsNullOrWhiteSpace(e.Computer) ||
                                    !string.IsNullOrWhiteSpace(e.User))
                        .ToList() ?? new List<AddressBookEntry>();
                    var json = JsonSerializer.Serialize(list, _jsonOpts);
                    File.WriteAllText(_path, json, new UTF8Encoding(false));
                    Logger.Info($"AddressBookStore: saved {list.Count} entries to {_path}");
                }
                catch (Exception ex)
                {
                    Logger.Warn($"AddressBookStore.Save failed: {ex.Message}");
                }
            }
        }
    }
}
