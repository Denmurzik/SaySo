using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace SaySo.Core.Updates;

public enum UpdateCheckOutcome
{
    /// <summary>Новее установленной версии релиза нет (или релизов ещё нет вовсе).</summary>
    UpToDate,

    /// <summary>Есть более новая версия.</summary>
    Available,

    /// <summary>Проверить не удалось (причина в <see cref="UpdateCheckResult.Failure"/>).</summary>
    Failed,
}

public enum UpdateFailure
{
    /// <summary>Нет связи с GitHub или он ответил ошибкой сервера.</summary>
    Network,
    Timeout,

    /// <summary>GitHub ограничил частоту запросов (403/429).</summary>
    RateLimited,

    /// <summary>Ответ не удалось разобрать.</summary>
    BadResponse,
}

public sealed record UpdateCheckResult(UpdateCheckOutcome Outcome, ReleaseInfo? Release = null, UpdateFailure? Failure = null)
{
    public static UpdateCheckResult UpToDate { get; } = new(UpdateCheckOutcome.UpToDate);

    public static UpdateCheckResult Available(ReleaseInfo release) => new(UpdateCheckOutcome.Available, release);

    public static UpdateCheckResult Failed(UpdateFailure failure) => new(UpdateCheckOutcome.Failed, Failure: failure);

    /// <summary>
    /// Попытка засчитывается в суточный лимит, кроме сбоев связи: без сети следующая проверка по событию
    /// (разблокировка, выход из сна) должна повториться, а не ждать сутки.
    /// </summary>
    public bool CountsAsAttempt => Outcome != UpdateCheckOutcome.Failed || Failure is UpdateFailure.RateLimited or UpdateFailure.BadResponse;
}

/// <summary>HttpClient для обновлений: один на операцию, после неё <c>Dispose</c> закрывает все соединения (в простое сокетов нет).</summary>
public static class UpdateHttp
{
    public static HttpClient Create() => new(new SocketsHttpHandler
    {
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(10),
        AutomaticDecompression = DecompressionMethods.All,
    })
    {
        Timeout = System.Threading.Timeout.InfiniteTimeSpan, // таймауты — на уровне запросов
    };
}

/// <summary>Запрос <c>/releases/latest</c> у GitHub и сравнение с установленной версией. Токена нет, ничего пользовательского не отправляется.</summary>
public sealed class UpdateChecker(HttpClient http, Version current, string? url = null, TimeSpan? timeout = null)
{
    public const string LatestReleaseUrl = "https://api.github.com/repos/Denmurzik/SaySo/releases/latest";
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private const int MaxResponseBytes = 1 << 20;

    /// <summary>Сбои связи, разбора и таймаут — в результате (<see cref="UpdateCheckOutcome.Failed"/>); отмену вызывающего исключение пробрасывает.</summary>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout ?? RequestTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url ?? LatestReleaseUrl);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("SaySo", VersionTag.Normalize(current).ToString(3)));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);

            switch (response.StatusCode)
            {
                case HttpStatusCode.NotFound:
                    return UpdateCheckResult.UpToDate; // у репозитория ещё нет ни одного релиза
                case HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests:
                    return UpdateCheckResult.Failed(UpdateFailure.RateLimited);
                case >= HttpStatusCode.InternalServerError:
                    return UpdateCheckResult.Failed(UpdateFailure.Network);
            }

            if (!response.IsSuccessStatusCode)
            {
                return UpdateCheckResult.Failed(UpdateFailure.BadResponse);
            }

            var body = await HttpLimits.ReadAsync(response.Content, MaxResponseBytes, deadline.Token).ConfigureAwait(false);
            if (body is null)
            {
                return UpdateCheckResult.Failed(UpdateFailure.BadResponse);
            }

            var release = ReleaseParser.Parse(body);
            return release is not null && release.Version > VersionTag.Normalize(current)
                ? UpdateCheckResult.Available(release)
                : UpdateCheckResult.UpToDate;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return UpdateCheckResult.Failed(UpdateFailure.Timeout);
        }
        catch (HttpRequestException)
        {
            return UpdateCheckResult.Failed(UpdateFailure.Network);
        }
        catch (JsonException)
        {
            return UpdateCheckResult.Failed(UpdateFailure.BadResponse);
        }
    }
}

internal static class HttpLimits
{
    /// <returns>Тело ответа или null, если оно больше <paramref name="maxBytes"/>.</returns>
    public static async Task<byte[]?> ReadAsync(HttpContent content, int maxBytes, CancellationToken ct)
    {
        if (content.Headers.ContentLength > maxBytes)
        {
            return null;
        }

        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}

/// <summary>Проверка с записью времени попытки в update-state.json; общий код демона и окна настроек.</summary>
public sealed class UpdateCheckRunner(UpdateStateStore store, Version current, Func<HttpClient> httpFactory, TimeProvider time)
{
    public async Task<UpdateCheckResult> RunAsync(CancellationToken ct = default)
    {
        UpdateCheckResult result;
        using (var http = httpFactory())
        {
            result = await new UpdateChecker(http, current).CheckAsync(ct).ConfigureAwait(false);
        }

        if (result.CountsAsAttempt)
        {
            var state = store.Load();
            state.LastCheckUtc = time.GetUtcNow();
            store.Save(state);
        }

        return result;
    }
}
