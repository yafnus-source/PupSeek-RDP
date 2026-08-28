using System;
using System.Collections.Generic;
using System.DirectoryServices;
using System.Linq;

namespace ShadowRDP.Services
{
    // Active Directory queries via DirectorySearcher. Mirror of Import-UsersFromAD
    // + Get-ADComputers + Get-RemoteUserFullName from the PowerShell version.
    //
    // Uses [System.DirectoryServices] (no RSAT required) — works on any
    // domain-joined Windows 10/11. Auto-detects LDAP path from %USERDNSDOMAIN%.
    public static class AdService
    {
        // Pull enabled users from AD. Returns sAMAccountName + displayName.
        // Skips entries that already exist in the address book (matched by
        // sAMAccountName, case-insensitive).
        public sealed class AdUser
        {
            public string SamAccountName { get; set; } = "";
            public string DisplayName { get; set; } = "";
        }

        // Returns users matching the filter, or empty list on error.
        public static List<AdUser> GetUsers(bool enabledOnly = true, string? ldapPath = null)
        {
            var result = new List<AdUser>();
            var path = ResolveLdapPath(ldapPath);
            if (path == null)
            {
                Logger.Warn("AdService.GetUsers: failed to resolve LDAP path");
                return result;
            }

            try
            {
                using var entry = new DirectoryEntry(path);
                using var searcher = new DirectorySearcher(entry)
                {
                    PageSize = 200,
                    ServerTimeLimit = TimeSpan.FromSeconds(30)
                };
                searcher.PropertiesToLoad.Add("sAMAccountName");
                searcher.PropertiesToLoad.Add("displayName");

                var filter = "(&(objectClass=user)(objectCategory=person)";
                if (enabledOnly) filter += "(!(userAccountControl:1.2.840.113556.1.4.803:=2))";
                filter += ")";
                searcher.Filter = filter;

                using var results = searcher.FindAll();
                foreach (SearchResult r in results)
                {
                    var sam = r.Properties.Contains("sAMAccountName") && r.Properties["sAMAccountName"].Count > 0
                        ? r.Properties["sAMAccountName"][0]?.ToString() ?? ""
                        : "";
                    if (string.IsNullOrEmpty(sam) || sam.EndsWith("$")) continue;

                    var dn = r.Properties.Contains("displayName") && r.Properties["displayName"].Count > 0
                        ? r.Properties["displayName"][0]?.ToString() ?? ""
                        : "";

                    result.Add(new AdUser { SamAccountName = sam, DisplayName = dn });
                }
                Logger.Info($"AdService.GetUsers: returned {result.Count} users");
            }
            catch (Exception ex)
            {
                Logger.Warn($"AdService.GetUsers failed: {ex.Message}");
            }
            return result;
        }

        // Look up displayName by sAMAccountName. Returns "" on miss/error.
        // Used by "Обновить ФИО" — for each entry's User field (after stripping
        // the DOMAIN\ prefix), find the corresponding FullName in AD.
        public static string GetDisplayName(string samAccountName, string? ldapPath = null)
        {
            if (string.IsNullOrWhiteSpace(samAccountName)) return "";
            var path = ResolveLdapPath(ldapPath);
            if (path == null) return "";

            try
            {
                using var entry = new DirectoryEntry(path);
                using var searcher = new DirectorySearcher(entry)
                {
                    Filter = $"(sAMAccountName={EscapeLdapFilter(samAccountName)})",
                    ServerTimeLimit = TimeSpan.FromSeconds(8)
                };
                searcher.PropertiesToLoad.Add("displayName");
                searcher.PropertiesToLoad.Add("name");
                var r = searcher.FindOne();
                if (r == null) return "";

                if (r.Properties.Contains("displayname") && r.Properties["displayname"].Count > 0)
                {
                    var v = r.Properties["displayname"][0]?.ToString();
                    if (!string.IsNullOrEmpty(v)) return v;
                }
                if (r.Properties.Contains("name") && r.Properties["name"].Count > 0)
                {
                    return r.Properties["name"][0]?.ToString() ?? "";
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"AdService.GetDisplayName failed for '{samAccountName}': {ex.Message}");
            }
            return "";
        }

        // Get all computer names from AD. Used by "Resolve computers for
        // imported users" — scan each one for its logged-on user via WMI.
        public static List<string> GetComputers(string? ldapPath = null)
        {
            var result = new List<string>();
            var path = ResolveLdapPath(ldapPath);
            if (path == null) return result;

            try
            {
                using var entry = new DirectoryEntry(path);
                using var searcher = new DirectorySearcher(entry)
                {
                    Filter = "(objectClass=computer)",
                    PageSize = 200,
                    ServerTimeLimit = TimeSpan.FromSeconds(30)
                };
                searcher.PropertiesToLoad.Add("name");
                searcher.PropertiesToLoad.Add("cn");

                using var results = searcher.FindAll();
                foreach (SearchResult r in results)
                {
                    var name = "";
                    if (r.Properties.Contains("name") && r.Properties["name"].Count > 0)
                        name = r.Properties["name"][0]?.ToString() ?? "";
                    if (string.IsNullOrEmpty(name) && r.Properties.Contains("cn") && r.Properties["cn"].Count > 0)
                        name = r.Properties["cn"][0]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(name)) result.Add(name);
                }
                Logger.Info($"AdService.GetComputers: returned {result.Count} computers");
            }
            catch (Exception ex)
            {
                Logger.Warn($"AdService.GetComputers failed: {ex.Message}");
            }
            return result;
        }

        // Resolve "LDAP://DC=foo,DC=bar" from $env:USERDNSDOMAIN. Returns null
        // if domain env var isn't set (e.g. workgroup machine).
        private static string? ResolveLdapPath(string? explicitPath)
        {
            if (!string.IsNullOrEmpty(explicitPath)) return explicitPath;
            var dns = Environment.GetEnvironmentVariable("USERDNSDOMAIN");
            if (string.IsNullOrEmpty(dns)) return null;
            var dcParts = dns.Split('.')
                .Where(p => !string.IsNullOrEmpty(p))
                .Select(p => $"DC={p}");
            return "LDAP://" + string.Join(",", dcParts);
        }

        // Escape special characters in sAMAccountName for LDAP filter safety.
        // sAMAccountName rarely contains these, but be safe.
        private static string EscapeLdapFilter(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            // The .NET DirectorySearcher FilterEscape handles the standard set.
            return value.Replace("\\", "\\5c").Replace("*", "\\2a")
                        .Replace("(", "\\28").Replace(")", "\\29")
                        .Replace("\0", "\\00");
        }
    }
}
