# Публикация NativeAOT в artifacts/publish. Требует ≥ 4 ГБ свободной ОЗУ (линковка ILC тяжёлая).
param([string]$Output = 'artifacts/publish')
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$freeGb = (Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory / 1MB
if ($freeGb -lt 4) { throw "Свободно $([math]::Round($freeGb, 1)) ГБ ОЗУ — для AOT-публикации нужно не меньше 4 ГБ." }

# ILCompiler ищет MSVC через vswhere; с одними Build Tools его нет в PATH.
$env:PATH = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer;' + $env:PATH

if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }
dotnet publish src/SaySo/SaySo.csproj -c Release -r win-x64 -o $Output
if ($LASTEXITCODE -ne 0) { throw "dotnet publish завершился с кодом $LASTEXITCODE" }

Get-ChildItem $Output -Filter *.pdb -Recurse | Remove-Item
$sizeMb = (Get-ChildItem $Output -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
$exeMb = (Get-Item (Join-Path $Output 'SaySo.exe')).Length / 1MB
Write-Host ("Готово: {0} — всего {1:N1} МБ, SaySo.exe {2:N1} МБ" -f $Output, $sizeMb, $exeMb)
