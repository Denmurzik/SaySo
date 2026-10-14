using System.Buffers.Binary;
using SaySo.Core.Audio.Wasapi;
using SaySo.Core.Dictation;
using SaySo.Core.Input;
using SaySo.Core.Overlay;
using SaySo.Core.Platform;

namespace SaySo.Core.Tests.Dictation;

internal static class TestAudio
{
    public static byte[] Speech(int ms)
    {
        var samples = ms * 16;
        var bytes = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), (short)(8000 * Math.Sin(2 * Math.PI * 300 * i / 16000.0)));
        }

        return bytes;
    }
}

internal sealed class FakeAudioSource(Action<ReadOnlySpan<byte>> onData, Action<MicrophoneException> onError) : IAudioSource
{
    public Task StartResult { get; set; } = Task.CompletedTask;

    public bool Stopped { get; private set; }

    public bool Disposed { get; private set; }

    public Task StartAsync() => StartResult;

    public void Push(byte[] pcm) => onData(pcm);

    public void Fail(MicrophoneException e) => onError(e);

    public void Stop() => Stopped = true;

    public void Dispose() => Disposed = true;
}

internal sealed class FakeOverlay : IDictationOverlay
{
    public List<string> Calls { get; } = [];

    public void ShowRecording() => Calls.Add("recording");

    public bool ThrowOnProcessing { get; set; }

    public void ShowProcessing()
    {
        if (ThrowOnProcessing)
        {
            throw new InvalidOperationException("overlay");
        }

        Calls.Add("processing");
    }

    public void ShowMessage(string text, OverlayTone tone) => Calls.Add($"message:{tone}:{text}");

    public void Hide() => Calls.Add("hide");
}

internal sealed class FakeOutput : ITextOutput
{
    public List<string> Texts { get; } = [];

    public InsertOutcome Outcome { get; set; } = InsertOutcome.Inserted;

    public TaskCompletionSource? Gate { get; set; }

    public Exception? Throw { get; set; }

    public async Task<InsertOutcome> InsertAsync(string text, IReadOnlyCollection<int> keysToRelease)
    {
        Texts.Add(text);
        if (Throw is not null)
        {
            throw Throw;
        }

        if (Gate is not null)
        {
            await Gate.Task;
        }

        return Outcome;
    }
}

internal sealed class FakeFeedback : IDictationFeedback
{
    public List<SoundKind> Sounds { get; } = [];

    public List<TrayState> Tray { get; } = [];

    public void Play(SoundKind kind) => Sounds.Add(kind);

    public void SetTray(TrayState state) => Tray.Add(state);
}

internal sealed class ManualScheduler : IScheduler
{
    public List<(TimeSpan Delay, Action Action, bool Cancelled)> Items { get; } = [];

    public IDisposable Schedule(TimeSpan delay, Action action)
    {
        Items.Add((delay, action, false));
        var index = Items.Count - 1;
        return new Cancel(() => Items[index] = Items[index] with { Cancelled = true });
    }

    public void Fire(TimeSpan delay)
    {
        foreach (var item in Items.Where(i => i.Delay == delay && !i.Cancelled).ToList())
        {
            item.Action();
        }
    }

    private sealed class Cancel(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
