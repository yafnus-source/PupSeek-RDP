using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using KeePassLib;
using KeePassLib.Interfaces;
using KeePassLib.Keys;
using KeePassLib.Security;
using KeePassLib.Serialization;

namespace ShadowRDP.Services
{
    // v3.7.0 — KeePass 2.x (.kdbx) integration via pt.KeePassLibStd
    // (KeePassLib 2.50 port to netstandard2.0; GPL-2.0).
    //
    // The vault is a real KeePass database file. Config (keepass.json)
    // stores only paths + an auto-unlock flag — the master password is
    // NEVER persisted. The database is opened on demand (startup if
    // AutoUnlockAtStartup, or via the "Пароли" tray/form entry) and
    // auto-locks after KeePassAutoLockMinutes of inactivity.
    //
    // Group layout (created only on demand when SAVING, never auto-created
    // on read — FindCreateGroup in this port CREATES groups even with
    // bOptional=true, so lookups walk the tree manually):
    //   /PupSeekRDP/RDP   — entries named after the computer
    //   /PupSeekRDP/SSH   — entries named "host" or "host:port"
    //   /<anything else>  — free-form groups from the "Пароли" manager
    //
    // KeePassLib API notes (this port differs from the original):
    //   - PwDatabase.Save(IStatusLogger) — NO ioInfo/key parameters.
    //   - CompositeKey.AddUserKey(...) returns void.
    //   - PwGroup.Groups.UCount is uint, GetAt(uint).
    //   - No SomeProtect — pass the raw password string to KcpPassword.
    public static class KeePassVault
    {
        public const string RootGroupName = "PupSeekRDP";
        public const string RdpGroupName = "RDP";
        public const string SshGroupName = "SSH";
        // v3.7.2 — Web group for WebView2 sessions. Entry name = host (or
        // host:port for non-standard ports), same convention as SSH. Filled
        // automatically by the "Подтянуть из базы" popup when the user
        // focuses a login form input on a Web session page.
        public const string WebGroupName = "Web";
        public const string FtpGroupName = "FTP";
        public const string RdpGroupPath = RootGroupName + "/" + RdpGroupName;
        public const string SshGroupPath = RootGroupName + "/" + SshGroupName;
        public const string WebGroupPath = RootGroupName + "/" + WebGroupName;
        public const string FtpGroupPath = RootGroupName + "/" + FtpGroupName;
        // v3.7.1 — auto-lock after 15 minutes of inactivity (was 5). Combined
        // with the SecureString auto-reopen below, the user no longer has to
        // retype the master password when the timer fires mid-session.
        public const int DefaultAutoLockMinutes = 15;

        public sealed class Config
        {
            public string DbPath { get; set; } = "";
            public string KeyFilePath { get; set; } = "";
            public bool AutoUnlockAtStartup { get; set; }
        }

        public sealed class VaultEntry
        {
            public string Name { get; set; } = "";
            public string GroupPath { get; set; } = "";
            public string Login { get; set; } = "";
            public string Password { get; set; } = "";
            public string Notes { get; set; } = "";
        }

        private static readonly object _lock = new object();
        private static readonly string _configPath = AppPaths.KeePassConfigPath;
        private static PwDatabase? _db;
        private static Timer? _autoLockTimer;

        // v3.7.1 — last successful master key kept in memory for silent
        // auto-reopen after the auto-lock timer fires. The master password
        // is STILL never written to disk; the SecureString lives only for
        // the process lifetime and is cleared by an explicit Lock() (the
        // "Заблокировать" button in the vault form). Auto-lock (timer) does
        // NOT clear it — that's the whole point of the feature.
        private static SecureString? _lastMasterPassword;
        private static string? _lastDbPath;
        private static string? _lastKeyFilePath;

        private static readonly JsonSerializerOptions _jsonOpts = new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public static bool IsOpen
        {
            get { lock (_lock) return _db != null; }
        }

        public static string CurrentDbPath
        {
            get { lock (_lock) return _db?.IOConnectionInfo.Path ?? ""; }
        }

        public static Config LoadConfig()
        {
            try
            {
                if (!File.Exists(_configPath)) return new Config();
                var json = File.ReadAllText(_configPath, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json)) return new Config();
                return JsonSerializer.Deserialize<Config>(json) ?? new Config();
            }
            catch (Exception ex)
            {
                Logger.Warn($"KeePassVault.LoadConfig failed: {ex.Message}");
                return new Config();
            }
        }

        // Saves keepass.json. Never stores a password. Returns an error
        // message on failure, null on success.
        public static string? SaveConfig(Config cfg)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                var json = JsonSerializer.Serialize(cfg, _jsonOpts);
                File.WriteAllText(_configPath, json, new UTF8Encoding(false));
                return null;
            }
            catch (Exception ex)
            {
                Logger.Warn($"KeePassVault.SaveConfig failed: {ex.Message}");
                return ex.Message;
            }
        }

        // Opens (or creates, if DbPath doesn't exist) the .kdbx database.
        // Returns an error message on failure, null on success.
        public static string? Open(string dbPath, string keyFilePath, string password)
        {
            lock (_lock)
            {
                try
                {
                    var ioInfo = IOConnectionInfo.FromPath(dbPath);
                    var key = new CompositeKey();
                    if (!string.IsNullOrEmpty(password))
                        key.AddUserKey(new KcpPassword(password));
                    if (!string.IsNullOrEmpty(keyFilePath) && File.Exists(keyFilePath))
                        key.AddUserKey(new KcpKeyFile(keyFilePath));

                    var db = new PwDatabase();
                    if (File.Exists(dbPath))
                    {
                        db.Open(ioInfo, key, new NullStatusLogger());
                    }
                    else
                    {
                        db.New(ioInfo, key);
                        EnsureGroup(db.RootGroup, RootGroupName);
                    }

                    _db = db;
                    RestartAutoLockTimer();
                    SaveMasterKey(dbPath, keyFilePath, password);
                    Logger.Info($"KeePassVault: database opened ({dbPath})");
                    return null;
                }
                catch (InvalidCompositeKeyException)
                {
                    Logger.Warn("KeePassVault: wrong master key");
                    return "Неверный мастер-пароль или файл-ключ.";
                }
                catch (Exception ex)
                {
                    Logger.Warn($"KeePassVault.Open failed: {ex.Message}");
                    return ex.Message;
                }
            }
        }

        // Closes the database (locks it). The master password is discarded —
        // it was never stored on disk, only in-memory as a SecureString for
        // auto-reopen. v3.7.1: explicit Lock() (the "Заблокировать" button)
        // clears the SecureString too, so the next open requires the dialog.
        public static void Lock()
        {
            lock (_lock)
            {
                if (_db != null)
                {
                    Logger.Info("KeePassVault: locked (explicit)");
                    _db.Close();
                    _db = null;
                }
                _autoLockTimer?.Dispose();
                _autoLockTimer = null;
                ClearMasterKey();
            }
        }

        // v3.7.1 — timer-only lock. Closes the database but KEEPS the
        // SecureString so the next access can silently reopen via
        // EnsureReopened(). User only sees the unlock dialog again after an
        // explicit Lock() or a failed reopen attempt.
        private static void AutoLock()
        {
            lock (_lock)
            {
                if (_db != null)
                {
                    Logger.Info("KeePassVault: auto-locked (timer)");
                    try { _db.Close(); } catch { }
                    _db = null;
                }
                _autoLockTimer?.Dispose();
                _autoLockTimer = null;
            }
        }

        // v3.7.1 — silently reopen the database with the saved master key.
        // Returns true on success, false if no saved key / reopen failed.
        // Callers that want to check IsOpen should call EnsureReopened()
        // first so an auto-locked vault comes back transparently.
        public static bool EnsureReopened()
        {
            lock (_lock)
            {
                if (_db != null) return true;
                return TryReopen();
            }
        }

        // Resets the auto-lock countdown. Called on every vault access.
        public static void RegisterActivity()
        {
            lock (_lock)
            {
                RestartAutoLockTimer();
            }
        }

        // Finds an entry by name in groupPath (e.g. "PupSeekRDP/RDP").
        // Returns (Login, Password) or null if not found. Never creates
        // groups during a lookup.
        public static (string Login, string Password)? FindLogin(string groupPath, string entryName)
        {
            lock (_lock)
            {
                if (_db == null) return null;
                var group = FindGroup(_db.RootGroup, groupPath);
                if (group == null) return null;
                var entry = FindEntry(group, entryName);
                if (entry == null) return null;
                RestartAutoLockTimer();
                return (entry.Strings.ReadSafe(PwDefs.UserNameField),
                        entry.Strings.ReadSafe(PwDefs.PasswordField));
            }
        }

        // Saves/updates an entry in groupPath. Missing groups in the path
        // are created — this is an explicit SAVE, so creating is intended.
        // Returns an error message on failure, null on success.
        public static string? SaveLogin(string groupPath, string entryName, string login, string password, string? notes = null)
        {
            lock (_lock)
            {
                if (_db == null) return "База не открыта.";
                try
                {
                    var group = GetOrCreateGroup(_db.RootGroup, groupPath);
                    var entry = FindEntry(group, entryName);
                    if (entry == null)
                    {
                        entry = new PwEntry(true, true)
                        {
                            CreationTime = DateTime.UtcNow
                        };
                        entry.Strings.Set(PwDefs.TitleField, new ProtectedString(false, entryName));
                        // AddEntry(pe, true) wires up ParentGroup — bare
                        // Entries.Add() leaves it null and the port's
                        // serializer then throws OperationCanceledException.
                        group.AddEntry(entry, true);
                    }
                    entry.Strings.Set(PwDefs.UserNameField, new ProtectedString(false, login ?? ""));
                    entry.Strings.Set(PwDefs.PasswordField, new ProtectedString(false, password ?? ""));
                    if (!string.IsNullOrEmpty(notes))
                        entry.Strings.Set(PwDefs.NotesField, new ProtectedString(false, notes));
                    entry.LastModificationTime = DateTime.UtcNow;

                    _db.Save(new NullStatusLogger());
                    RestartAutoLockTimer();
                    Logger.Info($"KeePassVault: saved entry '{entryName}' in {groupPath}");
                    return null;
                }
                catch (Exception ex)
                {
                    Logger.Warn($"KeePassVault.SaveLogin failed: {ex.Message}");
                    return ex.Message;
                }
            }
        }

        public static bool DeleteEntry(string groupPath, string entryName)
        {
            lock (_lock)
            {
                if (_db == null) return false;
                try
                {
                    var group = FindGroup(_db.RootGroup, groupPath);
                    if (group == null) return false;
                    var entry = FindEntry(group, entryName);
                    if (entry == null) return false;
                    group.Entries.Remove(entry);
                    _db.Save(new NullStatusLogger());
                    RestartAutoLockTimer();
                    Logger.Info($"KeePassVault: deleted entry '{entryName}' in {groupPath}");
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Warn($"KeePassVault.DeleteEntry failed: {ex.Message}");
                    return false;
                }
            }
        }

        // Lists entries in a group. Group must exist; null otherwise.
        public static List<VaultEntry>? ListEntries(string groupPath)
        {
            lock (_lock)
            {
                if (_db == null) return null;
                var group = FindGroup(_db.RootGroup, groupPath);
                if (group == null) return null;
                var list = new List<VaultEntry>();
                foreach (var e in group.Entries)
                {
                    list.Add(new VaultEntry
                    {
                        Name = e.Strings.ReadSafe(PwDefs.TitleField),
                        GroupPath = groupPath,
                        Login = e.Strings.ReadSafe(PwDefs.UserNameField),
                        Password = e.Strings.ReadSafe(PwDefs.PasswordField),
                        Notes = e.Strings.ReadSafe(PwDefs.NotesField)
                    });
                }
                RestartAutoLockTimer();
                return list;
            }
        }

        // Recursively lists all groups under root for the manager tree.
        public static List<string> ListGroups()
        {
            lock (_lock)
            {
                var result = new List<string>();
                if (_db == null) return result;
                CollectGroups(_db.RootGroup, "", result);
                return result;
            }
        }

        // --- group tree helpers -------------------------------------------

        // Walks "A/B/C" under root. Returns null if any part is missing.
        // NEVER creates anything.
        private static PwGroup? FindGroup(PwGroup root, string groupPath)
        {
            var cur = root;
            foreach (var part in SplitPath(groupPath))
            {
                var next = FindChildGroup(cur, part);
                if (next == null) return null;
                cur = next;
            }
            return cur;
        }

        // Walks "A/B/C" under root, creating missing groups along the way.
        private static PwGroup GetOrCreateGroup(PwGroup root, string groupPath)
        {
            var cur = root;
            foreach (var part in SplitPath(groupPath))
            {
                var next = FindChildGroup(cur, part);
                if (next == null)
                {
                    next = new PwGroup(true, true)
                    {
                        Name = part,
                        CreationTime = DateTime.UtcNow
                    };
                    // AddGroup(pg, true) wires up ParentGroup — bare
                    // Groups.Add() leaves it null and the port's serializer
                    // then throws OperationCanceledException.
                    cur.AddGroup(next, true);
                }
                cur = next;
            }
            return cur;
        }

        // Case-insensitive child lookup by exact name.
        private static PwGroup? FindChildGroup(PwGroup parent, string name)
        {
            for (uint i = 0; i < parent.Groups.UCount; i++)
            {
                var g = parent.Groups.GetAt(i);
                if (g != null && string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase))
                    return g;
            }
            return null;
        }

        private static PwEntry? FindEntry(PwGroup group, string entryName)
        {
            foreach (var e in group.Entries)
            {
                if (string.Equals(e.Strings.ReadSafe(PwDefs.TitleField), entryName, StringComparison.OrdinalIgnoreCase))
                    return e;
            }
            return null;
        }

        private static void CollectGroups(PwGroup group, string prefix, List<string> result)
        {
            for (uint i = 0; i < group.Groups.UCount; i++)
            {
                var g = group.Groups.GetAt(i);
                if (g == null) continue;
                var path = string.IsNullOrEmpty(prefix) ? g.Name : prefix + "/" + g.Name;
                result.Add(path);
                CollectGroups(g, path, result);
            }
        }

        private static string[] SplitPath(string groupPath) =>
            (groupPath ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        private static void RestartAutoLockTimer()
        {
            _autoLockTimer?.Dispose();
            _autoLockTimer = new Timer(_ => AutoLock(), null,
                TimeSpan.FromMinutes(DefaultAutoLockMinutes), Timeout.InfiniteTimeSpan);
        }

        // v3.7.1 — capture master key after a successful Open(). The plain
        // password string passed in here is the same one KeePassLib already
        // has in memory for the live _db; we just hold a SecureString copy
        // so the timer path can reopen without prompting.
        private static void SaveMasterKey(string dbPath, string keyFilePath, string password)
        {
            _lastDbPath = dbPath;
            _lastKeyFilePath = string.IsNullOrEmpty(keyFilePath) ? null : keyFilePath;
            _lastMasterPassword?.Dispose();
            var ss = new SecureString();
            foreach (var c in password ?? "") ss.AppendChar(c);
            ss.MakeReadOnly();
            _lastMasterPassword = ss;
        }

        private static void ClearMasterKey()
        {
            _lastMasterPassword?.Dispose();
            _lastMasterPassword = null;
            _lastDbPath = null;
            _lastKeyFilePath = null;
        }

        // Attempts to reopen the database with the saved master key. MUST be
        // called under _lock. On any failure the saved key is invalidated —
        // the next caller will get false and the dialog path will run.
        private static bool TryReopen()
        {
            if (_db != null) return true;
            if (_lastMasterPassword == null || string.IsNullOrEmpty(_lastDbPath)) return false;
            if (!File.Exists(_lastDbPath)) { ClearMasterKey(); return false; }

            string? plain = null;
            try
            {
                var ioInfo = IOConnectionInfo.FromPath(_lastDbPath);
                var key = new CompositeKey();
                plain = SecureStringToString(_lastMasterPassword);
                if (!string.IsNullOrEmpty(plain))
                    key.AddUserKey(new KcpPassword(plain));
                if (!string.IsNullOrEmpty(_lastKeyFilePath) && File.Exists(_lastKeyFilePath))
                    key.AddUserKey(new KcpKeyFile(_lastKeyFilePath));

                var db = new PwDatabase();
                db.Open(ioInfo, key, new NullStatusLogger());
                _db = db;
                RestartAutoLockTimer();
                Logger.Info($"KeePassVault: auto-reopened ({_lastDbPath})");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn($"KeePassVault auto-reopen failed: {ex.Message}");
                ClearMasterKey();
                return false;
            }
            finally
            {
                if (plain != null) plain = null; // best-effort; SecureString is the durable store
            }
        }

        private static string SecureStringToString(SecureString ss)
        {
            var ip = IntPtr.Zero;
            try
            {
                ip = Marshal.SecureStringToBSTR(ss);
                return Marshal.PtrToStringBSTR(ip) ?? "";
            }
            finally
            {
                if (ip != IntPtr.Zero) Marshal.ZeroFreeBSTR(ip);
            }
        }

        private static PwGroup EnsureGroup(PwGroup root, string name)
        {
            var existing = FindChildGroup(root, name);
            if (existing != null) return existing;
            var g = new PwGroup(true, true)
            {
                Name = name,
                CreationTime = DateTime.UtcNow
            };
            root.AddGroup(g, true);
            return g;
        }
    }
}
