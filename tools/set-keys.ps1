# Сохраняет ключи Gemini (можно несколько) и Groq в %LOCALAPPDATA%\SaySo\secrets.bin,
# зашифровав их DPAPI текущего пользователя. Формат совместим с SaySo:
# JSON {"geminiApiKeys":[...],"groqApiKey":"..."}, дополнительная энтропия "SaySo.v1".
# Запуск в своём терминале: powershell -NoProfile -File tools\set-keys.ps1
# Ввод скрыт; пустой ввод оставляет прежние ключи. Ключи нигде не печатаются.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security

$dir = Join-Path $env:LOCALAPPDATA 'SaySo'
$file = Join-Path $dir 'secrets.bin'
$entropy = [Text.Encoding]::UTF8.GetBytes('SaySo.v1')
$scope = [Security.Cryptography.DataProtectionScope]::CurrentUser

$gemini = @()
$groq = $null
if (Test-Path $file) {
    try {
        $plain = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($file), $entropy, $scope)
        $current = [Text.Encoding]::UTF8.GetString($plain) | ConvertFrom-Json
        if ($current.geminiApiKeys) { $gemini = @($current.geminiApiKeys) }
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

$newGemini = Read-Secret ("Ключи Gemini через запятую или пробел (сейчас сохранено: {0}; Enter — оставить)" -f $gemini.Count)
$newGroq = Read-Secret ("Ключ Groq ({0}; Enter — оставить)" -f $(if ($groq) { 'задан' } else { 'не задан' }))
if ($newGemini) { $gemini = @($newGemini -split '[,;\s]+' | Where-Object { $_ } | Select-Object -Unique) }
if ($newGroq) { $groq = $newGroq }

$data = [ordered]@{ geminiApiKeys = @($gemini); groqApiKey = $groq }
$json = ConvertTo-Json -InputObject $data -Compress
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$cipher = [Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes($json), $entropy, $scope)
[IO.File]::WriteAllBytes($file, $cipher)
Write-Host "Сохранено (зашифровано DPAPI): $file"
Write-Host ("Ключей Gemini: {0}; Groq: {1}" -f $gemini.Count, $(if ($groq) { 'задан' } else { 'не задан' }))
