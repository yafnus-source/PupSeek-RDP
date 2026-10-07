using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using ShadowRDP.Models;

namespace ShadowRDP.Helpers
{
    // Парсер iCalendar (RFC 5545) — читает VEVENT-блоки из .ics файла,
    // экспортированного Outlook/Google Calendar/Exchange.
    // Поддерживает: folded lines, \n\, \; \\ экранирование, DTSTART в
    // форматах (UTC Z / TZID / VALUE=DATE), UID/SUMMARY/LOCATION/DESCRIPTION.
    public static class IcsParser
    {
        public static List<CalendarEvent> ParseFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return new List<CalendarEvent>();
            return ParseText(File.ReadAllText(path, Encoding.UTF8));
        }

        // v4.9.4 — парсинг из строки (текст, скачанный по URL автосинка).
        public static List<CalendarEvent> ParseText(string text)
        {
            var result = new List<CalendarEvent>();
            if (string.IsNullOrWhiteSpace(text)) return result;
            var lines = Unfold(text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'));
            CalendarEvent? cur = null;
            foreach (var raw in lines)
            {
                var line = raw.TrimEnd('\r');
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.StartsWith("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase))
                {
                    cur = new CalendarEvent();
                    continue;
                }
                if (line.StartsWith("END:VEVENT", StringComparison.OrdinalIgnoreCase))
                {
                    if (cur != null)
                    {
                        // Дефолт для целодневных — конец дня.
                        if (cur.AllDay && cur.End == null) cur.End = cur.Start.Date.AddDays(1).AddTicks(-1);
                        if (cur.End == null) cur.End = cur.Start;
                        if (!string.IsNullOrWhiteSpace(cur.Summary) && cur.Start != default)
                            result.Add(cur);
                    }
                    cur = null;
                    continue;
                }
                if (cur == null) continue;
                ParseProp(line, cur);
            }
            return result;
        }

        // Разворачивает folded lines (переносы строк ICS: следующая строка
        // начинается с пробела/таба и является продолжением предыдущей).
        private static List<string> Unfold(string[] raw)
        {
            var list = new List<string>();
            var sb = new StringBuilder();
            foreach (var line in raw)
            {
                if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
                {
                    sb.Append(line.Substring(1));
                }
                else
                {
                    if (sb.Length > 0) list.Add(sb.ToString());
                    sb.Clear();
                    sb.Append(line.TrimEnd('\r'));
                }
            }
            if (sb.Length > 0) list.Add(sb.ToString());
            return list;
        }

        private static void ParseProp(string line, CalendarEvent ev)
        {
            int colon = line.IndexOf(':');
            if (colon <= 0) return;
            string nameRaw = line.Substring(0, colon);
            string value = line.Substring(colon + 1);
            string name = nameRaw;
            var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Разбираем параметры свойства: DTSTART;TZID=Europe/Moscow;VALUE=DATE:20260915
            int semi = nameRaw.IndexOf(';');
            if (semi >= 0)
            {
                name = nameRaw.Substring(0, semi);
                foreach (var part in nameRaw.Substring(semi + 1).Split(';'))
                {
                    if (string.IsNullOrWhiteSpace(part)) continue;
                    int eq = part.IndexOf('=');
                    if (eq > 0) props[part.Substring(0, eq).Trim()] = part.Substring(eq + 1).Trim();
                }
            }

            switch (name.ToUpperInvariant())
            {
                case "UID":
                    ev.Uid = value.Trim();
                    break;
                case "SUMMARY":
                    ev.Summary = Unescape(value.Trim());
                    break;
                case "LOCATION":
                    ev.Location = Unescape(value.Trim());
                    break;
                case "DESCRIPTION":
                    ev.Description = Unescape(value.Trim());
                    break;
                case "DTSTART":
                {
                    bool dateOnly = props.ContainsKey("VALUE") &&
                                    props["VALUE"].Equals("DATE", StringComparison.OrdinalIgnoreCase);
                    var dt = ParseDateTime(value, dateOnly);
                    if (dt.HasValue) { ev.Start = dt.Value; ev.AllDay = dateOnly; }
                    break;
                }
                case "DTEND":
                {
                    bool dateOnly = props.ContainsKey("VALUE") &&
                                    props["VALUE"].Equals("DATE", StringComparison.OrdinalIgnoreCase);
                    var dt = ParseDateTime(value, dateOnly);
                    if (dt.HasValue) ev.End = dt.Value;
                    break;
                }
            }
        }

        // Основные форматы DTSTART:
        //   20260915T140000Z            — UTC (Z-суффикс)
        //   20260915T140000             — «флоатинг» локальное время (как есть)
        //   20260915                    — целый день (VALUE=DATE)
        private static DateTime? ParseDateTime(string v, bool dateOnly)
        {
            v = (v ?? "").Trim();
            if (v.Length == 0) return null;
            try
            {
                string body = v;
                bool utc = body.EndsWith("Z", StringComparison.OrdinalIgnoreCase);
                if (utc) body = body.Substring(0, body.Length - 1);

                if (dateOnly || body.Length == 8)
                {
                    if (DateTime.TryParseExact(body, "yyyyMMdd", CultureInfo.InvariantCulture,
                            DateTimeStyles.None, out var d))
                        return d.Date;
                    return null;
                }

                // yyyyMMddTHHmmss или yyyyMMddTHHmm (без секунд)
                string[] fmts = { "yyyyMMdd'T'HHmmss", "yyyyMMdd'T'HHmm", "yyyyMMdd'T'HHmmss.fff" };
                if (DateTime.TryParseExact(body, fmts, CultureInfo.InvariantCulture,
                        utc ? DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal
                            : DateTimeStyles.None, out var t))
                {
                    return utc ? t.ToLocalTime() : t;
                }

                // Запасной вариант — общий парсер.
                if (DateTime.TryParse(body, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t2))
                    return utc ? t2.ToLocalTime() : t2;
                return null;
            }
            catch { return null; }
        }

        private static string Unescape(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var sb = new StringBuilder(s.Length);
            bool bs = false;
            foreach (var ch in s)
            {
                if (bs)
                {
                    sb.Append(ch switch
                    {
                        'n' => '\n',
                        'N' => '\n',
                        't' => '\t',
                        '\\' => '\\',
                        ';' => ';',
                        ',' => ',',
                        _ => ch
                    });
                    bs = false;
                }
                else if (ch == '\\')
                {
                    bs = true;
                }
                else
                {
                    sb.Append(ch);
                }
            }
            if (bs) sb.Append('\\');
            return sb.ToString();
        }
    }
}