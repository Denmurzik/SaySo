using System.Runtime.CompilerServices;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.System.Com;
using Windows.Win32.System.Com.StructuredStorage;
using Windows.Win32.UI.Shell.PropertiesSystem;

namespace SaySo.Core.Audio.Wasapi;

/// <summary>
/// Вызовы COM-методов с возвратом HRESULT. Публичные методы, которые генерирует CsWin32 для этих интерфейсов,
/// возвращают void и бросают COMException (через Marshal) — здесь нужен «сырой» код ошибки, поэтому вызываем
/// функции из таблицы виртуальных методов напрямую (структура Vtbl — внутренняя для сборки).
/// </summary>
internal static unsafe class ComCalls
{
    public static HRESULT EnumAudioEndpointsHr(this ref IMMDeviceEnumerator self, EDataFlow dataFlow, DEVICE_STATE mask, IMMDeviceCollection** devices)
    {
        var p = (IMMDeviceEnumerator*)Unsafe.AsPointer(ref self);
        return (*(IMMDeviceEnumerator.Vtbl**)p)->EnumAudioEndpoints_4(p, dataFlow, mask, devices);
    }

    public static HRESULT GetDefaultAudioEndpointHr(this ref IMMDeviceEnumerator self, EDataFlow dataFlow, ERole role, IMMDevice** device)
    {
        var p = (IMMDeviceEnumerator*)Unsafe.AsPointer(ref self);
        return (*(IMMDeviceEnumerator.Vtbl**)p)->GetDefaultAudioEndpoint_5(p, dataFlow, role, device);
    }

    public static HRESULT GetDeviceHr(this ref IMMDeviceEnumerator self, char* id, IMMDevice** device)
    {
        var p = (IMMDeviceEnumerator*)Unsafe.AsPointer(ref self);
        return (*(IMMDeviceEnumerator.Vtbl**)p)->GetDevice_6(p, id, device);
    }

    public static HRESULT GetCountHr(this ref IMMDeviceCollection self, uint* count)
    {
        var p = (IMMDeviceCollection*)Unsafe.AsPointer(ref self);
        return (*(IMMDeviceCollection.Vtbl**)p)->GetCount_4(p, count);
    }

    public static HRESULT ItemHr(this ref IMMDeviceCollection self, uint index, IMMDevice** device)
    {
        var p = (IMMDeviceCollection*)Unsafe.AsPointer(ref self);
        return (*(IMMDeviceCollection.Vtbl**)p)->Item_5(p, index, device);
    }

    public static HRESULT ActivateHr(this ref IMMDevice self, Guid* iid, CLSCTX context, void** result)
    {
        var p = (IMMDevice*)Unsafe.AsPointer(ref self);
        return (*(IMMDevice.Vtbl**)p)->Activate_4(p, iid, context, null, result);
    }

    public static HRESULT OpenPropertyStoreHr(this ref IMMDevice self, STGM access, IPropertyStore** store)
    {
        var p = (IMMDevice*)Unsafe.AsPointer(ref self);
        return (*(IMMDevice.Vtbl**)p)->OpenPropertyStore_5(p, access, store);
    }

    public static HRESULT GetIdHr(this ref IMMDevice self, PWSTR* id)
    {
        var p = (IMMDevice*)Unsafe.AsPointer(ref self);
        return (*(IMMDevice.Vtbl**)p)->GetId_6(p, id);
    }

    public static HRESULT GetStateHr(this ref IMMDevice self, DEVICE_STATE* state)
    {
        var p = (IMMDevice*)Unsafe.AsPointer(ref self);
        return (*(IMMDevice.Vtbl**)p)->GetState_7(p, state);
    }

    public static HRESULT GetValueHr(this ref IPropertyStore self, PROPERTYKEY* key, PROPVARIANT* value)
    {
        var p = (IPropertyStore*)Unsafe.AsPointer(ref self);
        return (*(IPropertyStore.Vtbl**)p)->GetValue_6(p, key, value);
    }

    public static HRESULT InitializeHr(this ref IAudioClient self, AUDCLNT_SHAREMODE mode, uint flags, long bufferDuration, long periodicity, WAVEFORMATEX* format)
    {
        var p = (IAudioClient*)Unsafe.AsPointer(ref self);
        return (*(IAudioClient.Vtbl**)p)->Initialize_4(p, mode, flags, bufferDuration, periodicity, format, null);
    }

    public static HRESULT SetEventHandleHr(this ref IAudioClient self, HANDLE handle)
    {
        var p = (IAudioClient*)Unsafe.AsPointer(ref self);
        return (*(IAudioClient.Vtbl**)p)->SetEventHandle_14(p, handle);
    }

    public static HRESULT GetServiceHr(this ref IAudioClient self, Guid* iid, void** service)
    {
        var p = (IAudioClient*)Unsafe.AsPointer(ref self);
        return (*(IAudioClient.Vtbl**)p)->GetService_15(p, iid, service);
    }

    public static HRESULT StartHr(this ref IAudioClient self)
    {
        var p = (IAudioClient*)Unsafe.AsPointer(ref self);
        return (*(IAudioClient.Vtbl**)p)->Start_11(p);
    }

    public static HRESULT StopHr(this ref IAudioClient self)
    {
        var p = (IAudioClient*)Unsafe.AsPointer(ref self);
        return (*(IAudioClient.Vtbl**)p)->Stop_12(p);
    }

    public static HRESULT GetNextPacketSizeHr(this ref IAudioCaptureClient self, uint* size)
    {
        var p = (IAudioCaptureClient*)Unsafe.AsPointer(ref self);
        return (*(IAudioCaptureClient.Vtbl**)p)->GetNextPacketSize_6(p, size);
    }

    public static HRESULT GetBufferHr(this ref IAudioCaptureClient self, byte** data, uint* frames, uint* flags)
    {
        var p = (IAudioCaptureClient*)Unsafe.AsPointer(ref self);
        return (*(IAudioCaptureClient.Vtbl**)p)->GetBuffer_4(p, data, frames, flags, null, null);
    }

    public static HRESULT ReleaseBufferHr(this ref IAudioCaptureClient self, uint frames)
    {
        var p = (IAudioCaptureClient*)Unsafe.AsPointer(ref self);
        return (*(IAudioCaptureClient.Vtbl**)p)->ReleaseBuffer_5(p, frames);
    }
}
