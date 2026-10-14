# Запускает SaySo.exe, ждёт и печатает память, число загруженных модулей Microsoft.UI* и заголовок окна.
param(
    [string]$Exe = 'artifacts/publish/SaySo.exe',
    [switch]$Settings,
    [int]$WaitSeconds = 4,
    [switch]$KeepRunning
)
$ErrorActionPreference = 'Stop'
$p = if ($Settings) { Start-Process $Exe -ArgumentList '--settings' -PassThru } else { Start-Process $Exe -PassThru }
Start-Sleep -Seconds $WaitSeconds
$p.Refresh()
if ($p.HasExited) { Write-Host "Процесс завершился с кодом $($p.ExitCode)"; exit 1 }
$xaml = @($p.Modules | Where-Object { $_.ModuleName -like 'Microsoft.UI*' }).Count
Write-Host ('PID={0} WS={1:N1} MB Private={2:N1} MB XamlModules={3} Title=[{4}]' -f $p.Id, ($p.WorkingSet64 / 1MB), ($p.PrivateMemorySize64 / 1MB), $xaml, $p.MainWindowTitle)
if (-not $KeepRunning) { Stop-Process -Id $p.Id }
