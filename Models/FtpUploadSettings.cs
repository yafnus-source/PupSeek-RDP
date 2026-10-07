using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ShadowRDP.Models
{
    public enum FtpProtocol { Ftp = 0, FtpsExplicit = 1, Sftp = 2 }
    public enum FtpScheduleMode { Interval = 0, Daily = 1, Weekly = 2 }
    public enum SftpAuthMode { Password = 0, PrivateKey = 1 }

    public class FtpUploadSettings
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = false;

        [JsonPropertyName("host")]
        public string Host { get; set; } = "";

        [JsonPropertyName("port")]
        public int Port { get; set; } = 21;

        [JsonPropertyName("remoteDir")]
        public string RemoteDir { get; set; } = "/upload";

        [JsonPropertyName("protocol")]
        public FtpProtocol Protocol { get; set; } = FtpProtocol.Ftp;

        [JsonPropertyName("dataConnection")]
        public string DataConnection { get; set; } = "Passive"; // Passive/Active for FTP/FTPS

        [JsonPropertyName("uploadEvents")]
        public bool UploadEvents { get; set; } = true;

        [JsonPropertyName("uploadZabbix")]
        public bool UploadZabbix { get; set; } = true;

        [JsonPropertyName("scheduleMode")]
        public FtpScheduleMode ScheduleMode { get; set; } = FtpScheduleMode.Interval;

        [JsonPropertyName("intervalMinutes")]
        public int IntervalMinutes { get; set; } = 30;

        [JsonPropertyName("dailyTime")]
        public string DailyTime { get; set; } = "02:00"; // HH:mm

        [JsonPropertyName("weeklyDays")]
        public List<DayOfWeek> WeeklyDays { get; set; } = new List<DayOfWeek> { DayOfWeek.Monday };

        [JsonPropertyName("runOnStartup")]
        public bool RunOnStartup { get; set; } = false;

        [JsonPropertyName("keepassEntry")]
        public string KeepassEntry { get; set; } = ""; // Title in PupSeekRDP/FTP

        // SFTP PK
        [JsonPropertyName("sftpAuthMode")]
        public SftpAuthMode SftpAuthMode { get; set; } = SftpAuthMode.Password;

        [JsonPropertyName("privateKeyPath")]
        public string PrivateKeyPath { get; set; } = "";

        // last run tracking (not persisted as setting? but keep)
        [JsonPropertyName("lastRun")]
        public DateTime? LastRun { get; set; }
    }
}
