using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Forms;
using ShadowRDP.Helpers;
using ShadowRDP.Models;

namespace ShadowRDP.Services
{
    // Напоминалки TODO.шной: раз в 30 c проверяет сроки; просроченные и не
    // уведомлённые задачи показывают баллун из трея (работает даже когда окно
    // свёрнуто). Повторяющиеся задачи промотаются на следующее наступление.
    public static class TodoReminderService
    {
        private static readonly object _lock = new object();
        private static System.Threading.Timer? _timer;
        private static bool _running;
        private static int _busy;

        public static void Start()
        {
            lock (_lock)
            {
                if (_running) return;
                _running = true;
            }
            try
            {
                _timer = new System.Threading.Timer(_ => Tick(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30));
                Logger.Info("TodoReminderService started (30s)");
            }
            catch (Exception ex) { try { Logger.Warn($"TodoReminderService Start failed: {ex.Message}"); } catch { } }
        }

        // v4.7 — остановка для жизненного цикла модуля (отключение TODO.шной).
        public static void Stop()
        {
            lock (_lock)
            {
                _running = false;
                try { _timer?.Dispose(); } catch { }
                _timer = null;
            }
            try { Logger.Info("TodoReminderService stopped"); } catch { }
        }

        private static void Tick()
        {
            if (Interlocked.Exchange(ref _busy, 1) == 1) return;
            try
            {
                var now = DateTime.Now;
                List<TodoItem>? list = TodoStore.Load();
                bool changed = false;
                foreach (var it in list)
                {
                    if (it == null || it.Done || it.Status == TodoStatus.Cancelled) continue;
                    if (!it.DueAt.HasValue) continue;
                    // v3.9.3CZ — оповещение за N минут до срока; повтор —
                    // пере-напоминать каждые 10 мин, пока не выполнена.
                    var fireAt = it.DueAt.Value.AddMinutes(-Math.Max(0, it.RemindMinutes));
                    if (it.Notified)
                    {
                        if (!it.RepeatRemind) continue;
                        if (!it.LastRemindedAt.HasValue || it.LastRemindedAt.Value.AddMinutes(10) > now) continue;
                        if (it.DueAt.Value < now.AddHours(-24)) continue; // grace как ниже
                        try
                        {
                            ShadowRdpAppContext.ShowBalloon(
                                "Напоминание · TODO",
                                $"«{it.Text}» — {it.DueAt.Value:dd.MM HH:mm} (повторно)",
                                ToolTipIcon.Info,
                                openTodo: true);
                            System.Media.SystemSounds.Asterisk.Play();
                            it.LastRemindedAt = now;
                            changed = true;
                        }
                        catch { }
                        continue;
                    }
                    if (fireAt > now) continue;
                    // v4.9.4 — grace-период: задачи, просроченные больше чем на 24 часа,
                    // не бомбардируют баллунами (напр. приехали с телефона/календаря
                    // с давним сроком и были добавлены после наступления времени).
                    if (it.DueAt.Value < now.AddHours(-24)) continue;

                    changed = true;
                    try
                    {
                        var mark = TodoSchedule.Mark(it.Repeat);
                        string suffix = string.IsNullOrWhiteSpace(mark) ? "" : " · " + mark;
                        // v3.9.3CZ — упреждение видно в тексте («за 15 мин»).
                        string early = it.RemindMinutes > 0 && fireAt < it.DueAt.Value
                            ? $" · за {it.RemindMinutes} мин" : "";
                        ShadowRdpAppContext.ShowBalloon(
                            "Напоминание · TODO",
                            $"«{it.Text}» — {it.DueAt.Value:dd.MM HH:mm}{suffix}{early}",
                            ToolTipIcon.Info,
                            openTodo: true);
                        System.Media.SystemSounds.Asterisk.Play();
                    }
                    catch { }
                    it.LastRemindedAt = now;

                    if (it.Repeat == TodoRepeat.None)
                    {
                        it.Notified = true; // одноразовое напоминание
                    }
                    else
                    {
                        try
                        {
                            it.DueAt = TodoSchedule.Next(it.Repeat, it.DueAt.Value, it.RepeatDays, it.RepeatDayOfMonth);
                            it.Notified = false; // промотка на следующий раз
                        }
                        catch { it.Notified = true; }
                    }
                }
                if (changed) TodoStore.Save(list);
            }
            catch (Exception ex) { try { Logger.Warn($"TodoReminderService tick failed: {ex.Message}"); } catch { } }
            finally { Interlocked.Exchange(ref _busy, 0); }
        }
    }
}