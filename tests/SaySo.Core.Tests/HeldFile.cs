namespace SaySo.Core.Tests;

/// <summary>Держит файл открытым с FileShare.None (имитация блокировки другим процессом).</summary>
public sealed class HeldFile : IDisposable
{
    private readonly FileStream _stream;
    private Thread? _releaser;

    public HeldFile(string path)
    {
        _stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    /// <summary>Освобождает файл в фоне через указанное время; возвращается сразу.</summary>
    public HeldFile ReleaseAfter(TimeSpan delay)
    {
        _releaser = new Thread(() =>
        {
            Thread.Sleep(delay);
            _stream.Dispose();
        });
        _releaser.Start();
        return this;
    }

    public void Dispose()
    {
        _releaser?.Join();
        _stream.Dispose();
    }
}
