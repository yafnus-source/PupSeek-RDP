using System;
using System.Diagnostics;
using System.DirectoryServices;
using System.Linq;
using System.Management;
using System.Windows.Forms;

namespace ShadowRDP.Services
{
    // Remote actions against a computer: send popup message, restart, logoff.
    // Mirror of Send-RemoteMessage / Invoke-RemoteShutdown from the .ps1
    // version.
    //
    // All methods are fire-and-forget from the UI's perspective — they kick
    // off the action via WMI and return immediately.
    public static class RemoteActions
    {
        // Send a popup message to all logged-in users on the target computer.
        // Uses the Win32 ShutdownTracker / SendMessage method — same one
        // shutdown.exe uses internally. The popup shows on top of whatever
        // the target user has open.
        public static bool SendMessage(string computer, string message, int timeoutSec = 60)
        {
            if (string.IsNullOrWhiteSpace(computer)) return false;
            try
            {
                var scopePath = new ManagementPath($"\\\\{computer}\\root\\cimv2");
                var scope = new ManagementScope(scopePath,
                    new ConnectionOptions
                    {
                        Timeout = TimeSpan.FromSeconds(6),
                        Authentication = AuthenticationLevel.PacketPrivacy
                    });
                scope.Connect();

                using var osClass = new ManagementClass(scope, new ManagementPath("Win32_Process"), null);
                // Wrap the message body in escaped quotes so the inner
                // msg.exe call doesn't break on spaces / special chars.
                // msg.exe args: <session|*> /TIME:<sec> <message>. We target *
                // to send to all logged-in users on the box.
                var safeMsg = (message ?? "").Replace("\"", "\\\"");
                var args = $"msg * /TIME:{timeoutSec} \"{safeMsg}\"";
                using var inParams = osClass.GetMethodParameters("Create");
                inParams["CommandLine"] = args;
                using var outParams = osClass.InvokeMethod("Create", inParams, null);
                var retVal = outParams?.Properties["ReturnValue"]?.Value;
                if (retVal != null && (uint)retVal == 0)
                {
                    Logger.Info($"SendMessage to {computer}: '{message}'");
                    return true;
                }
                Logger.Warn($"SendMessage to {computer} returned {retVal}");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Warn($"SendMessage failed on {computer}: {ex.Message}");
                return false;
            }
        }

        // Restart the remote computer. force=true means don't wait for apps
        // to close gracefully. Reason gets written to the event log.
        public static bool Restart(string computer, bool force = true, int timeoutSec = 30, string reason = "")
        {
            if (string.IsNullOrWhiteSpace(computer)) return false;
            try
            {
                var scopePath = new ManagementPath($"\\\\{computer}\\root\\cimv2");
                var scope = new ManagementScope(scopePath,
                    new ConnectionOptions
                    {
                        Timeout = TimeSpan.FromSeconds(6),
                        Authentication = AuthenticationLevel.PacketPrivacy,
                        EnablePrivileges = true
                    });
                scope.Connect();

                using var osClass = new ManagementClass(scope, new ManagementPath("Win32_OperatingSystem"), null);
                using var inParams = osClass.GetMethodParameters("Win32ShutdownTracker");
                // Win32ShutdownTracker args:
                //   Timeout — seconds before action
                //   Message — popup text shown to user
                //   ShutdownType — bit flags: 6 = shutdown, 2 = force, 4 = reboot
                //   ReasonCode — written to event log
                uint shutdownType = 4;          // reboot
                if (force) shutdownType |= 2;   // OR with force flag
                inParams["Timeout"] = timeoutSec;
                inParams["Message"] = reason.Length == 0 ? "Перезагрузка инициирована администратором" : reason;
                inParams["ShutdownType"] = shutdownType;
                inParams["ReasonCode"] = (uint)0;

                using var outParams = osClass.InvokeMethod("Win32ShutdownTracker", inParams, null);
                var retVal = outParams?.Properties["ReturnValue"]?.Value;
                if (retVal != null && (uint)retVal == 0)
                {
                    Logger.Info($"Restart initiated on {computer} (force={force}, timeout={timeoutSec}s)");
                    return true;
                }
                Logger.Warn($"Restart on {computer} returned {retVal}");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Warn($"Restart failed on {computer}: {ex.Message}");
                return false;
            }
        }

        // End a specific session on the remote computer by ID. Uses logoff.exe
        // which is much simpler than the WMI equivalent.
        public static bool EndSession(string computer, int sessionId)
        {
            if (string.IsNullOrWhiteSpace(computer)) return false;
            if (sessionId <= 0) return false;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "logoff.exe",
                    Arguments = $"{sessionId} /SERVER:{computer.Trim()} /V",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) return false;
                if (!proc.WaitForExit(8000)) { try { proc.Kill(); } catch { } return false; }
                Logger.Info($"EndSession on {computer} (id={sessionId}, exit={proc.ExitCode})");
                return proc.ExitCode == 0;
            }
            catch (Exception ex)
            {
                Logger.Warn($"EndSession failed on {computer} (id={sessionId}): {ex.Message}");
                return false;
            }
        }

        // Launch an MSC applet (Computer Management, Services, Event Viewer)
        // against a remote computer. All three accept /computer:\\NAME.
        // UseShellExecute=true so the MSC has its own process + UI without
        // being tied to ours.
        public static bool OpenMsc(string computer, string mscName)
        {
            if (string.IsNullOrWhiteSpace(computer)) return false;
            if (string.IsNullOrWhiteSpace(mscName)) return false;
            try
            {
                var unc = computer.Trim().Replace("\\", "");
                var psi = new ProcessStartInfo
                {
                    FileName = mscName,
                    Arguments = $"/computer=\\\\{unc}",
                    UseShellExecute = true
                };
                Process.Start(psi);
                Logger.Info($"Opened {mscName} on {computer}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn($"OpenMsc '{mscName}' on {computer} failed: {ex.Message}");
                return false;
            }
        }

        // Open \\computer\c$ in Explorer — admin share access. Requires admin
        // on the target (UAC elevation must be in effect).
        public static bool OpenCShare(string computer)
        {
            if (string.IsNullOrWhiteSpace(computer)) return false;
            try
            {
                var unc = computer.Trim().Replace("\\", "");
                var psi = new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\\\\{unc}\\c$",
                    UseShellExecute = true
                };
                Process.Start(psi);
                Logger.Info($"Opened C$ on {computer}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn($"OpenCShare on {computer} failed: {ex.Message}");
                return false;
            }
        }

        // Launch an RDP session in the tabbed RDP Manager. If the address
        // book has an IP for this computer, uses the IP; otherwise uses the
        // computer name directly. The manager is a singleton — repeated calls
        // add new tabs instead of new windows.
        //
        // v3.9.3 — transient: подключение из книги НЕ пишет сессию
        // в сайдбар и на диск (вкладка открывается, следа нет).
        // Повторный коннект к тому же хосту переиспользует вкладку.
        public static bool LaunchRdp(string computer)
        {
            if (string.IsNullOrWhiteSpace(computer)) return false;
            try
            {
                // Resolve IP from address book.
                string? ip = null;
                try
                {
                    var entries = AddressBookStore.Load();
                    var match = entries.FirstOrDefault(e =>
                        string.Equals(e.Computer, computer, StringComparison.OrdinalIgnoreCase));
                    if (match != null && !string.IsNullOrWhiteSpace(match.IPs))
                    {
                        ip = match.IPs.Split(',', ';').FirstOrDefault()?.Trim();
                        if (!string.IsNullOrEmpty(ip))
                            Logger.Info($"RDP: resolved {computer} → {ip} via address book");
                    }
                }
                catch { /* fall through with computer name */ }

                // Open in RDP Manager (singleton — reuses tab if already open).
                var mgr = Forms.RdpManagerForm.Instance;
                if (mgr.TryShowSession(computer.Trim()))
                {
                    Logger.Info($"RDP Manager: reused existing tab for {computer}");
                    return true;
                }
                mgr.AddSession(computer.Trim(), ip, transient: true);
                Logger.Info($"RDP Manager: transient session for {computer} (target: {ip ?? computer}), no sidebar trace");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn($"LaunchRdp on {computer} failed: {ex.Message}");
                Logger.Warn("LaunchRdp: falling back to standalone mstsc.exe (this is what opens fullscreen RDP outside the manager)");
                // Fallback: launch standalone mstsc.
                try
                {
                    Process.Start("mstsc.exe", $"/v:{computer.Trim()}");
                    return true;
                }
                catch { return false; }
            }
        }

        // Set the password of a LOCAL user account on a remote computer via
        // the ADSI WinNT provider. Uses SAM RPC under the hood — same channel
        // as compmgmt.msc "Local Users and Groups" uses, NOT net user's
        // command-line argument path (which leaks the password through
        // process listing).
        //
        // Requires admin on the target (we already elevate at startup). The
        // password must meet the target's local complexity policy or the
        // call fails — the caller surfaces the exception text.
        //
        // The password value is NEVER logged — only the fact that a change
        // was attempted and the outcome.
        public static bool SetLocalUserPassword(string computer, string userName, string newPassword)
        {
            if (string.IsNullOrWhiteSpace(computer)) return false;
            if (string.IsNullOrWhiteSpace(userName)) return false;
            if (newPassword == null) return false;
            try
            {
                var path = $"WinNT://{computer.Trim()}/{userName.Trim()},user";
                using var entry = new DirectoryEntry(path);
                // SetPassword can internally try multiple protocols (NetAPI,
                // LDAP if the name resolves to a domain). For pure local
                // accounts on a workgroup/domain-joined machine it lands on
                // SAM RPC and completes in 1-2 seconds.
                entry.Invoke("SetPassword", new object[] { newPassword });
                entry.CommitChanges();
                Logger.Info($"Password changed on {computer} for local user '{userName}'");
                return true;
            }
            catch (Exception ex)
            {
                // InnerException often has the real reason (access denied,
                // complexity violation, account doesn't exist, etc.).
                var msg = ex.InnerException?.Message ?? ex.Message;
                Logger.Warn($"SetLocalUserPassword on {computer}/{userName} failed: {msg}");
                throw new Exception(msg, ex);
            }
        }
    }
}

