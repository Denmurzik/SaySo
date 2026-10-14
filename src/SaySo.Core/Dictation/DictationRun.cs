using SaySo.Core.Audio;
using SaySo.Core.Engines;
using SaySo.Core.Overlay;

namespace SaySo.Core.Dictation;

/// <summary>
/// Одна реплика. <see cref="PushAudio"/> вызывается из потока захвата, <see cref="StartPrimary"/> — из главного,
/// событие <c>Failed</c> сессии — из фонового; <see cref="FinishAsync"/> — только после остановки захвата.
/// </summary>
public sealed class DictationRun : IAsyncDisposable
{
    /// <summary>Сессий основного движка на одну реплику (первая + смены ключа).</summary>
    public const int MaxPrimaryAttempts = 4;

    private readonly IDictationEngines _engines;
    private readonly object _gate = new();
    private readonly PcmChunker _chunker = new();
    private readonly UtteranceBuffer _utterance = new();
    private IStreamingSession? _primary;
    private EngineErrorKind? _primaryError;
    private long _routedBytes;
    private int _attempts;
    private float _peakLevel;
    private bool _primaryStarted;
    private bool _finishing;
    private bool _disposed;

    internal DictationRun(IDictationEngines engines)
    {
        _engines = engines;
    }

    public float LastLevel { get; private set; }

    public long RecordedBytes
    {
        get
        {
            lock (_gate)
            {
                return _utterance.Length;
            }
        }
    }

    public void StartPrimary()
    {
        lock (_gate)
        {
            if (_primaryStarted || _disposed)
            {
                return;
            }

            _primaryStarted = true;
            TryStartSession();
        }
    }

    public void PushAudio(ReadOnlySpan<byte> pcm)
    {
        var level = LevelMeter.Compute(pcm);
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            LastLevel = level;
            _peakLevel = Math.Max(_peakLevel, level);
            _utterance.Append(pcm);
            _chunker.Append(pcm, RouteChunk);
        }
    }

    public async Task<DictationResult> FinishAsync(CancellationToken ct)
    {
        bool silent;
        lock (_gate)
        {
            _finishing = true;
            var rest = _chunker.Flush();
            if (rest.Length > 0)
            {
                RouteChunk(rest);
            }

            silent = AudioFormat.BytesToMilliseconds(_utterance.Length) < DictationPipeline.MinSpeechMs
                     || _peakLevel < DictationPipeline.SilenceLevel;
        }

        try
        {
            if (silent)
            {
                return DictationResult.Empty;
            }

            while (true)
            {
                IStreamingSession? primary;
                lock (_gate)
                {
                    primary = _primary;
                }

                if (primary is null)
                {
                    break;
                }

                EngineErrorKind failure;
                try
                {
                    var text = await primary.CompleteAsync(ct).WaitAsync(DictationPipeline.PrimaryCompleteTimeout, ct).ConfigureAwait(false);
                    return new DictationResult(text, "gemini", null);
                }
                catch (TranscriptionException e)
                {
                    failure = e.Kind;
                }
                catch (TimeoutException)
                {
                    failure = EngineErrorKind.Timeout;
                }

                lock (_gate)
                {
                    _primaryError = failure;
                    _primary = null;
                    if (IsKeySpecific(failure))
                    {
                        TryStartSession(); // следующий ключ получает всю запись целиком
                    }
                }

                await primary.DisposeAsync().ConfigureAwait(false);
            }

            var primaryError = _primaryError ?? EngineErrorKind.NotConfigured;
            var fallback = _engines.Fallback
                ?? throw new DictationFailedException(primaryError, null, OverlayMessages.NoFallback(primaryError));

            byte[] wav;
            lock (_gate)
            {
                wav = WavWriter.Create(_utterance);
            }

            try
            {
                var text = await fallback.TranscribeAsync(wav, ct).ConfigureAwait(false);
                return new DictationResult(text, fallback.Name, primaryError);
            }
            catch (TranscriptionException e)
            {
                throw new DictationFailedException(primaryError, e.Kind, OverlayMessages.BothFailed(primaryError, e.Kind));
            }
        }
        finally
        {
            await DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        IStreamingSession? primary;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            primary = _primary;
            _primary = null;
            _utterance.Clear();
        }

        if (primary is not null)
        {
            await primary.DisposeAsync().ConfigureAwait(false);
        }
    }

    // Квота и недействительный ключ лечатся другим ключом; сеть, регион и протокол — нет.
    private static bool IsKeySpecific(EngineErrorKind kind) =>
        kind is EngineErrorKind.QuotaExceeded or EngineErrorKind.AuthFailed;

    // Вызывается под _gate.
    private void TryStartSession()
    {
        if (_attempts >= MaxPrimaryAttempts || _engines.Primary is not { } engine)
        {
            _primaryError ??= EngineErrorKind.NotConfigured;
            return;
        }

        _attempts++;
        IStreamingSession session;
        try
        {
            session = engine.StartSession();
        }
        catch (TranscriptionException e)
        {
            _primaryError = e.Kind;
            return;
        }

        session.Failed += e => OnPrimaryFailed(session, e);
        _primary = session;

        // Досылаем всё, что уже ушло фрагментами (остаток чанкера придёт обычным путём).
        var sent = (int)Math.Min(_routedBytes, _utterance.Length);
        if (sent > 0)
        {
            var audio = new byte[sent];
            _utterance.CopyTo(audio);
            for (var offset = 0; offset < sent; offset += AudioFormat.ChunkBytes)
            {
                session.PushAudio(audio.AsMemory(offset, Math.Min(AudioFormat.ChunkBytes, sent - offset)));
            }
        }
    }

    // Сбой сессии во время записи: сразу сменить ключ, не дожидаясь конца реплики.
    private void OnPrimaryFailed(IStreamingSession session, TranscriptionException e)
    {
        lock (_gate)
        {
            if (_disposed || _finishing || !ReferenceEquals(session, _primary))
            {
                return; // в конце реплики сбой обрабатывает FinishAsync
            }

            _primaryError = e.Kind;
            _primary = null;
            if (IsKeySpecific(e.Kind))
            {
                TryStartSession();
            }
        }

        _ = session.DisposeAsync();
    }

    // Вызывается под _gate.
    private void RouteChunk(byte[] chunk)
    {
        _routedBytes += chunk.Length;
        if (_primaryStarted)
        {
            _primary?.PushAudio(chunk);
        }
    }
}
