using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using ShadowRDP.Models;

namespace ShadowRDP.Services
{
    // Isolated store: only todo.json + todo_settings.json in %PROGRAMDATA%\PupSeekRDP\
    // No dependency on RDP / WMI / KeePass. Deleting todo.json fully resets feature (rollback).
    public static class TodoStore
    {
        // v4.9.4 — внешние обработчики (главная форма: бейдж TODO в сайдбаре)
        // узнают о любом изменении списка. Срабатывает после записи на диск
        // (Save — единая точка сохранения). Хендлер сам решает, нужен ли
        // Invoke на UI-поток — событие приходит на потоке вызывающего.
        public static event Action? DataChanged;

        private static readonly object _lock = new object();
        private static readonly JsonSerializerOptions _opts = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
        private static readonly JsonSerializerOptions _readOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        // ---- Items ----
        public static List<TodoItem> Load()
        {
            lock (_lock)
            {
                try
                {
                    var path = AppPaths.TodoPath;
                    if (!File.Exists(path)) return new List<TodoItem>();
                    var json = File.ReadAllText(path, Encoding.UTF8);
                    if (string.IsNullOrWhiteSpace(json)) return new List<TodoItem>();
                    var list = JsonSerializer.Deserialize<List<TodoItem>>(json, _readOpts);
                    return list ?? new List<TodoItem>();
                }
                catch (Exception ex) { try { Logger.Warn($"TodoStore Load failed: {ex.Message}"); } catch { } return new List<TodoItem>(); }
            }
        }

        public static void Save(List<TodoItem> items)
        {
            lock (_lock)
            {
                try
                {
                    var path = AppPaths.TodoPath;
                    Directory.CreateDirectory(AppPaths.DataDir);
                    var safe = items ?? new List<TodoItem>();
                    // sort: Active first, then by CreatedAt desc
                    var sorted = safe.OrderBy(x => x.Done).ThenByDescending(x => x.CreatedAt).ToList();
                    File.WriteAllText(path, JsonSerializer.Serialize(sorted, _opts), new UTF8Encoding(false));
                    // best-effort mirror to monitor_data for free sync (like Dashboard)
                    try { AppPaths.SyncToMonitor(path, "todo.json"); } catch { }
                }
                catch (Exception ex) { try { Logger.Warn($"TodoStore Save failed: {ex.Message}"); } catch { } }
            }
            try { DataChanged?.Invoke(); } catch { }
        }

        public static TodoItem Add(string text, string source = "manual", DateTime? dueAt = null, string? calendarUid = null)
        {
            lock (_lock)
            {
                var list = Load();
                var item = new TodoItem
                {
                    Id = Guid.NewGuid(),
                    Text = (text ?? "").Trim(),
                    Done = false,
                    Status = TodoStatus.Active,
                    CreatedAt = DateTime.Now,
                    DueAt = dueAt,
                    Source = source,
                    CalendarUid = calendarUid
                };
                if (string.IsNullOrWhiteSpace(item.Text)) item.Text = "(пустая задача)";
                list.Insert(0, item);
                Save(list);
                return item;
            }
        }

        // v3.9.3CZ — полное создание задачи из окна «Новая задача»
        // (сигнатуру Add не трогаем — её проверяет харнесс).
        public static TodoItem AddTask(string text, DateTime? dueAt, TodoRepeat repeat,
            IList<int> days, int dayOfMonth, int remindMinutes, bool repeatRemind)
        {
            lock (_lock)
            {
                var list = Load();
                var item = new TodoItem
                {
                    Id = Guid.NewGuid(),
                    Text = string.IsNullOrWhiteSpace(text) ? "(пустая задача)" : text.Trim(),
                    Done = false,
                    Status = TodoStatus.Active,
                    CreatedAt = DateTime.Now,
                    DueAt = dueAt,
                    Source = "manual",
                    Repeat = repeat,
                    RepeatDays = repeat == TodoRepeat.Weekly && days != null
                        ? days.Where(d => d >= 0 && d <= 6).Distinct().OrderBy(d => d).ToList()
                        : new List<int>(),
                    RepeatDayOfMonth = repeat == TodoRepeat.Monthly ? Math.Clamp(dayOfMonth, 1, 31) : 0,
                    RemindMinutes = Math.Max(0, remindMinutes),
                    RepeatRemind = repeatRemind
                };
                list.Insert(0, item);
                Save(list);
                return item;
            }
        }

        // Import helper for calendar sync: deduplicate by Id
        public static int Merge(IEnumerable<TodoItem> incoming)
        {
            if (incoming == null) return 0;
            lock (_lock)
            {
                var existing = Load();
                var ids = new HashSet<Guid>(existing.Select(x => x.Id));
                System.Collections.Generic.HashSet<string>? tomb = null;
                try
                {
                    var del = LoadSettings().DeletedIds;
                    if (del != null && del.Count > 0)
                        tomb = new System.Collections.Generic.HashSet<string>(del, System.StringComparer.OrdinalIgnoreCase);
                }
                catch { }
                int added = 0;
                foreach (var it in incoming)
                {
                    if (it == null || string.IsNullOrWhiteSpace(it.Text)) continue;
                    if (ids.Contains(it.Id)) continue;
                    if (tomb != null && tomb.Contains(it.Id.ToString())) continue; // удалена — не воскрешать
                    if (it.Id == Guid.Empty) it.Id = Guid.NewGuid();
                    if (it.CreatedAt == default) it.CreatedAt = DateTime.Now;
                    existing.Insert(0, it);
                    ids.Add(it.Id);
                    added++;
                }
                if (added > 0) Save(existing);
                return added;
            }
        }

        public static void Update(TodoItem item)
        {
            if (item == null) return;
            lock (_lock)
            {
                var list = Load();
                var idx = list.FindIndex(x => x.Id == item.Id);
                if (idx >= 0) list[idx] = item;
                else list.Insert(0, item);
                Save(list);
            }
        }

        public static void Delete(Guid id)
        {
            lock (_lock)
            {
                var list = Load();
                var n = list.RemoveAll(x => x.Id == id);
                if (n > 0) Save(list);
                // Tombstone: следующий опрос не должен вернуть задачу с сервера.
                // Сохраняется даже при n == 0 (повторное удаление / уже нет локально).
                try
                {
                    var s = LoadSettings();
                    s.DeletedIds ??= new System.Collections.Generic.List<string>();
                    var key = id.ToString();
                    if (!s.DeletedIds.Contains(key))
                    {
                        s.DeletedIds.Add(key);
                        while (s.DeletedIds.Count > 2000) s.DeletedIds.RemoveAt(0);
                        SaveSettings(s);
                    }
                }
                catch { }
            }
            // v3.9.3CZ — телефонный синк вырезан: удаления только локальные.
        }

        // Массовое удаление: один проход (Load один раз, Save один раз),
        // tombstones и fire-and-forget на сервер — для каждого Id.
        public static void DeleteMany(IEnumerable<Guid> ids)
        {
            if (ids == null) return;
            var set = new HashSet<Guid>(ids.Where(g => g != Guid.Empty));
            if (set.Count == 0) return;
            lock (_lock)
            {
                var list = Load();
                var n = list.RemoveAll(x => set.Contains(x.Id));
                if (n > 0) Save(list);
                try
                {
                    var s = LoadSettings();
                    s.DeletedIds ??= new System.Collections.Generic.List<string>();
                    foreach (var id in set)
                    {
                        var key = id.ToString();
                        if (!s.DeletedIds.Contains(key)) s.DeletedIds.Add(key);
                    }
                    while (s.DeletedIds.Count > 2000) s.DeletedIds.RemoveAt(0);
                    SaveSettings(s);
                }
                catch { }
            }
            // v3.9.3CZ — телефонный синк вырезан: удаления только локальные.
        }

        public static void ToggleDone(Guid id)
        {
            lock (_lock)
            {
                var list = Load();
                var it = list.FirstOrDefault(x => x.Id == id);
                if (it == null) return;
                it.Done = !it.Done;
                it.Status = it.Done ? TodoStatus.Done : TodoStatus.Active;
                Save(list);
            }
        }

        public static void Postpone(Guid id, DateTime dueAt)
        {
            lock (_lock)
            {
                var list = Load();
                var it = list.FirstOrDefault(x => x.Id == id);
                if (it == null) return;
                it.DueAt = dueAt;
                it.Status = TodoStatus.Postponed;
                Save(list);
            }
        }

        public static void MoveToDate(Guid id, DateTime dueAt)
        {
            Postpone(id, dueAt);
        }

        // Расписание (повтор + ближайший срок). days — int 0=Вс..6=Сб, dayOfMonth 1..31.
        public static void ApplySchedule(Guid id, TodoRepeat repeat, IList<int> days, int dayOfMonth, DateTime? dueAt)
        {
            lock (_lock)
            {
                var list = Load();
                var it = list.FirstOrDefault(x => x.Id == id);
                if (it == null) return;
                it.Repeat = repeat;
                it.RepeatDays = repeat == TodoRepeat.Weekly && days != null
                    ? days.Where(d => d >= 0 && d <= 6).Distinct().OrderBy(d => d).ToList()
                    : new List<int>();
                it.RepeatDayOfMonth = repeat == TodoRepeat.Monthly ? Math.Clamp(dayOfMonth, 1, 31) : 0;
                if (dueAt.HasValue) it.DueAt = dueAt;
                it.Notified = false;
                it.Status = TodoStatus.Active;
                Save(list);
            }
        }

        // ---- Settings ----
        public static TodoSyncSettings LoadSettings()
        {
            lock (_lock)
            {
                try
                {
                    var path = AppPaths.TodoSettingsPath;
                    if (!File.Exists(path)) return new TodoSyncSettings();
                    var json = File.ReadAllText(path, Encoding.UTF8);
                    if (string.IsNullOrWhiteSpace(json)) return new TodoSyncSettings();
                    return JsonSerializer.Deserialize<TodoSyncSettings>(json, _readOpts) ?? new TodoSyncSettings();
                }
                catch { return new TodoSyncSettings(); }
            }
        }

        public static void SaveSettings(TodoSyncSettings s)
        {
            lock (_lock)
            {
                try
                {
                    Directory.CreateDirectory(AppPaths.DataDir);
                    File.WriteAllText(AppPaths.TodoSettingsPath, JsonSerializer.Serialize(s ?? new TodoSyncSettings(), _opts), new UTF8Encoding(false));
                }
                catch (Exception ex) { try { Logger.Warn($"TodoStore SaveSettings failed: {ex.Message}"); } catch { } }
            }
        }
    }
}
