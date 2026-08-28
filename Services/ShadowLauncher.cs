using System;
using System.Diagnostics;

namespace ShadowRDP.Services
{
    // Launches mstsc.exe in shadow mode against a remote session. Mirror of
    // btnShadow.Add_Click from the PowerShell version.
    //
    // mstsc args:
    //   /shadow:ID       — session ID to shadow (required)
    //   /v:COMPUTER      — remote host (required)
    //   /control         — shadow with control (can move mouse / type);
    //                      without it the shadow is view-only
    //   /noConsentPrompt — skip the "allow shadow?" prompt on the target.
    //                      Requires admin on the target PC.
    //
    // mstsc returns immediately — it spawns its own window. We just need to
    // make sure the process starts.
    public static class ShadowLauncher
    {
        public static void Run(int sessionId, string computer, bool withControl, bool noConsent, bool fullscreen = false)
        {
            if (string.IsNullOrWhiteSpace(computer))
            {
                throw new ArgumentException("Computer name required", nameof(computer));
            }
            if (sessionId <= 0)
            {
                throw new ArgumentException("Session ID must be > 0", nameof(sessionId));
            }

            var args = $"/shadow:{sessionId} /v:{computer.Trim()}";
            if (withControl) args += " /control";
            if (noConsent)   args += " /noConsentPrompt";
            // v3.5.3 — /f is silently ignored by mstsc for /shadow. Kept the
            // parameter for API stability but no longer appended.
            // if (fullscreen)  args += " /f";

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "mstsc.exe",
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                Process.Start(psi);
                Logger.Info($"Shadow launched: session {sessionId} on {computer} (control={withControl}, noConsent={noConsent})");
            }
            catch (Exception ex)
            {
                Logger.Error($"Shadow launch failed ({sessionId}@{computer}): {ex.Message}");
                throw;
            }
        }
    }
}
