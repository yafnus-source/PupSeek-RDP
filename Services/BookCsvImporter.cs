using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ShadowRDP.Models;

namespace ShadowRDP.Services
{
    // v3.8.x — Reads back address-book entries from the CSV produced by
    // BookCsvExporter (RFC 4180, UTF-8, English column keys in the header).
    //
    // Header mapping is case-insensitive and tolerant of missing/extra
    // columns: unknown columns are ignored, missing ones stay empty. Rows
    // where BOTH Computer and User are empty are dropped (same junk-filter
    // rule as AddressBookStore).
    public static class BookCsvImporter
    {
        // English header keys (case-insensitive) → AddressBookEntry setter.
        private static readonly Dictionary<string, string> _columnKeys =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Computer", "Computer" },
                { "FullName", "FullName" },
                { "User", "User" },
                { "Phone", "Phone" },
                { "IPs", "IPs" },
                { "LastSeen", "LastSeen" },
                { "Group", "Group" }
            };

        public static List<AddressBookEntry> ImportFromCsv(string filePath)
        {
            var rows = ParseFile(filePath);
            if (rows.Count == 0) return new List<AddressBookEntry>();

            // First row = header. Map each header cell to a field key.
            var header = rows[0];
            var colIndex = new Dictionary<int, string>();
            for (int i = 0; i < header.Count; i++)
            {
                if (colIndex.ContainsKey(i)) continue;
                if (_columnKeys.TryGetValue(header[i] ?? "", out var key))
                    colIndex[i] = key;
            }

            var entries = new List<AddressBookEntry>();
            for (int r = 1; r < rows.Count; r++)
            {
                var row = rows[r];
                var e = new AddressBookEntry();
                foreach (var pair in colIndex)
                {
                    if (pair.Key >= row.Count) continue;
                    SetField(e, pair.Value, row[pair.Key] ?? "");
                }
                // Same junk filter as AddressBookStore.Load().
                if (string.IsNullOrWhiteSpace(e.Computer) && string.IsNullOrWhiteSpace(e.User))
                    continue;
                entries.Add(e);
            }
            return entries;
        }

        private static void SetField(AddressBookEntry e, string key, string value)
        {
            switch (key)
            {
                case "Computer": e.Computer = value.Trim(); break;
                case "FullName": e.FullName = value.Trim(); break;
                case "User": e.User = value.Trim(); break;
                case "Phone": e.Phone = value.Trim(); break;
                case "IPs": e.IPs = value.Trim(); break;
                case "LastSeen": e.LastSeen = value.Trim(); break;
                case "Group": e.Group = value.Trim(); break;
            }
        }

        // RFC 4180 splitter: fields are comma-separated, a field is quoted
        // if it contains comma/quote/newline, embedded quotes are doubled.
        private static List<List<string?>> ParseFile(string filePath)
        {
            var lines = new List<List<string?>>();
            using (var reader = new StreamReader(filePath, Encoding.UTF8))
            {
                var record = new List<string?>();
                var field = new StringBuilder();
                bool inQuotes = false;

                int ch;
                while ((ch = reader.Read()) != -1)
                {
                    char c = (char)ch;
                    if (inQuotes)
                    {
                        if (c == '"')
                        {
                            // "" inside a quoted field = escaped quote.
                            if (reader.Peek() == '"')
                            {
                                reader.Read();
                                field.Append('"');
                            }
                            else inQuotes = false;
                        }
                        else field.Append(c);
                    }
                    else if (c == '"')
                    {
                        inQuotes = true;
                    }
                    else if (c == ',')
                    {
                        record.Add(field.ToString());
                        field.Clear();
                    }
                    else if (c == '\r' || c == '\n')
                    {
                        // Handle CRLF as one terminator.
                        if (c == '\r' && reader.Peek() == '\n') reader.Read();
                        record.Add(field.ToString());
                        field.Clear();
                        lines.Add(record);
                        record = new List<string?>();
                    }
                    else
                    {
                        field.Append(c);
                    }
                }
                if (field.Length > 0 || record.Count > 0)
                {
                    record.Add(field.ToString());
                    lines.Add(record);
                }
            }
            return lines;
        }
    }
}
