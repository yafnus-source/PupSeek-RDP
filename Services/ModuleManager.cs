using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ShadowRDP.Services
{
    // v4.7 — менеджер встраиваемых модулей. Реестр известных модулей
    // (пока только "todo"), состояние вкл/выкл — в modules.json.
    // Формат: {"todo":true}. Отсутствие записи = модуль «не установлен».
    // Отключение НЕ удаляет данные (todo.json остаётся — включение вернёт).
    public static class ModuleManager
    {
        private static readonly object _lock = new object();
        private static Dictionary<string, bool> _state = null!;
        private static bool _loaded;

        // Каталог известных модулей. Новые модули добавляются сюда.
        private static readonly Models.AppModule[] _catalog =
        {
            new TodoModule()
        };

        public static event Action<string, bool>? StateChanged;

        public static IReadOnlyList<Models.AppModule> All => _catalog;

        public static IReadOnlyList<Models.AppModule> GetEnabled()
            => _catalog.Where(m => IsEnabled(m.Id)).ToArray();

        public static Models.AppModule? Get(string id)
            => _catalog.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

        public static bool IsEnabled(string id)
        {
            var m = Get(id);
            if (m == null) return false;
            lock (_lock)
            {
                EnsureLoaded();
                if (_state.TryGetValue(m.Id, out var on)) return on;
                return m.DefaultEnabled;
            }
        }

        public static void SetEnabled(string id, bool enabled)
        {
            var m = Get(id);
            if (m == null) return;
            bool changed;
            lock (_lock)
            {
                EnsureLoaded();
                changed = !_state.TryGetValue(m.Id, out var cur) || cur != enabled;
                _state[m.Id] = enabled;
                Save();
            }
            if (changed)
            {
                try { if (enabled) m.OnEnable(); else m.OnDisable(); } catch { }
                try { StateChanged?.Invoke(m.Id, enabled); } catch { }
            }
        }

        // Старт приложения: разбудить включённые модули (их сервисы).
        public static void StartEnabled()
        {
            EnsureLoaded();
            foreach (var m in _catalog)
            {
                if (!IsEnabled(m.Id)) continue;
                try { m.OnEnable(); } catch { }
            }
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _state = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(AppPaths.ModulesSettingsPath))
                {
                    var txt = File.ReadAllText(AppPaths.ModulesSettingsPath, Encoding.UTF8);
                    var d = JsonSerializer.Deserialize<Dictionary<string, bool>>(txt);
                    if (d != null)
                        foreach (var kv in d) _state[kv.Key] = kv.Value;
                }
            }
            catch { }
            _loaded = true;
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                var txt = JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(AppPaths.ModulesSettingsPath, txt, new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
