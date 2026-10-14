# Удаляет SaySo: останавливает, снимает автозапуск и задачу Планировщика, удаляет файлы и ярлык.
# Запуск: powershell -ExecutionPolicy Bypass -File .\uninstall.ps1   (-WhatIf — показать действия, ничего не меняя)
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$Target,
    [switch]$RemoveData
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'tools/assert-safe-target.ps1')

$Target = if ($Target) { Resolve-UserPath $Target } else { Join-Path $env:LOCALAPPDATA 'Programs\SaySo' }
Assert-SafeTarget -Target $Target -Repo $PSScriptRoot -Source $null
$targetExists = Test-Path -LiteralPath $Target
if ($targetExists -and -not (Test-Path -LiteralPath (Join-Path $Target 'SaySo.exe'))) {
    throw "В '$Target' нет SaySo.exe — это не папка установки SaySo, отказ."
}
if (-not $targetExists) { Write-Host "Папка '$Target' не найдена — снимаю только автозапуск, ярлык и задачу." }

$exe = Join-Path $Target 'SaySo.exe'
$prefix = $Target.TrimEnd('\') + '\'
if ($targetExists -and $PSCmdlet.ShouldProcess($Target, 'Остановить SaySo')) { Stop-TargetProcesses $Target }

# Что не удалось убрать (left) и что не тронуто как чужое (foreign) — для итогового сообщения.
$left = [System.Collections.Generic.List[string]]::new()
$foreign = [System.Collections.Generic.List[string]]::new()

# Задача Планировщика: удаляем, только если она запускает SaySo из этой папки.
# Если задачи нет, schtasks пишет в stderr; при ErrorActionPreference=Stop Windows PowerShell 5.1 счёл бы это ошибкой.
$ErrorActionPreference = 'Continue'
& schtasks.exe /Query /TN SaySo *> $null
$taskExists = $LASTEXITCODE -eq 0
$taskXml = $null
$xmlReadable = $false
if ($taskExists) {
    $taskXml = (& schtasks.exe /Query /TN SaySo /XML 2>$null) -join "`n"
    $xmlReadable = $LASTEXITCODE -eq 0
}
$ErrorActionPreference = 'Stop'
if ($taskExists) {
    if (-not $xmlReadable) {
        Write-Warning 'Задача Планировщика SaySo найдена, но прочитать её описание (schtasks /Query /XML) не удалось — не могу проверить, что она относится к этой установке, поэтому оставлена. Удалите вручную при необходимости: schtasks /Delete /TN SaySo /F (от администратора).'
        $left.Add('задача Планировщика SaySo (принадлежность не проверена)')
    }
    elseif ($taskXml.IndexOf($exe, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        Write-Warning 'Задача Планировщика SaySo относится к другой установке — оставлена.'
        $foreign.Add('задача Планировщика SaySo')
    }
    elseif ($PSCmdlet.ShouldProcess('Задача Планировщика SaySo', 'Удалить')) {
        # Задача создана с наивысшими правами — удалить её можно только с правами администратора.
        try {
            Start-Process schtasks.exe -ArgumentList '/Delete', '/TN', 'SaySo', '/F' -Verb RunAs -Wait
        }
        catch {
            Write-Warning "Запрос UAC отклонён или не удался: $($_.Exception.Message)"
        }

        $ErrorActionPreference = 'Continue'
        & schtasks.exe /Query /TN SaySo *> $null
        $stillThere = $LASTEXITCODE -eq 0
        $ErrorActionPreference = 'Stop'
        if ($stillThere) {
            Write-Warning 'Задача Планировщика SaySo не удалена. Удалите её вручную: schtasks /Delete /TN SaySo /F (от администратора).'
            $left.Add('задача Планировщика SaySo')
        }
    }
}

# Автозапуск: значение Run снимаем, только если оно указывает в эту папку.
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runValue = (Get-ItemProperty -Path $runKey -Name SaySo -ErrorAction SilentlyContinue).SaySo
if ($runValue) {
    if (-not $runValue.TrimStart('"').StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        $foreign.Add('автозапуск (HKCU Run)')
    }
    elseif ($PSCmdlet.ShouldProcess('Автозапуск (HKCU Run)', 'Удалить значение SaySo')) {
        Remove-ItemProperty -Path $runKey -Name SaySo -ErrorAction SilentlyContinue
        if ($null -ne (Get-ItemProperty -Path $runKey -Name SaySo -ErrorAction SilentlyContinue).SaySo) { $left.Add('автозапуск (HKCU Run)') }
    }
}

# Ярлык «Пуск»: удаляем, только если он ведёт в эту папку.
$lnk = Join-Path ([Environment]::GetFolderPath('Programs')) 'SaySo.lnk'
if (Test-Path -LiteralPath $lnk) {
    $linkTarget = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk).TargetPath
    if (-not ($linkTarget -and $linkTarget.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase))) {
        $foreign.Add('ярлык в меню «Пуск»')
    }
    elseif ($PSCmdlet.ShouldProcess($lnk, 'Удалить ярлык')) {
        Remove-Item -LiteralPath $lnk -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $lnk) { $left.Add('ярлык в меню «Пуск»') }
    }
}

if ($targetExists -and $PSCmdlet.ShouldProcess($Target, 'Удалить папку')) {
    try {
        Remove-Item -LiteralPath $Target -Recurse -Force
    }
    catch {
        throw "Не удалось удалить '$Target' (возможно, SaySo ещё запущен с повышенными правами — закройте его и повторите): $($_.Exception.Message)"
    }
}

$dataDir = Join-Path $env:LOCALAPPDATA 'SaySo'
if ($RemoveData -and $PSCmdlet.ShouldProcess($dataDir, 'Удалить настройки, ключи и историю')) {
    Remove-Item -LiteralPath $dataDir -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $dataDir) { $left.Add("данные в '$dataDir'") }
}

if (-not $WhatIfPreference) {
    if ($left.Count -eq 0) {
        Write-Host ('SaySo удалён' + $(if ($RemoveData) { ' вместе с настройками, ключами и историей' } else { ' (настройки и история сохранены)' }))
    }
    else {
        Write-Warning ('SaySo удалён не полностью. Осталось: ' + ($left -join '; ') + '.')
    }

    if ($foreign.Count -gt 0) {
        Write-Host ('Не тронуто, так как относится к другой установке: ' + ($foreign -join '; ') + '.')
    }
}
