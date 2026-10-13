# Сохраняет ключи Gemini и Groq в %LOCALAPPDATA%\SaySo\secrets.bin, зашифровав их DPAPI текущего пользователя.
# Формат совместим с SaySo (JSON {"geminiApiKey","groqApiKey"}, дополнительная энтропия "SaySo.v1").
# Запуск в своём терминале: powershell -NoProfile -File tools\set-keys.ps1
# Ввод скрыт; пустой ввод оставляет прежний ключ. Ключи нигде не печатаются.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security

$dir = Join-Path $env:LOCALAPPDATA 'SaySo'
$file = Join-Path $dir 'secrets.bin'
$entropy = [Text.Encoding]::UTF8.GetBytes('SaySo.v1')
$scope = [Security.Cryptography.DataProtectionScope]::CurrentUser

$gemini = $null
$groq = $null
if (Test-Path $file) {
    try {
        $plain = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($file), $entropy, $scope)
        $current = [Text.Encoding]::UTF8.GetString($plain) | ConvertFrom-Json
        $gemini = $current.geminiApiKey
        $groq = $current.groqApiKey
    }
    catch {
        Write-Host 'Прежний файл ключей не читается — он будет перезаписан.'
    }
}

function Read-Secret([string]$Prompt) {
    $secure = Read-Host -Prompt $Prompt -AsSecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr).Trim() }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

$state = { param($v) if ([string]::IsNullOrWhiteSpace($v)) { 'не задан' } else { 'задан' } }
$newGemini = Read-Secret ("Ключ Gemini ({0}; Enter — оставить)" -f (& $state $gemini))
$newGroq = Read-Secret ("Ключ Groq ({0}; Enter — оставить)" -f (& $state $groq))
if ($newGemini) { $gemini = $newGemini }
if ($newGroq) { $groq = $newGroq }

$json = [ordered]@{ geminiApiKey = $gemini; groqApiKey = $groq } | ConvertTo-Json -Compress
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$cipher = [Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes($json), $entropy, $scope)
[IO.File]::WriteAllBytes($file, $cipher)
Write-Host "Сохранено (зашифровано DPAPI): $file"
Write-Host ("Gemini: {0}, Groq: {1}" -f (& $state $gemini), (& $state $groq))
