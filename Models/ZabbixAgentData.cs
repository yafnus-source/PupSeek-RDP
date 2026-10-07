using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ShadowRDP.Models
{
    public class ZabbixAgentDisk
    {
        [JsonPropertyName("mount")]
        public string Mount { get; set; } = "";
        [JsonPropertyName("total")]
        public long Total { get; set; }
        [JsonPropertyName("free")]
        public long Free { get; set; }
        [JsonPropertyName("pfree")]
        public double PFree { get; set; }
        [JsonPropertyName("used")]
        public long Used => Total - Free;
        [JsonPropertyName("pused")]
        public double PUsed => 100 - PFree;
    }

    public class ZabbixAgentNetIf
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";
        [JsonPropertyName("inBps")]
        public long InBps { get; set; }
        [JsonPropertyName("outBps")]
        public long OutBps { get; set; }
        [JsonPropertyName("totalBps")]
        public long TotalBps => InBps + OutBps;
        [JsonPropertyName("speed")]
        public long Speed { get; set; }
        [JsonPropertyName("status")]
        public string Status { get; set; } = "";
    }

    public class ZabbixAgentService
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";
        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; } = "";
        [JsonPropertyName("state")]
        public string State { get; set; } = ""; // running/stopped/paused
        [JsonPropertyName("startup")]
        public string Startup { get; set; } = ""; // auto/manual/disabled
        [JsonPropertyName("isCritical")]
        public bool IsCritical { get; set; }
    }

    public class ZabbixAgentDiskIo
    {
        [JsonPropertyName("device")]
        public string Device { get; set; } = "";
        [JsonPropertyName("readOps")]
        public long ReadOps { get; set; }
        [JsonPropertyName("writeOps")]
        public long WriteOps { get; set; }
        [JsonPropertyName("readBps")]
        public long ReadBps { get; set; }
        [JsonPropertyName("writeBps")]
        public long WriteBps { get; set; }
        [JsonPropertyName("readLatencyMs")]
        public double ReadLatencyMs { get; set; }
        [JsonPropertyName("writeLatencyMs")]
        public double WriteLatencyMs { get; set; }
    }

    public class ZabbixAgentData
    {
        [JsonPropertyName("host")]
        public string Host { get; set; } = "";
        [JsonPropertyName("time")]
        public DateTime Time { get; set; } = DateTime.Now;
        [JsonPropertyName("agentPing")]
        public bool AgentPing { get; set; }
        [JsonPropertyName("agentVersion")]
        public string AgentVersion { get; set; } = "";
        [JsonPropertyName("cpuLoad")]
        public double CpuLoad { get; set; } // 0-100
        [JsonPropertyName("memTotal")]
        public long MemTotal { get; set; }
        [JsonPropertyName("memAvailable")]
        public long MemAvailable { get; set; }
        [JsonPropertyName("memPFree")]
        public double MemPFree { get; set; }
        [JsonPropertyName("uptime")]
        public long UptimeSec { get; set; }
        [JsonPropertyName("disks")]
        public List<ZabbixAgentDisk> Disks { get; set; } = new();
        [JsonPropertyName("netIfs")]
        public List<ZabbixAgentNetIf> NetIfs { get; set; } = new();
        [JsonPropertyName("services")]
        public List<ZabbixAgentService> Services { get; set; } = new();
        [JsonPropertyName("diskIo")]
        public List<ZabbixAgentDiskIo> DiskIo { get; set; } = new();
        [JsonPropertyName("tempC")]
        public double? TempC { get; set; }
        [JsonPropertyName("error")]
        public string Error { get; set; } = "";
        [JsonPropertyName("source")]
        public string Source { get; set; } = "agent"; // "agent" or "snmp"

        [JsonIgnore]
        public bool HasError => !string.IsNullOrEmpty(Error) || !AgentPing;
        [JsonIgnore]
        public double MemUsedPct => 100 - MemPFree;
        [JsonIgnore]
        public TimeSpan Uptime => TimeSpan.FromSeconds(UptimeSec);
    }

    public class ZabbixAgentSettings
    {
        [JsonPropertyName("pollMinutes")]
        public int PollMinutes { get; set; } = 15;
        [JsonPropertyName("timeoutMs")]
        public int TimeoutMs { get; set; } = 3000;
        [JsonPropertyName("concurrency")]
        public int Concurrency { get; set; } = 12;

        [JsonPropertyName("cpuWarn")]
        public double CpuWarn { get; set; } = 80;
        [JsonPropertyName("cpuCrit")]
        public double CpuCrit { get; set; } = 95;
        [JsonPropertyName("ramWarn")]
        public double RamWarn { get; set; } = 15;
        [JsonPropertyName("ramCrit")]
        public double RamCrit { get; set; } = 5;
        [JsonPropertyName("diskWarn")]
        public double DiskWarn { get; set; } = 20;
        [JsonPropertyName("diskCrit")]
        public double DiskCrit { get; set; } = 10;
        [JsonPropertyName("tempWarn")]
        public double TempWarn { get; set; } = 70;
        [JsonPropertyName("tempCrit")]
        public double TempCrit { get; set; } = 85;

        [JsonPropertyName("netWarn")]
        public double NetWarn { get; set; } = 80; // % of speed
        [JsonPropertyName("netCrit")]
        public double NetCrit { get; set; } = 95;

        // v3.9.3 — алерты здоровья: нет пинга / мало места на диске.
        // Баллун + звук, один раз на инцидент до восстановления.
        [JsonPropertyName("pingAlert")]
        public bool PingAlert { get; set; } = true;
        [JsonPropertyName("diskFreeAlert")]
        public bool DiskFreeAlert { get; set; } = true;
        [JsonPropertyName("diskFreeGb")]
        public double DiskFreeGb { get; set; } = 10;
        [JsonPropertyName("alertSound")]
        public bool AlertSound { get; set; } = true;

        [JsonPropertyName("extraKeys")]
        public List<string> ExtraKeys { get; set; } = new();

        [JsonPropertyName("agentConfigPath")]
        public string AgentConfigPath { get; set; } = @"C:\Program Files\Zabbix Agent\zabbix_agentd.conf";
        [JsonPropertyName("agentServiceName")]
        public string AgentServiceName { get; set; } = "Zabbix Agent";

        [JsonPropertyName("snmpEnabled")]
        public bool SnmpEnabled { get; set; } = true;
        [JsonPropertyName("snmpFallback")]
        public bool SnmpFallback { get; set; } = true;
        [JsonPropertyName("snmpPort")]
        public int SnmpPort { get; set; } = 161;
        [JsonPropertyName("snmpCommunity")]
        public string SnmpCommunity { get; set; } = "public";
        [JsonPropertyName("snmpVersion")]
        public string SnmpVersion { get; set; } = "v2c"; // "v1" or "v2c"
        [JsonPropertyName("snmpTimeoutMs")]
        public int SnmpTimeoutMs { get; set; } = 800;
    }
}
