using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ShadowRDP.Models;

namespace ShadowRDP.Services
{
    // Minimal SNMPv1/v2c client over UDP (BER-encoded, no external deps вЂ”
    // same style as the hand-rolled ZBXD client). Supports GET and WALK over
    // the MIB-2 / HOST-RESOURCES subtree, enough to build a ZabbixAgentData
    // for network devices and servers that don't run the Zabbix agent.
    public static class SnmpClient
    {
        // HOST-RESOURCES-MIB
        private const string HrProcessorLoad = "1.3.6.1.2.1.25.3.3.1.2";
        private const string HrStorageTable = "1.3.6.1.2.1.25.2.3.1";
        // hrStorageType values (OIDs under hrStorageTypes)
        private const string HrStorageTypeRam = "1.3.6.1.2.1.25.2.1.2";
        private const string HrStorageTypeVirtual = "1.3.6.1.2.1.25.2.1.3";
        private const string HrStorageTypeFixedDisk = "1.3.6.1.2.1.25.2.1.4";

        // IF-MIB
        private const string IfTable = "1.3.6.1.2.1.2.2.1";

        private const string SysDescrOid = "1.3.6.1.2.1.1.1.0";
        private const string SysUpTimeOid = "1.3.6.1.2.1.1.3.0";

        private const int MaxWalkEntries = 512;

        public static async Task<ZabbixAgentData> CollectAsync(string host, int timeoutMs, string community, string version, ushort port, CancellationToken ct = default)
        {
            var data = new ZabbixAgentData { Host = host, Time = DateTime.Now, Source = "snmp" };
            bool v2c = !string.Equals(version, "v1", StringComparison.OrdinalIgnoreCase);
            try
            {
                // VPN-aware: РїСЂРѕР±СѓРµРј РІСЃРµ РєР°РЅРґРёРґР°С‚С‹ вЂ” РёРјСЏ С…РѕСЃС‚Р° + РІСЃРµ IP РёР· РєРЅРёРіРё.
                var targets = await ResolveTargetsAsync(host);
                if (targets.Count == 0) throw new IOException("no target address for host");
                SnmpConn? conn = null;
                SnmpResponse? sys = null;
                string checkCommunity = community;
                foreach (var t in targets)
                {
                    try
                    {
                        var c = new SnmpConn(t, port, checkCommunity, v2c ? 1 : 0, timeoutMs);
                        var s = await c.GetAsync(new[] { SysDescrOid, SysUpTimeOid }, ct);
                        if (!string.IsNullOrEmpty(s.GetString(SysDescrOid)) || !string.IsNullOrEmpty(s.GetString(SysUpTimeOid)))
                        {
                            conn = c; sys = s;
                            break;
                        }
                    }
                    catch { /* РїСЂРѕР±СѓРµРј СЃР»РµРґСѓСЋС‰РёР№ Р°РґСЂРµСЃ */ }
                }
                if (conn == null || sys == null)
                    throw new IOException($"no SNMP response on [{string.Join(", ", targets)}] (check community / firewall / UDP {port} / РЅСѓР¶РЅС‹Р№ IP РІ РєРЅРёРіРµ)");

                string sysDescr = sys.GetString(SysDescrOid) ?? "";
                string sysUptime = sys.GetString(SysUpTimeOid) ?? "";

                data.AgentVersion = (sysDescr + " вЂў SNMP").Trim();
                if (data.AgentVersion.Length > 90) data.AgentVersion = data.AgentVersion.Substring(0, 87) + "вЂ¦";
                if (long.TryParse(sysUptime, out var ticks)) data.UptimeSec = ticks / 100; // TimeTicks = 1/100 s

                // CPU: average hrProcessorLoad
                var cpuLoads = await conn.WalkAsync(HrProcessorLoad, ct);
                if (cpuLoads.Count > 0)
                {
                    double sum = 0;
                    foreach (var v in cpuLoads.Values) if (double.TryParse(v, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d)) sum += d;
                    data.CpuLoad = Math.Round(sum / cpuLoads.Count, 1);
                }

                // Storage: RAM + disks from hrStorageTable
                var stType = await conn.WalkIndexedAsync(HrStorageTable + ".2", ct);
                var stDescr = await conn.WalkIndexedAsync(HrStorageTable + ".3", ct);
                var stUnits = await conn.WalkIndexedAsync(HrStorageTable + ".4", ct);
                var stSize = await conn.WalkIndexedAsync(HrStorageTable + ".5", ct);
                var stUsed = await conn.WalkIndexedAsync(HrStorageTable + ".6", ct);
                CollectStorage(stType, stDescr, stUnits, stSize, stUsed, data);

                // Interfaces: descr + speed + oper status
                var ifDescr = await conn.WalkIndexedAsync(IfTable + ".2", ct);
                var ifSpeed = await conn.WalkIndexedAsync(IfTable + ".5", ct);
                var ifOper = await conn.WalkIndexedAsync(IfTable + ".8", ct);
                foreach (var key in ifDescr.Keys)
                {
                    string descr = ifDescr.GetValueOrDefault(key) ?? "";
                    if (string.IsNullOrWhiteSpace(descr)) continue;
                    data.NetIfs.Add(new ZabbixAgentNetIf
                    {
                        Name = descr,
                        Speed = UtilsParseInt64(ifSpeed.GetValueOrDefault(key) ?? "0", "0"),
                        Status = UtilsParseInt64(ifOper.GetValueOrDefault(key) ?? "0", "0") == 1 ? "up" : "down"
                    });
                }

                data.AgentPing = true;
            }
            catch (Exception ex)
            {
                data.Error = "SNMP: " + ex.Message;
                data.AgentPing = false;
            }
            return data;
        }

        private static async Task<List<string>> ResolveTargetsAsync(string host)
        {
            var list = new List<string>();
            string clean = host.Trim();
            if (clean.Contains(":")) clean = clean.Split(':')[0];
            if (!string.IsNullOrWhiteSpace(clean)) list.Add(clean);
            try
            {
                var entries = AddressBookStore.Load();
                var match = entries.FirstOrDefault(e => string.Equals(e.Computer, clean, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                    match = ZabbixAgentStore.LoadManualHosts()
                        .FirstOrDefault(e => string.Equals(e.Computer, clean, StringComparison.OrdinalIgnoreCase));
                if (match != null && !string.IsNullOrWhiteSpace(match.IPs))
                {
                    foreach (var ipRaw in match.IPs.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var ip = ipRaw.Trim();
                        if (!string.IsNullOrWhiteSpace(ip) && IPAddress.TryParse(ip, out _))
                            list.Add(ip);
                    }
                }
            }
            catch { }
            return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void CollectStorage(Dictionary<string, string> stType, Dictionary<string, string> stDescr, Dictionary<string, string> stUnits, Dictionary<string, string> stSize, Dictionary<string, string> stUsed, ZabbixAgentData data)
        {
            long memTotal = 0, memUsed = 0;
            foreach (var key in stSize.Keys)
            {
                long size = UtilsParseInt64(stSize.GetValueOrDefault(key) ?? "0", "0");
                long used = UtilsParseInt64(stUsed.GetValueOrDefault(key) ?? "0", "0");
                long units = Math.Max(1, UtilsParseInt64(stUnits.GetValueOrDefault(key) ?? "1", "1"));
                if (size <= 0) continue;
                string type = stType.GetValueOrDefault(key) ?? "";
                string descr = stDescr.GetValueOrDefault(key) ?? "";
                if (string.Equals(type, HrStorageTypeRam, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(type, HrStorageTypeVirtual, StringComparison.OrdinalIgnoreCase))
                {
                    memTotal += size * units;
                    memUsed += used * units;
                }
                else if (string.Equals(type, HrStorageTypeFixedDisk, StringComparison.OrdinalIgnoreCase))
                {
                    long total = size * units;
                    long free = (size - used) * units;
                    if (free < 0) free = 0;
                    data.Disks.Add(new ZabbixAgentDisk
                    {
                        Mount = descr.Length > 32 ? descr.Substring(0, 32) : descr,
                        Total = total,
                        Free = free,
                        PFree = Math.Round((double)free / total * 100, 1)
                    });
                }
            }
            if (memTotal > 0) data.MemTotal = memTotal;
            if (memTotal > 0) data.MemAvailable = memTotal - memUsed;
            if (memTotal > 0) data.MemPFree = Math.Round((double)(memTotal - memUsed) / memTotal * 100, 1);
        }

        private static long UtilsParseInt64(string s, string fallback)
        {
            if (long.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v)) return v;
            if (long.TryParse(fallback, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var f)) return f;
            return 0;
        }

        // ----- transport + BER -----

        private sealed class SnmpConn
        {
            private readonly string _host;
            private readonly int _port;
            private readonly string _community;
            private readonly int _version; // 0 = v1, 1 = v2c
            private readonly int _timeoutMs;
            private int _requestId = new Random().Next(1, int.MaxValue);

            public SnmpConn(string host, int port, string community, int version, int timeoutMs)
            {
                _host = host; _port = port; _community = community; _version = version; _timeoutMs = Math.Max(300, timeoutMs);
            }

            public async Task<SnmpResponse> GetAsync(IReadOnlyList<string> oids, CancellationToken ct)
            {
                byte[] req = BuildRequest(0xA0, oids.Select(o => new VarBind(o, null)).ToList());
                return await RoundTripAsync(req, ct);
            }

            public async Task<Dictionary<string, string>> WalkAsync(string baseOid, CancellationToken ct)
            {
                var result = new Dictionary<string, string>();
                string next = baseOid;
                for (int i = 0; i < MaxWalkEntries; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    byte[] req = BuildRequest(0xA1, new List<VarBind> { new VarBind(next, null) });
                    var resp = await RoundTripAsync(req, ct);
                    if (resp.ErrorStatus != 0) break;
                    if (resp.Vars.Count == 0) break;
                    var v = resp.Vars[0];
                    if (!IsChildOf(v.Oid, baseOid)) break;
                    if (v.IsEndOfMibView) break;
                    if (v.Value == null) break;
                    if (!result.ContainsKey(v.Oid)) result[v.Oid] = v.Value;
                    else if (ContainsSameOid(result, v.Oid)) return result;
                    next = v.Oid;
                }
                return result;
            }

            public async Task<Dictionary<string, string>> WalkIndexedAsync(string baseOid, CancellationToken ct)
            {
                var raw = await WalkAsync(baseOid, ct);
                var result = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var kv in raw)
                {
                    string suffix = kv.Key.Substring(baseOid.Length).TrimStart('.');
                    result[suffix] = kv.Value;
                }
                return result;
            }

            private static bool ContainsSameOid(Dictionary<string, string> d, string oid)
            {
                // If two identical OIDs were returned, the walk is looping вЂ” stop.
                int cnt = 0;
                foreach (var k in d.Keys) if (string.Equals(k, oid, StringComparison.Ordinal) && ++cnt > 1) return true;
                return false;
            }

            private static bool IsChildOf(string oid, string parent)
            {
                return oid == parent || (oid.StartsWith(parent + ".", StringComparison.Ordinal));
            }

            private async Task<SnmpResponse> RoundTripAsync(byte[] req, CancellationToken ct)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(_timeoutMs);
                using var udp = new UdpClient();
                var send = udp.SendAsync(req, req.Length, _host, _port);
                var sendTimeout = Task.Delay(_timeoutMs, cts.Token);
                if (await Task.WhenAny(send, sendTimeout) != send) throw new TimeoutException($"send {_host}:{_port} timeout");
                await send;

                var any = udp.ReceiveAsync();
                var recvTimeout = Task.Delay(_timeoutMs, cts.Token);
                if (await Task.WhenAny(any, recvTimeout) != any) throw new TimeoutException($"read {_host}:{_port} timeout");
                var result = await any;
                return ParseResponse(result.Buffer);
            }

            private byte[] BuildRequest(byte pduType, List<VarBind> vars)
            {
                int requestId = _requestId++;
                var body = new MemoryStream();
                body.WriteTlv(0x02, EncodeInt(requestId));
                body.WriteTlv(0x02, EncodeInt(0)); // error-status
                body.WriteTlv(0x02, EncodeInt(0)); // error-index
                var vbList = new MemoryStream();
                foreach (var vb in vars)
                {
                    var vbInner = new MemoryStream();
                    vbInner.WriteTlv(0x06, EncodeOid(vb.Oid));
                    vbInner.WriteTlv(0x05, Array.Empty<byte>()); // NULL value
                    vbList.WriteTlv(0x30, vbInner.ToArray());
                }
                body.WriteTlv(0x30, vbList.ToArray()); // varbind-list SEQUENCE
                var pdu = body.ToArray();

                var msg = new MemoryStream();
                msg.WriteTlv(0x02, EncodeInt(_version));
                msg.WriteTlv(0x04, Encoding.UTF8.GetBytes(_community));
                msg.WriteTlv(pduType, pdu);
                var msgOuter = msg.ToArray();
                return new byte[] { 0x30, (byte)msgOuter.Length }.Concat(msgOuter).ToArray();
            }
        }

        private sealed class VarBind
        {
            public string Oid;
            public string? Value;
            public bool IsEndOfMibView;
            public VarBind(string oid, string? value, bool endOfMib = false) { Oid = oid; Value = value; IsEndOfMibView = endOfMib; }
        }

        private sealed class SnmpResponse
        {
            public int ErrorStatus;
            public List<VarBind> Vars = new();
            public string GetString(string oid)
            {
                var v = Vars.FirstOrDefault(x => string.Equals(x.Oid, oid, StringComparison.Ordinal));
                return v?.Value ?? "";
            }
        }

        private static SnmpResponse ParseResponse(byte[] data)
        {
            var resp = new SnmpResponse();
            var tlvs = ParseTlvStream(data);
            if (tlvs.Count == 0) return resp;
            var outer = tlvs[0];
            if (outer.Type != 0x30) return resp;
            var pduOuter = outer.Children.FirstOrDefault(x => x.Type == 0xA0 || x.Type == 0xA1 || x.Type == 0xA2);
            if (pduOuter == null) return resp;
            var vals = pduOuter.Children;
            // [request-id, error-status, error-index, varbindlist]
            int errIdx = 1;
            if (vals.Count > errIdx) resp.ErrorStatus = (int)ReadInt(vals[errIdx].Value);
            var vbList = vals.FirstOrDefault(x => x.Type == 0x30);
            if (vbList == null) return resp;
            foreach (var vb in vbList.Children)
            {
                var oidTlv = vb.Children.FirstOrDefault(x => x.Type == 0x06);
                if (oidTlv == null) continue;
                string oid = ReadOid(oidTlv.Value);
                var valTlv = vb.Children.Count > 1 ? vb.Children[1] : null;
                var vb2 = new VarBind(oid, null);
                if (valTlv == null) { resp.Vars.Add(vb2); continue; }
                if (valTlv.Type == 0x81 || valTlv.Type == 0x80) { resp.Vars.Add(vb2); continue; } // noSuchInstance/noSuchObject
                if (valTlv.Type == 0x82) { vb2.IsEndOfMibView = true; resp.Vars.Add(vb2); continue; }
                vb2.Value = ReadValue(valTlv.Type, valTlv.Value);
                resp.Vars.Add(vb2);
            }
            return resp;
        }

        private static string ReadValue(byte type, byte[] val)
        {
            switch (type)
            {
                case 0x02: // INTEGER / signed
                    return ReadInt(val).ToString();
                case 0x41: // Counter32
                case 0x42: // Gauge32 / unsigned
                case 0x43: // TimeTicks
                case 0x67: // Unsigned32
                    return ReadUnsign(val).ToString();
                case 0x46: // Counter64
                    return ReadUnsign64(val).ToString();
                case 0x04: // OCTET STRING
                    return Encoding.UTF8.GetString(val);
                case 0x40: // IpAddress
                    return string.Join(".", val);
                case 0x06: // OID value
                    return ReadOid(val);
                default:
                    return Convert.ToHexString(val);
            }
        }

        private static long ReadInt(byte[] b)
        {
            if (b.Length == 0) return 0;
            bool neg = (b[0] & 0x80) != 0;
            long v = 0;
            foreach (var x in b) v = (v << 8) | x;
            if (neg && b.Length < 8) v -= 1L << (8 * b.Length);
            return v;
        }

        private static long ReadUnsign(byte[] b)
        {
            long v = 0;
            foreach (var x in b) v = (v << 8) | x;
            return v;
        }

        private static long ReadUnsign64(byte[] b)
        {
            long v = 0;
            foreach (var x in b) v = (v << 8) | x;
            return v;
        }

        // ----- BER helpers -----

        private sealed class Tlv
        {
            public byte Type;
            public byte[] Value = Array.Empty<byte>();
            public List<Tlv> Children = new();
        }

        private static List<Tlv> ParseTlvStream(byte[] data)
        {
            var list = new List<Tlv>();
            int off = 0;
            while (off + 2 <= data.Length)
            {
                var t = ParseTlv(data, ref off);
                if (t == null) break;
                list.Add(t);
            }
            return list;
        }

        private static Tlv? ParseTlv(byte[] data, ref int off)
        {
            if (off + 2 > data.Length) return null;
            byte type = data[off++];
            int len = data[off++];
            if ((len & 0x80) != 0)
            {
                int nLen = len & 0x7F;
                if (off + nLen > data.Length) return null;
                len = 0;
                for (int i = 0; i < nLen; i++) len = (len << 8) | data[off++];
            }
            if (off + len > data.Length) return null;
            var tlv = new Tlv { Type = type };
            if ((type & 0x20) != 0 && len > 0)
            {
                int inner = off;
                int end = off + len;
                while (inner + 2 <= end)
                {
                    var child = ParseTlv(data, ref inner);
                    if (child == null) break;
                    tlv.Children.Add(child);
                }
                off = end;
            }
            else
            {
                tlv.Value = new byte[len];
                Array.Copy(data, off, tlv.Value, 0, len);
                off += len;
            }
            return tlv;
        }

        private static byte[] EncodeInt(long v)
        {
            bool neg = v < 0;
            ulong uv = neg ? (ulong)(-(v + 1)) + 1 : (ulong)v;
            var bytes = new List<byte>();
            do { bytes.Insert(0, (byte)(uv & 0xFF)); uv >>= 8; } while (uv != 0 && !(bytes.Count >= 8));
            if (bytes.Count == 0) bytes.Add(0);
            if (neg && (bytes[0] & 0x80) == 0) bytes.Insert(0, 0xFF);
            else if (!neg && (bytes[0] & 0x80) != 0) bytes.Insert(0, 0x00);
            var outB = new byte[bytes.Count];
            for (int i = 0; i < bytes.Count; i++) outB[i] = bytes[i];
            return outB;
        }

        private static byte[] EncodeOid(string oid)
        {
            var parts = oid.Split('.').Select(s => { long.TryParse(s, out var v); return v; }).ToArray();
            var bytes = new List<byte>();
            if (parts.Length >= 2)
            {
                var first = parts[0] * 40 + parts[1];
                bytes.Add((byte)first);
                for (int i = 2; i < parts.Length; i++) bytes.AddRange(EncodeSubId(parts[i]));
            }
            return bytes.ToArray();
        }

        private static IEnumerable<byte> EncodeSubId(long v)
        {
            var tmp = new List<byte>();
            do { tmp.Insert(0, (byte)(v & 0x7F)); v >>= 7; } while (v != 0);
            for (int i = 0; i < tmp.Count - 1; i++) tmp[i] = (byte)(tmp[i] | 0x80);
            return tmp;
        }

        private static string ReadOid(byte[] b)
        {
            if (b.Length == 0) return "";
            var parts = new List<long>();
            long first = b[0];
            parts.Add(Math.Min(first / 40, 2));
            parts.Add(first % 40);
            long cur = 0;
            bool any = false;
            for (int i = 1; i < b.Length; i++)
            {
                cur = (cur << 7) | (b[i] & 0x7F);
                any = true;
                if ((b[i] & 0x80) == 0) { parts.Add(cur); cur = 0; any = false; }
            }
            if (any) parts.Add(cur);
            return string.Join(".", parts);
        }
    }

    internal static class MemoryStreamExtensions
    {
        public static void WriteTlv(this MemoryStream ms, byte type, byte[] content)
        {
            ms.WriteByte(type);
            if (content.Length < 0x80)
            {
                ms.WriteByte((byte)content.Length);
            }
            else
            {
                var lenBytes = new List<byte>();
                int len = content.Length;
                while (len > 0) { lenBytes.Insert(0, (byte)(len & 0xFF)); len >>= 8; }
                ms.WriteByte((byte)(0x80 | lenBytes.Count));
                foreach (var b in lenBytes) ms.WriteByte(b);
            }
            ms.Write(content, 0, content.Length);
        }
    }
}