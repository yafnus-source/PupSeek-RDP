using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Windows.Forms;

namespace ShadowRDP.Helpers
{
    // Global drag-to-reorder for every DataGridView, plus persistence of the
    // resulting column order.
    //
    //   - Wire() enables native AllowUserToOrderColumns (grab a header with
    //     the left button and drag — a drop indicator appears).
    //   - The order is saved in two ways so it can never be lost:
    //       1. live, debounced, on ColumnDisplayIndexChanged (fires while the
    //          user drags a column), and
    //       2. a final flush when the owning form closes (catches a reorder
    //          made inside the debounce window). NOT on grid Dispose: by the
    //          time a grid is disposed FindForm() is null and columns are
    //          cleared, so a dispose-time save wrote nothing (or an empty
    //          array) — that's the v3.7.x bug where order was never kept.
    //   - Restore happens on HandleCreated (first show), when columns exist
    //     and the grid is about to be painted, so no flicker of the default
    //     order. Keys are derived from the owning form's type name; an
    //     explicit key can be passed where the form is embedded elsewhere
    //     (AddressBookForm lives inside RdpManagerForm).
    //   - State lives in %PROGRAMDATA%\PupSeekRDP\column_order.json, a file
    //     separate from column_widths.json on purpose: the width file is
    //     deserialized as a strict Dictionary<string, Dictionary<string,int>>,
    //     so an extra array key there would break the width persistence.
    //
    // One grid per form is assumed (every report dialog has exactly one).
    // The RdpManagerForm Web-info dialog hides its column headers, so there
    // is nothing to drag there — Wire() is simply not called for it.
    public static class GridColumnOrder
    {
        private static readonly ConditionalWeakTable<DataGridView, object> _wired = new();

        public static void Wire(DataGridView grid) => Wire(grid, null);

        public static void Wire(DataGridView grid, string? formKey)
        {
            if (_wired.TryGetValue(grid, out _)) return;
            _wired.Add(grid, new object());

            grid.AllowUserToOrderColumns = true;

            bool restoring = false;
            bool formHooked = false;
            var debounce = new System.Windows.Forms.Timer { Interval = 400 };
            long lastReorderLogMs = 0;

            // Restore once, when the grid first gets a handle (columns already
            // exist; runs after Wire() since every grid is wired in its ctor).
            grid.HandleCreated += (s, e) =>
            {
                var key = ResolveKey(grid, formKey);
                if (key == null || grid.Columns.Count == 0) return;
                restoring = true;
                try { Restore(grid, key); }
                finally { restoring = false; }

                // Final flush when the owning form closes. Guarantees the
                // order survives even if the drag happened just before close.
                var form = grid.FindForm();
                if (form != null && !formHooked)
                {
                    formHooked = true;
                    form.FormClosed += (fs, fe) =>
                    {
                        try { if (!grid.IsDisposed && !grid.Disposing) Save(grid, key); }
                        catch { }
                    };
                }
            };

            // Save (debounced) after the user drags a column. Suppressed while
            // Restore is assigning DisplayIndex.
            grid.ColumnDisplayIndexChanged += (s, e) =>
            {
                if (restoring) return;
                var key = ResolveKey(grid, formKey);
                if (key == null)
                {
                    Logger.Warn("GridColumnOrder: column moved but no owning form found");
                    return;
                }
                var now = Environment.TickCount64;
                if (now - lastReorderLogMs > 1000)
                {
                    Logger.Info($"GridColumnOrder: reorder detected in {key}, queued save");
                    lastReorderLogMs = now;
                }
                debounce.Stop();
                debounce.Start();
            };

            debounce.Tick += (s, e) =>
            {
                debounce.Stop();
                if (grid.IsDisposed || grid.Disposing) return;
                var key = ResolveKey(grid, formKey);
                if (key != null) Save(grid, key);
            };

            grid.Disposed += (s, e) =>
            {
                debounce.Stop();
                debounce.Dispose();
            };
        }

        // Explicit per-form key wins (AddressBookForm is embedded in
        // RdpManagerForm, so FindForm() would report the host form);
        // otherwise derive from the owning form's type name.
        private static string? ResolveKey(DataGridView grid, string? formKey)
        {
            if (!string.IsNullOrEmpty(formKey)) return formKey;
            return grid.FindForm()?.GetType().Name;
        }

        private static string KeyFor(string formKey) => formKey + ".order";

        private static void Restore(DataGridView grid, string formKey)
        {
            try
            {
                var path = AppPaths.ColumnOrderPath;
                if (!File.Exists(path)) return;
                var doc = JsonNode.Parse(File.ReadAllText(path));
                var order = doc?[KeyFor(formKey)]?.AsArray();
                if (order == null || order.Count == 0) return;

                var assigned = new HashSet<string>();
                int index = 0;
                foreach (var node in order)
                {
                    var name = node?.GetValue<string>();
                    if (string.IsNullOrEmpty(name) || !grid.Columns.Contains(name)) continue;
                    if (!assigned.Add(name)) continue;  // guard duplicates
                    grid.Columns[name]!.DisplayIndex = index++;
                }
                // Any column not present in the saved order appends at the end.
                foreach (DataGridViewColumn c in grid.Columns)
                {
                    if (assigned.Contains(c.Name)) continue;
                    c.DisplayIndex = index++;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"GridColumnOrder Restore ({formKey}) failed: {ex.Message}");
            }
        }

        private static void Save(DataGridView grid, string formKey)
        {
            try
            {
                var path = AppPaths.ColumnOrderPath;
                JsonObject root;
                if (File.Exists(path))
                {
                    root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
                }
                else
                {
                    root = new JsonObject();
                }

                var names = new JsonArray();
                foreach (DataGridViewColumn c in grid.Columns.Cast<DataGridViewColumn>().OrderBy(c => c.DisplayIndex))
                    names.Add(c.Name);
                root[KeyFor(formKey)] = names;

                // .NET 8: JsonNode.ToJsonString(options) throws
                // "JsonSerializerOptions instance must specify a TypeInfoResolver
                // setting" unless the options carry one. Without it every save
                // failed silently (v3.7.x: column order was never remembered).
                var opts = new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
                };
                File.WriteAllText(path, root.ToJsonString(opts));
                Logger.Info($"GridColumnOrder: saved {formKey} order [{string.Join(",", names.Select(n => (string?)n))}]");
            }
            catch (Exception ex)
            {
                Logger.Warn($"GridColumnOrder Save ({formKey}) failed: {ex.Message}");
            }
        }
    }
}
