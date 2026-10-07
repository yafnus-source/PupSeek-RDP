using System;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ShadowRDP.Services
{
    // v3.9.4 — удалённое выполнение одной команды для «Консоли команд».
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

        // Экранирование для одинарных кавычек PowerShell.
        public static string PsQuote(string s) => (s ?? "").Replace("'", "''");

        // Собирает скрипт Invoke-Command; пары хост/команда закодированы
        // так, чтобы спецсимволы не сломали разбор.
        public static string BuildWinrmScript(string host, string command)
        {
            return $"Invoke-Command -ComputerName '{PsQuote(host)}' " +
                   "-ScriptBlock { param($c) & ([scriptblock]::Create($c)) } " +
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
                    WindowStyle = ProcessWindowStyle.Hidden
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
                        try { r.Output = (await outTask).Trim(); } catch { }
                        try { r.Error = Combine(r.Error, (await errTask).Trim()); } catch { }
                        return r;
                    }
                }
                r.Output = (await outTask).Trim();
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
                        r.Output = (await Task.Run(() => File.ReadAllText(uncPath))).Trim();
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
