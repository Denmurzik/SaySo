using System.Buffers;
using System.Net.WebSockets;

namespace SaySo.Core.Engines.Gemini;

/// <summary>Реальный транспорт на <see cref="ClientWebSocket"/> (системные сетевые настройки, в т.ч. VPN).</summary>
public sealed class ClientWebSocketTransport : IWebSocketTransport
{
    private const int MaxMessageBytes = 1024 * 1024;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private readonly ClientWebSocket _ws = new();

    public int? CloseStatus => _ws.CloseStatus is { } s ? (int)s : null;

    public string? CloseDescription => _ws.CloseStatusDescription;

    public async Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        foreach (var (name, value) in headers)
        {
            _ws.Options.SetRequestHeader(name, value);
        }

        _ws.Options.CollectHttpResponseDetails = true;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeout);
        try
        {
            await _ws.ConnectAsync(uri, timeout.Token).ConfigureAwait(false);
        }
        catch (WebSocketException e)
        {
            var http = (int)_ws.HttpStatusCode;
            throw new TranscriptionException(GeminiErrorClassifier.ClassifyHandshake(http),
                $"Не удалось подключиться к Gemini (HTTP {http}).", e);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TranscriptionException(EngineErrorKind.Timeout, "Подключение к Gemini заняло больше 10 с.");
        }
    }

    public Task SendTextAsync(ReadOnlyMemory<byte> utf8, CancellationToken ct) =>
        _ws.SendAsync(utf8, WebSocketMessageType.Text, endOfMessage: true, ct).AsTask();

    public async Task<byte[]?> ReceiveAsync(CancellationToken ct)
    {
        var buffer = new ArrayBufferWriter<byte>(4096);
        while (true)
        {
            var result = await _ws.ReceiveAsync(buffer.GetMemory(4096), ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            buffer.Advance(result.Count);
            if (result.EndOfMessage)
            {
                return buffer.WrittenSpan.ToArray();
            }

            if (buffer.WrittenCount > MaxMessageBytes)
            {
                throw new TranscriptionException(EngineErrorKind.Protocol, "Слишком большое сообщение от Gemini.");
            }
        }
    }

    public async Task CloseAsync(CancellationToken ct)
    {
        if (_ws.State != WebSocketState.Open && _ws.State != WebSocketState.CloseReceived)
        {
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException)
        {
            // Соединение уже оборвано — закрывать нечего.
        }
    }

    public ValueTask DisposeAsync()
    {
        _ws.Dispose();
        return ValueTask.CompletedTask;
    }
}
