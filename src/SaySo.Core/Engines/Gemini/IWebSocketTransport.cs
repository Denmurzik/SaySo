namespace SaySo.Core.Engines.Gemini;

/// <summary>Минимальный WebSocket-транспорт (подменяется в тестах).</summary>
public interface IWebSocketTransport : IAsyncDisposable
{
    int? CloseStatus { get; }

    string? CloseDescription { get; }

    Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken ct);

    Task SendTextAsync(ReadOnlyMemory<byte> utf8, CancellationToken ct);

    /// <summary>Целое сообщение (текстовое или бинарное); null — сервер закрыл соединение.</summary>
    Task<byte[]?> ReceiveAsync(CancellationToken ct);

    Task CloseAsync(CancellationToken ct);
}
