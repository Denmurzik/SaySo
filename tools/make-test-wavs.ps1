# Генерирует тестовые WAV (16 кГц, 16 бит, моно) системным TTS в artifacts/testwavs.
# Использование: powershell -NoProfile -File tools/make-test-wavs.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Speech

$out = Join-Path $PSScriptRoot '..\artifacts\testwavs'
New-Item -ItemType Directory -Force -Path $out | Out-Null

$synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
$voice = $synth.GetInstalledVoices() | Where-Object { $_.VoiceInfo.Culture.Name -eq 'ru-RU' } | Select-Object -First 1
if (-not $voice) { throw 'Не найден русский голос TTS (нужен Microsoft Irina).' }
$synth.SelectVoice($voice.VoiceInfo.Name)
$format = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(16000, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)

$phrases = [ordered]@{
    'correction' = 'Ну, э-э, давай встретимся в пятницу, нет, в субботу, в три часа дня.'
    'fillers'    = 'Короче, типа, надо купить молоко, хлеб и, это самое, десяток яиц.'
    'mixed'      = 'Задеплой сервис в кубернетис через гитхаб экшенс и проверь логи в графане.'
    'long'       = 'Сегодня мы обсудим три вопроса. Первый — бюджет на следующий квартал. Второй, ну, сроки запуска нового продукта. И третий — найм двух разработчиков, а нет, трёх разработчиков в команду.'
}
foreach ($name in $phrases.Keys) {
    $path = Join-Path $out "$name.wav"
    $synth.SetOutputToWaveFile($path, $format)
    $synth.Speak($phrases[$name])
    $synth.SetOutputToNull()
    Write-Host "OK $path"
}

# 2 секунды тишины.
$silence = Join-Path $out 'silence.wav'
$bytes = New-Object byte[] (44 + 64000)
$w = New-Object System.IO.BinaryWriter([System.IO.File]::Create($silence))
$w.Write([Text.Encoding]::ASCII.GetBytes('RIFF')); $w.Write([int](36 + 64000)); $w.Write([Text.Encoding]::ASCII.GetBytes('WAVEfmt '))
$w.Write([int]16); $w.Write([int16]1); $w.Write([int16]1); $w.Write([int]16000); $w.Write([int]32000); $w.Write([int16]2); $w.Write([int16]16)
$w.Write([Text.Encoding]::ASCII.GetBytes('data')); $w.Write([int]64000); $w.Write((New-Object byte[] 64000)); $w.Close()
Write-Host "OK $silence"
