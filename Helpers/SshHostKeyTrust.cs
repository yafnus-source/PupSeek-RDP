using System;
using System.Threading.Tasks;
using System.Windows.Forms;
using Renci.SshNet.Common;
using ShadowRDP.Services;

namespace ShadowRDP.Helpers
{
    // v3.9.4 — проверка host key при подключении (SSH и SFTP).
    // Общий обработчик HostKeyReceived:
    //   1) отпечаток есть в known_hosts → доверяем;
    //   2) нет → диалог «Доверять?» (No по умолчанию);
    //   3) отпечаток ИЗМЕНИЛСЯ → красный диалог (MITM), замена ключа
    //      только явным «Да».
    // Диалог показывается синхронно: если вызов с UI-потока (SFTP.Connect
    // на форме) — MessageBox напрямую; с фонового потока SSH-клиента —
    // BeginInvoke на marshal-контроле и блокировка только SSH-потока.
    public static class SshHostKeyTrust
    {
        public static void Handle(HostKeyEventArgs e, string host, int port, Control? marshal)
        {
            try
            {
                var fp = KnownHostsStore.Fingerprint(e.HostKey);
                var keyName = e.HostKeyName ?? "";
                var stored = KnownHostsStore.Find(host, port);

                if (stored != null)
                {
                    if (stored.Sha256 == fp)
                    {
                        e.CanTrust = true;
                        return;
                    }
                    bool replace = Ask(host, port, keyName, fp, stored, marshal);
                    if (replace) KnownHostsStore.Upsert(host, port, keyName, fp);
                    AuditLogger.Write("ssh.hostkey", $"{host}:{port}", replace ? "mismatch-replace" : "mismatch-blocked", fp);
                    e.CanTrust = replace;
                    return;
                }

                bool trust = Ask(host, port, keyName, fp, null, marshal);
                if (trust) KnownHostsStore.Upsert(host, port, keyName, fp);
                AuditLogger.Write("ssh.hostkey", $"{host}:{port}", trust ? "trusted" : "rejected", fp);
                e.CanTrust = trust;
            }
            catch (Exception ex)
            {
                Logger.Warn($"SshHostKeyTrust: {host}:{port}: {ex.Message}");
                e.CanTrust = false;
            }
        }

        private static bool Ask(string host, int port, string keyName, string sha256,
            KnownHostsStore.Entry? stored, Control? marshal)
        {
            var tcs = new TaskCompletionSource<bool>();
            void Show()
            {
                try
                {
                    string msg = stored == null
                        ? $"Хост {host}:{port} неизвестен — ключ ранее не встречался.\n\n" +
                          $"Тип ключа: {keyName}\nОтпечаток: {sha256}\n\n" +
                          "Доверять этому ключу?"
                        : $"ВНИМАНИЕ: ключ хоста {host}:{port} ИЗМЕНИЛСЯ!\n\n" +
                          $"Был:  {stored.Sha256} ({stored.KeyName})\n" +
                          $"Стал: {sha256} ({keyName})\n\n" +
                          "Возможна подмена сервера (MITM).\nПродолжить и заменить ключ?";
                    var res = MessageBox.Show(marshal, msg, "SSH: проверка хост-ключа",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2);
                    tcs.TrySetResult(res == DialogResult.Yes);
                }
                catch
                {
                    tcs.TrySetResult(false);
                }
            }

            try
            {
                if (marshal != null && marshal.InvokeRequired && marshal.IsHandleCreated)
                    marshal.BeginInvoke((Action)Show);
                else
                    Show();
                return tcs.Task.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Logger.Warn($"SshHostKeyTrust.Ask: {ex.Message}");
                return false;
            }
        }
    }
}
