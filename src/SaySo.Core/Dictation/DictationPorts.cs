using SaySo.Core.Audio.Wasapi;
using SaySo.Core.Input;
using SaySo.Core.Overlay;
using SaySo.Core.Platform;

namespace SaySo.Core.Dictation;

/// <summary>Источник звука одной сессии (в демоне — WASAPI).</summary>
public interface IAudioSource : IDisposable
{
    Task StartAsync();

    void Stop();
}

public delegate IAudioSource AudioSourceFactory(Action<ReadOnlySpan<byte>> onData, Action<MicrophoneException> onError);

public interface IDictationOverlay
{
    void ShowRecording();

    void ShowProcessing();

    /// <summary>Сообщение скрывается само через несколько секунд.</summary>
    void ShowMessage(string text, OverlayTone tone);

    void Hide();
}

public interface ITextOutput
{
    Task<InsertOutcome> InsertAsync(string text, IReadOnlyCollection<int> keysToRelease);
}

public interface IDictationFeedback
{
    void Play(SoundKind kind);

    /// <param name="detail">Полный текст ошибки для подсказки значка (только для <see cref="TrayState.Error"/>).</param>
    void SetTray(TrayState state, string? detail = null);
}

public interface IScheduler
{
    IDisposable Schedule(TimeSpan delay, Action action);
}

public enum DictationState
{
    Idle,
    Recording,
    Finishing,
}
