using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using ShadowRDP.Services;

namespace ShadowRDP
{
    // Entry point. ApplicationContext-based so the tray icon keeps the app
    // alive when the main form is hidden (mirrors the PowerShell version's
    // $script:appContext pattern).
    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // FIRST thing — migrate the data folder from legacy ShadowRDP
            // → PupSeekRDP. Must run BEFORE Logger.Info because Logger
            // creates the new folder (and an empty placeholder if the
            // folder was empty), which would shadow the legacy data.
            try { AppPaths.EnsureDataDir(); } catch { }
            // v3.8.x — seed / load the user-defined groups (groups.json) so the
            // Book filter pills reflect any persisted custom groups on launch.
            try { ShadowRDP.Helpers.ComputerGroup.EnsureLoaded(); } catch { }

            // SECOND — log that we even started. If this line never makes
            // it to the log file, the .exe being run is either an old
            // build or crashing before reaching here.
            try
            {
                Logger.Info($"##### Pup Seek RDP .exe entry point reached (PID {Environment.ProcessId}, elevated={AdminElevation.IsAdministrator()}, args=[{string.Join(",", args)}])");
            }
            catch { }

            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // v2.1.88: opt this process into IE11 Edge mode for the WebBrowser
            // control. Without this, embedded WebBrowser defaults to IE7 mode
            // and modern sites (Grafana, webmin, etc.) render with errors.
            Helpers.IeEmulation.Enable();

            // .NET 8 ships with only ASCII + Unicode-family encodings. qwinsta.exe
            // outputs in the system OEM code page (866 on Russian Windows). Without
            // this registration, Encoding.GetEncoding(866) throws
            // NotSupportedException. Registering once at startup makes all legacy
            // code pages available for the lifetime of the process.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            // --no-splash is passed by RelaunchElevated so the elevated
            // instance skips the splash (user already saw it in the
            // non-elevated instance before UAC).
            var skipSplash = args.Contains("--no-splash", StringComparer.OrdinalIgnoreCase);
            var alreadyAdmin = AdminElevation.IsAdministrator();

            if (!skipSplash && !alreadyAdmin)
            {
                ShowSplashBlocking(3000);
            }

            // v3.0.18 — auto-elevate without asking. App MUST be admin for
            // shadow RDP (qwinsta /server:<remote>). Previously (v3.0.16-17)
            // we ran non-elevated and shadow was broken. v3.0.16's elevation
            // DIALOG was removed per request, but elevation itself is
            // required — now it's automatic, just the UAC system prompt.
            if (!alreadyAdmin)
            {
                Logger.Info("Not admin — auto-relaunching elevated via UAC");
                if (AdminElevation.RelaunchElevated("--no-splash"))
                {
                    return;  // elevated process is starting, exit this instance
                }
                // UAC declined or relaunch failed — can't continue without admin.
                MessageBox.Show(
                    "Pup Seek RDP требует прав администратора для работы shadow RDP (qwinsta /server).",
                    "Pup Seek RDP",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            var isAdmin = true;
            Logger.Info($"===== Pup Seek RDP C# starting (PID {Environment.ProcessId}, admin={isAdmin}) =====");

            // One-shot diagnostic: ask Windows Error Reporting to keep a crash
            // dump for this app. A native access violation (v3.7.x: seen in
            // the OpenFiles domain scan) dies without any managed trace, so a
            // dump is the only way to locate it. We run elevated after the
            // auto-UAC, so the HKLM write works; failure is silently ignored.
            try { EnsureLocalDumps(); } catch { }

            // Clean up any pre-v2.1.31 DPAPI password file. The password is
            // no longer persisted (in-memory only) so the user gets a fresh
            // prompt on every app launch.
            try
            {
                var oldPwdPath = AppPaths.RdpPasswordPath;
                if (System.IO.File.Exists(oldPwdPath))
                {
                    System.IO.File.Delete(oldPwdPath);
                    Logger.Info($"Deleted stale DPAPI password file: {oldPwdPath}");
                }
            }
            catch (Exception ex) { Logger.Warn($"Failed to clean up old DPAPI file: {ex.Message}"); }

            // Crash diagnostics. Register BEFORE any window/context exists.
            // No SetUnhandledExceptionMode here: it throws if a window already
            // exists on the thread (v3.7.x bug — the splash created one, so
            // every launch died with InvalidOperationException). ThreadException
            // fires for UI-thread exceptions by default; just hooking it works.
            Application.ThreadException += (s, e) =>
            {
                try { Logger.Error($"UI thread exception: {e.Exception}"); } catch { }
                try
                {
                    MessageBox.Show(
                        $"Возникла ошибка:\n{e.Exception.Message}\n\nПодробности — в debug.log.",
                        "Pup Seek RDP",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
                catch { }
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try { Logger.Error($"Unhandled exception: {e.ExceptionObject}"); } catch { }
            };

            var context = new ShadowRdpAppContext(isAdmin);

            // Wipe any TERMSRV/* credentials we pushed to Windows Credential
            // Manager for the session — they were CRED_PERSIST_SESSION but
            // we still want them gone when the app exits, not lingering
            // until logoff.
            Application.ApplicationExit += (s, e) =>
            {
                try { Controls.RdpClient.WipeAllWrittenCreds(); } catch { }
            };
            Application.Run(context);
        }

        // Configures WER LocalDumps for PupSeekRDP.exe (HKLM, requires admin).
        // Full dumps (DumpType=2) land in %PROGRAMDATA%\PupSeekRDP\crashdumps.
        private static void EnsureLocalDumps()
        {
            var keyPath = @"SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\PupSeekRDP.exe";
            using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(keyPath);
            if (key == null) return;
            var dumpFolder = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "PupSeekRDP", "crashdumps");
            key.SetValue("DumpFolder", dumpFolder, Microsoft.Win32.RegistryValueKind.ExpandString);
            key.SetValue("DumpType", 2, Microsoft.Win32.RegistryValueKind.DWord);
            key.SetValue("DumpCount", 5, Microsoft.Win32.RegistryValueKind.DWord);
            try { Logger.Info($"WER LocalDumps configured: {dumpFolder}"); } catch { }
        }

        // Shows the splash for `ms` milliseconds, blocking the calling
        // thread but pumping messages so the splash renders correctly.
        // Closes itself via AutoCloseAfter; we just wait for that here.
        private static void ShowSplashBlocking(int ms)
        {
            try
            {
                var splash = new Forms.SplashForm();
                splash.Show();
                splash.Refresh();
                splash.AutoCloseAfter(ms);
                // Pump messages until the splash closes itself. Without
                // DoEvents, Thread.Sleep would freeze the splash form.
                while (!splash.IsDisposed && splash.Visible)
                {
                    Application.DoEvents();
                    Thread.Sleep(20);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Splash show failed: {ex.Message}");
            }
        }

        // v3.0.16 — admin-rights dialog (AskUserForElevation) removed.
        // App runs with whatever rights it was launched with.
    }
}
