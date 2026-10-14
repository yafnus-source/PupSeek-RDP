# Генератор ключей PupSeekRDP v4 — обёртка над KeyGen.exe. НЕ поставляется клиентам.
# Использование:
#   .\make-license.ps1 -Modules zabbix,events -Days 30      # срочный на 30 дней
#   .\make-license.ps1 -Modules zabbix -Days 0               # бессрочный (Days 0)
#   .\make-license.ps1 -Trial                                # пробный (все модули, 10 дней)
#   .\make-license.ps1 -ListModules                          # список id модулей
# Проверка: вставить строку в «Инструменты → Лицензия…» — окно покажет точную ошибку, если что не так.
# Файл в UTF-8 с BOM (иначе PowerShell 5.1 давится на кириллице).
param(
    [string[]]$Modules = @(),
    [int]$Days = 0,
    [switch]$Trial,
    [switch]$ListModules,
    [string]$KeyGen = '',
    [string]$Key = ''
)

$Known = @('events','zabbix','webmon','inventory','todo','ftp-upload','ai','domain')
if ($ListModules) { $Known; return }

function Find-KeyGen([string]$hint) {
    if ($hint -ne '' -and (Test-Path $hint)) { return $hint }
    $cands = @(
        (Join-Path $PSScriptRoot 'KeyGen.exe')
    )
    foreach ($c in $cands) { if (Test-Path $c) { return $c } }
    return $null
}

$kg = Find-KeyGen $KeyGen
if ($kg -eq $null) {
    Write-Error 'Не найден KeyGen.exe рядом со скриптом (укажите -KeyGen).'
    return
}

$kf = $Key
if ($kf -eq '') {
    $kf = Join-Path (Split-Path -Parent $kg) 'license-private.pem'
}
if (-not (Test-Path $kf)) {
    Write-Error "Нет приватного ключа: $kf"
    return
}

$args2 = @('--key', $kf)
if ($Trial) { $args2 += '--trial' }
else {
    $ids = @($Modules | ForEach-Object { $_ -split '[,;\s]+' } | ForEach-Object { "$_".Trim().ToLowerInvariant() } | Where-Object { $_ -ne '' } | Sort-Object -Unique)
    if ($ids.Count -eq 0) { $ids = $Known }
    foreach ($m in $ids) {
        if ($Known -notcontains $m) { Write-Warning "Неизвестный модуль: $m (известные: $($Known -join ', '))" }
    }
    $args2 += @('--modules', ($ids -join ','), '--days', "$Days")
}

& $kg @args2
