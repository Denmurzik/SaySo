# Общие проверки для install.ps1 / uninstall.ps1: не даём стереть или перезаписать чужую папку.
# Подключается через dot-source: . (Join-Path $PSScriptRoot 'tools/assert-safe-target.ps1')

function Test-InsideOrEqual([string]$Path, [string]$Parent) {
    $p = $Path.TrimEnd('\')
    $q = $Parent.TrimEnd('\')
    return $p.Equals($q, [StringComparison]::OrdinalIgnoreCase) -or $p.StartsWith($q + '\', [StringComparison]::OrdinalIgnoreCase)
}

# Превращает пользовательский путь в полный (от каталога PowerShell, а не от рабочего каталога .NET).
function Resolve-UserPath([string]$Path) {
    return [IO.Path]::GetFullPath($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path))
}

# Бросает исключение, если $Target нельзя использовать как папку установки.
function Assert-SafeTarget([string]$Target, [string]$Repo, [string]$Source) {
    $full = [IO.Path]::GetFullPath($Target)
    $t = $full.TrimEnd('\')
    if ($t -eq [IO.Path]::GetPathRoot($full).TrimEnd('\')) { throw "Папка установки '$Target' — корень диска, отказ." }

    # Системные и пользовательские каталоги: сама папка или любой её родитель не годятся.
    $protected = @(
        $env:windir, $env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:ProgramW6432, $env:ProgramData,
        $env:USERPROFILE, $env:LOCALAPPDATA, $env:APPDATA,
        $(if ($env:LOCALAPPDATA) { Join-Path $env:LOCALAPPDATA 'Programs' }),
        [Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('MyDocuments')
    ) | Where-Object { $_ }
    foreach ($dir in $protected) {
        if (Test-InsideOrEqual $dir $t) { throw "Папка установки '$Target' совпадает с системным/пользовательским каталогом '$dir' или содержит его, отказ." }
    }

    if (Test-InsideOrEqual $t $env:windir) { throw "Папка установки '$Target' внутри каталога Windows, отказ." }
    if (Test-InsideOrEqual $Repo $t) { throw "Папка установки '$Target' совпадает с репозиторием или содержит его, отказ." }
    if ($Source -and ((Test-InsideOrEqual $t $Source) -or (Test-InsideOrEqual $Source $t))) {
        throw "Папка установки '$Target' и источник '$Source' вложены друг в друга, отказ."
    }
}

# Процессы SaySo из папки установки; процессы с недоступным путём (с повышенными правами) тоже считаем кандидатами.
function Get-TargetProcesses([string]$Target) {
    $prefix = $Target.TrimEnd('\') + '\'
    @(Get-Process SaySo -ErrorAction SilentlyContinue | Where-Object {
            $path = $_.Path
            (-not $path) -or $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
        })
}

# Просит демон выйти по IPC, ждёт, затем добивает оставшиеся процессы из папки установки.
function Stop-TargetProcesses([string]$Target) {
    $procs = Get-TargetProcesses $Target
    if ($procs.Count -eq 0) { return }
    & (Join-Path $PSScriptRoot 'send-ipc.ps1') -Message Quit *> $null
    $procs | Wait-Process -Timeout 5 -ErrorAction SilentlyContinue
    $left = Get-TargetProcesses $Target
    if ($left.Count -gt 0) {
        $left | Stop-Process -Force -ErrorAction Continue
        $left | Wait-Process -Timeout 5 -ErrorAction SilentlyContinue
    }
}
