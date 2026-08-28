using System;
using System.Diagnostics;
using System.Security.Principal;

namespace ShadowRDP.Services
{
    // Admin-rights detection + elevation helper.
    //
    // qwinsta.exe /server:<remote> requires admin (or Distributed COM Users
    // membership) on the target — without it you get the misleading
    // "No sessions exist for *" error. We surface this in the title bar and
    // offer to relaunch elevated at startup.
    public static class AdminElevation
    {
        public static bool IsAdministrator()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        // Re-launch the current .exe with admin rights via UAC. Returns true if
        // the elevation was started successfully — caller MUST exit immediately
        // so the elevated process is the only one running.
        // Optional `args` are passed through to the relaunched process —
        // used for "--no-splash" so the elevated instance skips the splash
        // the user already saw in the non-elevated instance.
        public static bool RelaunchElevated(params string[] args)
        {
            try
            {
                var exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath)) return false;
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true,        // required for Verb="runas"
                    Verb = "runas",
                    WorkingDirectory = Environment.CurrentDirectory
                };
                if (args != null && args.Length > 0)
                {
                    // ShellExecute expects args joined as a single string.
                    var parts = new System.Collections.Generic.List<string>(args.Length);
                    foreach (var a in args)
                    {
                        // Quote args containing spaces; we don't expect any
                        // but the rule of thumb is to always quote.
                        parts.Add(a.Contains(' ') ? $"\"{a}\"" : a);
                    }
                    psi.Arguments = string.Join(" ", parts);
                }
                Process.Start(psi);
                return true;
            }
            catch (Exception ex)
            {
                // User clicked "No" on the UAC prompt, or some other failure.
                Logger.Warn($"Elevation refused/failed: {ex.Message}");
                return false;
            }
        }
    }
}
