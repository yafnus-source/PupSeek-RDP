using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;

namespace ShadowRDP.Services
{
    public static class ZabbixAgentConfigurator
    {
        public static string GetLocalIp()
        {
            try
            {
                using var socket = new System.Net.Sockets.UdpClient("8.8.8.8", 80);
                return ((System.Net.IPEndPoint)socket.Client.LocalEndPoint).Address.ToString();
            }
            catch
            {
                var ni = NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback);
                return ni?.GetIPProperties().UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.Address.ToString() ?? "127.0.0.1";
            }
        }

        public static async Task<AgentConfigResult> ConfigureOneAsync(
            string host, string agentConfigPath, string agentServiceName,
            Action<string, string> onProgress = null, CancellationToken ct = default)
        {
            string localIp = GetLocalIp();
            onProgress?.Invoke(host, "проверка WMI...");
            var r = new AgentConfigResult { Host = host };

            try
            {
                // Проверяем WMI доступность
                bool wmiOk = await TestWmiAsync(host, ct);
                if (!wmiOk)
                {
                    r.Error = "WMI недоступен";
                    return r;
                }

                onProgress?.Invoke(host, "чтение конфига...");

                // PowerShell-скрипт на удалённом хосте:
                // 1) читает конфиг, 2) бэкапит, 3) добавляет IP, 4) пишет, 5) валидирует, 6) перезапускает
                string psScript = BuildPsScript(agentConfigPath, localIp, agentServiceName);
                string cmd = $"powershell.exe -NoProfile -NonInteractive -Command \"{psScript.Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ")}\"";

                var result = await WmiRunAsync(host, cmd, 30000, ct);

                // Анализируем результат
                string output = (result.Output ?? "").Trim();
                try { Logger.Info($"ZabbixAgent WMI {host}: exit={result.ExitCode} out={output}"); } catch { }

                if (result.ExitCode == -1)
                {
                    r.Error = result.Error ?? "WMI ошибка";
                    return r;
                }

                if (output.Contains("ERR:"))
                {
                    r.Error = output.Split(new[] { "ERR:" }, StringSplitOptions.None).Last().Trim();
                    return r;
                }

                if (output.Contains("OK:"))
                {
                    r.Ok = true;
                    r.Message = output.Replace("OK:", "").Trim();
                    onProgress?.Invoke(host, "готово");
                }
                else
                {
                    r.Ok = true;
                    r.Message = "команда выполнена (нет подтверждения)";
                    onProgress?.Invoke(host, "выполнено");
                }
            }
            catch (Exception ex)
            {
                r.Error = ex.Message;
            }
            return r;
        }

        private static string BuildPsScript(string configPath, string localIp, string serviceName)
        {
            string cfg = configPath.Replace("'", "''");
            string ip = localIp.Replace("'", "''");
            string svc = serviceName.Replace("'", "''");
            string backupDir = System.IO.Path.GetDirectoryName(configPath)?.Replace("'", "''") ?? @"C:\Program Files\Zabbix Agent";

            // PowerShell-скрипт: бэкап → чтение → добавление IP → запись → валидация → рестарт
            string nl = "`n"; // PowerShell newline в строке
            string ps = "";
            ps += "$ErrorActionPreference='Stop'" + nl;
            ps += "$cfg='" + cfg + "'" + nl;
            ps += "$ip='" + ip + "'" + nl;
            ps += "$svc='" + svc + "'" + nl;
            ps += "$backupDir='" + backupDir + "'" + nl;
            ps += "try {" + nl;
            ps += "  if(-not(Test-Path $cfg)){throw 'File not found: '+$cfg}" + nl;
            ps += "  $ts=Get-Date -Format 'yyyyMMdd_HHmmss'" + nl;
            ps += "  $bak=$backupDir+'\\zabbix_agentd.conf.bak_'+$ts" + nl;
            ps += "  Copy-Item $cfg $bak -Force" + nl;
            ps += "  $content=Get-Content $cfg -Raw" + nl;
            ps += "  $lines=$content -split [char]10" + nl;
            ps += "  $found=$false; $modified=$false; $out=@()" + nl;
            ps += "  foreach($l in $lines){" + nl;
            ps += "    $t=$l.Trim()" + nl;
            ps += "    if($t -match '^Server=' -and $t -notmatch '^ServerActive'){" + nl;
            ps += "      $found=$true" + nl;
            ps += "      $raw=$t.Substring(8).Trim()" + nl;
            ps += "      $ci=$raw.IndexOf('#')" + nl;
            ps += "      if($ci -ge 0){$raw=$raw.Substring(0,$ci).Trim()}" + nl;
            ps += "      $parts=@($raw.Split(',') | ForEach-Object{$_.Trim()} | Where-Object{$_ -ne ''})" + nl;
            ps += "      if($parts -notcontains $ip){$parts+=$ip; $modified=$true}" + nl;
            ps += "      $out+=('Server='+($parts -join ','))" + nl;
            ps += "    } else { $out+=$l }" + nl;
            ps += "  }" + nl;
            ps += "  if(-not $found){" + nl;
            ps += "    $ins=0" + nl;
            ps += "    for($i=0;$i -lt $out.Count;$i++){" + nl;
            ps += "      if($out[$i].Trim().StartsWith('#') -or [string]::IsNullOrWhiteSpace($out[$i].Trim())){$ins=$i+1}else{break}" + nl;
            ps += "    }" + nl;
            ps += "    $al=[System.Collections.ArrayList]@($out)" + nl;
            ps += "    $al.Insert($ins,'Server='+$ip)" + nl;
            ps += "    $out=$al.ToArray(); $modified=$true" + nl;
            ps += "  }" + nl;
            ps += "  if(-not $modified){ Write-Output ('OK:IP '+$ip+' already configured'); exit 0 }" + nl;
            ps += "  $newC=$out -join [char]10" + nl;
            ps += "  Set-Content -Path $cfg -Value $newC -Encoding UTF8 -Force" + nl;
            ps += "  $verify=Get-Content $cfg" + nl;
            ps += "  $ok=$false" + nl;
            ps += "  foreach($v in $verify){" + nl;
            ps += "    if($v.Trim() -match '^Server=' -and $v.Trim() -notmatch '^ServerActive'){" + nl;
            ps += "      if($v -match [regex]::Escape($ip)){$ok=$true;break}" + nl;
            ps += "    }" + nl;
            ps += "  }" + nl;
            ps += "  if(-not $ok){ Copy-Item $bak $cfg -Force; throw 'Validation failed, rolled back' }" + nl;
            ps += "  try {" + nl;
            ps += "    $s=Get-Service -Name $svc -ErrorAction Stop" + nl;
            ps += "    if($s.Status -eq 'Running'){ Stop-Service -Name $svc -Force; Start-Sleep -Seconds 3 }" + nl;
            ps += "    Start-Service -Name $svc -ErrorAction Stop" + nl;
            ps += "    Write-Output ('OK:'+$ip+' added, service restarted')" + nl;
            ps += "  } catch { Write-Output ('OK:'+$ip+' added, restart: '+$_.Exception.Message) }" + nl;
            ps += "} catch { Write-Output ('ERR:'+$_.Exception.Message) }" + nl;

            return ps;
        }

        private static async Task<bool> TestWmiAsync(string host, CancellationToken ct)
        {
            try
            {
                var scope = new ManagementScope($"\\\\{host}\\root\\cimv2");
                scope.Connect();
                var query = new ObjectQuery("SELECT * FROM Win32_OperatingSystem");
                var searcher = new ManagementObjectSearcher(scope, query);
                await Task.Run(() => searcher.Get(), ct);
                return true;
            }
            catch { return false; }
        }

        private static async Task<WmiResult> WmiRunAsync(string host, string command, int timeoutMs, CancellationToken ct)
        {
            var result = new WmiResult();
            try
            {
                var connOpts = new ConnectionOptions
                {
                    EnablePrivileges = true,
                    Timeout = TimeSpan.FromMilliseconds(timeoutMs)
                };
                var scope = new ManagementScope($"\\\\{host}\\root\\cimv2", connOpts);
                scope.Connect();

                var processClass = new ManagementClass(scope, new ManagementPath("Win32_Process"), new ObjectGetOptions());
                var inParams = processClass.GetMethodParameters("Create");
                inParams["CommandLine"] = command;
                var outParams = processClass.InvokeMethod("Create", inParams, null);

                uint pid = (uint)(outParams["ProcessId"] ?? 0);
                uint ret = (uint)(outParams["ReturnValue"] ?? 1);

                if (ret != 0)
                {
                    result.ExitCode = (int)ret;
                    result.Error = $"Create вернул {ret}";
                    return result;
                }

                // Ждём завершения процесса — опрашиваем через WMI
                int waited = 0;
                while (waited < timeoutMs)
                {
                    await Task.Delay(1000, ct);
                    waited += 1000;
                    try
                    {
                        var procQuery = new ObjectQuery($"SELECT ExitCode FROM Win32_Process WHERE ProcessId = {pid}");
                        var procSearcher = new ManagementObjectSearcher(scope, procQuery);
                        foreach (ManagementObject proc in procSearcher.Get())
                        {
                            var exitCode = proc["ExitCode"];
                            if (exitCode != null)
                            {
                                result.ExitCode = (int)(uint)exitCode;
                                return result;
                            }
                        }
                    }
                    catch { break; }
                }

                result.ExitCode = 0;
            }
            catch (Exception ex)
            {
                result.ExitCode = -1;
                result.Error = ex.Message;
            }
            return result;
        }

        private struct WmiResult
        {
            public int ExitCode;
            public string Output;
            public string Error;
        }
    }

    public class AgentConfigResult
    {
        public string Host { get; set; } = "";
        public bool Ok { get; set; }
        public string Message { get; set; } = "";
        public string Error { get; set; } = "";
    }
}
