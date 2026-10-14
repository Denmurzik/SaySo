# Устанавливает SaySo из artifacts/publish (сначала ./build.ps1), создаёт ярлык в меню «Пуск» и запускает.
param(
    [string]$Source = 'artifacts/publish',
    [string]$Target = (Join-Path $env:LOCALAPPDATA 'Programs\SaySo'),
    [switch]$NoShortcut,
    [switch]$NoLaunch
)
$ErrorActionPreference = 'Stop'
# Относительные пути считаем от текущего каталога вызова, а не от корня репозитория.
$Source = [IO.Path]::GetFullPath($Source)
$Target = [IO.Path]::GetFullPath($Target)
Set-Location $PSScriptRoot
if (-not (Test-Path (Join-Path $Source 'SaySo.exe'))) { throw "Нет $Source\SaySo.exe — сначала выполните ./build.ps1" }

# Остановить запущенный экземпляр (если он есть), чтобы перезаписать файлы.
& (Join-Path $PSScriptRoot 'tools/send-ipc.ps1') -Message Quit *> $null
Start-Sleep -Milliseconds 800
Get-Process SaySo -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$Target*" } | Stop-Process -Force

New-Item -ItemType Directory -Force -Path $Target | Out-Null
Copy-Item (Join-Path $Source '*') $Target -Recurse -Force
Get-ChildItem $Target -Filter *.pdb -Recurse | Remove-Item

if (-not $NoShortcut) {
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Programs')) 'SaySo.lnk'))
    $link.TargetPath = Join-Path $Target 'SaySo.exe'
    $link.WorkingDirectory = $Target
    $link.Description = 'SaySo — голосовая диктовка'
    $link.Save()
}

if (-not $NoLaunch) { Start-Process (Join-Path $Target 'SaySo.exe') }
Write-Host "SaySo установлен в $Target"
