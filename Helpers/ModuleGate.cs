using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace ShadowRDP.Helpers
{
    // v4.0 — единая точка проверки платных модулей. Нет доступа —
    // мягкий апсейл (окно «модуль платный» + переход в лицензию),
    // пункты не прячем: видно, за что платить.
    public static class ModuleGate
    {
        // Ключ палитры → модуль. Нет записи = ядро (всегда доступно).
        public static readonly IReadOnlyDictionary<string, string> PaletteKeyToModule =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["dash-events"] = "events",
                ["dash-monitor"] = "zabbix",
                ["dash-web"] = "webmon",
                ["inv-soft"] = "inventory",
                ["inv-hard"] = "inventory",
                ["todo"] = "todo",
            };

        public static bool CanUse(string module)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(module)) return true;
                return Services.LicenseStore.ActiveModules().Contains(module);
            }
            catch { return false; }
        }

        // true — доступ есть (или триал). false — показали апсейл.
        public static bool Require(IWin32Window owner, string module)
        {
            try
            {
                if (CanUse(module)) return true;
                Forms.LicenseUpsell.Show(owner, module);
                return false;
            }
            catch { return false; }
        }
    }
}
