using System.Runtime.InteropServices;
using SaySo.Core.Diagnostics;
using SaySo.Core.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.System.Com;

namespace SaySo.Core.Audio.Wasapi;

/// <summary>
/// Захват микрофона в общем режиме WASAPI с автоконвертацией в PCM 16 кГц / 16 бит / моно.
/// Свой поток на каждую сессию; в простое ничего не держит.
/// </summary>
public sealed unsafe class WasapiCapture : IDisposable
{
    private const long BufferDuration100ns = 2_000_000; // 200 мс
    private const uint StreamFlags = 0x00040000   // AUDCLNT_STREAMFLAGS_EVENTCALLBACK
                                    | 0x80000000  // AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM
                                    | 0x08000000; // AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY
    private const uint BufferFlagSilent = 0x2;    // AUDCLNT_BUFFERFLAGS_SILENT
    private static readonly TimeSpan StopJoinTimeout = TimeSpan.FromSeconds(3);

    private readonly string? _deviceId;
    private readonly string? _deviceName;
    private readonly Action<ReadOnlySpan<byte>> _onData;
    private readonly Action<MicrophoneException>? _onError;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private byte[] _silence = new byte[AudioFormat.ChunkBytes * 2];
    private volatile bool _stopRequested;
    private Thread? _thread;
    private nint _stopEvent;

    /// <param name="deviceName">
    /// Понятное имя выбранного микрофона: если устройства с <paramref name="deviceId"/> нет (например, USB-гарнитура
    /// в другом порту получила новый идентификатор), ищется активное устройство с тем же именем.
    /// </param>
    public WasapiCapture(string? deviceId, Action<ReadOnlySpan<byte>> onData, Action<MicrophoneException>? onError = null, string? deviceName = null)
    {
        _deviceId = deviceId;
        _deviceName = deviceName;
        _onData = onData;
        _onError = onError;
    }

    public Task StartAsync()
    {
        if (_thread is not null)
        {
            throw new InvalidOperationException("Захват уже запущен.");
        }

        _stopEvent = Native.CreateEventW(null, 1, 0, null);
        if (_stopEvent == 0)
        {
            throw MicrophoneException.FromWin32(Marshal.GetLastPInvokeError(), "CreateEvent(stop)");
        }

        _thread = new Thread(Run) { IsBackground = true, Name = "SaySo.Capture", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        return _started.Task;
    }

    public void Stop()
    {
        if (_thread is null)
        {
            return;
        }

        _stopRequested = true;
        Native.SetEvent(_stopEvent);
        var thread = _thread;
        _thread = null;
        if (!thread.Join(StopJoinTimeout))
        {
            // Поток завис в драйвере (Activate/Initialize). Главный поток ждать его не должен; событие остановки не закрываем —
            // поток может ещё его ждать, а увидев _stopRequested, сам завершится и освободит свои ресурсы.
            Log.Info("Поток захвата не завершился вовремя — оставлен в фоне");
            _stopEvent = 0;
            return;
        }

        Native.CloseHandle(_stopEvent);
        _stopEvent = 0;
    }

    public void Dispose() => Stop();

    private void Run()
    {
        var com = Win32.CoInitializeEx(null, COINIT.COINIT_MULTITHREADED);
        IMMDeviceEnumerator* enumerator = null;
        IMMDevice* device = null;
        IAudioClient* client = null;
        IAudioCaptureClient* capture = null;
        nint dataEvent = 0;
        var started = false;
        try
        {
            enumerator = WasapiDevices.CreateEnumerator();
            device = OpenDevice(enumerator, _deviceId, _deviceName);
            var iidClient = IAudioClient.IID_Guid;
            WasapiDevices.Check(device->ActivateHr(&iidClient, CLSCTX.CLSCTX_ALL, (void**)&client), "IMMDevice.Activate");

            var format = new WAVEFORMATEX
            {
                wFormatTag = 1, // WAVE_FORMAT_PCM
                nChannels = AudioFormat.Channels,
                nSamplesPerSec = AudioFormat.SampleRate,
                nAvgBytesPerSec = AudioFormat.BytesPerSecond,
                nBlockAlign = AudioFormat.Channels * AudioFormat.BytesPerSample,
                wBitsPerSample = AudioFormat.BitsPerSample,
                cbSize = 0,
            };
            WasapiDevices.Check(client->InitializeHr(AUDCLNT_SHAREMODE.AUDCLNT_SHAREMODE_SHARED, StreamFlags, BufferDuration100ns, 0, &format), "IAudioClient.Initialize");

            dataEvent = Native.CreateEventW(null, 0, 0, null);
            if (dataEvent == 0)
            {
                throw MicrophoneException.FromWin32(Marshal.GetLastPInvokeError(), "CreateEvent(data)");
            }

            WasapiDevices.Check(client->SetEventHandleHr(new HANDLE(dataEvent)), "IAudioClient.SetEventHandle");
            var iidCapture = IAudioCaptureClient.IID_Guid;
            WasapiDevices.Check(client->GetServiceHr(&iidCapture, (void**)&capture), "IAudioClient.GetService");
            WasapiDevices.Check(client->StartHr(), "IAudioClient.Start");
            started = true;
            _started.TrySetResult();

            var handles = stackalloc nint[2] { dataEvent, _stopEvent };
            while (!_stopRequested)
            {
                Native.WaitForMultipleObjects(2, handles, 0, 500);
                if (_stopRequested)
                {
                    break;
                }

                Drain(capture);
            }

            client->StopHr();
        }
        catch (MicrophoneException e)
        {
            Report(e, started);
        }
        catch (Exception e)
        {
            // Необработанное исключение потока захвата уронило бы весь процесс: оборачиваем и сообщаем обычным путём
            // (в журнал — только тип, текст исключения может содержать лишнее).
            Log.Info($"Поток захвата: {e.GetType().Name}");
            Report(new MicrophoneException(MicrophoneErrorKind.Other, e.HResult, "Поток захвата: " + e.GetType().Name), started);
        }
        finally
        {
            if (capture != null)
            {
                capture->Release();
            }

            if (client != null)
            {
                client->Release();
            }

            if (device != null)
            {
                device->Release();
            }

            if (enumerator != null)
            {
                enumerator->Release();
            }

            if (dataEvent != 0)
            {
                Native.CloseHandle(dataEvent);
            }

            if (com.Succeeded)
            {
                Win32.CoUninitialize();
            }

            _started.TrySetException(new MicrophoneException(MicrophoneErrorKind.Other, 0, "Захват остановлен до запуска."));
        }
    }

    private void Report(MicrophoneException e, bool started)
    {
        if (!started)
        {
            _started.TrySetException(e);
        }
        else if (!_stopRequested)
        {
            try
            {
                _onError?.Invoke(e);
            }
            catch (Exception callbackError)
            {
                Log.Error("Обработчик ошибки захвата", callbackError); // сбой обработчика тоже не должен ронять поток
            }
        }
    }

    private void Drain(IAudioCaptureClient* capture)
    {
        while (true)
        {
            uint packet;
            WasapiDevices.Check(capture->GetNextPacketSizeHr(&packet), "GetNextPacketSize");
            if (packet == 0)
            {
                return;
            }

            byte* data;
            uint frames;
            uint flags;
            WasapiDevices.Check(capture->GetBufferHr(&data, &frames, &flags), "GetBuffer");
            var bytes = (int)frames * AudioFormat.BytesPerSample;
            if ((flags & BufferFlagSilent) != 0)
            {
                if (_silence.Length < bytes)
                {
                    _silence = new byte[bytes];
                }

                _onData(_silence.AsSpan(0, bytes));
            }
            else
            {
                _onData(new ReadOnlySpan<byte>(data, bytes));
            }

            WasapiDevices.Check(capture->ReleaseBufferHr(frames), "ReleaseBuffer");
        }
    }

    private static IMMDevice* OpenDevice(IMMDeviceEnumerator* enumerator, string? deviceId, string? deviceName)
    {
        IMMDevice* device = null;
        if (!string.IsNullOrEmpty(deviceId))
        {
            device = OpenActiveById(enumerator, deviceId);
            if (device != null)
            {
                return device;
            }
        }

        if (!string.IsNullOrEmpty(deviceId) || !string.IsNullOrWhiteSpace(deviceName))
        {
            // Выбранного по идентификатору устройства нет: ищем активное с тем же именем (другой порт USB).
            device = OpenByName(enumerator, deviceId, deviceName);
            if (device != null)
            {
                return device;
            }

            // Имя устройства в журнал не пишем: гарнитуры часто названы по владельцу, а журнал прикладывают к обращениям.
            Log.Info("Выбранный микрофон недоступен — используется микрофон по умолчанию");
        }

        WasapiDevices.Check(enumerator->GetDefaultAudioEndpointHr(EDataFlow.eCapture, ERole.eConsole, &device), "GetDefaultAudioEndpoint");
        return device;
    }

    private static IMMDevice* OpenActiveById(IMMDeviceEnumerator* enumerator, string deviceId)
    {
        IMMDevice* device = null;
        fixed (char* id = deviceId)
        {
            if (enumerator->GetDeviceHr(id, &device).Failed || device == null)
            {
                return null;
            }
        }

        DEVICE_STATE state;
        if (device->GetStateHr(&state).Succeeded && state == DEVICE_STATE.DEVICE_STATE_ACTIVE)
        {
            return device;
        }

        device->Release();
        return null;
    }

    private static IMMDevice* OpenByName(IMMDeviceEnumerator* enumerator, string? deviceId, string? deviceName)
    {
        try
        {
            var endpoints = WasapiDevices.ListActive(enumerator)
                .Select(d => new CaptureEndpoint(d.Id, d.Name, true));
            var resolution = MicrophoneSelector.Resolve(deviceId, deviceName, endpoints);
            if (resolution.Endpoint is not { } endpoint)
            {
                return null;
            }

            var found = OpenActiveById(enumerator, endpoint.Id);
            if (found != null && resolution.Match == MicrophoneMatch.ByName)
            {
                Log.Info("Выбранный микрофон найден по имени (идентификатор изменился)");
            }

            return found;
        }
        catch (MicrophoneException e)
        {
            Log.Error("Поиск микрофона по имени", e);
            return null;
        }
    }
}
