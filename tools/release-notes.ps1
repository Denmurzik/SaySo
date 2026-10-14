# Вырезает из CHANGELOG.md секцию версии (заголовок "## [X.Y.Z] - дата") и пишет её в файл — это заметки GitHub Release.
# Запуск: powershell -File tools\release-notes.ps1 -Version 0.1.1 -Out artifacts\release-notes.md
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$Out,
    [string]$Changelog
)
$ErrorActionPreference = 'Stop'
if (-not $Changelog) { $Changelog = Join-Path $PSScriptRoot '..\CHANGELOG.md' }

$lines = [IO.File]::ReadAllLines((Resolve-Path -LiteralPath $Changelog), [System.Text.Encoding]::UTF8)
$heading = '^##\s+\[?v?' + [regex]::Escape($Version) + '\]?(\s|$)'
$start = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match $heading) { $start = $i; break }
}
if ($start -lt 0) { throw "В $Changelog нет секции версии $Version (ожидается заголовок вида '## [$Version] - дата')." }

$end = $lines.Count
for ($j = $start + 1; $j -lt $lines.Count; $j++) {
    if ($lines[$j] -match '^##\s') { $end = $j; break }
}
$body = if ($end - $start -gt 1) { ($lines[($start + 1)..($end - 1)] -join "`n").Trim() } else { '' }
if (-not $body) { throw "Секция версии $Version в $Changelog пуста." }

$outPath = if ([IO.Path]::IsPathRooted($Out)) { $Out } else { Join-Path (Get-Location).Path $Out }
New-Item -ItemType Directory -Force -Path (Split-Path $outPath) | Out-Null
[IO.File]::WriteAllText($outPath, $body + "`n", (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Заметки к $Version записаны в $outPath ($($body.Split("`n").Count) строк)"
