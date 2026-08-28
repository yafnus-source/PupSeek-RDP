using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;

namespace ShadowRDP.Services
{
    // Persists per-form window sizes between launches. Mirror of the .ps1
    // Load-WindowState / Save-WindowState functions. Stored in
    // %PROGRAMDATA%\ShadowRDP\windows.json — same shared location as the
    // other data files (works across user accounts during UAC elevation).
    //
    // Schema:
    //   { "mainForm": { "w": 1200, "h": 800 }, "addressBook": {...}, ... }
    //
    // We only persist width+height — not position (Windows handles placement
    // restoration separately via the OS-level window-placement feature) and
    // not state (maximized/minimized) to keep things simple.
    public static class WindowStateStore
    {
        private static readonly object _lock = new object();
        private static readonly string _path = AppPaths.WindowStatePath;

        private static readonly JsonSerializerOptions _jsonOpts = new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        // Restore the saved size for a form. No-op if no entry exists — the
        // form's hardcoded ClientSize stays as the default.
        //
        // We ALSO hook Form.Shown — Windows can asynchronously apply a
        // saved window placement AFTER Form.Show() runs, overriding the
        // Size we just set. Re-applying in Shown catches that case.
        public static void Restore(Form form, string key)
        {
            if (form == null || string.IsNullOrEmpty(key)) return;

            var entry = TryGetEntry(key);
            if (entry == null)
            {
                Logger.Info($"WindowStateStore.Restore({key}): no saved entry");
                return;
            }
            Logger.Info($"WindowStateStore.Restore({key}): saved W={entry.W} H={entry.H} state={entry.State ?? "norm"}");
            if ((entry.W > 0 && entry.H > 0) || entry.State == "max")
            {
                // Best-effort apply in constructor context — works when the
                // form's handle is created from this Size, but unreliable if
                // WinForms deferred-handle path or async window-placement
                // restoration overrides it.
                try
                {
                    ApplyEntry(form, entry);
                    Logger.Info($"WindowStateStore.Restore({key}): ctor applied Size={form.Size.Width}x{form.Size.Height} state={form.WindowState}");
                }
                catch (Exception ex) { Logger.Warn($"WindowStateStore.Restore({key}): ctor setter failed: {ex.Message}"); }

                // Re-apply on Load — handle exists by then, the setter is
                // reliable. This is the path that actually applies in most
                // scenarios (the ctor-time setter may be undone by the
                // Show()/CreateHandle pipeline).
                form.Load += (s, e) =>
                {
                    try
                    {
                        if (form.IsDisposed) return;
                        var before = form.Size;
                        ApplyEntry(form, entry);
                        Logger.Info($"WindowStateStore.Load({key}): applied (was {before.Width}x{before.Height}, now {form.Size.Width}x{form.Size.Height} state={form.WindowState})");
                    }
                    catch (Exception ex) { Logger.Warn($"WindowStateStore.Load({key}) failed: {ex.Message}"); }
                };

                // Final belt-and-suspenders: re-apply 50ms after Shown. Catches
                // the case where Windows asynchronously applies a cached
                // window placement AFTER Load.
                form.Shown += (s, e) =>
                {
                    var t = new Timer { Interval = 50 };
                    t.Tick += (ts, te) =>
                    {
                        t.Stop();
                        t.Dispose();
                        try
                        {
                            if (form.IsDisposed) return;
                            if (entry.State == "max")
                            {
                                if (form.WindowState != FormWindowState.Maximized)
                                    form.WindowState = FormWindowState.Maximized;
                                return;
                            }
                            var cur = form.Size;
                            if (cur.Width != entry.W || cur.Height != entry.H)
                            {
                                form.Size = ClampToScreen(new System.Drawing.Size(entry.W, entry.H), form);
                                Logger.Info($"WindowStateStore.Shown({key}): re-applied W={entry.W} H={entry.H} (was {cur.Width}x{cur.Height})");
                            }
                        }
                        catch { }
                    };
                    t.Start();
                };
            }
        }

        // Apply a persisted layout to a form. Maximized forms restore their
        // maximized state; normal forms restore their size, clamped to the
        // current screen working area.
        private static void ApplyEntry(Form form, SizeEntry entry)
        {
            if (entry.State == "max")
            {
                form.WindowState = FormWindowState.Maximized;
                return;
            }
            if (entry.W > 0 && entry.H > 0)
                form.Size = ClampToScreen(new System.Drawing.Size(entry.W, entry.H), form);
        }

        private static SizeEntry? TryGetEntry(string key)
        {
            try
            {
                var state = LoadAll();
                if (!state.TryGetValue(key, out var entry)) return null;
                return entry;
            }
            catch (Exception ex)
            {
                Logger.Warn($"WindowStateStore.TryGetEntry failed for '{key}': {ex.Message}");
                return null;
            }
        }

        // Clamp a persisted size to the working area of the form's screen so a
        // stale/oversized entry (or one saved from a larger monitor) never
        // opens a window that overflows the current desktop.
        private static System.Drawing.Size ClampToScreen(System.Drawing.Size s, Form form)
        {
            try
            {
                var area = form.IsHandleCreated
                    ? Screen.FromHandle(form.Handle).WorkingArea
                    : Screen.PrimaryScreen!.WorkingArea;
                var w = Math.Max(400, Math.Min(s.Width, area.Width));
                var h = Math.Max(300, Math.Min(s.Height, area.Height));
                return new System.Drawing.Size(w, h);
            }
            catch
            {
                return s;
            }
        }
        public static void Save(Form form, string key)
        {
            if (form == null || string.IsNullOrEmpty(key)) return;
            try
            {
                // If maximized, persist the maximized state plus the
                // pre-maximize normal size (RestoreBounds) as a fallback.
                int w = form.Width, h = form.Height;
                string? state = null;
                if (form.WindowState == FormWindowState.Maximized)
                {
                    state = "max";
                    var rb = form.RestoreBounds;
                    if (rb.Width > 0 && rb.Height > 0) { w = rb.Width; h = rb.Height; }
                }
                var stateAll = LoadAll();
                stateAll[key] = new SizeEntry { W = w, H = h, State = state };
                SaveAll(stateAll);
                Logger.Info($"WindowStateStore.Save({key}): saved W={w} H={h} state={state ?? "norm"}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"WindowStateStore.Save failed for '{key}': {ex.Message}");
            }
        }

        // Convenience: restore + hook FormClosed to save. Call after the
        // form's Size + MinimumSize are set, before Show/ShowDialog.
        public static void Register(Form form, string key)
        {
            Restore(form, key);
            form.FormClosed += (s, e) => Save(form, key);
        }

        // === Internal ===

        private sealed class SizeEntry
        {
            [JsonPropertyName("w")] public int W { get; set; }
            [JsonPropertyName("h")] public int H { get; set; }
            [JsonPropertyName("state")] public string? State { get; set; }
        }

        private static Dictionary<string, SizeEntry> LoadAll()
        {
            lock (_lock)
            {
                try
                {
                    if (!File.Exists(_path)) return new Dictionary<string, SizeEntry>();
                    var json = File.ReadAllText(_path, Encoding.UTF8);
                    if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, SizeEntry>();
                    return JsonSerializer.Deserialize<Dictionary<string, SizeEntry>>(json)
                           ?? new Dictionary<string, SizeEntry>();
                }
                catch
                {
                    return new Dictionary<string, SizeEntry>();
                }
            }
        }

        private static void SaveAll(Dictionary<string, SizeEntry> state)
        {
            lock (_lock)
            {
                AppPaths.EnsureDataDir();
                var json = JsonSerializer.Serialize(state, _jsonOpts);
                File.WriteAllText(_path, json, new UTF8Encoding(false));
            }
        }
    }
}
