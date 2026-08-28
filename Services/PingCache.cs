using System;
using System.Collections.Concurrent;

namespace ShadowRDP.Services
{
    // In-memory cache of last-known ping state per computer. Survives across
    // address-book dialog open/close — so when the user reopens the book
    // within the same app run, they immediately see alive/dead circles
    // instead of all-grey.
    //
    // Not persisted to disk — we lose it on app exit. That's deliberate:
    // ping state goes stale within minutes on a real network, so a persisted
    // value would mislead the user more often than help.
    //
    // Mirror of $script:abPingCache from the .ps1 version (which was also
    // in-memory only).
    public static class PingCache
    {
        // Computer (lowercase) → (Alive, when).
        private static readonly ConcurrentDictionary<string, (bool Alive, DateTime At)> _cache = new();

        public static bool TryGet(string computer, out bool alive, out DateTime at)
        {
            alive = false;
            at = DateTime.MinValue;
            if (string.IsNullOrEmpty(computer)) return false;
            if (_cache.TryGetValue(computer.ToLowerInvariant(), out var entry))
            {
                alive = entry.Alive;
                at = entry.At;
                return true;
            }
            return false;
        }

        public static void Set(string computer, bool alive)
        {
            if (string.IsNullOrEmpty(computer)) return;
            _cache[computer.ToLowerInvariant()] = (alive, DateTime.Now);
        }

        // Returns true if we have NO recent ping result for this computer
        // (older than maxAge, or never pinged). Used to decide whether to
        // trigger a refresh on dialog open.
        public static bool IsStale(string computer, TimeSpan maxAge)
        {
            if (!TryGet(computer, out _, out var at)) return true;
            return (DateTime.Now - at) > maxAge;
        }

        // === Global throttle ===
        //
        // Last time ANY ping was performed (across all computers). Used to
        // avoid re-triggering an auto-ping sweep every time the user reopens
        // the address book — if the last sweep was less than maxAge ago, we
        // skip and just render from cache.
        private static DateTime _lastSweepAt = DateTime.MinValue;

        public static bool IsSweepStale(TimeSpan maxAge)
        {
            return (DateTime.Now - _lastSweepAt) > maxAge;
        }

        public static void MarkSweepDone()
        {
            _lastSweepAt = DateTime.Now;
        }

        // Mostly for tests / debugging.
        public static void Clear() => _cache.Clear();
    }
}
