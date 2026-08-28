using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace ShadowRDP.Services
{
    // Collects a structured report about a remote computer for the "Отчёт о
    // компьютере" dialog. Mirror of Get-ComputerInfo + Get-RemotePrograms
    // from the PowerShell version.
    //
    // Returns a flat list of (Section, Property, Value) tuples — the form
    // renders sections as accent-blue headers and properties as banded rows.
    //
    // All WMI queries use the timeout helper from WmiService — same reason
    // (avoid hanging forever on dead RPC). Pre-flight ping fast-fails on
    // offline hosts.
    public static class ComputerInfoService
    {
        public sealed class Row
        {
            public bool IsSection { get; set; }
            public string Property { get; set; } = "";
            public string Value { get; set; } = "";
        }

        public static List<Row> Collect(string computer)
        {
            var rows = new List<Row>();

            // Pre-flight ping.
            if (!PingService.IsAlive(computer, 1500))
            {
                AddSection(rows, "Статус");
                AddRow(rows, "Online", "НЕТ (ping не прошёл)");
                AddRow(rows, "", "Компьютер не в сети — сбор информации невозможен.");
                return rows;
            }

            // Operating System.
            AddSection(rows, "Операционная система");
            try
            {
                var os = QuerySingle(computer, "SELECT * FROM Win32_OperatingSystem");
                if (os != null)
                {
                    AddRow(rows, "Имя", os["Caption"]?.ToString() ?? "");
                    AddRow(rows, "Версия", $"{os["Version"]} ({os["OSArchitecture"]})");
                    AddRow(rows, "Сборка", os["BuildNumber"]?.ToString() ?? "");
                    if (os["LastBootUpTime"] != null)
                    {
                        try
                        {
                            var dt = ManagementDateTimeConverter.ToDateTime(os["LastBootUpTime"].ToString());
                            AddRow(rows, "Последняя загрузка", dt.ToString("yyyy-MM-dd HH:mm:ss"));
                        }
                        catch { }
                    }
                }
                else { AddRow(rows, "Ошибка", "WMI таймаут"); }
            }
            catch (Exception ex) { AddRow(rows, "Ошибка", ex.Message); }

            // Computer.
            AddSection(rows, "Компьютер");
            try
            {
                var cs = QuerySingle(computer, "SELECT * FROM Win32_ComputerSystem");
                if (cs != null)
                {
                    AddRow(rows, "Имя", cs["Name"]?.ToString() ?? "");
                    AddRow(rows, "Производитель", cs["Manufacturer"]?.ToString() ?? "");
                    AddRow(rows, "Модель", cs["Model"]?.ToString() ?? "");
                    AddRow(rows, "Домен", cs["Domain"]?.ToString() ?? "");
                    AddRow(rows, "Залогинен", cs["UserName"]?.ToString() ?? "");
                    if (cs["TotalPhysicalMemory"] != null &&
                        ulong.TryParse(cs["TotalPhysicalMemory"].ToString(), out var mem))
                    {
                        AddRow(rows, "RAM, ГБ", Math.Round(mem / 1024.0 / 1024 / 1024, 2).ToString("0.00"));
                    }
                }
                else { AddRow(rows, "Ошибка", "WMI таймаут"); }
            }
            catch (Exception ex) { AddRow(rows, "Ошибка", ex.Message); }

            // Network.
            AddSection(rows, "Сеть");
            try
            {
                var nets = QueryCollection(computer,
                    "SELECT * FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled=true");
                if (nets.Count == 0) AddRow(rows, "", "(нет IP-адаптеров)");
                foreach (var net in nets)
                {
                    AddRow(rows, "Адаптер", net["Description"]?.ToString() ?? "");
                    AddRow(rows, "  IP", string.Join(", ", AsStringArray(net["IPAddress"])));
                    AddRow(rows, "  MAC", net["MACAddress"]?.ToString() ?? "");
                    AddRow(rows, "  Шлюз", string.Join(", ", AsStringArray(net["DefaultIPGateway"])));
                    AddRow(rows, "  DNS", string.Join(", ", AsStringArray(net["DNSServerSearchOrder"])));
                }
            }
            catch (Exception ex) { AddRow(rows, "Ошибка", ex.Message); }

            // CPU.
            AddSection(rows, "Процессор");
            try
            {
                var cpus = QueryCollection(computer, "SELECT * FROM Win32_Processor");
                if (cpus.Count == 0) AddRow(rows, "", "(нет данных)");
                foreach (var cpu in cpus)
                {
                    AddRow(rows, "Имя", cpu["Name"]?.ToString() ?? "");
                    AddRow(rows, "  Ядра/Потоки",
                        $"{cpu["NumberOfCores"]} / {cpu["NumberOfLogicalProcessors"]}");
                }
            }
            catch (Exception ex) { AddRow(rows, "Ошибка", ex.Message); }

            // Disks.
            AddSection(rows, "Диски");
            try
            {
                var disks = QueryCollection(computer, "SELECT * FROM Win32_LogicalDisk WHERE DriveType=3");
                if (disks.Count == 0) AddRow(rows, "", "(нет дисков)");
                foreach (var d in disks)
                {
                    var sizeGb = ToGb(d["Size"]);
                    var freeGb = ToGb(d["FreeSpace"]);
                    AddRow(rows, d["DeviceID"]?.ToString() ?? "",
                        $"{sizeGb:0.00} ГБ всего, {freeGb:0.00} ГБ свободно");
                }
            }
            catch (Exception ex) { AddRow(rows, "Ошибка", ex.Message); }

            // Installed programs (Remote Registry first, WMI StdRegProv fallback).
            AddSection(rows, "Установленные программы");
            try
            {
                var progs = GetInstalledPrograms(computer);
                if (progs.Count == 0) AddRow(rows, "", "(Remote Registry недоступен)");
                else foreach (var p in progs) AddRow(rows, "", p);
            }
            catch (Exception ex) { AddRow(rows, "Ошибка", ex.Message); }

            return rows;
        }

        // === Helpers ===

        private static void AddSection(List<Row> rows, string title)
        {
            rows.Add(new Row { IsSection = true, Property = title, Value = "" });
        }

        private static void AddRow(List<Row> rows, string prop, string val)
        {
            rows.Add(new Row { IsSection = false, Property = prop, Value = val });
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
                foreach (ManagementObject mo in results)
                {
                    return mo;
                }
                return null;
            }
            catch (Exception ex)
            {
                Logger.Warn($"WMI query failed on {computer}: {ex.Message}");
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
                Logger.Warn($"WMI collection query failed on {computer}: {ex.Message}");
            }
            return list;
        }

        private static IEnumerable<string> AsStringArray(object? v)
        {
            if (v is string[] arr) return arr;
            if (v != null) return new[] { v.ToString() ?? "" };
            return Array.Empty<string>();
        }

        private static double ToGb(object? v)
        {
            if (v != null && ulong.TryParse(v.ToString(), out var bytes))
            {
                return bytes / 1024.0 / 1024 / 1024;
            }
            return 0;
        }

        // Two strategies inside one call: Remote Registry (preferred), then
        // WMI StdRegProv fallback. Same approach as the .ps1 version.
        //
        // Remote Registry service is OFF by default on Win10/11 workstations,
        // so on most end-user PCs we end up using the StdRegProv fallback —
        // which works because it uses the same DCOM/WMI channel that the
        // rest of the report already relies on.
        [SupportedOSPlatform("windows")]
        private static List<string> GetInstalledPrograms(string computer)
        {
            var result = new List<string>();
            var paths = new[]
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            };

            // Method 1: Remote Registry via .NET.
            try
            {
                using var root = RegistryKey.OpenRemoteBaseKey(RegistryHive.LocalMachine, computer);
                foreach (var path in paths)
                {
                    using var key = root.OpenSubKey(path);
                    if (key == null) continue;
                    foreach (var subName in key.GetSubKeyNames())
                    {
                        using var sub = key.OpenSubKey(subName);
                        var name = sub?.GetValue("DisplayName")?.ToString();
                        if (string.IsNullOrEmpty(name)) continue;
                        var ver = sub?.GetValue("DisplayVersion")?.ToString();
                        result.Add(string.IsNullOrEmpty(ver) ? name : $"{name} (v{ver})");
                    }
                }
            }
            catch (Exception ex)
            {
                // Expected on Win10/11 (Remote Registry disabled). Fall through.
                Logger.Info($"GetInstalledPrograms Remote Registry failed on {computer}: {ex.Message}");
            }

            if (result.Count > 0) { result.Sort(); return result; }

            // Method 2: WMI StdRegProv. hDefKey must be UInt32 — passing
            // 0x80000002 as int (-2147483646) throws "too large/small for
            // UInt32" on the WMI side. Use uint literal explicitly.
            const uint HKEY_LOCAL_MACHINE = 0x80000002;
            try
            {
                var scopePath = new ManagementPath($"\\\\{computer}\\root\\default:StdRegProv");
                var options = new ConnectionOptions
                {
                    Timeout = TimeSpan.FromSeconds(6),
                    Authentication = AuthenticationLevel.PacketPrivacy
                };
                var scope = new ManagementScope(scopePath, options);
                scope.Connect();
                using var regClass = new ManagementClass(scope, scopePath, null);

                foreach (var basePath in paths)
                {
                    // EnumKey(hDefKey, sSubKeyName) → outParams.sNames (string[])
                    using var enumIn = regClass.GetMethodParameters("EnumKey");
                    enumIn["hDefKey"] = HKEY_LOCAL_MACHINE;
                    enumIn["sSubKeyName"] = basePath;
                    using var enumOut = regClass.InvokeMethod("EnumKey", enumIn, null);
                    if (enumOut == null) continue;
                    var sNamesObj = enumOut.Properties["sNames"]?.Value;
                    if (sNamesObj is not string[] sNames || sNames.Length == 0) continue;

                    foreach (var subName in sNames)
                    {
                        var fullPath = $"{basePath}\\{subName}";

                        // GetStringValue(hDefKey, sSubKeyName, sValueName) → outParams.sValue
                        var name = GetString(regClass, HKEY_LOCAL_MACHINE, fullPath, "DisplayName");
                        if (string.IsNullOrEmpty(name)) continue;
                        var ver = GetString(regClass, HKEY_LOCAL_MACHINE, fullPath, "DisplayVersion");
                        result.Add(string.IsNullOrEmpty(ver) ? name : $"{name} (v{ver})");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"GetInstalledPrograms StdRegProv failed on {computer}: {ex.Message}");
            }

            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
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
    }
}
