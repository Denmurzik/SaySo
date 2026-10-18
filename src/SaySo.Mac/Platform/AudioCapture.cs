using System.Runtime.InteropServices;
using SaySo.Core.Audio;
using SaySo.Core.Audio.Wasapi;
using SaySo.Core.Diagnostics;
using SaySo.Mac.Interop;

namespace SaySo.Mac.Platform;

/// <summary>
/// Захват микрофона на macOS: AudioQueue сам приводит звук устройства к PCM 16 кГц / 16 бит / моно (формат SaySo),
/// буферы по 100 мс приходят на поток AudioQueue. Выбранное устройство ищется по UID, а если его нет (другой порт USB,
/// новый UID) — по имени, как в Windows (<see cref="MicrophoneSelector"/>); иначе — микрофон macOS по умолчанию.
/// Отключение устройства во время записи (kAudioDevicePropertyDeviceIsAlive или остановка очереди) приходит в onError
/// один раз. В простое ничего не держит: очередь создаётся на каждую сессию.
/// </summary>
internal sealed class AudioCapture : IAudioCapture
{
    private const int BufferCount = 3;
    private const uint BufferBytes = AudioFormat.ChunkBytes;

    private static readonly Dictionary<nint, AudioCapture> Live = [];
    private static long _nextId;

    private readonly string? _deviceId;
    private readonly string? _deviceName;
    private readonly Action<ReadOnlySpan<byte>> _onData;
    private readonly Action<MicrophoneException>? _onError;
    private readonly bool _requestPermission;
    private readonly nint _id;
    private readonly object _gate = new();
    private nint _queue;
    private uint _device;
    private bool _aliveListener;
    private volatile bool _stopping;
    private int _errorRaised;
    private bool _started;

    /// <param name="requestPermission">
    /// Разрешения ещё не спрашивали: показать системный запрос и дождаться ответа (окно настроек). Демон спрашивает при
    /// старте, а в момент диктовки ждать диалога нельзя — запись тогда сразу заканчивается ошибкой доступа.
    /// </param>
    public AudioCapture(string? deviceId, Action<ReadOnlySpan<byte>> onData, Action<MicrophoneException>? onError = null,
        string? deviceName = null, bool requestPermission = false)
    {
        _deviceId = deviceId;
        _deviceName = deviceName;
        _onData = onData;
        _onError = onError;
        _requestPermission = requestPermission;
        _id = (nint)Interlocked.Increment(ref _nextId);
    }

    public async Task StartAsync()
    {
        if (_started)
        {
            throw new InvalidOperationException("Захват уже запущен.");
        }

        _started = true;
        var permission = MicrophonePermission.Current();
        if (permission == MicrophonePermission.Status.NotDetermined && _requestPermission)
        {
            permission = await MicrophonePermission.RequestAsync().ConfigureAwait(false)
                ? MicrophonePermission.Status.Authorized
                : MicrophonePermission.Status.Denied;
        }

        if (permission != MicrophonePermission.Status.Authorized)
        {
            if (permission == MicrophonePermission.Status.NotDetermined)
            {
                _ = MicrophonePermission.RequestAsync(); // системный запрос — к следующей попытке
            }

            throw new MicrophoneException(MicrophoneErrorKind.AccessDenied, 0, $"Нет разрешения на микрофон ({permission})");
        }

        // Создание очереди и запуск устройства (Bluetooth-гарнитура просыпается до секунды) — не на главном потоке.
        await Task.Run(() =>
        {
            using var pool = AutoreleasePool.Push();
            StartCore();
        }).ConfigureAwait(false);
    }

    public void Stop()
    {
        nint queue;
        lock (_gate)
        {
            _stopping = true;
            queue = _queue;
        }

        if (queue != 0)
        {
            CoreAudio.AudioQueueStop(queue, 1);
        }
    }

    public void Dispose()
    {
        Stop();
        nint queue;
        lock (_gate)
        {
            queue = _queue;
            _queue = 0;
        }

        RemoveAliveListener();
        if (queue != 0)
        {
            CoreAudio.AudioQueueDispose(queue, 1); // после возврата очередь больше не вызывает обратных вызовов
        }

        lock (Live)
        {
            Live.Remove(_id);
        }
    }

    private unsafe void StartCore()
    {
        _device = ResolveDevice(out var uid);
        if (_device == 0)
        {
            throw new MicrophoneException(MicrophoneErrorKind.NotFound, 0, "Нет устройств записи");
        }

        var format = new CoreAudio.StreamDescription
        {
            SampleRate = AudioFormat.SampleRate,
            FormatId = CoreAudio.FormatLinearPcm,
            FormatFlags = CoreAudio.FormatFlagsSignedPacked,
            BytesPerPacket = AudioFormat.BytesPerSample * AudioFormat.Channels,
            FramesPerPacket = 1,
            BytesPerFrame = AudioFormat.BytesPerSample * AudioFormat.Channels,
            ChannelsPerFrame = AudioFormat.Channels,
            BitsPerChannel = AudioFormat.BitsPerSample,
        };

        lock (Live)
        {
            Live[_id] = this;
        }

        nint queue;
        Check(CoreAudio.AudioQueueNewInput(&format, &OnBuffer, _id, 0, 0, 0, &queue), "AudioQueueNewInput");
        lock (_gate)
        {
            if (_stopping)
            {
                CoreAudio.AudioQueueDispose(queue, 1); // Stop пришёл, пока создавали очередь
                return;
            }

            _queue = queue;
        }

        if (uid is not null)
        {
            var cf = ObjC.Str(uid); // NSString — это CFStringRef
            Check(CoreAudio.AudioQueueSetProperty(queue, CoreAudio.QueuePropertyCurrentDevice, &cf, (uint)sizeof(nint)), "AudioQueue: устройство");
        }

        for (var i = 0; i < BufferCount; i++)
        {
            CoreAudio.QueueBuffer* buffer;
            Check(CoreAudio.AudioQueueAllocateBuffer(queue, BufferBytes, &buffer), "AudioQueueAllocateBuffer");
            Check(CoreAudio.AudioQueueEnqueueBuffer(queue, buffer, 0, 0), "AudioQueueEnqueueBuffer");
        }

        CoreAudio.AudioQueueAddPropertyListener(queue, CoreAudio.QueuePropertyIsRunning, &OnRunningChanged, _id);
        AddAliveListener();
        Check(CoreAudio.AudioQueueStart(queue, 0), "AudioQueueStart");
    }

    /// <returns>Устройство и UID для очереди (null — микрофон по умолчанию, очередь берёт его сама).</returns>
    private uint ResolveDevice(out string? uid)
    {
        uid = null;
        var requested = !string.IsNullOrWhiteSpace(_deviceId) || !string.IsNullOrWhiteSpace(_deviceName);
        if (requested)
        {
            var devices = AudioDevices.ListCaptureDevices();
            var resolution = MicrophoneSelector.Resolve(_deviceId, _deviceName,
                devices.Select(d => new CaptureEndpoint(d.Id, d.Name, true)));
            if (resolution.Endpoint is { } endpoint && AudioDevices.FindByUid(endpoint.Id) is var found and not 0)
            {
                if (resolution.Match == MicrophoneMatch.ByName)
                {
                    Log.Info("Микрофон найден по имени (UID изменился)");
                }

                uid = endpoint.Id;
                return found;
            }

            Log.Info("Выбранный микрофон не подключён: запись с микрофона macOS по умолчанию");
        }

        return AudioDevices.DefaultInput();
    }

    private unsafe void AddAliveListener()
    {
        var address = new CoreAudio.PropertyAddress(CoreAudio.PropertyDeviceIsAlive, CoreAudio.ScopeGlobal);
        _aliveListener = CoreAudio.AudioObjectAddPropertyListener(_device, &address, &OnDevicePropertyChanged, _id) == 0;
    }

    private unsafe void RemoveAliveListener()
    {
        if (!_aliveListener)
        {
            return;
        }

        _aliveListener = false;
        var address = new CoreAudio.PropertyAddress(CoreAudio.PropertyDeviceIsAlive, CoreAudio.ScopeGlobal);
        CoreAudio.AudioObjectRemovePropertyListener(_device, &address, &OnDevicePropertyChanged, _id);
    }

    private void RaiseLost(string reason)
    {
        if (_stopping || Interlocked.Exchange(ref _errorRaised, 1) != 0)
        {
            return;
        }

        Log.Info($"Микрофон: {reason}");
        _onError?.Invoke(new MicrophoneException(MicrophoneErrorKind.DeviceLost, 0, reason));
    }

    private static AudioCapture? Find(nint id)
    {
        lock (Live)
        {
            return Live.GetValueOrDefault(id);
        }
    }

    private static void Check(int status, string operation)
    {
        if (status != 0)
        {
            throw new MicrophoneException(MicrophoneErrorKind.Other, status, $"{operation}: {CoreAudio.Describe(status)}");
        }
    }

    [UnmanagedCallersOnly]
    private unsafe static void OnBuffer(nint user, nint queue, CoreAudio.QueueBuffer* buffer, nint startTime, uint packets, nint descriptions)
    {
        try
        {
            if (Find(user) is not { } self || self._stopping)
            {
                return;
            }

            if (buffer->AudioDataByteSize > 0)
            {
                self._onData(new ReadOnlySpan<byte>((void*)buffer->AudioData, (int)buffer->AudioDataByteSize));
            }

            if (!self._stopping)
            {
                CoreAudio.AudioQueueEnqueueBuffer(queue, buffer, 0, 0);
            }
        }
        catch (Exception e)
        {
            Log.Error("Буфер микрофона", e);
        }
    }

    [UnmanagedCallersOnly]
    private unsafe static void OnRunningChanged(nint user, nint queue, uint property)
    {
        try
        {
            if (Find(user) is not { } self || self._stopping)
            {
                return;
            }

            uint running = 1;
            var size = (uint)sizeof(uint);
            if (CoreAudio.AudioQueueGetProperty(queue, CoreAudio.QueuePropertyIsRunning, &running, &size) == 0 && running == 0)
            {
                self.RaiseLost("очередь записи остановилась сама");
            }
        }
        catch (Exception e)
        {
            Log.Error("Состояние очереди записи", e);
        }
    }

    [UnmanagedCallersOnly]
    private unsafe static int OnDevicePropertyChanged(uint device, uint count, CoreAudio.PropertyAddress* addresses, nint user)
    {
        try
        {
            if (Find(user) is not { } self || self._stopping)
            {
                return 0;
            }

            uint alive = 1;
            var size = (uint)sizeof(uint);
            var address = new CoreAudio.PropertyAddress(CoreAudio.PropertyDeviceIsAlive, CoreAudio.ScopeGlobal);
            if (CoreAudio.AudioObjectGetPropertyData(device, &address, 0, null, &size, &alive) != 0 || alive == 0)
            {
                self.RaiseLost("устройство отключено во время записи");
            }
        }
        catch (Exception e)
        {
            Log.Error("Состояние устройства записи", e);
        }

        return 0;
    }
}

/// <summary>Захват микрофона (для подмены в проверке микрофона окна настроек).</summary>
internal interface IAudioCapture : IDisposable
{
    Task StartAsync();

    void Stop();
}
