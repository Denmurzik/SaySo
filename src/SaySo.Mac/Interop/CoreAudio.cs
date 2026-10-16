using System.Runtime.InteropServices;

namespace SaySo.Mac.Interop;

/// <summary>Core Audio HAL (устройства) и AudioToolbox (AudioQueue для записи).</summary>
internal static unsafe partial class CoreAudio
{
    public const string CoreAudioLib = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
    public const string AudioToolboxLib = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";

    public const uint SystemObject = 1;
    public const uint PropertyDevices = 0x64657623;            // 'dev#'
    public const uint PropertyDefaultInputDevice = 0x64496E20; // 'dIn '
    public const uint PropertyDeviceUid = 0x75696420;          // 'uid '
    public const uint PropertyName = 0x6C6E616D;               // 'lnam'
    public const uint PropertyStreams = 0x73746D23;            // 'stm#'
    public const uint PropertyDeviceIsAlive = 0x6C69766E;      // 'livn'
    public const uint ScopeGlobal = 0x676C6F62;                // 'glob'
    public const uint ScopeInput = 0x696E7074;                 // 'inpt'
    public const uint ElementMain = 0;

    public const uint FormatLinearPcm = 0x6C70636D;            // 'lpcm'
    public const uint FormatFlagsSignedPacked = 0x4 | 0x8;     // kAudioFormatFlagIsSignedInteger | kAudioFormatFlagIsPacked
    public const uint QueuePropertyCurrentDevice = 0x61716364; // 'aqcd'
    public const uint QueuePropertyIsRunning = 0x6171726E;     // 'aqrn'

    [StructLayout(LayoutKind.Sequential)]
    public struct PropertyAddress(uint selector, uint scope, uint element = ElementMain)
    {
        public uint Selector = selector;
        public uint Scope = scope;
        public uint Element = element;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct StreamDescription
    {
        public double SampleRate;
        public uint FormatId;
        public uint FormatFlags;
        public uint BytesPerPacket;
        public uint FramesPerPacket;
        public uint BytesPerFrame;
        public uint ChannelsPerFrame;
        public uint BitsPerChannel;
        public uint Reserved;
    }

    /// <summary>AudioQueueBuffer (поля до mUserData — остальные не нужны).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct QueueBuffer
    {
        public uint AudioDataBytesCapacity;
        public nint AudioData;
        public uint AudioDataByteSize;
        public nint UserData;
    }

    [LibraryImport(CoreAudioLib)]
    public static partial int AudioObjectGetPropertyDataSize(uint objectId, PropertyAddress* address, uint qualifierSize, void* qualifier, uint* size);

    [LibraryImport(CoreAudioLib)]
    public static partial int AudioObjectGetPropertyData(uint objectId, PropertyAddress* address, uint qualifierSize, void* qualifier, uint* size, void* data);

    [LibraryImport(CoreAudioLib)]
    public static partial int AudioObjectAddPropertyListener(uint objectId, PropertyAddress* address,
        delegate* unmanaged<uint, uint, PropertyAddress*, nint, int> listener, nint clientData);

    [LibraryImport(CoreAudioLib)]
    public static partial int AudioObjectRemovePropertyListener(uint objectId, PropertyAddress* address,
        delegate* unmanaged<uint, uint, PropertyAddress*, nint, int> listener, nint clientData);

    [LibraryImport(AudioToolboxLib)]
    public static partial int AudioQueueNewInput(StreamDescription* format,
        delegate* unmanaged<nint, nint, QueueBuffer*, nint, uint, nint, void> callback, nint userData,
        nint callbackRunLoop, nint callbackRunLoopMode, uint flags, nint* queue);

    [LibraryImport(AudioToolboxLib)]
    public static partial int AudioQueueAllocateBuffer(nint queue, uint size, QueueBuffer** buffer);

    [LibraryImport(AudioToolboxLib)]
    public static partial int AudioQueueEnqueueBuffer(nint queue, QueueBuffer* buffer, uint packetDescriptions, nint descriptions);

    [LibraryImport(AudioToolboxLib)]
    public static partial int AudioQueueStart(nint queue, nint startTime);

    [LibraryImport(AudioToolboxLib)]
    public static partial int AudioQueueStop(nint queue, byte immediate);

    [LibraryImport(AudioToolboxLib)]
    public static partial int AudioQueueDispose(nint queue, byte immediate);

    [LibraryImport(AudioToolboxLib)]
    public static partial int AudioQueueSetProperty(nint queue, uint id, void* data, uint size);

    [LibraryImport(AudioToolboxLib)]
    public static partial int AudioQueueGetProperty(nint queue, uint id, void* data, uint* size);

    [LibraryImport(AudioToolboxLib)]
    public static partial int AudioQueueAddPropertyListener(nint queue, uint id,
        delegate* unmanaged<nint, nint, uint, void> listener, nint userData);

    /// <summary>Строка CoreFoundation в .NET (без освобождения).</summary>
    public static string? CFString(nint cfString) => cfString == 0 ? null : ObjC.ToStr(cfString); // CFString и NSString — один тип

    /// <summary>Код OSStatus часто — четыре символа ('!dev', 'what').</summary>
    public static string Describe(int status)
    {
        var bytes = BitConverter.GetBytes(status);
        Array.Reverse(bytes);
        return bytes.All(b => b is >= 0x20 and < 0x7F)
            ? $"'{System.Text.Encoding.ASCII.GetString(bytes)}' ({status})"
            : status.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
