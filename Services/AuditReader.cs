using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace ShadowRDP.Services
{
    // v3.9.4 — чтение аудит-журнала для просмотрщика. Файлы месячные
    // audit_*.jsonl + сжатые audit_*.jsonl.gz (ретеншн). Запись одна —
    // строка JSON {ts,user,action,target,result,details}.
    public static class AuditReader
    {
        public sealed class Record
        {
            public string Ts { get; set; } = "";
            public string User { get; set; } = "";
            public string Action { get; set; } = "";
            public string Target { get; set; } = "";
            public string Result { get; set; } = "";
            public string Details { get; set; } = "";
        }

        // Разбор одной строки JSONL. null — мусор/пустая строка.
        public static Record? ParseLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return null;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                static string Get(JsonElement e, string name) =>
                    e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                        ? v.GetString() ?? "" : "";
                return new Record
                {
                    Ts = Get(r, "ts"),
                    User = Get(r, "user"),
                    Action = Get(r, "action"),
                    Target = Get(r, "target"),
                    Result = Get(r, "result"),
                    Details = Get(r, "details")
                };
            }
            catch
            {
                return null;
            }
        }

        // Все записи всех месяцев, новые сверху. limit — защита UI:
        // старые отсекаются после сортировки.
        public static List<Record> ReadAll(int limit = 5000)
        {
            var result = new List<Record>();
            try
            {
                var path = AppPaths.AuditLogPath;
                var dir = Path.GetDirectoryName(path);
                if (dir == null || !Directory.Exists(dir)) return result;
                foreach (var f in Directory.GetFiles(dir, "audit_*.jsonl*"))
                {
                    try
                    {
                        foreach (var line in ReadLines(f))
                        {
                            var rec = ParseLine(line);
                            if (rec != null) result.Add(rec);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"AuditReader: {Path.GetFileName(f)}: {ex.Message}");
                    }
                }
                result.Sort((a, b) => string.Compare(b.Ts, a.Ts, StringComparison.Ordinal));
                if (result.Count > limit)
                    result.RemoveRange(limit, result.Count - limit);
            }
            catch (Exception ex)
            {
                Logger.Warn($"AuditReader: scan failed: {ex.Message}");
            }
            return result;
        }

        private static IEnumerable<string> ReadLines(string file)
        {
            if (file.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            {
                using var fs = File.OpenRead(file);
                using var gz = new GZipStream(fs, CompressionMode.Decompress);
                using var sr = new StreamReader(gz);
                string? line;
                while ((line = sr.ReadLine()) != null) yield return line;
            }
            else
            {
                foreach (var line in File.ReadLines(file)) yield return line;
            }
        }
    }
}
