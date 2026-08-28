using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ShadowRDP.Models;

namespace ShadowRDP.Services
{
    // v3.8.x — Export/import of the RDP Manager sidebar contents: the RDP
    // session list (RdpSessionStore.Entry) plus the SSH/Web shortcuts
    // (CustomSession). Written as a single JSON file:
    //
    //   { "rdp": [...], "custom": [...] }
    //
    // Serialization mirrors the stores' options (UnsafeRelaxedJsonEscaping,
    // WhenWritingNull) plus a string enum converter so SessionKind reads as
    // "Ssh"/"Web" instead of a number.
    public static class SessionExportService
    {
        private sealed class FileModel
        {
            public List<RdpSessionStore.Entry> Rdp { get; set; } = new();
            public List<CustomSession> Custom { get; set; } = new();
        }

        private static readonly JsonSerializerOptions _jsonOpts = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() }
        };

        public static void Write(
            string filePath,
            IEnumerable<RdpSessionStore.Entry> rdp,
            IEnumerable<CustomSession> custom)
        {
            var model = new FileModel
            {
                Rdp = rdp?.Where(e => e != null && !string.IsNullOrWhiteSpace(e.Computer)).ToList()
                     ?? new List<RdpSessionStore.Entry>(),
                Custom = custom?.Where(c => c != null &&
                        !string.IsNullOrWhiteSpace(c.Name) && !string.IsNullOrWhiteSpace(c.Target)).ToList()
                     ?? new List<CustomSession>()
            };
            var json = JsonSerializer.Serialize(model, _jsonOpts);
            File.WriteAllText(filePath, json, new UTF8Encoding(false));
        }

        public static (List<RdpSessionStore.Entry> Rdp, List<CustomSession> Custom) Read(string filePath)
        {
            var json = File.ReadAllText(filePath, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json))
                return (new List<RdpSessionStore.Entry>(), new List<CustomSession>());

            var model = JsonSerializer.Deserialize<FileModel>(json, _jsonOpts)
                        ?? new FileModel();
            var rdp = model.Rdp?
                .Where(e => e != null && !string.IsNullOrWhiteSpace(e.Computer))
                .ToList() ?? new List<RdpSessionStore.Entry>();
            var custom = model.Custom?
                .Where(c => c != null &&
                            !string.IsNullOrWhiteSpace(c.Name) && !string.IsNullOrWhiteSpace(c.Target))
                .ToList() ?? new List<CustomSession>();
            return (rdp, custom);
        }
    }
}
