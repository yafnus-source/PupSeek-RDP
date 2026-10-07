using System.Collections.Generic;
using System.IO;
using Renci.SshNet;

namespace ShadowRDP.Services
{
    // v3.9.4 — сборка SSH ConnectionInfo: ключ (publickey) либо пароль.
    // Логика повторяет FtpUploadService.CreateSftpClient, но общая для
    // терминала и SFTP. Password используется как фраза ключа, если
    // ключ настроен и файл существует; иначе — как пароль (password auth).
    public static class SshAuth
    {
        public static bool KeyConfigured(string? keyPath) => !string.IsNullOrWhiteSpace(keyPath);

        public static bool KeyUsable(string? keyPath) =>
            !string.IsNullOrWhiteSpace(keyPath) && File.Exists(keyPath);

        public static ConnectionInfo BuildConnectionInfo(
            string host, int port, string username, string password, string? keyPath)
        {
            var methods = new List<AuthenticationMethod>();
            if (KeyUsable(keyPath))
            {
                var pk = new PrivateKeyFile(keyPath!,
                    string.IsNullOrWhiteSpace(password) ? null : password);
                methods.Add(new PrivateKeyAuthenticationMethod(username, new IPrivateKeySource[] { pk }));
            }
            else
            {
                if (KeyConfigured(keyPath))
                    Logger.Warn($"SshAuth: ключ '{keyPath}' не найден — подключение по паролю");
                methods.Add(new PasswordAuthenticationMethod(username, password));
            }
            return new ConnectionInfo(host, port, username, methods.ToArray());
        }
    }
}
