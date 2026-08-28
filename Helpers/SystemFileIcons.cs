using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ShadowRDP.Helpers
{
    // v3.2.6 — Pulls small (16×16) system icons for files and folders via
    // SHGetFileInfo. Used by SftpForm to give ListView items real Explorer
    // icons instead of plain text. Icons are cached per-extension so we
    // only call SHGetFileInfo once for ".txt", once for ".pdf", etc.
    public static class SystemFileIcons
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
            ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private const uint SHGFI_ICON = 0x100;
        private const uint SHGFI_SMALLICON = 0x1;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x10;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;

        // Two ImageLists (one per SftpForm instance, but cheap because we
        // only add per-extension). Index lookup cached in a dict.
        private static readonly Dictionary<string, int> _cache = new(StringComparer.OrdinalIgnoreCase);
        private static ImageList? _shared;

        // Returns a single shared ImageList pre-populated as extensions are
        // requested. ListView uses indices into this list.
        public static ImageList GetImageList()
        {
            if (_shared != null) return _shared;
            _shared = new ImageList
            {
                ImageSize = new Size(16, 16),
                ColorDepth = ColorDepth.Depth32Bit
            };
            return _shared;
        }

        // Look up the icon index for a file name (with extension) or for a
        // directory. Adds a new icon to the shared ImageList on first sight
        // of a new extension.
        public static int IndexFor(string name, bool isDir)
        {
            var key = isDir ? "<dir>" : ("file:" + (System.IO.Path.GetExtension(name) ?? "").ToLowerInvariant());
            if (_cache.TryGetValue(key, out int idx)) return idx;

            var list = GetImageList();
            var info = new SHFILEINFO();
            uint attr = isDir ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
            // Pass a fake path with the right extension; USEFILEATTRIBUTES
            // lets SHGetFileInfo work even if the file doesn't exist on disk.
            var fakePath = isDir ? "___dir___" : "___file___" + (System.IO.Path.GetExtension(name) ?? "");
            SHGetFileInfo(fakePath, attr, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(),
                SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES);
            if (info.hIcon != IntPtr.Zero)
            {
                try
                {
                    // v3.6.8 — Bitmap.FromHicon loses the icon's mask: the
                    // transparent pixels come out as opaque black, so every
                    // icon showed a black square on the light row background
                    // in SftpForm. Icon.ToBitmap() renders an eager 32bpp
                    // ARGB copy (mask -> alpha) that survives DestroyIcon.
                    using var tmpIcon = Icon.FromHandle(info.hIcon);
                    using var tmp = tmpIcon.ToBitmap();
                    list.Images.Add(tmp);
                    idx = list.Images.Count - 1;
                }
                catch { idx = -1; }
                DestroyIcon(info.hIcon);
            }
            else
            {
                idx = -1;
            }
            _cache[key] = idx;
            return idx;
        }
    }
}
