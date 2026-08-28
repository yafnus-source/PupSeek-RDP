using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.DirectoryServices;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace ShadowRDP.Services
{
    // Enumerates and closes open files on remote computers via the
    // netapi32 API — the same API the MMC snap-in "Shared Folders →
    // Open Files" uses. Works over SMB/RPC, no WinRM required.
    //
    // Data comes back as FILE_INFO_3 rows (level 3 — the only level that
    // reliably returns full data from Samba-based servers; level 2 returns
    // just the file-id column from those servers, with no strings):
    //   fi3_id          — numeric file id (needed for NetFileClose)
    //   fi3_pathname    — full local path of the open file/folder
    //   fi3_username    — who has it open
    //   fi3_num_locks   — number of locks held
    //   fi3_permissions — access permissions (not surfaced in the UI)
    //
    // FILE_INFO_3 native layout (x64, stride 32):
    //   id @0 (4)  permissions @4 (4)  num_locks @8 (4)  pad @12
    //   pathname* @16 (8)  username* @24 (8)
    // On x86: id@0, perms@4, locks@8, pathname@12 (4), username@16 (4), stride 20.
    // The string pointers are ABSOLUTE and point into the same buffer, right
    // after the struct block.
    public static class OpenFilesService
    {
        // Default timeout mirrors the other services (10s). NetFileEnum
        // on a dead/unreachable host can hang on the RPC; we cut it.
        public const int DefaultTimeoutMs = 10000;

        // Upper bound for the byte[] copy of a netapi buffer. Real buffers
        // are a few KB; this is just a safety cap so an oversized VMM region
        // can never cause a huge allocation or an over-long copy.
        private const long MaxBufferBytes = 16 * 1024 * 1024;

        public sealed class OpenFile
        {
            public string Computer { get; set; } = "";
            public uint FileId { get; set; }
            public string Path { get; set; } = "";
            public string User { get; set; } = "";
            public uint Locks { get; set; }

            // Heuristic: a path segment that ends with a dot-separated
            // extension is a file, otherwise we treat it as a folder.
            // Rare edge case: files without an extension show as "Папка".
            public bool IsFolder()
            {
                var path = Path;
                if (string.IsNullOrEmpty(path)) return true;
                var name = path.TrimEnd('\\');
                var slash = name.LastIndexOf('\\');
                name = slash >= 0 ? name.Substring(slash + 1) : name;
                return name.IndexOf('.') < 0;
            }
        }

        public sealed class Result
        {
            public List<OpenFile> Files { get; set; } = new List<OpenFile>();
            public bool TimedOut { get; set; }
            public string ErrorText { get; set; } = "";
        }

        public static Result Query(string computer, int timeoutMs = DefaultTimeoutMs)
        {
            var result = new Result();
            if (string.IsNullOrWhiteSpace(computer))
            {
                result.ErrorText = "Имя компьютера не задано.";
                return result;
            }

            var server = "\\\\" + computer.Trim();

            // NetFileEnum has no timeout of its own, so it runs on a
            // watchdog thread and we cut it if the RPC stalls. The native
            // buffer is owned ENTIRELY by that thread: it parses the rows
            // and calls NetApiBufferFree itself, only after NetFileEnum has
            // returned. The calling thread never touches the buffer, so a
            // timeout (thread still inside NetFileEnum) can never race with
            // a NetApiBufferFree — the watchdog simply finishes on its own
            // and cleans up, and nothing is leaked.
            var buffer = IntPtr.Zero;
            var entriesRead = 0;
            var totalEntries = 0;
            var resumeHandle = IntPtr.Zero;
            var apiError = 0;
            Exception? ex = null;
            var files = new List<OpenFile>();

            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    try
                    {
                        apiError = NetFileEnum(server, null, null, 3, out buffer,
                            -1, out entriesRead, out totalEntries, ref resumeHandle);
                    }
                    catch (Exception e)
                    {
                        ex = e;
                        return;
                    }

                    if (apiError == 0 && buffer != IntPtr.Zero && entriesRead > 0)
                    {
                        // Copy the buffer into managed memory, using
                        // VirtualQuery to bound the copy to committed,
                        // readable pages. The FILE_INFO_3 string pointers are
                        // absolute and point into the buffer, but we translate
                        // them to offsets against the byte[] copy with bounds
                        // checks instead of dereferencing the native pointers
                        // directly — that keeps every read within managed
                        // memory, so a NULL or stray pointer can never fault
                        // the process.
                        var mbi = new MEMORY_BASIC_INFORMATION();
                        if (VirtualQuery(buffer, out mbi, Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) != 0
                            && mbi.RegionSize > 0)
                        {
                            var baseAddr = buffer.ToInt64();
                            var avail = mbi.BaseAddress.ToInt64() + mbi.RegionSize - baseAddr;
                            if (avail > MaxBufferBytes) avail = MaxBufferBytes;
                            if (avail > 0)
                            {
                                var raw = new byte[avail];
                                Marshal.Copy(buffer, raw, 0, (int)avail);

                                // FILE_INFO_3 uses the native struct layout
                                // (fixed stride/offsets per architecture).
                                // We still sanity-check the first entry's path
                                // pointer: a valid pointer lands past the
                                // struct block and decodes to a printable
                                // null-terminated UTF-16 string.
                                var stride = IntPtr.Size == 8 ? 32 : 20;
                                var pathOff = IntPtr.Size == 8 ? 16 : 12;
                                if (IntPtr.Size == 8 && entriesRead > 0)
                                {
                                    if (stride * entriesRead + stride > raw.Length
                                        || !LooksLikeStringRef(raw, baseAddr, BitConverter.ToInt64(raw, pathOff), (long)stride * entriesRead))
                                    {
                                        stride = 0; // guard against a bad parse
                                    }
                                }
                                var userOff = pathOff + IntPtr.Size;
                                var maxEntries = stride > 0
                                    ? Math.Min(entriesRead, raw.Length / stride)
                                    : 0;

                                for (var i = 0; i < maxEntries; i++)
                                {
                                    var off = i * stride;
                                    var id = BitConverter.ToUInt32(raw, off);
                                    var locks = BitConverter.ToUInt32(raw, off + 8);
                                    long pathPtr, userPtr;
                                    if (IntPtr.Size == 8)
                                    {
                                        pathPtr = BitConverter.ToInt64(raw, off + pathOff);
                                        userPtr = BitConverter.ToInt64(raw, off + userOff);
                                    }
                                    else
                                    {
                                        pathPtr = BitConverter.ToInt32(raw, off + pathOff);
                                        userPtr = BitConverter.ToInt32(raw, off + userOff);
                                    }

                                    files.Add(new OpenFile
                                    {
                                        FileId = id,
                                        Path = ReadStringFromRaw(raw, baseAddr, pathPtr),
                                        User = ReadStringFromRaw(raw, baseAddr, userPtr),
                                        Locks = locks
                                    });
                                }
                                Logger.Info($"OpenFiles on {computer}: {files.Count} open files (level 3)");
                            }
                        }
                    }
                }
                finally
                {
                    // NetFileEnum has fully returned by now, so the buffer is
                    // no longer owned by the API call. Freeing it here — on
                    // the same thread that produced it — is the one and only
                    // race-free moment.
                    if (buffer != IntPtr.Zero) NetApiBufferFree(buffer);
                    buffer = IntPtr.Zero;
                }
            });
            thread.IsBackground = true;
            thread.Start();

            if (!thread.Join(timeoutMs))
            {
                result.TimedOut = true;
                result.ErrorText = $"Timeout: NetFileEnum не ответил за {timeoutMs / 1000} с. Компьютер может быть offline или RPC заблокирован.";
                Logger.Warn($"OpenFiles timeout on {computer} after {timeoutMs}ms");
                return result;
            }

            if (ex != null)
            {
                result.ErrorText = ex.Message;
                Logger.Error($"OpenFiles exception on {computer}: {ex.Message}");
                return result;
            }

            if (apiError != 0)
            {
                result.ErrorText = DescribeError(apiError);
                Logger.Warn($"OpenFiles on {computer} error {apiError}: {result.ErrorText}");
                return result;
            }

            result.Files = files;
            return result;
        }

        // Closes a single open file handle on the remote computer.
        public static bool Close(string computer, uint fileId)
        {
            try
            {
                var server = "\\\\" + computer.Trim();
                var err = NetFileClose(server, fileId);
                if (err != 0)
                {
                    Logger.Warn($"OpenFiles close on {computer} id={fileId} error {err}: {DescribeError(err)}");
                    return false;
                }
                Logger.Info($"OpenFiles closed on {computer} id={fileId}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"OpenFiles close exception on {computer} id={fileId}: {ex.Message}");
                return false;
            }
        }

        // Closes every open handle in `files` that matches `path` on the
        // given computer (all users). Returns the number of handles closed.
        // `files` is the snapshot taken at query time — stale handles
        // silently fail and are skipped.
        public static int CloseAll(string computer, List<OpenFile> files, string path)
        {
            var closed = 0;
            foreach (var f in files)
            {
                if (!string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)) continue;
                if (Close(computer, f.FileId)) closed++;
            }
            return closed;
        }

        // Per-server share list cache for the session. Shares change rarely;
        // a failed enumeration is NOT cached so the next attempt retries.
        private static readonly ConcurrentDictionary<string, List<(string Name, string Path)>> ShareCache =
            new ConcurrentDictionary<string, List<(string Name, string Path)>>(StringComparer.OrdinalIgnoreCase);

        // Resolves a server-local path (e.g. "C:\Data\file.xlsx") into a
        // network (UNC) path the client can open in Explorer:
        // "\\SERVER\Data\file.xlsx". The share prefix is found by matching
        // the local path against the server's disk shares (NetShareEnum),
        // longest prefix wins — admin shares (C$, D$, ...) act as the
        // natural fallback for paths not covered by a named share. Returns
        // null when the server can't be reached or no share matches.
        public static string? ResolveUncPath(string computer, string localPath)
        {
            if (string.IsNullOrWhiteSpace(computer) || string.IsNullOrWhiteSpace(localPath)) return null;
            var normalized = localPath.Trim().Replace('/', '\\');
            if (normalized.StartsWith(@"\\", StringComparison.Ordinal)) return normalized;

            if (!ShareCache.TryGetValue(computer, out var shares))
            {
                shares = EnumerateShares(computer);
                if (shares.Count == 0) return null;
                ShareCache[computer] = shares;
            }

            var bestName = "";
            var bestPath = "";
            var bestLen = -1;
            foreach (var s in shares)
            {
                var sharePath = s.Path.Trim().Replace('/', '\\').TrimEnd('\\');
                if (string.IsNullOrEmpty(sharePath) || sharePath.Length <= bestLen) continue;
                if (!normalized.StartsWith(sharePath, StringComparison.OrdinalIgnoreCase)) continue;
                if (sharePath.Length < normalized.Length && normalized[sharePath.Length] != '\\') continue;
                bestLen = sharePath.Length;
                bestName = s.Name;
                bestPath = sharePath;
            }

            if (bestLen < 0) return null;

            var unc = @"\\" + computer.Trim() + @"\" + bestName;
            var rest = normalized.Substring(bestLen).TrimStart('\\');
            if (rest.Length > 0) unc += @"\" + rest;
            return unc;
        }

        // Enumerates the server's disk shares (SHARE_INFO_2, level 2) using
        // the same defensive pattern as Query/NetFileEnum: the call runs on
        // a watchdog thread so an RPC stall can be cut, and the native buffer
        // is copied into managed memory (VirtualQuery-bounded) then parsed
        // with offset checks — the buffer is freed only on the producing
        // thread after NetShareEnum has returned.
        private static List<(string Name, string Path)> EnumerateShares(string computer, int timeoutMs = DefaultTimeoutMs)
        {
            var result = new List<(string Name, string Path)>();
            if (string.IsNullOrWhiteSpace(computer)) return result;
            var server = "\\\\" + computer.Trim();

            var buffer = IntPtr.Zero;
            var entriesRead = 0;
            var totalEntries = 0;
            var resumeHandle = IntPtr.Zero;
            var apiError = 0;
            Exception? ex = null;

            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    try
                    {
                        apiError = NetShareEnum(server, 2, out buffer, -1,
                            out entriesRead, out totalEntries, ref resumeHandle);
                    }
                    catch (Exception e)
                    {
                        ex = e;
                        return;
                    }

                    if (apiError == 0 && buffer != IntPtr.Zero && entriesRead > 0)
                    {
                        var mbi = new MEMORY_BASIC_INFORMATION();
                        if (VirtualQuery(buffer, out mbi, Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) != 0
                            && mbi.RegionSize > 0)
                        {
                            var baseAddr = buffer.ToInt64();
                            var avail = mbi.BaseAddress.ToInt64() + mbi.RegionSize - baseAddr;
                            if (avail > MaxBufferBytes) avail = MaxBufferBytes;
                            if (avail > 0)
                            {
                                var raw = new byte[avail];
                                Marshal.Copy(buffer, raw, 0, (int)avail);

                                // SHARE_INFO_2 native layout:
                                //   netname* @0, type @8, path* @40 (x64, stride 56)
                                //   netname* @0, type @4, path* @24 (x86, stride 32)
                                var stride = IntPtr.Size == 8 ? 56 : 32;
                                var pathOff = IntPtr.Size == 8 ? 40 : 24;
                                var maxEntries = Math.Min(entriesRead, raw.Length / stride);

                                for (var i = 0; i < maxEntries; i++)
                                {
                                    var off = i * stride;
                                    var type = BitConverter.ToUInt32(raw, off + (IntPtr.Size == 8 ? 8 : 4));
                                    if ((type & 0x0F) != 0) continue;  // disk shares only

                                    long netPtr, pathPtr;
                                    if (IntPtr.Size == 8)
                                    {
                                        netPtr = BitConverter.ToInt64(raw, off);
                                        pathPtr = BitConverter.ToInt64(raw, off + pathOff);
                                    }
                                    else
                                    {
                                        netPtr = BitConverter.ToInt32(raw, off);
                                        pathPtr = BitConverter.ToInt32(raw, off + pathOff);
                                    }
                                    var name = ReadStringFromRaw(raw, baseAddr, netPtr);
                                    var path = ReadStringFromRaw(raw, baseAddr, pathPtr);
                                    if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(path))
                                        result.Add((name, path));
                                }
                            }
                        }
                    }
                }
                finally
                {
                    if (buffer != IntPtr.Zero) NetApiBufferFree(buffer);
                    buffer = IntPtr.Zero;
                }
            });
            thread.IsBackground = true;
            thread.Start();

            if (!thread.Join(timeoutMs))
            {
                Logger.Warn($"OpenFiles share enum timeout on {computer} after {timeoutMs}ms");
                return result;
            }
            if (ex != null)
            {
                Logger.Warn($"OpenFiles share enum exception on {computer}: {ex.Message}");
                return result;
            }
            if (apiError != 0)
            {
                Logger.Warn($"OpenFiles share enum on {computer} error {apiError}: {DescribeError(apiError)}");
                return result;
            }
            return result;
        }

        // Pull the list of file servers from AD — computer accounts whose
        // operatingSystem attribute mentions "Server". Scanning just these
        // is far cheaper than querying every workstation in the domain.
        public static List<string> GetFileServers(string? ldapPath = null)
        {
            var result = new List<string>();
            var path = ResolveLdapPath(ldapPath);
            if (path == null)
            {
                Logger.Warn("OpenFilesService.GetFileServers: failed to resolve LDAP path");
                return result;
            }

            try
            {
                using var entry = new DirectoryEntry(path);
                using var searcher = new DirectorySearcher(entry)
                {
                    Filter = "(&(objectClass=computer)(operatingSystem=*Server*))",
                    PageSize = 200,
                    ServerTimeLimit = TimeSpan.FromSeconds(30)
                };
                searcher.PropertiesToLoad.Add("name");
                searcher.PropertiesToLoad.Add("cn");

                using var results = searcher.FindAll();
                foreach (SearchResult r in results)
                {
                    var name = "";
                    if (r.Properties.Contains("name") && r.Properties["name"].Count > 0)
                        name = r.Properties["name"][0]?.ToString() ?? "";
                    if (string.IsNullOrEmpty(name) && r.Properties.Contains("cn") && r.Properties["cn"].Count > 0)
                        name = r.Properties["cn"][0]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(name)) result.Add(name);
                }
                Logger.Info($"OpenFilesService.GetFileServers: returned {result.Count} servers");
            }
            catch (Exception ex)
            {
                Logger.Warn($"OpenFilesService.GetFileServers failed: {ex.Message}");
            }
            return result;
        }

        // Resolve "LDAP://DC=foo,DC=bar" from $env:USERDNSDOMAIN. Returns
        // null if domain env var isn't set (e.g. workgroup machine).
        private static string? ResolveLdapPath(string? explicitPath)
        {
            if (!string.IsNullOrEmpty(explicitPath)) return explicitPath;
            var dns = Environment.GetEnvironmentVariable("USERDNSDOMAIN");
            if (string.IsNullOrEmpty(dns)) return null;
            var dcParts = dns.Split('.')
                .Where(p => !string.IsNullOrEmpty(p))
                .Select(p => $"DC={p}");
            return "LDAP://" + string.Join(",", dcParts);
        }

        private static string DescribeError(int error)
        {
            // Map common netapi / network errors to Russian messages.
            switch (error)
            {
                case 5:
                    return "Отказано в доступе. Нет прав администратора на компьютере.";
                case 53:
                    return "Сетевой путь не найден. Компьютер недоступен.";
                case 1722:
                    return "RPC-сервер недоступен. Возможно, служба 'Сервер' остановлена.";
                case 2103:
                    return "Сетевой путь больше не доступен.";
                case 2250:
                    return "Сетевое соединение недоступно.";
                case 6118:
                    return "Список серверов недоступен.";
                default:
                    // Fall back to Windows' own localized message.
                    return new Win32Exception(error).Message;
            }
        }

        // Heuristic used to pick the correct FILE_INFO_2 stride: a plausible
        // string pointer must land at or after the struct block and decode to
        // a printable null-terminated UTF-16 string (at least one letter).
        private static bool LooksLikeStringRef(byte[] raw, long baseAddr, long ptr, long minRel)
        {
            if (ptr == 0) return false;
            var rel = ptr - baseAddr;
            if (rel < minRel || rel >= raw.Length) return false;

            var start = (int)rel;
            var letters = 0;
            for (var i = 0; i < 200 && start + i * 2 + 1 < raw.Length; i++)
            {
                var c = (char)(raw[start + i * 2] | (raw[start + i * 2 + 1] << 8));
                if (c == '\0') return letters > 0;
                if (c == 0xFFFE || c == 0xFFFF) return false;
                if (char.IsLetter(c)) letters++;
            }
            return false; // no null terminator found — not a string
        }

        // Decode a null-terminated UTF-16 string directly from the managed
        // copy of the netapi buffer. The pointer is translated to an offset
        // relative to the buffer base and bounds-checked against the copy,
        // so a NULL or garbage pointer simply yields "" — this can never
        // fault the process (unlike Marshal.PtrToStringUni or per-char
        // Marshal.ReadInt16 on remote-buffer pointers).
        private static string ReadStringFromRaw(byte[] raw, long baseAddr, long ptr)
        {
            if (ptr == 0) return "";
            var rel = ptr - baseAddr;
            if (rel < 0 || rel >= raw.Length) return "";

            var sb = new StringBuilder(64);
            var start = (int)rel;
            for (var i = 0; i < 1024 && start + i * 2 + 1 < raw.Length; i++)
            {
                var c = (char)(raw[start + i * 2] | (raw[start + i * 2 + 1] << 8));
                if (c == '\0') break;
                sb.Append(c);
            }
            return sb.ToString();
        }

        [DllImport("netapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int NetFileEnum(
            string servername,
            string? basepath,
            string? username,
            int level,
            out IntPtr bufptr,
            int prefmaxlen,
            out int entriesread,
            out int totalentries,
            ref IntPtr resume_handle);

        [DllImport("netapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int NetFileClose(string servername, uint fileid);

        [DllImport("netapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int NetShareEnum(
            string servername,
            int level,
            out IntPtr bufptr,
            int prefmaxlen,
            out int entriesread,
            out int totalentries,
            ref IntPtr resume_handle);

        [DllImport("netapi32.dll")]
        private static extern int NetApiBufferFree(IntPtr buffer);

        // Returns the size of the contiguous region of committed, readable
        // pages that starts at lpAddress — used to safely bound the byte[]
        // copy of the netapi buffer (NetApiBufferSize is unreliable for
        // remote RPC-allocated buffers).
        [DllImport("kernel32.dll")]
        private static extern int VirtualQuery(
            IntPtr lpAddress,
            out MEMORY_BASIC_INFORMATION lpBuffer,
            int dwLength);

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORY_BASIC_INFORMATION
        {
            public IntPtr BaseAddress;
            public IntPtr AllocationBase;
            public uint AllocationProtect;
            public long RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
        }
    }
}
