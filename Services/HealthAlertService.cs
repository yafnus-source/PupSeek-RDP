using System;
using System.Collections.Generic;
using System.Linq;

namespace ShadowRDP.Services
{
    // v3.9.3 — уведомления о здоровье хостов: нет пинга / мало места на диске.
    // Проверки дёргаются из HostPingService (после раунда) и из
    // ZabbixAgentPollerService (после опроса). Канал: трей-баллун + звук.
    // Антиспам: каждый инцидент уведомляется ОДИН раз — ключ живёт в _active,
    // пока условие не исчезнет (восстановление снимает ключ молча, в лог).
    public static class HealthAlertService
    {
        private static readonly object _lock = new object();
        private static readonly HashSet<string> _active =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Для харнесса: если задан, вместо баллуна+звука пишем сюда.
        public static Action<string, string>? TestSink;

        public static void ResetForTests()
        {
            lock (_lock) _active.Clear();
        }

        public static int ActiveCount
        {
            get { lock (_lock) return _active.Count; }
        }

        public static void CheckPing(
            IReadOnlyDictionary<string, Models.HostPingPoint> last, bool enabled, bool sound)
        {
            var items = new List<(string Key, string Title, string Text)>();
            try
            {
                foreach (var kv in last)
                {
                    if (kv.Value != null && kv.Value.Ok) continue;
                    string when = kv.Value != null && kv.Value.Time != default
                        ? kv.Value.Time.ToString("HH:mm") : "?";
                    items.Add((
                        "ping:" + kv.Key,
                        "Нет пинга — " + kv.Key,
                        $"{kv.Key} не отвечает на ICMP (проверка {when})"));
                }
            }
            catch { }
            // v3.9.3 — скоуп по видам: пинг и диски трекаются независимо,
            // иначе чужие инциденты считались бы восстановившимися.
            Sync(items, "ping:", enabled, sound);
        }
        public static void CheckDisks(
            IEnumerable<Models.ZabbixAgentData> latest, double thresholdGb, bool enabled, bool sound)
        {
            var items = new List<(string Key, string Title, string Text)>();
            try
            {
                long threshold = (long)(Math.Max(0.1, thresholdGb) * 1024L * 1024L * 1024L);
                foreach (var d in latest ?? Enumerable.Empty<Models.ZabbixAgentData>())
                {
                    if (d == null || d.Disks == null) continue;
                    foreach (var disk in d.Disks)
                    {
                        if (disk == null || disk.Free < 0 || disk.Free >= threshold) continue;
                        double freeGb = disk.Free / 1024.0 / 1024.0 / 1024.0;
                        items.Add((
                            $"disk:{d.Host}:{disk.Mount}",
                            $"Мало места — {d.Host} {disk.Mount}",
                            $"{freeGb:0.0} ГБ свободно (порог {thresholdGb:0.#} ГБ)"));
                    }
                }
            }
            catch { }
            Sync(items, "disk:", enabled, sound);
        }

        private static void Sync(
            List<(string Key, string Title, string Text)> current, string scope, bool enabled, bool sound)
        {
            List<(string Key, string Title, string Text)> fresh = new List<(string, string, string)>();
            lock (_lock)
            {
                var cur = new HashSet<string>(current.Select(i => i.Key), StringComparer.OrdinalIgnoreCase);
                // Восстановившиеся — снять молча (в лог). Только свой скоуп:
                // чужие ключи не трогаем.
                var recovered = _active.Where(k => k.StartsWith(scope, StringComparison.OrdinalIgnoreCase) && !cur.Contains(k)).ToList();
                foreach (var k in recovered) _active.Remove(k);
                if (recovered.Count > 0)
                {
                    try { Logger.Info("HealthAlert: recovered: " + string.Join(", ", recovered)); } catch { }
                }
                // Трекинг всегда (чтобы включение позже не флудило старым),
                // уведомления — только если enabled.
                foreach (var i in current)
                {
                    if (_active.Contains(i.Key)) continue;
                    _active.Add(i.Key);
                    if (enabled) fresh.Add(i);
                }
            }
            foreach (var i in fresh) Notify(i.Title, i.Text, sound);
        }

        private static void Notify(string title, string text, bool sound)
        {
            try
            {
                if (TestSink != null) { TestSink(title, text); return; }
                ShadowRdpAppContext.ShowBalloon(title, text, System.Windows.Forms.ToolTipIcon.Warning);
                if (sound)
                {
                    try { System.Media.SystemSounds.Exclamation.Play(); } catch { }
                }
                Logger.Info("HealthAlert: " + title + " — " + text);
            }
            catch { }
        }
    }
}
