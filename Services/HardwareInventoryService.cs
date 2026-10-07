using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Threading;
using System.Threading.Tasks;

namespace ShadowRDP.Services
{
    // v3.9.3BD — сводка железа по компьютерам книги (для формы
    // "Сводная таблица HARD"): процессор, RAM, диски. Живой опрос по WMI
    // (таймауты как в ComputerInfoService + ping fast-fail), параллелизм
    // с семафором и отменой — паттерн SoftwareInventoryForm (Сканировать).
    public static class HardwareInventoryService
    {
        public sealed class DiskRec
        {
            public string Mount { get; set; } = "";
            public double TotalGb { get; set; }
            public double FreeGb { get; set; }
            public double UsedGb => Math.Max(0, TotalGb - FreeGb);
            public double PFree => TotalGb > 0 ? FreeGb / TotalGb * 100.0 : 100.0;
        }

        public sealed class HardwareEntry
        {
            public string Computer { get; set; } = "";
            public bool Online { get; set; }
            public string Cpu { get; set; } = "";
            public string Cores { get; set; } = "";
            public double RamGb { get; set; }
            public List<DiskRec> Disks { get; set; } = new();
            public string Error { get; set; } = "";
        }

        public static List<HardwareEntry> Scan(
            IEnumerable<string> computers,
            IProgress<(int done, int total, string host)>? progress = null,
            CancellationToken token = default)
        {
            var list = computers
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => c.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var result = new HardwareEntry[list.Count];
            int done = 0;
            var opt = new ParallelOptions
            {
                CancellationToken = token,
                MaxDegreeOfParallelism = 10
            };
            try
            {
                Parallel.For(0, list.Count, opt, i =>
                {
                    token.ThrowIfCancellationRequested();
                    result[i] = CollectOne(list[i]);
                    int n = Interlocked.Increment(ref done);
                    try { progress?.Report((n, list.Count, list[i])); } catch { }
                });
            }
            catch (OperationCanceledException) { throw; }
            catch { }
            return result.Where(e => e != null).OrderBy(e => e.Computer, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public sealed class HardwareCache
        {
            public DateTime Time { get; set; } = DateTime.Now;
            public List<HardwareEntry> Entries { get; set; } = new();
        }

        // v3.9.3BD — таблица показывает последний скан, живой опрос
        // только по кнопке «Сканировать».
        public static void SaveCache(List<HardwareEntry> entries)
        {
            try
            {
                var path = AppPaths.HardwareInventoryPath;
                System.IO.Directory.CreateDirectory(AppPaths.DataDir);
                var doc = new HardwareCache { Time = DateTime.Now, Entries = entries };
                System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(doc));
            }
            catch (Exception ex) { try { Logger.Warn($"HardwareInventory SaveCache: {ex.Message}"); } catch { } }
        }

        public static HardwareCache? LoadCache()
        {
            try
            {
                var path = AppPaths.HardwareInventoryPath;
                if (!System.IO.File.Exists(path)) return null;
                var doc = System.Text.Json.JsonSerializer.Deserialize<HardwareCache>(
                    System.IO.File.ReadAllText(path));
                if (doc?.Entries == null) return null;
                return doc;
            }
            catch (Exception ex) { try { Logger.Warn($"HardwareInventory LoadCache: {ex.Message}"); } catch { } return null; }
        }

        public static HardwareEntry CollectOne(string computer)
        {
            var e = new HardwareEntry { Computer = computer };
            try
            {
                if (!PingService.IsAlive(computer, 1500))
                {
                    e.Online = false;
                    e.Error = "не в сети";
                    return e;
                }
                e.Online = true;
                // CPU.
                try
                {
                    var cpus = QueryCollection(computer, "SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
                    var cpu = cpus.FirstOrDefault();
                    if (cpu != null)
                    {
                        e.Cpu = CleanCpuName(cpu["Name"]?.ToString() ?? "");
                        e.Cores = $"{cpu["NumberOfCores"]} / {cpu["NumberOfLogicalProcessors"]}";
                    }
                }
                catch { }
                // RAM.
                try
                {
                    var cs = QuerySingle(computer, "SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
                    if (cs != null && cs["TotalPhysicalMemory"] != null &&
                        ulong.TryParse(cs["TotalPhysicalMemory"].ToString(), out var mem))
                        e.RamGb = Math.Round(mem / 1024.0 / 1024 / 1024, 1);
                }
                catch { }
                // Disks.
                try
                {
                    var disks = QueryCollection(computer, "SELECT DeviceID, Size, FreeSpace FROM Win32_LogicalDisk WHERE DriveType=3");
                    foreach (var d in disks)
                    {
                        e.Disks.Add(new DiskRec
                        {
                            Mount = d["DeviceID"]?.ToString() ?? "",
                            TotalGb = ToGb(d["Size"]),
                            FreeGb = ToGb(d["FreeSpace"])
                        });
                    }
                }
                catch { }
            }
            catch (Exception ex)
            {
                e.Online = false;
                e.Error = ex.Message;
                try { Logger.Warn($"HardwareInventory {computer}: {ex.Message}"); } catch { }
            }
            return e;
        }

        private static string CleanCpuName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            return name.Replace("(R)", "").Replace("(TM)", "").Replace("(tm)", "")
                .Replace("  ", " ").Trim();
        }

        private static ManagementObject? QuerySingle(string computer, string wql)
        {
            try
            {
                var path = new ManagementPath($"\\\\{computer}\\root\\cimv2");
                var options = new ConnectionOptions
                {
                    Timeout = TimeSpan.FromSeconds(6),
                    Authentication = AuthenticationLevel.PacketPrivacy
                };
                var scope = new ManagementScope(path, options);
                using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql));
                using var results = searcher.Get();
                foreach (ManagementObject mo in results) return mo;
                return null;
            }
            catch (Exception ex)
            {
                try { Logger.Warn($"HardwareInventory WMI failed on {computer}: {ex.Message}"); } catch { }
                return null;
            }
        }

        private static List<ManagementObject> QueryCollection(string computer, string wql)
        {
            var list = new List<ManagementObject>();
            try
            {
                var path = new ManagementPath($"\\\\{computer}\\root\\cimv2");
                var options = new ConnectionOptions
                {
                    Timeout = TimeSpan.FromSeconds(6),
                    Authentication = AuthenticationLevel.PacketPrivacy
                };
                var scope = new ManagementScope(path, options);
                using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql));
                using var results = searcher.Get();
                foreach (ManagementObject mo in results) list.Add(mo);
            }
            catch (Exception ex)
            {
                try { Logger.Warn($"HardwareInventory WMI collection failed on {computer}: {ex.Message}"); } catch { }
            }
            return list;
        }

        private static double ToGb(object? v)
        {
            if (v != null && ulong.TryParse(v.ToString(), out var bytes))
                return Math.Round(bytes / 1024.0 / 1024 / 1024, 1);
            return 0;
        }
    }
}
