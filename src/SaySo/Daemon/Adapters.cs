using System.ComponentModel;
using SaySo.Core.Audio.Wasapi;
using SaySo.Core.Diagnostics;
using SaySo.Core.Dictation;
using SaySo.Core.Input;
using SaySo.Core.Platform;

namespace SaySo.Daemon;

/// <summary>
/// Микрофон сессии. Координатор ловит <see cref="MicrophoneException"/> отдельно от прочих сбоев, поэтому любой сбой
/// запуска оборачивается в неё; <see cref="Stop"/> и <see cref="Dispose"/> не бросают (их зовут при откате и отмене).
/// </summary>
internal sealed class WasapiAudioSource(string? deviceId, Action<ReadOnlySpan<byte>> onData, Action<MicrophoneException> onError) : IAudioSource
{
    private readonly WasapiCapture _capture = new(deviceId, onData, onError);

    public async Task StartAsync()
    {
        try
        {
            await _capture.StartAsync();
        }
        catch (MicrophoneException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw new MicrophoneException(MicrophoneErrorKind.Other, e.HResult, $"Запуск захвата: {e.GetType().Name}");
        }
    }

    public void Stop()
    {
        try
        {
            _capture.Stop();
        }
        catch (Exception e)
        {
            Log.Error("Остановка захвата", e);
        }
    }

    public void Dispose()
    {
        try
        {
            _capture.Dispose();
        }
        catch (Exception e)
        {
            Log.Error("Освобождение захвата", e);
        }
    }
}

internal sealed class InserterOutput(TextInserter inserter) : ITextOutput
{
    public Task<InsertOutcome> InsertAsync(string text, IReadOnlyCollection<int> keysToRelease) =>
        inserter.InsertAsync(text, keysToRelease);
}

internal sealed class DaemonFeedback(Action<TrayState> setTray) : IDictationFeedback
{
    public void Play(SoundKind kind) => SoundPlayer.Play(kind);

    public void SetTray(TrayState state) => setTray(state);
}

/// <summary>Таймеры координатора на окне демона: действия выполняются на главном потоке.</summary>
internal sealed class WindowScheduler(DaemonWindow window, Action onFailure) : IScheduler
{
    public IDisposable Schedule(TimeSpan delay, Action action)
    {
        var handle = new Handle(action);
        try
        {
            handle.Timer = window.SetTimer(delay, handle.Run);
        }
        catch (Win32Exception e)
        {
            // Тихая потеря таймера означала бы запись без автостопа: сообщаем владельцу, он отменит сессию с ошибкой.
            Log.Error("Таймер диктовки", e);
            handle.Dispose();
            onFailure();
        }

        return handle;
    }

    // KillTimer не убирает уже поставленный в очередь WM_TIMER, поэтому отмену проверяем и в самом обратном вызове:
    // после Dispose действие не выполнится никогда.
    private sealed class Handle(Action action) : IDisposable
    {
        private bool _cancelled;

        public IDisposable? Timer { get; set; }

        public void Run()
        {
            if (!_cancelled)
            {
                action();
            }
        }

        public void Dispose()
        {
            _cancelled = true;
            Timer?.Dispose();
        }
    }
}
