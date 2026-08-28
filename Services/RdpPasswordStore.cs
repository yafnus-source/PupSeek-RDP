using System;

namespace ShadowRDP.Services
{
    // Shared RDP credentials (login + password) for the current app
    // session. Held IN MEMORY ONLY — never persisted to disk.
    //
    // Why no persistence: if the user mistypes the password and we
    // stored it, every subsequent launch would silently use the wrong
    // password. Instead, the prompt appears on every app start so the
    // user gets a fresh chance. The in-memory cache lets the creds be
    // reused for every session within a single app run.
    //
    // To clear mid-session (user mistyped and wants to retry), the
    // RDP Manager header has a "сменить пароль" button that clears
    // the cache and re-prompts.
    //
    // Login is the SAM account name (e.g. "IvanovAD") without a
    // domain prefix — mstsc derives the domain automatically from the
    // client machine. If you need a different domain, the user can
    // enter "DOMAIN\user" in the login field and we pass it through.
    public static class RdpCredentialStore
    {
        public class Creds
        {
            public string Login { get; set; } = "";
            public string Password { get; set; } = "";
        }

        private static readonly object _lock = new object();
        private static Creds? _cached;

        public static bool HasCached
        {
            get
            {
                lock (_lock)
                {
                    return _cached != null
                        && !string.IsNullOrEmpty(_cached.Login)
                        && !string.IsNullOrEmpty(_cached.Password);
                }
            }
        }

        public static Creds? GetCached() { lock (_lock) return _cached; }

        public static void SetCached(string login, string password)
        {
            lock (_lock) _cached = new Creds { Login = login ?? "", Password = password ?? "" };
        }

        public static void Clear()
        {
            lock (_lock) _cached = null;
            Logger.Info("RdpCredentialStore: in-memory credentials cleared");
        }
    }
}
