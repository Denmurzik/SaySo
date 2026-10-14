# Собирает установщик artifacts/installer/SaySo-Setup-X.Y.Z.exe (Inno Setup 6) с файлом .sha256 и ZIP портативной сборки
# artifacts/release/SaySo-X.Y.Z-win-x64.zip. Версия X.Y.Z берётся из Directory.Build.props.
# Запуск: powershell -ExecutionPolicy Bypass -File .\build-installer.ps1
#   -SkipBuild        не запускать build.ps1, взять готовую публикацию из artifacts/publish
#   -PortableIscc     не искать установленный Inno Setup, использовать портативный ISCC из NuGet
#   -InnoVersion 6.x  версия пакета Tools.InnoSetup (по умолчанию — последняя 6.x)
# В систему ничего не устанавливается: если Inno Setup 6 не найден, ISCC скачивается в artifacts/tools.
[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [switch]$PortableIscc,
    [string]$InnoVersion
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # иначе Invoke-WebRequest в Windows PowerShell 5.1 качает в десятки раз медленнее
Set-Location $PSScriptRoot

# Последняя 6.x, если индекс NuGet недоступен. Нужна версия не ниже 6.3 (ArchitecturesAllowed=x64compatible).
$fallbackInnoVersion = '6.7.3'
$minInnoVersion = [version]'6.3.0'

# ---- Версия ----
$versionNode = Select-Xml -Path (Join-Path $PSScriptRoot 'Directory.Build.props') -XPath '//Version' | Select-Object -First 1
if (-not $versionNode) { throw 'В Directory.Build.props нет <Version>.' }
$version = $versionNode.Node.InnerText.Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Версия '$version' в Directory.Build.props — не X.Y.Z." }
Write-Host "Версия: $version"

# ---- Публикация ----
$publish = Join-Path $PSScriptRoot 'artifacts\publish'
if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot 'build.ps1')
    if (-not $?) { throw 'build.ps1 завершился с ошибкой.' }
}
$exe = Join-Path $publish 'SaySo.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "Нет $exe — запустите без -SkipBuild." }
$exeVersion = (Get-Item -LiteralPath $exe).VersionInfo.FileVersion
if ($exeVersion -notlike "$version.*" -and $exeVersion -ne $version) {
    throw "В $exe версия $exeVersion, а в Directory.Build.props $version — пересоберите публикацию (без -SkipBuild)."
}

# ---- ISCC ----
function Find-InstalledIscc {
    $found = [System.Collections.Generic.List[object]]::new()
    $uninstall = 'Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1'
    foreach ($key in "HKLM:\SOFTWARE\WOW6432Node\$uninstall", "HKLM:\SOFTWARE\$uninstall", "HKCU:\SOFTWARE\$uninstall") {
        $item = Get-ItemProperty -Path $key -ErrorAction SilentlyContinue
        if ($item -and $item.InstallLocation) {
            $found.Add([pscustomobject]@{ Path = Join-Path $item.InstallLocation 'ISCC.exe'; Version = $item.DisplayVersion })
        }
    }
    foreach ($dir in ${env:ProgramFiles(x86)}, $env:ProgramFiles, (Join-Path $env:LOCALAPPDATA 'Programs')) {
        if ($dir) { $found.Add([pscustomobject]@{ Path = Join-Path $dir 'Inno Setup 6\ISCC.exe'; Version = $null }) }
    }
    $onPath = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($onPath) { $found.Add([pscustomobject]@{ Path = $onPath.Source; Version = $null }) }

    foreach ($candidate in $found) {
        if (-not (Test-Path -LiteralPath $candidate.Path)) { continue }
        $v = $null
        if ($candidate.Version -and [version]::TryParse($candidate.Version, [ref]$v) -and $v -lt $minInnoVersion) {
            Write-Warning "Найден Inno Setup $v ($($candidate.Path)), нужен не ниже $minInnoVersion — пропускаю."
            continue
        }
        return $candidate.Path
    }
    return $null
}

function Get-PortableIscc {
    $toolsDir = Join-Path $PSScriptRoot 'artifacts\tools'
    $innoVer = $InnoVersion
    $cached = Get-ChildItem -LiteralPath $toolsDir -Directory -Filter 'innosetup-*' -ErrorAction SilentlyContinue |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'tools\ISCC.exe') }
    if ($innoVer) {
        $cached = $cached | Where-Object { $_.Name -eq "innosetup-$innoVer" }
    }
    $best = $cached | Sort-Object { [version]($_.Name -replace '^innosetup-', '') } | Select-Object -Last 1
    if ($best) { return Join-Path $best.FullName 'tools\ISCC.exe' }

    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    if (-not $innoVer) {
        try {
            $index = Invoke-RestMethod -Uri 'https://api.nuget.org/v3-flatcontainer/tools.innosetup/index.json'
            $innoVer = $index.versions |
                Where-Object { $_ -match '^6\.\d+\.\d+(\.\d+)?$' } |
                Sort-Object { [version]$_ } | Select-Object -Last 1
        }
        catch {
            Write-Warning "Не удалось получить список версий Tools.InnoSetup: $($_.Exception.Message)"
        }
        if (-not $innoVer) { $innoVer = $fallbackInnoVersion }
    }

    $dest = Join-Path $toolsDir "innosetup-$innoVer"
    $staging = "$dest.tmp"
    New-Item -ItemType Directory -Force -Path $toolsDir | Out-Null
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    $package = Join-Path $toolsDir "Tools.InnoSetup.$innoVer.zip"   # Expand-Archive/ZipFile принимают .nupkg только под именем .zip
    Write-Host "Скачиваю портативный Inno Setup $innoVer (NuGet Tools.InnoSetup)…"
    Invoke-WebRequest -Uri "https://www.nuget.org/api/v2/package/Tools.InnoSetup/$innoVer" -OutFile $package -UseBasicParsing

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($package)
    try {
        $root = [IO.Path]::GetFullPath($staging).TrimEnd('\') + '\'
        foreach ($entry in $archive.Entries) {
            if ($entry.FullName -notlike 'tools/*' -or -not $entry.Name) { continue }
            $target = [IO.Path]::GetFullPath((Join-Path $staging $entry.FullName.Replace('/', '\')))
            if (-not $target.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw "Недопустимый путь в пакете: $($entry.FullName)" }
            New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
        }
    }
    finally {
        $archive.Dispose()
    }
    Remove-Item -LiteralPath $package -Force
    if (-not (Test-Path -LiteralPath (Join-Path $staging 'tools\ISCC.exe'))) { throw 'В пакете Tools.InnoSetup нет tools/ISCC.exe.' }
    if (Test-Path -LiteralPath $dest) { Remove-Item -LiteralPath $dest -Recurse -Force }
    Move-Item -LiteralPath $staging -Destination $dest
    return Join-Path $dest 'tools\ISCC.exe'
}

$iscc = $null
if (-not $PortableIscc) { $iscc = Find-InstalledIscc }
if (-not $iscc) { $iscc = Get-PortableIscc }
Write-Host "ISCC: $iscc"

# ---- Установщик ----
$installerDir = Join-Path $PSScriptRoot 'artifacts\installer'
New-Item -ItemType Directory -Force -Path $installerDir | Out-Null
Get-ChildItem -LiteralPath $installerDir -Filter 'SaySo-Setup-*' | Remove-Item -Force

& $iscc "/DAppVersion=$version" "/DSourceDir=$publish" "/O$installerDir" '/Qp' (Join-Path $PSScriptRoot 'installer\SaySo.iss')
if ($LASTEXITCODE -ne 0) {
    throw "ISCC завершился с кодом $LASTEXITCODE. Если ошибка про x64compatible — нужен Inno Setup не ниже $minInnoVersion (-PortableIscc берёт портативный из NuGet)."
}

$setupName = "SaySo-Setup-$version.exe"
$setup = Join-Path $installerDir $setupName
if (-not (Test-Path -LiteralPath $setup)) { throw "ISCC не создал $setup." }

# <sha256 в нижнем регистре>, два пробела, имя файла, перевод строки — UTF-8 без BOM (формат sha256sum; его разбирает обновление в приложении).
$hash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$setup.sha256", "$hash  $setupName`n", (New-Object System.Text.UTF8Encoding($false)))

# ---- ZIP портативной сборки ----
$releaseDir = Join-Path $PSScriptRoot 'artifacts\release'
New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null
$zip = Join-Path $releaseDir "SaySo-$version-win-x64.zip"
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($publish, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)

$setupMb = (Get-Item -LiteralPath $setup).Length / 1MB
$zipMb = (Get-Item -LiteralPath $zip).Length / 1MB
Write-Host ("Готово:`n  {0} ({1:N1} МБ)`n  {0}.sha256 ({2})`n  {3} ({4:N1} МБ)" -f $setup, $setupMb, $hash, $zip, $zipMb)
