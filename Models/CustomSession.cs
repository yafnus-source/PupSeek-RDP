using System;

namespace ShadowRDP.Models
{
    // v2.1.83 — non-RDP "session shortcut" stored in RDP Manager sidebar.
    // Represents an SSH connection or a Web URL the user wants to launch
    // quickly from the sidebar (alongside RDP sessions). NO credentials
    // stored — SSH asks interactively, Web opens in browser as-is.
    //
    // Lives in custom_sessions.json (separate from rdp_sessions.json which
    // holds live RDP connection state).
    public class CustomSession
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "";
        public SessionKind Kind { get; set; } = SessionKind.Ssh;
        // For SSH: "host" or "host:port". For Web: full URL "https://…".
        public string Target { get; set; } = "";
        public string? Group { get; set; }
        public string? Notes { get; set; }
        // v3.8.x — drag&drop sidebar ordering. Explicit position within the
        // session's group (RDP + custom sessions share the numbering). 0
        // default keeps legacy order until the user reorders something.
        public int SortOrder { get; set; }
    }

    // v3.9.3 — Telnet добавлен СТРОГО в конец: Kind сериализуется
    // числом (Ssh=0, Web=1), сдвиг сломал бы старые custom_sessions.json.
    public enum SessionKind
    {
        Ssh,
        Web,
        Telnet
    }
}
