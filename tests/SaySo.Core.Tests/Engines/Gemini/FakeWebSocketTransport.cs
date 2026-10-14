using System.Text;
using System.Threading.Channels;
using SaySo.Core.Engines;
using SaySo.Core.Engines.Gemini;

namespace SaySo.Core.Tests.Engines.Gemini;

/// <summary>Сценарный WebSocket: тест управляет ответами сервера и видит отправленные сообщения.</summary>
internal sealed class FakeWebSocketTransport : IWebSocketTransport
{
    private readonly Channel<byte[]?> _incoming = Channel.CreateUnbounded<byte[]?>();
    private readonly List<string> _sent = [];

    public Uri? ConnectedUri { get; private set; }

    public IReadOnlyDictionary<string, string>? Headers { get; private set; }

    public Exception? ConnectException { get; set; }

    /// <summary>Реакция «сервера» на каждое отправленное клиентом сообщение.</summary>
    public Action<string, FakeWebSocketTransport>? OnSent { get; set; }

    public int? CloseStatus { get; private set; }

    public string? CloseDescription { get; private set; }

    public bool Disposed { get; private set; }

    public IReadOnlyList<string> Sent
    {
        get
        {
            lock (_sent)
            {
                return _sent.ToList();
            }
        }
    }

    public void ServerSends(string json) => _incoming.Writer.TryWrite(Encoding.UTF8.GetBytes(json));

    public void ServerCloses(int status, string? reason)
    {
        CloseStatus = status;
        CloseDescription = reason;
        _incoming.Writer.TryWrite(null);
    }

    public Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        if (ConnectException is not null)
        {
            throw ConnectException;
        }

        ConnectedUri = uri;
        Headers = headers;
        return Task.CompletedTask;
    }

    public Task SendTextAsync(ReadOnlyMemory<byte> utf8, CancellationToken ct)
    {
        var text = Encoding.UTF8.GetString(utf8.Span);
        lock (_sent)
        {
            _sent.Add(text);
        }

        OnSent?.Invoke(text, this);
        return Task.CompletedTask;
    }

    public async Task<byte[]?> ReceiveAsync(CancellationToken ct) => await _incoming.Reader.ReadAsync(ct);

    public Task CloseAsync(CancellationToken ct) => Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
