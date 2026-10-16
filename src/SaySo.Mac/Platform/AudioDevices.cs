using SaySo.Core.Audio.Wasapi;
using SaySo.Mac.Interop;

namespace SaySo.Mac.Platform;

/// <summary>Устройства записи Core Audio: идентификатор — UID устройства (постоянный между подключениями), имя — как в macOS.</summary>
internal static unsafe class AudioDevices
{
    /// <summary>Устройства, у которых есть входные потоки.</summary>
    public static IReadOnlyList<AudioDevice> ListCaptureDevices()
    {
        var result = new List<AudioDevice>();
        foreach (var id in AllDeviceIds())
        {
            if (!HasInput(id) || Uid(id) is not { } uid)
            {
                continue;
            }

            result.Add(new AudioDevice(uid, Name(id) ?? MicrophoneSelector.UnknownDeviceName));
        }

        return result;
    }

    /// <summary>Идентификатор устройства по UID; 0 — не подключено.</summary>
    public static uint FindByUid(string uid)
    {
        foreach (var id in AllDeviceIds())
        {
            if (HasInput(id) && Uid(id) == uid)
            {
                return id;
            }
        }

        return 0;
    }

    /// <summary>Микрофон macOS по умолчанию; 0 — его нет.</summary>
    public static uint DefaultInput()
    {
        var address = new CoreAudio.PropertyAddress(CoreAudio.PropertyDefaultInputDevice, CoreAudio.ScopeGlobal);
        uint id = 0;
        var size = (uint)sizeof(uint);
        return CoreAudio.AudioObjectGetPropertyData(CoreAudio.SystemObject, &address, 0, null, &size, &id) == 0 ? id : 0;
    }

    public static string? Uid(uint id) => StringProperty(id, CoreAudio.PropertyDeviceUid);

    public static string? Name(uint id) => StringProperty(id, CoreAudio.PropertyName);

    private static uint[] AllDeviceIds()
    {
        var address = new CoreAudio.PropertyAddress(CoreAudio.PropertyDevices, CoreAudio.ScopeGlobal);
        uint size = 0;
        if (CoreAudio.AudioObjectGetPropertyDataSize(CoreAudio.SystemObject, &address, 0, null, &size) != 0 || size == 0)
        {
            return [];
        }

        var ids = new uint[size / sizeof(uint)];
        fixed (uint* p = ids)
        {
            if (CoreAudio.AudioObjectGetPropertyData(CoreAudio.SystemObject, &address, 0, null, &size, p) != 0)
            {
                return [];
            }
        }

        return ids[..(int)(size / sizeof(uint))];
    }

    private static bool HasInput(uint id)
    {
        var address = new CoreAudio.PropertyAddress(CoreAudio.PropertyStreams, CoreAudio.ScopeInput);
        uint size = 0;
        return CoreAudio.AudioObjectGetPropertyDataSize(id, &address, 0, null, &size) == 0 && size > 0;
    }

    private static string? StringProperty(uint id, uint selector)
    {
        var address = new CoreAudio.PropertyAddress(selector, CoreAudio.ScopeGlobal);
        nint value = 0;
        var size = (uint)sizeof(nint);
        if (CoreAudio.AudioObjectGetPropertyData(id, &address, 0, null, &size, &value) != 0 || value == 0)
        {
            return null;
        }

        try
        {
            var text = CoreAudio.CFString(value)?.Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }
        finally
        {
            Quartz.CFRelease(value); // Get-правило CoreAudio для CFString: владелец — вызывающий
        }
    }
}
