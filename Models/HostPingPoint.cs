using System;

namespace ShadowRDP.Models
{
    // v4.7 — точка ICMP-пинга хоста (даш Z, колонка «Пинг»): опрос раз в час.
    public class HostPingPoint
    {
        public string Host { get; set; } = "";
        public bool Ok { get; set; }
        public long Ms { get; set; }
        public DateTime Time { get; set; } = DateTime.Now;
    }
}
