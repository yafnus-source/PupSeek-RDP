# Генератор ключей PupSeekRDP v4. НЕ поставляется клиентам, только для выдачи.
# Использование:
#   .\make-license.ps1 -Modules zabbix,events -Days 30      # срочный на 30 дней
#   .\make-license.ps1 -Modules zabbix -Days 0               # бессрочный (Days 0)
#   .\make-license.ps1 -Trial                                # пробный (все модули, 10 дней)
#   .\make-license.ps1 -ListModules                          # список id модулей
# Проверка: вставить строку в «Инструменты → Лицензия…» — окно покажет точную ошибку, если что не так.
param(
    [string[]]$Modules = @(),
    [int]$Days = 0,
    [switch]$Trial,
    [switch]$ListModules
)

$Secret = 'V9x4Q7mZ2pL8sT5wK3nR6yU0aB1cD4eF7gH2jJ5kK8'
$Known = @('events','zabbix','webmon','inventory','todo','ftp-upload','ai','domain')

if ($ListModules) { $Known; return }

if ($Trial) { $Modules = $Known; $Days = 10 }

if ($Modules.Count -eq 0) { $Modules = $Known }
# Терпим любой ввод: хоть массивом, хоть одной строкой через запятую.
$Modules = @($Modules | ForEach-Object { $_ -split '[,;\s]+' } | ForEach-Object { "$_".Trim().ToLowerInvariant() } | Where-Object { $_ -ne '' } | Sort-Object -Unique)
foreach ($m in $Modules) {
    if ($Known -notcontains $m) { Write-Warning "Неизвестный модуль: $m (известные: $($Known -join ', '))" }
}

$exp = 0
if ($Days -gt 0) { $exp = [DateTimeOffset]::UtcNow.AddDays($Days).ToUnixTimeSeconds() }

function B64Url([byte[]]$b) {
    [Convert]::ToBase64String($b).TrimEnd('=').Replace('+','-').Replace('/','_')
}

$payloadObj = [ordered]@{ m = $Modules; exp = $exp; trial = [bool]$Trial }
$payloadJson = $payloadObj | ConvertTo-Json -Compress
$pb = [Text.Encoding]::UTF8.GetBytes($payloadJson)
$hm = New-Object Security.Cryptography.HMACSHA256
$hm.Key = [Text.Encoding]::UTF8.GetBytes($Secret)
$sig = $hm.ComputeHash($pb)
$hm.Dispose()

$key = 'PSR1-' + (B64Url $pb) + '.' + (B64Url $sig)

# Самопроверка: подпись сошлась сама с собой (ловит опечатки в коде выше).
$hm2 = New-Object Security.Cryptography.HMACSHA256
$hm2.Key = [Text.Encoding]::UTF8.GetBytes($Secret)
$re = $hm2.ComputeHash($pb)
$hm2.Dispose()
$ok = [Convert]::ToBase64String($sig) -eq [Convert]::ToBase64String($re)

'M Modules : ' + ($Modules -join ', ')
'Exp     : ' + $(if ($exp -gt 0) { [DateTimeOffset]::FromUnixTimeSeconds($exp).ToLocalTime().ToString('dd.MM.yyyy') + " ($Days дн.)" } else { 'бессрочно' })
'Trial   : ' + [bool]$Trial
'SelfChk : ' + $(if ($ok) { 'OK' } else { 'FAIL — не выдавать!' })
''
$key
