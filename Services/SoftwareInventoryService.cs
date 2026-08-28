using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace ShadowRDP.Services
{
    // Scans a list of remote computers for installed software. Uses the same
    // dual-strategy approach as ComputerInfoService.GetInstalledPrograms:
    //   1. Remote Registry (fast, but disabled on Win10/11 by default)
    //   2. WMI StdRegProv fallback (works over DCOM)
    //
    // Returns a flat list of SoftwareEntry records — the form handles
    // aggregation and display.

    public static class SoftwareInventoryService
    {
        public sealed class SoftwareEntry
        {
            public string Computer { get; set; } = "";
            public string Name { get; set; } = "";
            public string Version { get; set; } = "";
        }

        public const string DefaultSharePath = @"\\FILESERVER\SoftwareInventory$";

        // Load software inventory from JSON files written by the GPO logon/startup script.
        // Each file is named <ComputerName>.json and contains:
        // { "Computer":"PC01", "OS":"...", "Timestamp":"...", "Programs":[{"Name":"...","Version":"..."}, ...] }
        [SupportedOSPlatform("windows")]
        public static List<SoftwareEntry> LoadFromShare(string sharePath, CancellationToken ct)
        {
            var results = new List<SoftwareEntry>();

            if (!Directory.Exists(sharePath))
            {
                Logger.Info($"SoftwareInventory: share not accessible: {sharePath}");
                return results;
            }

            var files = Directory.GetFiles(sharePath, "*.json");
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var json = File.ReadAllText(file, Encoding.UTF8);
                    var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    var computer = root.GetProperty("Computer").GetString() ?? Path.GetFileNameWithoutExtension(file);
                    var programs = root.GetProperty("Programs");
                    foreach (var prog in programs.EnumerateArray())
                    {
                        var name = prog.GetProperty("Name").GetString() ?? "";
                        var ver = prog.TryGetProperty("Version", out var v) ? v.GetString() ?? "" : "";
                        if (string.IsNullOrEmpty(name)) continue;
                        results.Add(new SoftwareEntry { Computer = computer, Name = name, Version = ver });
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"SoftwareInventory: failed to parse {file}: {ex.Message}");
                }
            }

            Logger.Info($"SoftwareInventory: loaded {results.Count} entries from {files.Length} files in {sharePath}");
            return results
                .OrderBy(e => e.Computer, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static readonly string[] RegistryPaths =
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        };

        // Patterns for Microsoft update entries to filter out.
        private static readonly string[] UpdatePrefixes =
        {
            "KB", "Security Update", "Update for", "Cumulative Update",
            "Servicing Stack Update", "Definition Update"
        };

        [SupportedOSPlatform("windows")]
        public static async Task<List<SoftwareEntry>> ScanAsync(
            List<string> computers,
            int maxConcurrency,
            CancellationToken ct,
            Action<int, int, string, int>? onProgress = null)
        {
            var results = new ConcurrentBag<SoftwareEntry>();
            int done = 0;
            int total = computers.Count;

            await Parallel.ForEachAsync(computers,
                new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency, CancellationToken = ct },
                (computer, token) =>
                {
                    int count = 0;
                    try
                    {
                        var entries = ScanOne(computer);
                        count = entries.Count;
                        foreach (var e in entries)
                            results.Add(e);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"SoftwareInventory scan failed for {computer}: {ex.Message}");
                    }
                    int current = Interlocked.Increment(ref done);
                    onProgress?.Invoke(current, total, computer, count);
                    return ValueTask.CompletedTask;
                });

            return results
                .OrderBy(e => e.Computer, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        [SupportedOSPlatform("windows")]
        private static List<SoftwareEntry> ScanOne(string computer)
        {
            var entries = new List<SoftwareEntry>();

            if (!PingService.IsAlive(computer, 1500))
            {
                Logger.Info($"SoftwareInventory {computer}: ping failed, skipped");
                return entries;
            }

            // Try to get Windows version via WMI Win32_OperatingSystem.
            try
            {
                var osScope = new ManagementScope($"\\\\{computer}\\root\\cimv2");
                osScope.Options = new ConnectionOptions
                {
                    Timeout = TimeSpan.FromSeconds(3),
                    Authentication = AuthenticationLevel.PacketPrivacy
                };
                var osQuery = new ObjectQuery("SELECT Caption, Version, BuildNumber FROM Win32_OperatingSystem");
                using var searcher = new ManagementObjectSearcher(osScope, osQuery);
                foreach (ManagementObject obj in searcher.Get())
                {
                    var caption = obj["Caption"]?.ToString() ?? "";
                    var version = obj["Version"]?.ToString() ?? "";
                    var build = obj["BuildNumber"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(caption))
                    {
                        var osVer = !string.IsNullOrEmpty(version) ? $"{caption} ({version} b{build})" : caption;
                        entries.Add(new SoftwareEntry { Computer = computer, Name = osVer, Version = "ОС" });
                    }
                }
            }
            catch (Exception ex) { Logger.Info($"SoftwareInventory {computer}: WMI OS query failed: {ex.Message}"); }

            // Method 1: Remote Registry via .NET (with 3s timeout).
            try
            {
                var rrTask = Task.Run(() =>
                {
                    using var root = RegistryKey.OpenRemoteBaseKey(RegistryHive.LocalMachine, computer);
                    foreach (var path in RegistryPaths)
                    {
                        using var key = root.OpenSubKey(path);
                        if (key == null) continue;
                        foreach (var subName in key.GetSubKeyNames())
                        {
                            using var sub = key.OpenSubKey(subName);
                            var name = sub?.GetValue("DisplayName")?.ToString();
                            if (string.IsNullOrEmpty(name) || IsFiltered(name)) continue;
                            var ver = sub?.GetValue("DisplayVersion")?.ToString();
                            entries.Add(new SoftwareEntry { Computer = computer, Name = name, Version = ver ?? "" });
                        }
                    }
                });
                if (!rrTask.Wait(TimeSpan.FromSeconds(3)))
                {
                    Logger.Info($"SoftwareInventory {computer}: Remote Registry timed out (3s)");
                    entries.Clear();
                }
            }
            catch (Exception ex)
            {
                // Expected on Win10/11 (Remote Registry disabled). Fall through.
                Logger.Info($"SoftwareInventory {computer}: Remote Registry failed: {ex.Message}");
            }

            if (entries.Count > 0) return entries;

            // Method 2: WMI StdRegProv. hDefKey must be UInt32.
            const uint HKEY_LOCAL_MACHINE = 0x80000002;
            try
            {
                var scopePath = new ManagementPath($"\\\\{computer}\\root\\default:StdRegProv");
                var options = new ConnectionOptions
                {
                    Timeout = TimeSpan.FromSeconds(10),
                    Authentication = AuthenticationLevel.PacketPrivacy
                };
                var scope = new ManagementScope(scopePath, options);
                scope.Connect();
                using var regClass = new ManagementClass(scope, scopePath, null);

                foreach (var basePath in RegistryPaths)
                {
                    using var enumIn = regClass.GetMethodParameters("EnumKey");
                    enumIn["hDefKey"] = HKEY_LOCAL_MACHINE;
                    enumIn["sSubKeyName"] = basePath;
                    using var enumOut = regClass.InvokeMethod("EnumKey", enumIn, null);
                    if (enumOut == null) continue;
                    var sNamesObj = enumOut.Properties["sNames"]?.Value;
                    if (sNamesObj is not string[] sNames || sNames.Length == 0) continue;

                    int found = 0;
                    foreach (var subName in sNames)
                    {
                        var fullPath = $"{basePath}\\{subName}";
                        var name = GetString(regClass, HKEY_LOCAL_MACHINE, fullPath, "DisplayName");
                        if (string.IsNullOrEmpty(name) || IsFiltered(name)) continue;
                        var ver = GetString(regClass, HKEY_LOCAL_MACHINE, fullPath, "DisplayVersion");
                        entries.Add(new SoftwareEntry { Computer = computer, Name = name, Version = ver ?? "" });
                        found++;
                    }
                    Logger.Info($"SoftwareInventory {computer}: {basePath} → {sNames.Length} subkeys, {found} matched");
                }
            }
            catch (Exception ex)
            {
                // StdRegProv also failed — computer unreachable or WMI disabled.
                Logger.Info($"SoftwareInventory {computer}: StdRegProv failed: {ex.Message}");
            }

            if (entries.Count > 0) return entries;

            // Method 3: WinRM (Invoke-Command) fallback.
            // WinRM uses port 5985/5986, often open in domain environments
            // when WMI/DCOM (port 135) is blocked by Windows Firewall.
            try
            {
                entries = ScanViaWinRM(computer);
            }
            catch (Exception ex)
            {
                Logger.Info($"SoftwareInventory {computer}: WinRM failed: {ex.Message}");
            }

            Logger.Info($"SoftwareInventory {computer}: {entries.Count} entries found");
            return entries;
        }

        [SupportedOSPlatform("windows")]
        private static List<SoftwareEntry> ScanViaWinRM(string computer)
        {
            var entries = new List<SoftwareEntry>();

            // PowerShell script: enumerate both Uninstall registry paths
            // and output JSON lines with DisplayName + DisplayVersion.
            var psScript = @"
$ErrorActionPreference='SilentlyContinue'
$r = @()
$paths = @(
  'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
  'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
)
foreach ($p in $paths) {
  Get-ItemProperty $p -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName } | ForEach-Object {
    $r += [PSCustomObject]@{N=$_.DisplayName;V=$_.DisplayVersion}
  }
}
$r | ConvertTo-Json -Compress
".Trim();

            var escapedScript = psScript.Replace("'", "''");
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -Command \"Invoke-Command -ComputerName '{computer}' -ScriptBlock {{ {escapedScript} }}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var proc = Process.Start(psi);
            if (proc == null) return entries;

            var output = proc.StandardOutput.ReadToEnd();
            var error = proc.StandardError.ReadToEnd();
            proc.WaitForExit(5000);

            if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            {
                if (!string.IsNullOrWhiteSpace(error))
                    Logger.Info($"SoftwareInventory {computer}: WinRM stderr: {error.Trim()}");
                return entries;
            }

            // Parse JSON: could be single object or array.
            output = output.Trim();
            try
            {
                if (output.StartsWith("["))
                {
                    var items = JsonSerializer.Deserialize<List<WinrmEntry>>(output);
                    if (items != null)
                        foreach (var item in items)
                            if (!string.IsNullOrEmpty(item.N) && !IsFiltered(item.N))
                                entries.Add(new SoftwareEntry { Computer = computer, Name = item.N, Version = item.V ?? "" });
                }
                else
                {
                    var item = JsonSerializer.Deserialize<WinrmEntry>(output);
                    if (item != null && !string.IsNullOrEmpty(item.N) && !IsFiltered(item.N))
                        entries.Add(new SoftwareEntry { Computer = computer, Name = item.N, Version = item.V ?? "" });
                }
            }
            catch (Exception ex)
            {
                Logger.Info($"SoftwareInventory {computer}: WinRM JSON parse failed: {ex.Message}");
            }

            return entries;
        }

        // Used for WinRM JSON deserialization.
        private sealed class WinrmEntry
        {
            public string? N { get; set; }
            public string? V { get; set; }
        }

        private static string GetString(ManagementClass regClass, uint hive, string path, string valueName)
        {
            try
            {
                using var inParams = regClass.GetMethodParameters("GetStringValue");
                inParams["hDefKey"] = hive;
                inParams["sSubKeyName"] = path;
                inParams["sValueName"] = valueName ?? "";
                using var outParams = regClass.InvokeMethod("GetStringValue", inParams, null);
                return outParams?.Properties["sValue"]?.Value?.ToString() ?? "";
            }
            catch { return ""; }
        }

        // Filter out Microsoft update/patch entries and service components.
        private static bool IsFiltered(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            foreach (var prefix in UpdatePrefixes)
            {
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            }
            // Filter .NET / Visual C++ redistributables (service components).
            if (name.Contains("Microsoft Visual C++", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.Contains(".NET", StringComparison.OrdinalIgnoreCase) && name.Contains("Runtime", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
