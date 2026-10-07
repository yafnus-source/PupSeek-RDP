using System;
using System.Collections.Generic;
using System.Linq;
using ShadowRDP.Models;

namespace ShadowRDP.Helpers
{
    // Следующее наступление повторяющейся задачи. DueAt хранит ближайший
    // срок (дата+время); при срабатывании напоминалка двигает его сюда.
    public static class TodoSchedule
    {
        public static DateTime Next(TodoRepeat repeat, DateTime from, IList<int> days, int dayOfMonth)
        {
            // from — момент "после которого ищем" (обычно текущее время или прошедший срок)
            switch (repeat)
            {
                case TodoRepeat.Daily:
                    return from.AddDays(1);

                case TodoRepeat.Weekly:
                    // ищем ближайший день недели из days строго после from
                    if (days != null && days.Count > 0)
                    {
                        for (int i = 1; i <= 7; i++)
                        {
                            var d = from.AddDays(i);
                            if (days.Contains((int)d.DayOfWeek)) // 0=Sunday == DayOfWeek.Sunday
                                return d;
                        }
                    }
                    return from.AddDays(1);

                case TodoRepeat.Monthly:
                    {
                        int target = Math.Clamp(dayOfMonth <= 0 ? DateTime.Today.Day : dayOfMonth, 1, 31);
                        var candidate = new DateTime(from.Year, from.Month, Math.Min(target, DateTime.DaysInMonth(from.Year, from.Month)), from.Hour, from.Minute, 0);
                        if (candidate > from) return candidate;
                        for (int m = 1; m <= 12; m++)
                        {
                            var nxt = from.AddMonths(m);
                            candidate = new DateTime(nxt.Year, nxt.Month, Math.Min(target, DateTime.DaysInMonth(nxt.Year, nxt.Month)), from.Hour, from.Minute, 0);
                            if (candidate > from) return candidate;
                        }
                        return from.AddMonths(1);
                    }

                default:
                    return from.AddDays(1);
            }
        }

        // Короткая подпись режима для колонки "Срок"
        public static string Mark(TodoRepeat repeat) => repeat switch
        {
            TodoRepeat.Daily => "ежедн.",
            TodoRepeat.Weekly => "еженед.",
            TodoRepeat.Monthly => "ежемес.",
            _ => ""
        };
    }
}