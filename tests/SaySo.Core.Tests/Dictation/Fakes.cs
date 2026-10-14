using SaySo.Core.Dictation;
using SaySo.Core.Engines;

namespace SaySo.Core.Tests.Dictation;

internal sealed class FakeSession : IStreamingSession
{
    public List<byte[]> Pushed { get; } = [];

    public Func<Task<string>> Complete { get; set; } = () => Task.FromResult("текст");

    public bool Disposed { get; private set; }

    public event Action<string>? InterimText;

    public event Action<TranscriptionException>? Failed;

    public byte[] AllPushed()
    {
        lock (Pushed)
        {
            return Pushed.SelectMany(x => x).ToArray();
        }
    }

    public void RaiseFailed(TranscriptionException e) => Failed?.Invoke(e);

    public void PushAudio(ReadOnlyMemory<byte> chunk)
    {
        lock (Pushed)
        {
            Pushed.Add(chunk.ToArray());
        }
    }

    public Task<string> CompleteAsync(CancellationToken ct) => Complete();

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        InterimText?.Invoke(""); // событие используется только реальными сессиями
        return ValueTask.CompletedTask;
    }
}

/// <summary>Сценарий по номеру попытки (0, 1, …). Исключение из фабрики ведёт себя как из StartSession.</summary>
internal sealed class FakeStreamingEngine : IStreamingEngine
{
    public List<FakeSession> Sessions { get; } = [];

    public Func<int, FakeSession> Factory { get; set; } = _ => new FakeSession();

    public int Attempts { get; private set; }

    public IStreamingSession StartSession()
    {
        var session = Factory(Attempts++);
        Sessions.Add(session);
        return session;
    }
}

internal sealed class FakeBatchEngine : IBatchEngine
{
    public List<byte[]> Calls { get; } = [];

    public Func<byte[], Task<string>> Handler { get; set; } = _ => Task.FromResult("резервный текст");

    public string Name => "groq";

    public Task<string> TranscribeAsync(byte[] wav, CancellationToken ct)
    {
        Calls.Add(wav);
        return Handler(wav);
    }
}

internal sealed class FakeEngines : IDictationEngines
{
    public IStreamingEngine? Primary { get; set; }

    public IBatchEngine? Fallback { get; set; }
}

internal static class Fail
{
    public static Task<string> With(EngineErrorKind kind) =>
        Task.FromException<string>(new TranscriptionException(kind, kind.ToString()));

    public static FakeSession Session(EngineErrorKind kind) => new() { Complete = () => With(kind) };
}
