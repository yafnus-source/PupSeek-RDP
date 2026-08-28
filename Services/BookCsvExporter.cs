using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ShadowRDP.Models;

namespace ShadowRDP.Services
{
    // v2.3.13 — Export address-book entries to CSV. Filters by group (empty
    // string = "all groups"), writes only the columns the user picked.
    //
    // Output format: RFC 4180.
    //   - Encoding: UTF-8 with BOM (so Excel opens Cyrillic correctly).
    //   - Separator: comma.
    //   - Quoting: a field is wrapped in double quotes if it contains a
    //     comma, double quote, newline, or leading/trailing whitespace.
    //     Embedded double quotes are doubled ("→"").
    public static class BookCsvExporter
    {
        // AddressBookEntry field keys the dialog/exporter/importer understand.
        // Order here is the display order in the CheckedListBox.
        // v3.8.x: "Group" added so CSV export/import round-trips without
        // losing the user-assigned group.
        public static readonly string[] AvailableColumns =
            { "Computer", "FullName", "User", "Phone", "IPs", "LastSeen", "Group" };

        public static void ExportToCsv(
            IEnumerable<AddressBookEntry> entries,
            string? groupFilter,
            IReadOnlyList<string> columns,
            string filePath)
        {
            if (columns == null || columns.Count == 0)
                throw new ArgumentException("At least one column must be selected.", nameof(columns));

            // Filter by group. Empty/whitespace groupFilter = all groups.
            // Comparison is case-insensitive (groups are user-typed strings).
            var filtered = entries.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(groupFilter))
            {
                var g = groupFilter.Trim();
                filtered = filtered.Where(e => string.Equals(e.Group ?? "", g, StringComparison.OrdinalIgnoreCase));
            }
            var rows = filtered.ToList();

            // UTF-8 with BOM — Excel needs the BOM to detect Cyrillic.
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", columns.Select(EscapeCsv)));
            foreach (var e in rows)
            {
                var values = columns.Select(c => GetFieldValue(e, c));
                sb.AppendLine(string.Join(",", values.Select(EscapeCsv)));
            }
            File.WriteAllText(filePath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }

        private static string GetFieldValue(AddressBookEntry e, string column) => column switch
        {
            "Computer" => e.Computer ?? "",
            "FullName" => e.FullName ?? "",
            "User" => e.User ?? "",
            "Phone" => e.Phone ?? "",
            "IPs" => e.IPs ?? "",
            "LastSeen" => e.LastSeen ?? "",
            "Group" => e.Group ?? "",
            _ => ""
        };

        // RFC 4180: wrap in quotes if the field contains comma, quote,
        // newline, or leading/trailing whitespace. Embedded quotes are doubled.
        private static string EscapeCsv(string? field)
        {
            if (field == null) return "";
            bool needsQuoting = field.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0
                || (field.Length > 0 && (char.IsWhiteSpace(field[0]) || char.IsWhiteSpace(field[^1])));
            if (!needsQuoting) return field;
            return "\"" + field.Replace("\"", "\"\"") + "\"";
        }
    }
}
