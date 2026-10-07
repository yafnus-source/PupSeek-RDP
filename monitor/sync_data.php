<?php
/**
 * sync_data.php — копирует JSON-файлы данных из %PROGRAMDATA%\PupSeekRDP в data/
 * Запуск: php sync_data.php (или через cron/task scheduler)
 * Также можно вызвать из C# через Process.Start("php", "sync_data.php")
 */
$srcDir = 'C:\ProgramData\PupSeekRDP';
$dstDir = __DIR__ . '/data';

if (!is_dir($dstDir)) mkdir($dstDir, 0755, true);

$files = [
    'dashboard_events.json',
    'zabbix_agent_history.json',
    'addressbook.json',
    'monitored_computers.json',
];

$ok = 0;
$fail = 0;
foreach ($files as $f) {
    $src = $srcDir . '\\' . $f;
    $dst = $dstDir . '/' . $f;
    if (is_file($src)) {
        if (copy($src, $dst)) {
            $ok++;
        } else {
            $fail++;
            error_log("sync_data: failed to copy $f");
        }
    }
}
echo "Synced: $ok ok, $fail failed\n";
