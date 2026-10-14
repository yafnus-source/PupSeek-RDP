# GUI-генератор ключей PupSeekRDP v4. НЕ поставляется клиентам.
# Запуск: powershell -NoProfile -ExecutionPolicy Bypass -File KeyGen-GUI.ps1
# Минтит через KeyGen.exe рядом со скриптом (см. Find-KeyGen).
# Файл в UTF-8 с BOM (иначе PowerShell 5.1 давится на кириллице).
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$Mods = @(
    @('events','События'), @('zabbix','Монитор'), @('webmon','Web-мониторинг'),
    @('inventory','HARD + SOFT'), @('todo','TODO и календарь'),
    @('ftp-upload','FTP-выгрузка'), @('ai','ИИ-расшифровка'), @('domain','Доменные')
)

function Find-KeyGen() {
    $cands = @(
        (Join-Path $PSScriptRoot 'KeyGen.exe')
    )
    foreach ($c in $cands) { if (Test-Path $c) { return $c } }
    return $null
}

$script:kgPath = Find-KeyGen
$script:keyPath = ''
if ($script:kgPath -ne $null) {
    $script:keyPath = Join-Path (Split-Path -Parent $script:kgPath) 'license-private.pem'
}

function Mint-Key([string[]]$ids, [int]$days, [bool]$trial) {
    if ($script:kgPath -eq $null) { throw 'Не найден KeyGen.exe' }
    $a = @('--key', $script:keyPath)
    if ($trial) { $a += '--trial' }
    else { $a += @('--modules', ($ids -join ','), '--days', "$days") }
    $out = & $script:kgPath @a 2>&1
    $lines = @($out | ForEach-Object { "$_".Trim() } | Where-Object { $_ -ne '' })
    if ($lines.Count -eq 0) { throw 'KeyGen ничего не вернул' }
    return @{ Key = $lines[-1]; Log = ($lines -join "`r`n") }
}

$f = New-Object Windows.Forms.Form
$f.Text = 'Генератор ключей PupSeekRDP'
$f.Size = New-Object Drawing.Size(460, 560)
$f.StartPosition = 'CenterScreen'
$f.FormBorderStyle = 'FixedDialog'
$f.MaximizeBox = $false
$f.Font = New-Object Drawing.Font('Segoe UI', 9)

$y = 12
$boxes = @()
foreach ($m in $Mods) {
    $c = New-Object Windows.Forms.CheckBox
    $c.Text = $m[1] + '  (' + $m[0] + ')'
    $c.Tag = $m[0]
    $c.Checked = $true
    $c.Location = New-Object Drawing.Point(14, $y)
    $c.Size = New-Object Drawing.Size(300, 22)
    $f.Controls.Add($c)
    $boxes += $c
    $y += 26
}

function Set-All([bool]$v) { foreach ($c in $boxes) { $c.Checked = $v } }
function Set-Ids([string[]]$ids) { foreach ($c in $boxes) { $c.Checked = $ids -contains $c.Tag } }

$btnMon = New-Object Windows.Forms.Button
$btnMon.Text = 'Мониторинг'
$btnMon.Location = New-Object Drawing.Point(14, $y)
$btnMon.Size = New-Object Drawing.Size(100, 26)
$btnMon.Add_Click({ Set-Ids @('events','zabbix','webmon') })
$btnAcc = New-Object Windows.Forms.Button
$btnAcc.Text = 'Учёт'
$btnAcc.Location = New-Object Drawing.Point(120, $y)
$btnAcc.Size = New-Object Drawing.Size(100, 26)
$btnAcc.Add_Click({ Set-Ids @('inventory','todo') })
$btnPro = New-Object Windows.Forms.Button
$btnPro.Text = 'Про (все)'
$btnPro.Location = New-Object Drawing.Point(226, $y)
$btnPro.Size = New-Object Drawing.Size(100, 26)
$btnPro.Add_Click({ Set-All $true })
$f.Controls.AddRange(@($btnMon, $btnAcc, $btnPro))
$y += 34

$rbPerp = New-Object Windows.Forms.RadioButton
$rbPerp.Text = 'Бессрочно'
$rbPerp.Checked = $true
$rbPerp.Location = New-Object Drawing.Point(14, $y)
$rbPerp.Size = New-Object Drawing.Size(100, 22)
$rbDays = New-Object Windows.Forms.RadioButton
$rbDays.Text = 'Дней:'
$rbDays.Location = New-Object Drawing.Point(120, $y)
$rbDays.Size = New-Object Drawing.Size(60, 22)
$numDays = New-Object Windows.Forms.NumericUpDown
$numDays.Minimum = 1
$numDays.Maximum = 3650
$numDays.Value = 30
$numDays.Location = New-Object Drawing.Point(182, $y)
$numDays.Size = New-Object Drawing.Size(70, 22)
$rbTrial = New-Object Windows.Forms.RadioButton
$rbTrial.Text = 'Пробный (10 дн.)'
$rbTrial.Location = New-Object Drawing.Point(260, $y)
$rbTrial.Size = New-Object Drawing.Size(130, 22)
$f.Controls.AddRange(@($rbPerp, $rbDays, $numDays, $rbTrial))
$y += 30

$btnGo = New-Object Windows.Forms.Button
$btnGo.Text = 'Сгенерировать'
$btnGo.Location = New-Object Drawing.Point(14, $y)
$btnGo.Size = New-Object Drawing.Size(130, 30)
$btnCopy = New-Object Windows.Forms.Button
$btnCopy.Text = 'Копировать ключ'
$btnCopy.Location = New-Object Drawing.Point(150, $y)
$btnCopy.Size = New-Object Drawing.Size(130, 30)
$btnCopy.Add_Click({
    if ($script:lastKey -ne $null -and $script:lastKey -ne '') {
        [Windows.Forms.Clipboard]::SetText($script:lastKey)
        [Windows.Forms.MessageBox]::Show($f, 'Ключ скопирован.', 'Готово')
    }
})
$f.Controls.AddRange(@($btnGo, $btnCopy))
$y += 38

$out = New-Object Windows.Forms.TextBox
$out.Multiline = $true
$out.ReadOnly = $true
$out.ScrollBars = 'Vertical'
$out.Font = New-Object Drawing.Font('Consolas', 9)
$out.Location = New-Object Drawing.Point(14, $y)
$out.Size = New-Object Drawing.Size(418, 150)
$f.Controls.Add($out)

$btnGo.Add_Click({
    $ids = @($boxes | Where-Object { $_.Checked } | ForEach-Object { $_.Tag })
    if ($ids.Count -eq 0) {
        $out.Text = 'Выберите хотя бы один модуль.'
        return
    }
    $trial = $rbTrial.Checked
    if ($trial) { $ids = @($Mods | ForEach-Object { $_[0] }) }
    $days = 0
    if ($trial) { $days = 10 }
    elseif ($rbDays.Checked) { $days = [int]$numDays.Value }
    try {
        $r = Mint-Key $ids $days $trial
        $script:lastKey = $r.Key
        $out.Text = $r.Log + "`r`n`r`n" + $r.Key
    } catch {
        $out.Text = 'Ошибка: ' + $_.Exception.Message
    }
})

[void]$f.ShowDialog()
