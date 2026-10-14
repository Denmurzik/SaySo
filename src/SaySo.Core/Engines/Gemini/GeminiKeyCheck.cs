using System.Net.WebSockets;

namespace SaySo.Core.Engines.Gemini;

/// <summary>Проверка ключа/модели/региона: подключение и setup без отправки аудио.</summary>
public static class GeminiKeyCheck
{
    public static async Task<EngineErrorKind?> CheckAsync(
        IWebSocketTransport transport,
        GeminiConnection connection,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await transport.ConnectAsync(connection.ConnectUri, connection.Headers, cts.Token).ConfigureAwait(false);
            await transport.SendTextAsync(GeminiMessages.BuildSetup(connection.Options), cts.Token).ConfigureAwait(false);
            while (true)
            {
                var message = await transport.ReceiveAsync(cts.Token).ConfigureAwait(false);
                if (message is null)
                {
                    return GeminiErrorClassifier.Classify(transport.CloseStatus, transport.CloseDescription);
                }

                if (GeminiServerParser.Parse(message).Any(e => e.Kind == GeminiEventKind.SetupComplete))
                {
                    return null;
                }
            }
        }
        catch (TranscriptionException e)
        {
            return e.Kind;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return EngineErrorKind.Timeout;
        }
        catch (Exception e) when (e is WebSocketException or IOException)
        {
            return EngineErrorKind.Network;
        }
        finally
        {
            try
            {
                await transport.CloseAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Закрытие — по возможности.
            }

            await transport.DisposeAsync().ConfigureAwait(false);
        }
    }
}
