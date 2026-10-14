using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.System.Com;
using Windows.Win32.System.Com.StructuredStorage;
using Windows.Win32.System.Variant;
using Windows.Win32.UI.Shell.PropertiesSystem;

namespace SaySo.Core.Audio.Wasapi;

public sealed record AudioDevice(string Id, string Name);

public static unsafe class WasapiDevices
{
    internal static readonly Guid MMDeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    /// <summary>Активные устройства записи с понятными именами.</summary>
    public static IReadOnlyList<AudioDevice> ListCaptureDevices() => ComThread.Run(() =>
    {
        var enumerator = CreateEnumerator();
        try
        {
            return (IReadOnlyList<AudioDevice>)ListActive(enumerator);
        }
        finally
        {
            enumerator->Release();
        }
    });

    /// <summary>Активные устройства записи; вызывается из потока с уже инициализированным COM.</summary>
    internal static List<AudioDevice> ListActive(IMMDeviceEnumerator* enumerator)
    {
        var result = new List<AudioDevice>();
        IMMDeviceCollection* collection = null;
        try
        {
            Check(enumerator->EnumAudioEndpointsHr(EDataFlow.eCapture, DEVICE_STATE.DEVICE_STATE_ACTIVE, &collection), "EnumAudioEndpoints");
            uint count;
            Check(collection->GetCountHr(&count), "GetCount");
            for (uint i = 0; i < count; i++)
            {
                IMMDevice* device = null;
                if (collection->ItemHr(i, &device).Failed)
                {
                    continue;
                }

                try
                {
                    result.Add(new AudioDevice(GetId(device), GetFriendlyName(device)));
                }
                finally
                {
                    device->Release();
                }
            }
        }
        finally
        {
            if (collection != null)
            {
                collection->Release();
            }
        }

        return result;
    }

    internal static IMMDeviceEnumerator* CreateEnumerator()
    {
        var clsid = MMDeviceEnumeratorClsid;
        var iid = IMMDeviceEnumerator.IID_Guid;
        IMMDeviceEnumerator* enumerator;
        Check(Win32.CoCreateInstance(&clsid, null, CLSCTX.CLSCTX_ALL, &iid, (void**)&enumerator), "CoCreateInstance(MMDeviceEnumerator)");
        return enumerator;
    }

    internal static void Check(HRESULT hr, string operation)
    {
        if (hr.Failed)
        {
            throw MicrophoneException.FromHResult(hr.Value, operation);
        }
    }

    internal static string GetId(IMMDevice* device)
    {
        PWSTR id;
        Check(device->GetIdHr(&id), "GetId");
        try
        {
            return id.ToString();
        }
        finally
        {
            Win32.CoTaskMemFree(id.Value);
        }
    }

    private static string GetFriendlyName(IMMDevice* device)
    {
        IPropertyStore* store = null;
        if (device->OpenPropertyStoreHr(STGM.STGM_READ, &store).Failed)
        {
            return "Микрофон";
        }

        try
        {
            var key = Win32.PKEY_Device_FriendlyName;
            PROPVARIANT value;
            if (store->GetValueHr(&key, &value).Failed)
            {
                return "Микрофон";
            }

            try
            {
                return value.Anonymous.Anonymous.vt == VARENUM.VT_LPWSTR
                    ? value.Anonymous.Anonymous.Anonymous.pwszVal.ToString()
                    : "Микрофон";
            }
            finally
            {
                Win32.PropVariantClear(&value);
            }
        }
        finally
        {
            store->Release();
        }
    }
}
