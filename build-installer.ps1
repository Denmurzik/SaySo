# Собирает установщик artifacts/installer/SaySo-Setup-X.Y.Z.exe (Inno Setup 6) с файлом .sha256 и ZIP портативной сборки
# artifacts/release/SaySo-X.Y.Z-win-x64.zip. Версия X.Y.Z берётся из Directory.Build.props.
# Запуск: powershell -ExecutionPolicy Bypass -File .\build-installer.ps1
#   -SkipBuild         не запускать build.ps1, взять готовую публикацию из artifacts/publish
#   -PortableIscc      не искать установленный Inno Setup, использовать портативный ISCC из NuGet (так собирает CI)
#   -InnoVersion X.Y.Z другая версия пакета Tools.InnoSetup ('latest' — последняя 6.x); у такой версии хэш пакета не закреплён
# В систему ничего не устанавливается: портативный ISCC скачивается в artifacts/tools. По умолчанию это закреплённая версия
# пакета; после скачивания проверяется её SHA-256.
[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [switch]$PortableIscc,
    [string]$InnoVersion
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # иначе Invoke-WebRequest в Windows PowerShell 5.1 качает в десятки раз медленнее
Set-Location $PSScriptRoot

# Закреплённый пакет Tools.InnoSetup. Его SHA-256 сверен с SHA-512 из каталога NuGet (packageHash записи 6.7.3).
# Менять версию — только вместе с хэшем: curl -L -o p.zip https://www.nuget.org/api/v2/package/Tools.InnoSetup/<версия>; sha256sum p.zip
$pinnedInnoVersion = '6.7.3'
$pinnedInnoSha256 = 'f780898e402ff80612cc8d9fcb8c6e02932bd1cb4c900ffdaa31f9341cfb49f4'
$minInnoVersion = [version]'6.3.0'   # ArchitecturesAllowed=x64compatible

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
# Версия установленного компилятора: из VersionInfo файла, а если её там нет (у ISCC из пакета NuGet 0.0.0.0) — из записи об установке в реестре.
# Кандидат без известной версии не годится: x64compatible требует Inno Setup не ниже 6.3.
function Get-IsccVersion([string]$Path, [string]$RegistryVersion) {
    $info = (Get-Item -LiteralPath $Path).VersionInfo
    foreach ($text in $info.ProductVersion, $info.FileVersion, $RegistryVersion) {
        $parsed = $null
        if ($text -and [version]::TryParse(($text -replace '[^\d.].*$', ''), [ref]$parsed) -and $parsed -gt [version]'0.0.0.0') { return $parsed }
    }
    return $null
}

function Find-InstalledIscc {
    $found = [System.Collections.Generic.List[object]]::new()
    $uninstall = 'Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1'
    foreach ($key in "HKLM:\SOFTWARE\WOW6432Node\$uninstall", "HKLM:\SOFTWARE\$uninstall", "HKCU:\SOFTWARE\$uninstall") {
        $item = Get-ItemProperty -Path $key -ErrorAction SilentlyContinue
        if ($item -and $item.InstallLocation) {
            $found.Add([pscustomobject]@{ Path = Join-Path $item.InstallLocation 'ISCC.exe'; RegistryVersion = $item.DisplayVersion })
        }
    }
    foreach ($dir in ${env:ProgramFiles(x86)}, $env:ProgramFiles, (Join-Path $env:LOCALAPPDATA 'Programs')) {
        if ($dir) { $found.Add([pscustomobject]@{ Path = Join-Path $dir 'Inno Setup 6\ISCC.exe'; RegistryVersion = $null }) }
    }
    $onPath = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($onPath) { $found.Add([pscustomobject]@{ Path = $onPath.Source; RegistryVersion = $null }) }

    foreach ($candidate in $found) {
        if (-not (Test-Path -LiteralPath $candidate.Path)) { continue }
        $v = Get-IsccVersion $candidate.Path $candidate.RegistryVersion
        if (-not $v) {
            Write-Warning "У $($candidate.Path) не удалось определить версию — пропускаю."
            continue
        }
        if ($v -lt $minInnoVersion) {
            Write-Warning "Найден Inno Setup $v ($($candidate.Path)), нужен не ниже $minInnoVersion — пропускаю."
            continue
        }
        return $candidate.Path
    }
    return $null
}

function Get-PortableIscc([string]$InnoVer, [string]$ExpectedSha256) {
    $toolsDir = Join-Path $PSScriptRoot 'artifacts\tools'
    $dest = Join-Path $toolsDir "innosetup-$InnoVer"
    $isccPath = Join-Path $dest 'tools\ISCC.exe'
    $marker = Join-Path $dest 'package.sha256'   # SHA-256 пакета, из которого распакован каталог

    if (Test-Path -LiteralPath $isccPath) {
        # Закреплённую версию из кэша берём, только если каталог распакован из пакета с нужным хэшем.
        if (-not $ExpectedSha256 -or ((Test-Path -LiteralPath $marker) -and ([IO.File]::ReadAllText($marker).Trim() -eq $ExpectedSha256))) {
            return $isccPath
        }
        Write-Warning "Кэш $dest не совпадает с закреплённым хэшем пакета — скачиваю заново."
    }

    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $staging = "$dest.tmp"
    New-Item -ItemType Directory -Force -Path $toolsDir | Out-Null
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    $package = Join-Path $toolsDir "Tools.InnoSetup.$InnoVer.zip"   # ZipFile принимает .nupkg только под именем .zip
    Write-Host "Скачиваю портативный Inno Setup $InnoVer (NuGet Tools.InnoSetup)…"
    Invoke-WebRequest -Uri "https://www.nuget.org/api/v2/package/Tools.InnoSetup/$InnoVer" -OutFile $package -UseBasicParsing

    $actual = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($ExpectedSha256) {
        if ($actual -ne $ExpectedSha256) {
            Remove-Item -LiteralPath $package -Force
            throw "SHA-256 пакета Tools.InnoSetup $InnoVer не совпал: ожидался $ExpectedSha256, получен $actual. Скачанное удалено."
        }
        Write-Host "SHA-256 пакета совпал с закреплённым."
    }
    else {
        Write-Warning "Хэш пакета Tools.InnoSetup $InnoVer не закреплён (SHA-256 $actual) — версия выбрана явно через -InnoVersion."
    }

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
    [IO.File]::WriteAllText((Join-Path $staging 'package.sha256'), $actual + "`n")
    if (Test-Path -LiteralPath $dest) { Remove-Item -LiteralPath $dest -Recurse -Force }
    Move-Item -LiteralPath $staging -Destination $dest
    return $isccPath
}

# Какая версия пакета нужна: по умолчанию закреплённая (с хэшем); 'latest' — последняя 6.x; иначе — явно заданная (без хэша).
if ($InnoVersion) { $PortableIscc = $true }
$innoVer = $pinnedInnoVersion
$innoSha256 = $pinnedInnoSha256
if ($InnoVersion -eq 'latest') {
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        $index = Invoke-RestMethod -Uri 'https://api.nuget.org/v3-flatcontainer/tools.innosetup/index.json'
        $latest = $index.versions |
            Where-Object { $_ -match '^6\.\d+\.\d+(\.\d+)?$' } |
            Sort-Object { [version]$_ } | Select-Object -Last 1
        if ($latest) { $innoVer = $latest }
    }
    catch {
        Write-Warning "Не удалось получить список версий Tools.InnoSetup: $($_.Exception.Message) — беру закреплённую $pinnedInnoVersion."
    }
}
elseif ($InnoVersion) {
    if ($InnoVersion -notmatch '^6\.\d+\.\d+(\.\d+)?$') { throw "-InnoVersion '$InnoVersion': нужна версия 6.x (X.Y.Z) или 'latest'." }
    $innoVer = $InnoVersion
}
if ($innoVer -ne $pinnedInnoVersion) { $innoSha256 = $null }

$iscc = $null
if (-not $PortableIscc) { $iscc = Find-InstalledIscc }
if (-not $iscc) { $iscc = Get-PortableIscc $innoVer $innoSha256 }
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
