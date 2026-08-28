using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace ShadowRDP.Services
{
    // Wraps qwinsta.exe and parses its column-formatted output into sessions.
    // Mirror of Invoke-Qwinsta + Parse-Sessions from the PowerShell version.
    //
    // qwinsta prints a header line containing an "ID" column, then one row
    // per session. Column positions in the header define where to slice each
    // row — we can't just Split(' ') because some rows have a missing SESSION
    // column (when the user has no service session). The PS version handles
    // this by extracting the user name from the position just before ID.
    //
    // Both EN and RU column names are tolerated — we look for the literal
    // "ID" token (case-insensitive), which is the same in both locales.
    public static class QwinstaService
    {
        public sealed class Session
        {
            public string UserName { get; set; } = "";
            public int Id { get; set; }
            public string State { get; set; } = "";
        }

        public sealed class Result
        {
            public List<Session> Sessions { get; set; } = new List<Session>();
            public int ExitCode { get; set; }
            public string Output { get; set; } = "";
            public string ErrorText { get; set; } = "";
            public bool TimedOut { get; set; }
        }

        // Default timeout matches the PS version (10s). qwinsta on a dead host
        // can hang up to ~30s on RPC; we cut it at the timeout and report it.
        public const int DefaultTimeoutMs = 10000;

        public static Result Query(string computer, int timeoutMs = DefaultTimeoutMs)
        {
            var result = new Result();
            if (string.IsNullOrWhiteSpace(computer))
            {
                result.ErrorText = "Имя компьютера не задано.";
                return result;
            }

            var psi = new ProcessStartInfo
            {
                FileName = "qwinsta.exe",
                Arguments = $"/server:{computer.Trim()}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.GetEncoding(866),  // OEM code page on Russian Windows
                StandardErrorEncoding = Encoding.GetEncoding(866)
            };

            try
            {
                using var proc = new Process();
                proc.StartInfo = psi;
                var stdout = new StringBuilder();
                var stderr = new StringBuilder();
                proc.OutputDataReceived += (s, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
                proc.ErrorDataReceived += (s, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

                if (!proc.Start())
                {
                    result.ErrorText = "Не удалось запустить qwinsta.exe";
                    return result;
                }
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                if (!proc.WaitForExit(timeoutMs))
                {
                    try { proc.Kill(); } catch { }
                    result.TimedOut = true;
                    result.ErrorText = $"Timeout: qwinsta не ответила за {timeoutMs / 1000} с. Компьютер может быть offline или RPC заблокирован.";
                    Logger.Warn($"Qwinsta timed out on {computer} after {timeoutMs}ms");
                    return result;
                }

                result.ExitCode = proc.ExitCode;
                result.Output = stdout.ToString();
                result.ErrorText = stderr.ToString().Trim();

                // Diagnostic: log the raw qwinsta output so we can see what
                // the parser is working with when sessions come back empty.
                Logger.Info($"Qwinsta raw output on {computer} (exit={proc.ExitCode}, {result.Output.Length} chars):{Environment.NewLine}{result.Output}");
                if (!string.IsNullOrEmpty(result.ErrorText))
                {
                    Logger.Warn($"Qwinsta stderr on {computer}: {result.ErrorText}");
                }

                if (proc.ExitCode != 0)
                {
                    // qwinsta exits non-zero on offline / access-denied. Still
                    // surface whatever output we got — sometimes there's a
                    // useful error message embedded in stdout.
                    Logger.Warn($"Qwinsta exit={proc.ExitCode} on {computer}: {result.ErrorText}");
                }

                result.Sessions = Parse(result.Output);
                return result;
            }
            catch (Exception ex)
            {
                result.ErrorText = ex.Message;
                Logger.Error($"Qwinsta exception on {computer}: {ex.Message}");
                return result;
            }
        }

        // Parse qwinsta output. Tolerant of EN/RU column headers — only the
        // "ID" token matters, which is identical in both locales.
        public static List<Session> Parse(string output)
        {
            var sessions = new List<Session>();
            if (string.IsNullOrWhiteSpace(output)) return sessions;

            var lines = output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

            // Find the header line — it must contain the "ID" token.
            string? header = null;
            foreach (var line in lines)
            {
                if (Regex.IsMatch(line, @"(?:^|\s)ID(?:\s|$)", RegexOptions.IgnoreCase))
                {
                    header = line;
                    break;
                }
            }
            if (header == null) return sessions;

            // Tokenise header into (name, start) pairs.
            var tokens = new List<(string Name, int Start)>();
            foreach (Match m in Regex.Matches(header, @"\S+"))
            {
                tokens.Add((m.Value, m.Index));
            }

            // Locate the ID column. The user name sits in the column immediately
            // before ID, the state in the column immediately after.
            int idIdx = -1;
            for (int i = 0; i < tokens.Count; i++)
            {
                if (string.Equals(tokens[i].Name, "ID", StringComparison.OrdinalIgnoreCase))
                {
                    idIdx = i;
                    break;
                }
            }
            if (idIdx < 1 || idIdx > tokens.Count - 2) return sessions;

            int userStart = tokens[idIdx - 1].Start;
            int idStart = tokens[idIdx].Start;
            int stateStart = tokens[idIdx + 1].Start;
            int stateEnd = (idIdx + 2 < tokens.Count) ? tokens[idIdx + 2].Start : -1;

            foreach (var line in lines)
            {
                if (line == header) continue;
                if (string.IsNullOrWhiteSpace(line)) continue;

                var userName = GetField(line, userStart, idStart);
                var idStr = GetField(line, idStart, stateStart);
                var state = GetField(line, stateStart, stateEnd);

                if (string.IsNullOrWhiteSpace(userName)) continue;
                if (!int.TryParse(idStr, out int id)) continue;

                sessions.Add(new Session
                {
                    UserName = userName,
                    Id = id,
                    State = state
                });
            }

            return sessions;
        }

        // Slice a substring of `line` from `start` up to (exclusive) `next`.
        // Same semantics as the PS Get-Field helper.
        private static string GetField(string line, int start, int next)
        {
            if (start < 0) return "";
            if (start >= line.Length) return "";
            int end = next >= 0 ? next : line.Length;
            int len = Math.Min(end - start, line.Length - start);
            if (len <= 0) return "";
            return line.Substring(start, len).Trim();
        }
    }
}
