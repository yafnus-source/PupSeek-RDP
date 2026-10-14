# GUI-генератор ключей PupSeekRDP v4. НЕ поставляется клиентам.
# Запуск: powershell -NoProfile -ExecutionPolicy Bypass -File KeyGen-GUI.ps1
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$Secret = 'V9x4Q7mZ2pL8sT5wK3nR6yU0aB1cD4eF7gH2jJ5kK8'
$Mods = @(
    @('events','События'), @('zabbix','Монитор'), @('webmon','Web-мониторинг'),
    @('inventory','HARD + SOFT'), @('todo','TODO и календарь'),
    @('ftp-upload','FTP-выгрузка'), @('ai','ИИ-расшифровка'), @('domain','Доменные')
)

function B64Url([byte[]]$b) {
    [Convert]::ToBase64String($b).TrimEnd('=').Replace('+','-').Replace('/','_')
}

function Mint-Key([string[]]$ids, [long]$exp, [bool]$trial) {
    $sl = @($ids | Sort-Object -Unique)
    $pj = [ordered]@{ m = $sl; exp = $exp; trial = $trial } | ConvertTo-Json -Compress
    $pb = [Text.Encoding]::UTF8.GetBytes($pj)
    $h = New-Object Security.Cryptography.HMACSHA256
    $h.Key = [Text.Encoding]::UTF8.GetBytes($Secret)
    $sg = $h.ComputeHash($pb)
    $h.Dispose()
    'PSR1-' + (B64Url $pb) + '.' + (B64Url $sg)
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
    $exp = 0
    $expTxt = 'бессрочно'
    if ($trial) {
        $exp = [DateTimeOffset]::UtcNow.AddDays(10).ToUnixTimeSeconds()
        $expTxt = [DateTimeOffset]::FromUnixTimeSeconds($exp).ToLocalTime().ToString('dd.MM.yyyy') + ' (пробный)'
    } elseif ($rbDays.Checked) {
        $exp = [DateTimeOffset]::UtcNow.AddDays([int]$numDays.Value).ToUnixTimeSeconds()
        $expTxt = [DateTimeOffset]::FromUnixTimeSeconds($exp).ToLocalTime().ToString('dd.MM.yyyy')
    }
    $script:lastKey = Mint-Key $ids $exp $trial
    $out.Text = 'Модули: ' + ($ids -join ', ') + "`r`n" + 'Срок: ' + $expTxt + "`r`n`r`n" + $script:lastKey
})

[void]$f.ShowDialog()
