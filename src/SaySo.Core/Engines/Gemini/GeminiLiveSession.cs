using System.Diagnostics;
using System.Net.WebSockets;
using System.Threading.Channels;

namespace SaySo.Core.Engines.Gemini;

/// <summary>Параметры подключения. Способ передачи ключа задан здесь — единственное место для его смены.</summary>
public sealed record GeminiConnection(Uri Endpoint, string ApiKey, GeminiSessionOptions Options)
{
    /// <summary>
    /// Подключение по адресу из настроек. Неверный адрес (его можно вписать в settings.json руками) —
    /// <see cref="TranscriptionException"/> с <see cref="EngineErrorKind.InvalidEndpoint"/>, а не <see cref="UriFormatException"/>.
    /// </summary>
    public static GeminiConnection Create(string endpoint, string apiKey, GeminiSessionOptions options) =>
        new(ParseEndpoint(endpoint), apiKey, options);

    /// <summary>Проверяет адрес WebSocket (абсолютный ws/wss); иначе <see cref="TranscriptionException"/> InvalidEndpoint.</summary>
    public static Uri ParseEndpoint(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.Scheme is "wss" or "ws"
            ? uri
            : throw new TranscriptionException(EngineErrorKind.InvalidEndpoint, "Неверный адрес сервера Gemini в настройках.");

    // Запись по умолчанию печатает все свойства, включая ApiKey: ToString() попадал бы в логи и исключения с открытым ключом.
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("Endpoint = ").Append(Endpoint).Append(", ApiKey = ***, Options = ").Append(Options);
        return true;
    }

    /// <summary>Адрес WebSocket (ключ в него не входит).</summary>
    public Uri ConnectUri => Endpoint;

    /// <summary>Ключ — заголовком x-goog-api-key (как в официальном Python SDK), чтобы он не попадал в URL.</summary>
    public IReadOnlyDictionary<string, string> Headers => new Dictionary<string, string> { ["x-goog-api-key"] = ApiKey };
}

/// <summary>Одна реплика через Gemini Live: подключение, буфер до setupComplete, отправка, ожидание финала.</summary>
public sealed class GeminiLiveSession : IStreamingSession
{
    private readonly IWebSocketTransport _transport;
    private readonly GeminiConnection _connection;
    private readonly CompletionTimings _timings;
    private readonly Channel<byte[]> _audio = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly TaskCompletionSource<string> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _setupComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new(); // защищает _assembler и _policy
    private readonly TranscriptAssembler _assembler = new();
    private readonly CompletionPolicy _policy;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _lifecycle = new(); // защищает _run и _disposed
    private Task? _run;
    private bool _disposed;
    private int _failed;

    public GeminiLiveSession(IWebSocketTransport transport, GeminiConnection connection, CompletionTimings timings)
    {
        _transport = transport;
        _connection = connection;
        _timings = timings;
        _policy = new CompletionPolicy(timings);
    }

    public event Action<string>? InterimText;

    public event Action<TranscriptionException>? Failed;

    private long Now => _clock.ElapsedMilliseconds;

    public void Start()
    {
        lock (_lifecycle)
        {
            if (_disposed || _run is not null)
            {
                return;
            }

            _run = Task.Run(RunAsync);
        }
    }

    public void PushAudio(ReadOnlyMemory<byte> chunk) => _audio.Writer.TryWrite(chunk.ToArray());

    public Task<string> CompleteAsync(CancellationToken ct)
    {
        _audio.Writer.TryComplete();
        return _result.Task.WaitAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        Task? run;
        lock (_lifecycle)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            run = _run;
        }

        _audio.Writer.TryComplete();
        _cts.Cancel();
        _result.TrySetCanceled();
        if (run is not null)
        {
            try
            {
                await run.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Ошибки сессии уже переданы в _result.
            }
        }

        _cts.Dispose();
    }

    private async Task RunAsync()
    {
        var ct = _cts.Token;
        // Токен фоновых операций сессии (приём, таймер настройки): отменяется при любом выходе из RunAsync,
        // чтобы цикл приёма и таймеры не переживали сессию.
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task? receive = null;
        try
        {
            await _transport.ConnectAsync(_connection.ConnectUri, _connection.Headers, ct).ConfigureAwait(false);
            await _transport.SendTextAsync(GeminiMessages.BuildSetup(_connection.Options), ct).ConfigureAwait(false);
            receive = ReceiveLoopAsync(runCts.Token);

            var setupTimeout = Task.Delay(_timings.SetupTimeoutMs, runCts.Token);
            var first = await Task.WhenAny(_setupComplete.Task, receive, setupTimeout).ConfigureAwait(false);
            if (first == setupTimeout)
            {
                ct.ThrowIfCancellationRequested();
                throw new TranscriptionException(EngineErrorKind.Timeout, "Gemini не подтвердил настройку сессии.");
            }

            if (first == receive)
            {
                await receive.ConfigureAwait(false); // пробросит ошибку закрытия
                throw new TranscriptionException(EngineErrorKind.Protocol, "Gemini закрыл соединение до начала сессии.");
            }

            await foreach (var chunk in _audio.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (receive.IsCompleted)
                {
                    // Сервер закрыл соединение посреди записи: сообщаем причину сразу, а не после отпускания клавиши.
                    await receive.ConfigureAwait(false);
                    throw new TranscriptionException(EngineErrorKind.Protocol, "Gemini закрыл соединение до конца записи.");
                }

                await _transport.SendTextAsync(GeminiMessages.BuildAudio(chunk), ct).ConfigureAwait(false);
            }

            await _transport.SendTextAsync(GeminiMessages.BuildAudioStreamEnd(), ct).ConfigureAwait(false);
            lock (_gate)
            {
                _policy.OnEndSent(Now);
            }

            _result.TrySetResult(await WaitForFinalAsync(receive, ct).ConfigureAwait(false));
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            _result.TrySetCanceled(ct); // отмена (DisposeAsync) — не ошибка сессии
        }
        catch (TranscriptionException e)
        {
            Fail(e);
        }
        catch (Exception e) when (e is WebSocketException or IOException or HttpRequestException)
        {
            Fail(new TranscriptionException(EngineErrorKind.Network, "Обрыв связи с Gemini.", e));
        }
        catch (Exception e)
        {
            // Любой другой сбой не должен оставить CompleteAsync ждать вечно.
            Fail(new TranscriptionException(EngineErrorKind.Protocol, "Непредвиденная ошибка сессии Gemini.", e));
        }
        finally
        {
            try
            {
                await _transport.CloseAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Закрытие — по возможности.
            }

            runCts.Cancel();
            try
            {
                await _transport.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Освобождение — по возможности.
            }

            if (receive is not null)
            {
                try
                {
                    await receive.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Ошибка приёма уже передана через Fail либо не важна (отмена при завершении).
                }
            }
        }
    }

    private async Task<string> WaitForFinalAsync(Task receive, CancellationToken ct)
    {
        CompletionState state;
        while (true)
        {
            var wake = Volatile.Read(ref _wake).Task;
            long next;
            lock (_gate)
            {
                state = _policy.Evaluate(Now);
                next = _policy.NextCheckAt();
            }

            if (state != CompletionState.Waiting)
            {
                break;
            }

            if (receive.IsCompleted)
            {
                if (receive.IsFaulted && !HasAnyText())
                {
                    await receive.ConfigureAwait(false); // пробросит классифицированную ошибку
                }

                break; // сервер закрыл соединение после конца потока — берём накопленное
            }

            var delay = TimeSpan.FromMilliseconds(Math.Max(1, next - Now));
            using (var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                await Task.WhenAny(wake, Task.Delay(delay, delayCts.Token), receive).ConfigureAwait(false);
                delayCts.Cancel(); // не копить неиспользованные таймеры
            }

            ct.ThrowIfCancellationRequested();
        }

        string text;
        lock (_gate)
        {
            text = _assembler.BuildResult(includePendingInterim: true);
        }

        // Сервер так и не прислал текста — это сбой основного движка (сработает резерв), а не «пустая» диктовка.
        if (text.Length == 0 && state == CompletionState.TimedOut)
        {
            throw new TranscriptionException(EngineErrorKind.Timeout, "Gemini не вернул текст вовремя.");
        }

        return text;
    }

    private bool HasAnyText()
    {
        lock (_gate)
        {
            return _assembler.BuildResult(includePendingInterim: true).Length > 0;
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (true)
        {
            var message = await _transport.ReceiveAsync(ct).ConfigureAwait(false);
            if (message is null)
            {
                var status = _transport.CloseStatus;
                if (status == 1000)
                {
                    Wake();
                    return;
                }

                throw new TranscriptionException(GeminiErrorClassifier.Classify(status, _transport.CloseDescription),
                    $"Gemini закрыл соединение (код {status?.ToString() ?? "нет"}): {_transport.CloseDescription}");
            }

            foreach (var e in GeminiServerParser.Parse(message))
            {
                string? interim = null;
                lock (_gate)
                {
                    _policy.OnEvent(e, Now);
                    switch (e.Kind)
                    {
                        case GeminiEventKind.SetupComplete:
                            _setupComplete.TrySetResult();
                            break;
                        case GeminiEventKind.Interim:
                            _assembler.SetInterim(e.Text ?? "");
                            interim = e.Text;
                            break;
                        case GeminiEventKind.Final:
                            _assembler.AddFinal(e.Text ?? "");
                            break;
                    }
                }

                if (!string.IsNullOrWhiteSpace(interim))
                {
                    try
                    {
                        InterimText?.Invoke(interim);
                    }
                    catch (Exception)
                    {
                        // Ошибка подписчика не должна обрывать приём.
                    }
                }
            }

            Wake();
        }
    }

    private void Wake() =>
        Interlocked.Exchange(ref _wake, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

    // Ошибка сообщается один раз и СНАЧАЛА подписчикам (кольцо ставит ключ на паузу, реплика меняет ключ),
    // и только потом — ожидающему CompleteAsync: иначе следующий запрос мог бы успеть взять тот же ключ.
    private void Fail(TranscriptionException e)
    {
        if (Interlocked.Exchange(ref _failed, 1) != 0)
        {
            return;
        }

        if (Failed is { } handlers)
        {
            foreach (var handler in handlers.GetInvocationList())
            {
                try
                {
                    ((Action<TranscriptionException>)handler)(e);
                }
                catch (Exception)
                {
                    // Сбой подписчика не должен помешать доставить ошибку в CompleteAsync.
                }
            }
        }

        _result.TrySetException(e);
    }
}
