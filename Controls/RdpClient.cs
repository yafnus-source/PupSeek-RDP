using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace ShadowRDP.Controls
{

    // RDP embedding via the Microsoft Terminal Services ActiveX control
    // (MsRdpClient9 from mstscax.dll). Unlike the mstsc.exe + SetParent
    // approach (which renders via DirectComposition and ignores Win32
    // clip regions — causing the desktop to spill outside the panel),
    // the ActiveX hosts the DirectX surface inside our own control
    // hierarchy, so Dock=Fill correctly clips the desktop to the panel.
    //
    // The ActiveX is loaded by ProgID resolution at construction time
    // (MSTSCAX.MsRdpClient9 with fallbacks) so we don't have to ship an
    // aximp-generated wrapper. GetOcx() retrieves the late-bound object
    // we configure via dynamic.
    public class RdpClient : AxHost
    {
        private dynamic? _ocx;
        private string _target = "";
        private string? _pendingTarget;
        private bool _connected;

        public bool IsConnected => _connected;
        public string Target => _target;

        // === Windows Credential Manager interop ===
        // We push creds with TargetName="TERMSRV/<ip>" — mstsc automatically
        // queries this target during Connect() and uses the stored creds.
        // Way more reliable than trying to reach IMsTscNonScriptable through
        // AxHost (which QI-rejects the cast) or via IDispatch (which doesn't
        // expose the "non-scriptable" property at all).
        //
        // CRED_PERSIST_SESSION keeps them in the user's logon session —
        // they survive app close but go away on logoff/restart. We also
        // track every target we wrote so we can wipe them on app exit
        // for cleanliness (matches the "in-memory only" intent).
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CREDENTIAL
        {
            public uint Flags;
            public uint Type;
            public string TargetName;
            public string Comment;
            public long LastWritten;  // FILETIME
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredWrite(ref CREDENTIAL cred, uint flags);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredDelete(string targetName, uint type, uint flags);

        private const uint CRED_TYPE_GENERIC = 1;
        private const uint CRED_PERSIST_SESSION = 1;

        // Targets we've pushed creds for — wiped on app exit so the user's
        // Credential Manager doesn't accumulate TERMSRV/ entries.
        private static readonly HashSet<string> _writtenTargets = new();
        private static readonly object _credLock = new object();

        public static void WipeAllWrittenCreds()
        {
            lock (_credLock)
            {
                foreach (var t in _writtenTargets)
                {
                    try { CredDelete(t, CRED_TYPE_GENERIC, 0); }
                    catch { /* best-effort */ }
                }
                _writtenTargets.Clear();
                Logger.Info("RDP ActiveX: wiped all session-stored credentials");
            }
        }

        private static void WriteCredForTarget(string target, string login, string password)
        {
            if (string.IsNullOrEmpty(login) || string.IsNullOrEmpty(password)) return;
            var credTarget = $"TERMSRV/{target}";
            var passwordBytes = Encoding.Unicode.GetBytes(password);
            var blobPtr = Marshal.AllocHGlobal(passwordBytes.Length);
            try
            {
                Marshal.Copy(passwordBytes, 0, blobPtr, passwordBytes.Length);
                var cred = new CREDENTIAL
                {
                    Type = CRED_TYPE_GENERIC,
                    TargetName = credTarget,
                    Comment = "Shadow RDP",
                    CredentialBlobSize = (uint)passwordBytes.Length,
                    CredentialBlob = blobPtr,
                    Persist = CRED_PERSIST_SESSION,
                    UserName = login
                };
                if (CredWrite(ref cred, 0))
                {
                    lock (_credLock) _writtenTargets.Add(credTarget);
                    Logger.Info($"RDP ActiveX: CredWrite OK for {credTarget} as {login}");
                }
                else
                {
                    var err = Marshal.GetLastWin32Error();
                    Logger.Warn($"RDP ActiveX: CredWrite failed for {credTarget}, Win32Error={err}");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(blobPtr);
            }
        }

        // Resolve CLSID once at class-init from the most recent ProgID
        // available on the machine. Falls back through the whole version
        // ladder down to legacy MsTscAx.
        private static readonly string Clsid = ResolveClsid();

        private static string ResolveClsid()
        {
            foreach (var progId in new[] {
                "MSTSCAX.MsRdpClient10",
                "MSTSCAX.MsRdpClient9",
                "MSTSCAX.MsRdpClient8",
                "MSTSCAX.MsRdpClient7",
                "MSTSCAX.MsRdpClientNotSafeForScripting",
                "MSTSCAX.MsRdpClient",
                "MSTSCAX.MsTscAx"
            })
            {
                try
                {
                    var t = Type.GetTypeFromProgID(progId, throwOnError: false);
                    if (t != null)
                    {
                        Logger.Info($"RDP ActiveX: ProgID '{progId}' resolved → CLSID {t.GUID:B}");
                        return t.GUID.ToString("B");
                    }
                }
                catch { /* try next */ }
            }
            // Hard fallback — known CLSID for legacy MsTscAx on Windows.
            Logger.Warn("RDP ActiveX: no ProgID resolved, falling back to legacy MsTscAx CLSID");
            return "{1C8F8E27-4D8E-11D1-8C8B-00AA00C29B47}";
        }

        public RdpClient() : base(Clsid)
        {
            BackColor = System.Drawing.Color.Black;
            Dock = DockStyle.Fill;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                _ocx = GetOcx();
                Logger.Info($"RDP ActiveX: OCX instantiated, type={_ocx?.GetType().Name ?? "(null)"}");

                // If Connect() was called before our handle existed, the
                // target (+ login + password) was stashed. Now that the
                // OCX is real, complete the connection.
                if (_pendingTarget != null)
                {
                    var target = _pendingTarget;
                    var login = _pendingLogin;
                    var pwd = _pendingPassword;
                    _pendingTarget = null;
                    _pendingLogin = null;
                    _pendingPassword = null;
                    DoConnect(target, login, pwd);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"RDP ActiveX: GetOcx failed: {ex.Message}");
            }
        }

        public void Connect(string target, string? login = null, string? password = null)
        {
            if (_connected) return;
            _target = target;

            // AxHost creates the OCX lazily on first handle creation, which
            // happens after the control is shown — but RdpManagerForm calls
            // Connect() right after constructing us. Defer until OCX is real.
            if (_ocx == null)
            {
                _pendingTarget = target;
                _pendingLogin = login;
                _pendingPassword = password;
                Logger.Info($"RDP ActiveX: deferring Connect for {target} until handle is created");
                return;
            }

            DoConnect(target, login, password);
        }

        private string? _pendingLogin;
        private string? _pendingPassword;

        private void DoConnect(string target, string? login = null, string? password = null)
        {
            if (_ocx == null)
            {
                Logger.Warn($"RDP ActiveX: DoConnect called but OCX still null for {target}");
                return;
            }

            try
            {
                // Server name / IP.
                _ocx.Server = target;

                // Set UserName explicitly so CredSSP/NLA knows who to
                // authenticate as. Login can be plain SAM name ("user"),
                // DOMAIN\user, or user@domain — all forms handled by mstsc.
                // Domain is NOT set programmatically — mstsc derives it
                // from the local machine / login string.
                if (!string.IsNullOrEmpty(login))
                {
                    try
                    {
                        _ocx.UserName = login;
                        Logger.Info($"RDP ActiveX: UserName set to '{login}' for {target}");
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"RDP ActiveX: UserName set failed: {ex.Message}");
                    }
                }

                // Push creds into Windows Credential Manager with target
                // TERMSRV/<ip>. mstsc automatically queries this during
                // Connect() and uses the stored creds — way more reliable
                // than IMsTscNonScriptable.ClearTextPassword (which the
                // AxHost RCW refuses to QI for). CRED_PERSIST_SESSION keeps
                // them in the logon session; we wipe them on app exit.
                if (!string.IsNullOrEmpty(login) && !string.IsNullOrEmpty(password))
                {
                    WriteCredForTarget(target, login, password);
                }

                // Try the newest AdvancedSettings interface available —
                // SmartSizing is on all of them but EnableCredSspSupport &
                // AuthenticationLevel are only on newer versions.
                dynamic? adv = ResolveAdvancedSettings(_ocx);
                if (adv != null)
                {
                    try { adv.SmartSizing = true; } catch { /* not on this version */ }
                    try { adv.EnableCredSspSupport = true; } catch { }
                    try { adv.AuthenticationLevel = 0; } catch { } // suppress cert prompt for IP targets
                    try { adv.EnableAutoReconnect = true; } catch { }
                    Logger.Info($"RDP ActiveX: AdvancedSettings configured (SmartSizing, CredSSP, AuthLevel=0)");
                }

                _ocx.Connect();
                _connected = true;
                Logger.Info($"RDP ActiveX: Connect() called for {target}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"RDP ActiveX: Connect failed for {target}: {ex.Message}");
                throw;
            }
        }

        private static dynamic? ResolveAdvancedSettings(dynamic ocx)
        {
            foreach (var prop in new[] {
                "AdvancedSettings10", "AdvancedSettings9", "AdvancedSettings8", "AdvancedSettings7",
                "AdvancedSettings6", "AdvancedSettings5", "AdvancedSettings4",
                "AdvancedSettings3", "AdvancedSettings2", "AdvancedSettings"
            })
            {
                try
                {
                    var v = ocx.GetType().InvokeMember(prop, BindingFlags.GetProperty, null, ocx, null);
                    if (v != null)
                    {
                        Logger.Info($"RDP ActiveX: using {prop}");
                        return v;
                    }
                }
                catch { /* try next */ }
            }
            return null;
        }

        public void Disconnect()
        {
            if (!_connected) return;
            try
            {
                _ocx?.Disconnect();
                Logger.Info($"RDP ActiveX: Disconnect() called for {_target}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"RDP ActiveX: Disconnect failed: {ex.Message}");
            }
            _connected = false;
        }

        // Compatibility shim — RdpManagerForm calls this on resize and
        // tab switch. AxHost + Dock=Fill handles sizing internally so
        // there's nothing for us to do.
        public void RefreshSize() { }
    }
}
