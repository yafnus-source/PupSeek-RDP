using System;

namespace ShadowRDP.Models
{
    // v3.9.4 — SSH-туннель (port forwarding). Привязан к SSH-сессии
    // (SessionId): креды не хранятся — спрашиваются при запуске.
    // Kind: "local" — локальный порт → Target через SSH;
    //       "remote" — порт на сервере → Target отсюда;
    //       "dynamic" — локальный SOCKS5-прокси.
    public class PortForward
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "";
        public string SessionId { get; set; } = "";
        public string Kind { get; set; } = "local";
        public int BindPort { get; set; }
        public string TargetHost { get; set; } = "";
        public int TargetPort { get; set; }
    }
}
