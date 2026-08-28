using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace ShadowRDP.Helpers
{
    // Small status circle for the address-book Ping column. Mirror of
    // New-PingStatusBitmap from the .ps1 version.
    //
    //   Alive   = green (#38A169) — host responds to ping
    //   Dead    = red   (#D54949) — host doesn't respond
    //   Unknown = grey  (#B4BAC4) — not yet checked
    //
    // 14x14 circle with a tiny inner highlight to give it some depth.
    // Cached after first use — Refresh All updates ~80 cells at once.
    public static class PingStatusIcon
    {
        public enum State { Unknown, Alive, Dead }

        private static readonly Dictionary<State, Bitmap> _cache = new();

        public static Bitmap Get(State state)
        {
            if (_cache.TryGetValue(state, out var cached)) return cached;
            var bmp = Draw(state);
            _cache[state] = bmp;
            return bmp;
        }

        private static Bitmap Draw(State state)
        {
            const int size = 14;
            var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var color = state switch
            {
                State.Alive   => Color.FromArgb(56, 161, 105),
                State.Dead    => Color.FromArgb(213, 73, 73),
                _             => Color.FromArgb(180, 186, 196)
            };
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 2, 2, size - 4, size - 4);

            // Subtle inner highlight (top-left).
            using var hl = new SolidBrush(Color.FromArgb(90, 255, 255, 255));
            g.FillEllipse(hl, 3, 3, 4, 4);
            return bmp;
        }
    }
}
