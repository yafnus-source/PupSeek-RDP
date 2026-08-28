using System.Text.Json.Serialization;

namespace ShadowRDP.Models
{
    // One address-book entry. Schema MUST stay byte-compatible with the
    // PowerShell version (ShadowRDP-GUI.ps1) — both versions read/write the
    // same `addressbook.json`, so adding/renaming fields here breaks the .ps1
    // and vice versa.
    //
    // Field order matches the .ps1 [pscustomobject] order so the JSON diff
    // stays minimal when entries are written by either version.
    public class AddressBookEntry
    {
        [JsonPropertyName("Computer")]
        public string Computer { get; set; } = "";

        [JsonPropertyName("User")]
        public string User { get; set; } = "";

        [JsonPropertyName("IPs")]
        public string IPs { get; set; } = "";

        [JsonPropertyName("FullName")]
        public string FullName { get; set; } = "";

        // Phone is the only field added in the C# rewrite that doesn't exist
        // in the latest .ps1 alfa. The .ps1 will simply ignore this field on
        // load (PS PSCustomObject is permissive about extra properties). When
        // the .ps1 writes back, it'll drop Phone — the C# version handles
        // missing Phone gracefully (defaults to "").
        [JsonPropertyName("Phone")]
        public string Phone { get; set; } = "";

        [JsonPropertyName("LastSeen")]
        public string LastSeen { get; set; } = "";

        // User-assigned category for visual grouping in the address book.
        // Empty string = no group assigned. Values are ComputerGroup.*
        // constants — kept as strings (not enum) so JSON stays human-readable
        // and the .ps1 version can read/write it without enum parsing.
        // Added in C# rewrite only — .ps1 will ignore this on load.
        [JsonPropertyName("Group")]
        public string Group { get; set; } = "";
    }
}
