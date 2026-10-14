using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ShadowRDP.Services
{
    // v3.9.4 — удалённое выполнение одной команды для «Терминала».
    // Канал 1 — WinRM (Invoke-Command, полный stdout/stderr/exit code);
    // канала 2 — WMI Win32_Process.Create с перенаправлением вывода во
    // временный файл на удалённой машине и чтением через admin$.
    // Fallback на WMI — только когда WinRM не дал вывода (exit!=0 и
    // stdout пуст), чтобы не выполнять команду дважды при ошибке самой
    // команды.
    public static class RemoteCommand
    {
        public sealed class Result
        {
            public bool Ok;
            public string Channel = "";
            public int ExitCode;
            public string Output = "";
            public string Error = "";
        }

        // v3.9.5 — кодировка вывода: powershell.exe/cmd пишут в локальную
        // OEM-кодовую страницу (для ru-RU это CP866), а .NET читает по
        // умолчанию UTF-8 — отсюда кракозябры. Регистрируем codepages-
        // провайдер и читаем в OEM; файл WMI-канала декодируем с детектом
        // (строгий UTF-8, иначе OEM).
        private static readonly Encoding OemEncoding = CreateOemEncoding();

        private static Encoding CreateOemEncoding()
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            }
            catch
            {
                return Encoding.UTF8;
            }
        }

        // Декодирует байты вывода: UTF-8 (BOM или строгая валидация),
        // иначе локальный OEM (CP866 и т.п.). ASCII проходит как UTF-8.
        public static string DecodeOutput(byte[]? bytes)
        {
            if (bytes == null || bytes.Length == 0) return "";
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            try
            {
                return new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (ArgumentException)
            {
                return OemEncoding.GetString(bytes);
            }
        }

        // Экранирование для одинарных кавычек PowerShell.
        public static string PsQuote(string s) => (s ?? "").Replace("'", "''");

        // B64S:<base64 UTF-8 текста> и B64B:<base64 сырых байт под
        // DecodeOutput> из WinRM-скрипта. Всё остальное (ошибки, legacy)
        // возвращается как есть: null = не наше.
        public static string? TryDecodeWinrmOutput(string? raw)
        {
            try
            {
                var t = (raw ?? "").Trim();
                string? b64 = null;
                bool isBytes = false;
                if (t.StartsWith("B64S:", StringComparison.Ordinal)) b64 = t.Substring(5).Trim();
                else if (t.StartsWith("B64B:", StringComparison.Ordinal))
                {
                    b64 = t.Substring(5).Trim();
                    isBytes = true;
                }
                else return null;
                if (b64.Length == 0) return "";
                if (b64.Length % 4 != 0) return null;
                foreach (var ch in b64)
                    if (!(ch >= 'A' && ch <= 'Z') && !(ch >= 'a' && ch <= 'z') &&
                        !(ch >= '0' && ch <= '9') && ch != '+' && ch != '/' && ch != '=')
                        return null;
                var bytes = Convert.FromBase64String(b64);
                return isBytes ? DecodeOutput(bytes) : new UTF8Encoding(false, true).GetString(bytes);
            }
            catch { return null; }
        }

        // Собирает скрипт Invoke-Command; пары хост/команда закодированы
        // так, чтобы спецсимволы не сломали разбор.
        // v3.9.5 — кириллица native-команд: бесконсольный wsmprovhost на
        // удалённой стороне декодирует их вывод через ANSI (1251) — диагноз
        // подтверждён замером ([Console]::OutputEncoding.WebName), сеттер
        // OEM там не срабатывает. Поэтому диспетчер на удалённой стороне:
        // cmdlet/функции/алиасы/.ps1/скриптовый синтаксис — PS-тракт
        // (объекты .NET, консоль не участвует); всё остальное (native exe,
        // пайпы, .bat) — побайтовый cmd-тракт: cmd пишет файл в OEM (как
        // проверенный WMI-канал), байты едут base64. Оба тракта ASCII-броня
        // (местный stdout-тракт мимо кодировок): B64S: = UTF-8 текст,
        // B64B: = сырые байты под DecodeOutput. $ProgressPreference давит
        // CLIXML-мусор в stderr с обеих сторон.
        public static string BuildWinrmScript(string host, string command)
        {
            return "$ProgressPreference = 'SilentlyContinue'; " +
                   $"Invoke-Command -ComputerName '{PsQuote(host)}' " +
                   "-ScriptBlock { param($c) " +
                   "$ProgressPreference = 'SilentlyContinue'; " +
                   "$t = $c.Trim(); " +
                   "$usePs = $false; " +
                   "try { " +
                   "if ($t -match '^(?i)(\\$|@|\\()') { $usePs = $true } " +
                   "elseif ($t -match '^(?i)(if|foreach|for|while|do|switch|function|filter|workflow|try|trap|using|param|class|enum)\\b') { $usePs = $true } " +
                   "else { " +
                   "$first = ($t -split '\\s+')[0]; " +
                   "if (Get-Command $first -CommandType Cmdlet,Function,Alias -EA SilentlyContinue) { $usePs = $true } " +
                   "else { $ext = Get-Command $first -CommandType ExternalScript -EA SilentlyContinue | Select-Object -First 1; " +
                   "if ($ext -and ([IO.Path]::GetExtension($ext.Definition) -eq '.ps1')) { $usePs = $true } } } " +
                   "} catch { $usePs = $false } " +
                   "if ($usePs) { " +
                   "try { [Console]::OutputEncoding = [System.Text.Encoding]::GetEncoding([System.Globalization.CultureInfo]::CurrentCulture.TextInfo.OEMCodePage) } catch { } " +
                   "$o = & ([scriptblock]::Create($c)) | Out-String; " +
                   "'B64S:' + [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes([string]$o)) } " +
                   "else { " +
                   "$tmp = [IO.Path]::Combine($env:TEMP, 'psrun_' + [Guid]::NewGuid().ToString('N') + '.bin'); " +
                   "try { & cmd.exe /c ($c + ' > \"' + $tmp + '\" 2>&1'); " +
                   "'B64B:' + [Convert]::ToBase64String([IO.File]::ReadAllBytes($tmp)) } " +
                   "finally { Remove-Item $tmp -EA SilentlyContinue } } } " +
                   $"-ArgumentList '{PsQuote(command)}'";
        }

        public static string EncodedCommand(string script) =>
            Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        public static async Task<Result> RunAsync(string host, string command,
            int timeoutMs, CancellationToken ct)
        {
            var r = await RunWinrmAsync(host, command, timeoutMs, ct);
            // Нет вывода и ошибка — пробуем WMI-канал.
            if (r.ExitCode != 0 && string.IsNullOrWhiteSpace(r.Output) && !ct.IsCancellationRequested)
            {
                var w = await RunWmiAsync(host, command, timeoutMs, ct);
                if (!string.IsNullOrWhiteSpace(w.Output) || w.Ok)
                    return w;
                r.Error = Combine(r.Error, w.Error);
            }
            return r;
        }

        // === Канал 1: WinRM ===
        private static async Task<Result> RunWinrmAsync(string host, string command,
            int timeoutMs, CancellationToken ct)
        {
            var r = new Result { Channel = "winrm" };
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -EncodedCommand {EncodedCommand(BuildWinrmScript(host, command))}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    // powershell.exe пишет stdout/stderr в OEM (CP866) —
                    // читаем в той же кодировке, иначе кракозябры.
                    StandardOutputEncoding = OemEncoding,
                    StandardErrorEncoding = OemEncoding
                };
                using var proc = Process.Start(psi);
                if (proc == null)
                {
                    r.Error = "не удалось запустить powershell";
                    return r;
                }
                // Чтение stdout/stderr до WaitForExit — иначе возможен
                // дедлок при переполнении буфера.
                var outTask = proc.StandardOutput.ReadToEndAsync();
                var errTask = proc.StandardError.ReadToEndAsync();
                var deadline = Environment.TickCount + timeoutMs;
                while (!proc.WaitForExit(200))
                {
                    if (ct.IsCancellationRequested || Environment.TickCount > deadline)
                    {
                        try { proc.Kill(); } catch { }
                        r.Error = ct.IsCancellationRequested ? "отменено" : "таймаут";
                        try { var t = await outTask; r.Output = (TryDecodeWinrmOutput(t) ?? t).Trim(); } catch { }
                        try { r.Error = Combine(r.Error, (await errTask).Trim()); } catch { }
                        return r;
                    }
                }
                try { var t = await outTask; r.Output = (TryDecodeWinrmOutput(t) ?? t).Trim(); } catch { }
                var err = (await errTask).Trim();
                r.ExitCode = proc.ExitCode;
                r.Ok = proc.ExitCode == 0;
                if (!string.IsNullOrWhiteSpace(err)) r.Error = err;
            }
            catch (Exception ex)
            {
                r.Error = ex.Message;
                r.ExitCode = -1;
            }
            return r;
        }

        // === Канал 2: WMI (вывод через файл на удалённой машине) ===
        private static async Task<Result> RunWmiAsync(string host, string command,
            int timeoutMs, CancellationToken ct)
        {
            var r = new Result { Channel = "wmi" };
            var tmpName = $"psrun_{Guid.NewGuid():N}.txt";
            var remotePath = $"C:\\Windows\\Temp\\{tmpName}";
            var uncPath = $"\\\\{host}\\admin$\\Temp\\{tmpName}";
            try
            {
                var connOpts = new ConnectionOptions
                {
                    EnablePrivileges = true,
                    Timeout = TimeSpan.FromMilliseconds(timeoutMs)
                };
                var scope = new ManagementScope($"\\\\{host}\\root\\cimv2", connOpts);
                await Task.Run(() => scope.Connect(), ct);

                // Команда выполняется через cmd, вывод/ошибки — в файл.
                var wrapped = $"cmd.exe /c {command} > \"{remotePath}\" 2>&1";
                uint ret = 0, pid = 0;
                await Task.Run(() =>
                {
                    var processClass = new ManagementClass(scope, new ManagementPath("Win32_Process"),
                        new ObjectGetOptions());
                    var inParams = processClass.GetMethodParameters("Create");
                    inParams["CommandLine"] = wrapped;
                    var outParams = processClass.InvokeMethod("Create", inParams, null);
                    ret = (uint)(outParams["ReturnValue"] ?? 1);
                    pid = (uint)(outParams["ProcessId"] ?? 0);
                }, ct);

                if (ret != 0)
                {
                    r.ExitCode = (int)ret;
                    r.Error = $"Win32_Process.Create вернул {ret}";
                    return r;
                }

                // Ждём завершения: у живого процесса ExitCode в этой системе
                // недоступен (WQL «SELECT ExitCode» → Invalid query), поэтому
                // сигнал — исчезновение процесса из выборки Win32_Process
                // (после завершения строка в выборке нет). ExitCode канала
                // WMI: 0 — завершился (файл вывода читается ниже), -1 —
                // таймаут/отмена; точный код даёт только WinRM-канал.
                var deadline = Environment.TickCount + timeoutMs;
                bool finished = false;
                while (Environment.TickCount < deadline && !finished && !ct.IsCancellationRequested)
                {
                    await Task.Delay(500, ct);
                    try
                    {
                        finished = await Task.Run(() =>
                        {
                            var q = new ObjectQuery($"SELECT ProcessId FROM Win32_Process WHERE ProcessId = {pid}");
                            using var searcher = new ManagementObjectSearcher(scope, q);
                            foreach (ManagementObject _ in searcher.Get()) return false; // ещё жив
                            return true; // исчез — завершён
                        }, ct);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        r.Error = Combine(r.Error, $"опрос: {ex.Message}");
                        break;
                    }
                }
                if (finished)
                {
                    r.ExitCode = 0;
                    r.Ok = true;
                }
                else
                {
                    r.ExitCode = -1;
                    r.Error = Combine(r.Error, ct.IsCancellationRequested ? "отменено" : "таймаут");
                }

                // Читаем файл вывода через admin$.
                try
                {
                    if (File.Exists(uncPath))
                    {
                        // cmd пишет файл в OEM; читаем байты с детектом
                        // UTF-8/OEM, чтобы кириллица не портилась.
                        var bytes = await Task.Run(() => File.ReadAllBytes(uncPath));
                        r.Output = DecodeOutput(bytes).Trim();
                        try { File.Delete(uncPath); } catch { }
                    }
                }
                catch (Exception ex)
                {
                    r.Error = Combine(r.Error, $"чтение вывода: {ex.Message}");
                }
            }
            catch (OperationCanceledException)
            {
                r.Error = "отменено";
                r.ExitCode = -1;
            }
            catch (Exception ex)
            {
                r.Error = Combine(r.Error, ex.Message);
                r.ExitCode = -1;
            }
            return r;
        }

        private static string Combine(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a)) return b ?? "";
            if (string.IsNullOrWhiteSpace(b)) return a;
            return a + " / " + b;
        }
    }
}
