# Устанавливает SaySo из artifacts/publish (сначала ./build.ps1), создаёт ярлык в меню «Пуск» и запускает.
# Запуск: powershell -ExecutionPolicy Bypass -File .\install.ps1   (-WhatIf — показать действия, ничего не меняя)
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$Source,
    [string]$Target,
    [switch]$NoShortcut,
    [switch]$NoLaunch
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'tools/assert-safe-target.ps1')

$Source = if ($Source) { Resolve-UserPath $Source } else { Join-Path $PSScriptRoot 'artifacts/publish' }
$Target = if ($Target) { Resolve-UserPath $Target } else { Join-Path $env:LOCALAPPDATA 'Programs\SaySo' }
Assert-SafeTarget -Target $Target -Repo $PSScriptRoot -Source $Source
if (-not (Test-Path -LiteralPath (Join-Path $Source 'SaySo.exe'))) { throw "Нет $Source\SaySo.exe — сначала выполните ./build.ps1" }

# В непустую папку ставим только поверх прежней установки SaySo.
if ((Test-Path -LiteralPath $Target) -and
    (Get-ChildItem -LiteralPath $Target -Force | Select-Object -First 1) -and
    -not (Test-Path -LiteralPath (Join-Path $Target 'SaySo.exe'))) {
    throw "Папка '$Target' не пуста и не содержит SaySo.exe — отказ, чтобы не затронуть чужие файлы."
}

if ($PSCmdlet.ShouldProcess($Target, 'Установить SaySo')) {
    # Остановить запущенный экземпляр из этой папки, чтобы перезаписать файлы.
    Stop-TargetProcesses $Target

    New-Item -ItemType Directory -Force -Path $Target | Out-Null
    # Копируем файл за файлом без .pdb — удалять ничего в папке установки не приходится.
    Get-ChildItem -LiteralPath $Source -Recurse -File | Where-Object { $_.Extension -ne '.pdb' } | ForEach-Object {
        $dest = Join-Path $Target $_.FullName.Substring($Source.TrimEnd('\').Length + 1)
        New-Item -ItemType Directory -Force -Path (Split-Path $dest) | Out-Null
        Copy-Item -LiteralPath $_.FullName -Destination $dest -Force
    }
}

if (-not $NoShortcut -and $PSCmdlet.ShouldProcess('Меню «Пуск»', 'Создать ярлык SaySo')) {
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Programs')) 'SaySo.lnk'))
    $link.TargetPath = Join-Path $Target 'SaySo.exe'
    $link.WorkingDirectory = $Target
    $link.Description = 'SaySo — голосовая диктовка'
    $link.Save()
}

if (-not $NoLaunch -and $PSCmdlet.ShouldProcess((Join-Path $Target 'SaySo.exe'), 'Запустить')) {
    Start-Process (Join-Path $Target 'SaySo.exe')
}

if (-not $WhatIfPreference) { Write-Host "SaySo установлен в $Target" }
