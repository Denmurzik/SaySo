# Удаляет SaySo: останавливает, снимает автозапуск и задачу Планировщика, удаляет файлы и ярлык.
param(
    [string]$Target = (Join-Path $env:LOCALAPPDATA 'Programs\SaySo'),
    [switch]$RemoveData
)
$ErrorActionPreference = 'Stop'
# Относительный путь считаем от текущего каталога вызова, а не от корня репозитория.
$Target = [IO.Path]::GetFullPath($Target)
Set-Location $PSScriptRoot

& (Join-Path $PSScriptRoot 'tools/send-ipc.ps1') -Message Quit *> $null
Start-Sleep -Milliseconds 800
Get-Process SaySo -ErrorAction SilentlyContinue | Stop-Process -Force

# Если задачи нет, schtasks пишет в stderr; при ErrorActionPreference=Stop Windows PowerShell 5.1 счёл бы это ошибкой.
$ErrorActionPreference = 'Continue'
& schtasks.exe /Query /TN SaySo *> $null
$taskExists = $LASTEXITCODE -eq 0
$ErrorActionPreference = 'Stop'
if ($taskExists) {
    # Задача создана с наивысшими правами — удалить её можно только с правами администратора.
    Start-Process schtasks.exe -ArgumentList '/Delete', '/TN', 'SaySo', '/F' -Verb RunAs -Wait
}

Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'SaySo' -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) 'SaySo.lnk') -ErrorAction SilentlyContinue
if (Test-Path $Target) { Remove-Item $Target -Recurse -Force }
if ($RemoveData) { Remove-Item (Join-Path $env:LOCALAPPDATA 'SaySo') -Recurse -Force -ErrorAction SilentlyContinue }
Write-Host ('SaySo удалён' + $(if ($RemoveData) { ' вместе с настройками, ключами и историей' } else { ' (настройки и история сохранены)' }))
