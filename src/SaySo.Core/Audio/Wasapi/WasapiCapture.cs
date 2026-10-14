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

    private readonly string? _deviceId;
    private readonly Action<ReadOnlySpan<byte>> _onData;
    private readonly Action<MicrophoneException>? _onError;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private byte[] _silence = new byte[AudioFormat.ChunkBytes * 2];
    private volatile bool _stopRequested;
    private Thread? _thread;
    private nint _stopEvent;

    public WasapiCapture(string? deviceId, Action<ReadOnlySpan<byte>> onData, Action<MicrophoneException>? onError = null)
    {
        _deviceId = deviceId;
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
        _thread.Join();
        _thread = null;
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
            device = OpenDevice(enumerator, _deviceId);
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
            if (!started)
            {
                _started.TrySetException(e);
            }
            else if (!_stopRequested)
            {
                _onError?.Invoke(e);
            }
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

    private static IMMDevice* OpenDevice(IMMDeviceEnumerator* enumerator, string? deviceId)
    {
        IMMDevice* device = null;
        if (!string.IsNullOrEmpty(deviceId))
        {
            fixed (char* id = deviceId)
            {
                if (enumerator->GetDeviceHr(id, &device).Succeeded && device != null)
                {
                    DEVICE_STATE state;
                    if (device->GetStateHr(&state).Succeeded && state == DEVICE_STATE.DEVICE_STATE_ACTIVE)
                    {
                        return device;
                    }

                    device->Release();
                    device = null;
                }
            }
            // Выбранного устройства нет — работаем с микрофоном по умолчанию.
        }

        WasapiDevices.Check(enumerator->GetDefaultAudioEndpointHr(EDataFlow.eCapture, ERole.eConsole, &device), "GetDefaultAudioEndpoint");
        return device;
    }
}
