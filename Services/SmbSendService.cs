using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ShadowRDP.Services
{
    // v3.9.5 — отправка файла на Windows-хост через админ-шару SMB
    // (пункт «Передать файл…» в меню книги). Текущие доменные права —
    // без пароля; иначе явный логин/пароль через WNetAddConnection2.
    public static class SmbSendService
    {
        // v3.9.5 — дефолт C$/Temp (создаётся при отправке, прав админа
        // хватает; ADMIN$ вёл в C:\Windows\Temp — неудобно искать).
        public const string DefaultRemoteDir = "C$/Temp";

        // "srv-dc03", "\\\\srv-dc03\\" → "srv-dc03".
        public static string CleanHost(string? computer)
        {
            if (string.IsNullOrWhiteSpace(computer)) return "";
            return computer.Trim().Replace("\\", "").Replace("/", "");
        }

        // "ADMIN$", "C$/Temp", "/ADMIN$/Temp/" → "ADMIN$/Temp".
        // Пусто → DefaultRemoteDir.
        public static string NormalizeRemoteDir(string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return DefaultRemoteDir;
            var d = dir.Trim().Replace('\\', '/').Trim('/');
            // "C:" без $ — тоже шара? Нет: только прямые имена шар.
            return d.Length == 0 ? DefaultRemoteDir : d;
        }

        // computer + remoteDir + fileName → \\computer\ADMIN$\Temp\file.
        public static string BuildUnc(string computer, string remoteDir, string fileName)
        {
            var host = CleanHost(computer);
            var d = NormalizeRemoteDir(remoteDir);
            var f = (fileName ?? "").Trim().Replace('/', '\\').Trim('\\');
            return $@"\\{host}\{d.Replace('/', '\\')}\{f}";
        }

        // Корень шары для net use: \\computer\ADMIN$ (первый сегмент).
        public static string BuildShareUnc(string computer, string remoteDir)
        {
            var host = CleanHost(computer);
            var first = NormalizeRemoteDir(remoteDir).Split('/')[0];
            return $@"\\{host}\{first}";
        }

        // Проверка полей без сети (для харнесса и формы).
        // Возвращает текст ошибки или null.
        public static string? Validate(string? localFile, string? computer,
            string? remoteDir, bool needCreds, string? login)
        {
            if (string.IsNullOrWhiteSpace(localFile)) return "Выберите локальный файл.";
            if (string.IsNullOrWhiteSpace(computer)) return "Укажите компьютер.";
            if (CleanHost(computer).Length == 0) return "Некорректное имя компьютера.";
            if (string.IsNullOrWhiteSpace(remoteDir)) return "Укажите путь на удалённом.";
            if (needCreds && string.IsNullOrWhiteSpace(login)) return "Укажите логин.";
            var f = (localFile ?? "").Trim();
            if (f.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return "Некорректный путь к файлу.";
            return null;
        }

        public static bool ShareReachable(string uncDir)
        {
            try { return Directory.Exists(uncDir); }
            catch { return false; }
        }

        public static void ConnectShare(string shareUnc, string login, string password)
        {
            var nr = new NETRESOURCE
            {
                dwScope = 0,
                dwType = 1, // RESOURCETYPE_DISK
                dwDisplayType = 0,
                dwUsage = 0,
                lpLocalName = null,
                lpRemoteName = shareUnc,
                lpComment = null,
                lpProvider = null
            };
            int rc = WNetAddConnection2(ref nr, password ?? "", login ?? "", 4 /*CONNECT_TEMPORARY*/);
            if (rc != 0)
                throw new IOException($"net use: {new System.ComponentModel.Win32Exception(rc).Message} (код {rc}).",
                    rc);
        }

        public static void DisconnectShare(string shareUnc)
        {
            try { WNetCancelConnection2(shareUnc, 0, true); } catch { }
        }

        // Копирование потоками с прогрессом (File.Copy бар не даёт).
        public static async Task CopyWithProgressAsync(string src, string dst,
            IProgress<(long Done, long Total)>? progress, CancellationToken ct)
        {
            const int buf = 1024 * 1024;
            using var fin = new FileStream(src, FileMode.Open, FileAccess.Read,
                FileShare.Read, buf, useAsync: true);
            long total = fin.Length;
            using var fout = new FileStream(dst, FileMode.Create, FileAccess.Write,
                FileShare.None, buf, useAsync: true);
            var tmp = new byte[buf];
            long done = 0;
            int n;
            progress?.Report((0, total));
            while ((n = await fin.ReadAsync(tmp, 0, tmp.Length, ct).ConfigureAwait(false)) > 0)
            {
                await fout.WriteAsync(tmp, 0, n, ct).ConfigureAwait(false);
                done += n;
                progress?.Report((done, total));
            }
        }

        [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
        private static extern int WNetAddConnection2(
            ref NETRESOURCE lpNetResource, string? lpPassword,
            string? lpUsername, uint dwFlags);

        [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
        private static extern int WNetCancelConnection2(
            string lpName, uint dwFlags, bool fForce);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NETRESOURCE
        {
            public uint dwScope;
            public uint dwType;
            public uint dwDisplayType;
            public uint dwUsage;
            public string? lpLocalName;
            public string? lpRemoteName;
            public string? lpComment;
            public string? lpProvider;
        }
    }
}
