using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace SaySo.Core.Storage;

/// <summary>
/// macOS: AES-256-GCM с ключом, который хранится в Связке ключей пользователя (generic password «SaySo»). Ключ создаётся
/// при первой записи; читать без ключа нельзя (нет ключа — данные не расшифровать, это повреждение). Связка ключей
/// пускает к записи только создавшую её программу: после обновления SaySo macOS один раз спросит разрешение.
/// Формат: «SSK1», nonce (12 байт), тег (16 байт), шифротекст; дополнительные данные AEAD — «SaySo.v1», как энтропия DPAPI.
/// </summary>
public static unsafe partial class KeychainProtector
{
    public const string Service = "SaySo";
    public const string Account = "data-key";

    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int ErrSecSuccess = 0;
    private const int ErrSecDuplicateItem = -25299;
    private const int ErrSecItemNotFound = -25300;
    private const uint Utf8Encoding = 0x08000100; // kCFStringEncodingUTF8

    private const string SecurityLib = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundationLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private static readonly object Gate = new();
    private static byte[]? _key;

    /// <summary>Для модульных тестов: ключ в памяти вместо Связки ключей (тесты не должны трогать связку пользователя).</summary>
    internal static byte[]? KeyOverride { get; set; }

    private static ReadOnlySpan<byte> Magic => "SSK1"u8;

    private static ReadOnlySpan<byte> AssociatedData => "SaySo.v1"u8;

    public static byte[] Protect(ReadOnlySpan<byte> plain)
    {
        var key = GetKey(create: true)!;
        var result = new byte[Magic.Length + NonceSize + TagSize + plain.Length];
        Magic.CopyTo(result);
        var nonce = result.AsSpan(Magic.Length, NonceSize);
        var tag = result.AsSpan(Magic.Length + NonceSize, TagSize);
        var cipher = result.AsSpan(Magic.Length + NonceSize + TagSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plain, cipher, tag, AssociatedData);
        return result;
    }

    public static byte[] Unprotect(ReadOnlySpan<byte> data)
    {
        if (data.Length < Magic.Length + NonceSize + TagSize || !data[..Magic.Length].SequenceEqual(Magic))
        {
            throw new CryptographicException("Файл не в формате SaySo для macOS.");
        }

        var key = GetKey(create: false) ?? throw new CryptographicException("Ключа шифрования SaySo нет в Связке ключей.");
        var nonce = data.Slice(Magic.Length, NonceSize);
        var tag = data.Slice(Magic.Length + NonceSize, TagSize);
        var cipher = data[(Magic.Length + NonceSize + TagSize)..];
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain, AssociatedData); // неверный тег — AuthenticationTagMismatchException (CryptographicException)
        return plain;
    }

    /// <returns>Ключ; null — его нет, а <paramref name="create"/> == false.</returns>
    private static byte[]? GetKey(bool create)
    {
        if (KeyOverride is { } overridden)
        {
            return overridden;
        }

        lock (Gate)
        {
            if (_key is not null)
            {
                return _key;
            }

            var status = Read(out var key);
            if (status == ErrSecItemNotFound && create)
            {
                var fresh = RandomNumberGenerator.GetBytes(KeySize);
                status = Add(fresh);
                if (status == ErrSecSuccess)
                {
                    key = fresh;
                }
                else if (status == ErrSecDuplicateItem)
                {
                    status = Read(out key); // ключ только что создал другой процесс SaySo (окно настроек или демон)
                }
            }

            if (status == ErrSecItemNotFound)
            {
                return null;
            }

            if (status != ErrSecSuccess || key is not { Length: KeySize })
            {
                throw new DataProtectionUnavailableException($"Связка ключей не отдала ключ SaySo (OSStatus {status}).");
            }

            _key = key;
            return key;
        }
    }

    private static int Read(out byte[]? key)
    {
        key = null;
        var api = Api.Instance;
        var query = api.Query(returnData: true);
        try
        {
            nint result = 0;
            var status = SecItemCopyMatching(query, &result);
            if (status == ErrSecSuccess && result != 0)
            {
                try
                {
                    var length = (int)CFDataGetLength(result);
                    key = new ReadOnlySpan<byte>(CFDataGetBytePtr(result), length).ToArray();
                }
                finally
                {
                    CFRelease(result);
                }
            }

            return status;
        }
        finally
        {
            CFRelease(query);
        }
    }

    private static int Add(byte[] key)
    {
        var api = Api.Instance;
        nint data;
        fixed (byte* p = key)
        {
            data = CFDataCreate(0, p, key.Length);
        }

        var label = api.String("SaySo");
        var attributes = api.Dictionary(
            (api.Class, api.ClassGenericPassword),
            (api.AttrService, api.ServiceName),
            (api.AttrAccount, api.AccountName),
            (api.AttrLabel, label),
            (api.ValueData, data));
        try
        {
            return SecItemAdd(attributes, null);
        }
        finally
        {
            CFRelease(attributes);
            CFRelease(label);
            CFRelease(data);
        }
    }

    [LibraryImport(SecurityLib)]
    private static partial int SecItemCopyMatching(nint query, nint* result);

    [LibraryImport(SecurityLib)]
    private static partial int SecItemAdd(nint attributes, nint* result);

    [LibraryImport(CoreFoundationLib)]
    private static partial nint CFDataCreate(nint allocator, byte* bytes, nint length);

    [LibraryImport(CoreFoundationLib)]
    private static partial byte* CFDataGetBytePtr(nint data);

    [LibraryImport(CoreFoundationLib)]
    private static partial nint CFDataGetLength(nint data);

    [LibraryImport(CoreFoundationLib)]
    private static partial nint CFStringCreateWithCString(nint allocator, byte* text, uint encoding);

    [LibraryImport(CoreFoundationLib)]
    private static partial nint CFDictionaryCreate(nint allocator, nint* keys, nint* values, nint count, nint keyCallBacks, nint valueCallBacks);

    [LibraryImport(CoreFoundationLib)]
    private static partial void CFRelease(nint value);

    /// <summary>Константы Security.framework и CoreFoundation (экспортированные переменные: читаем значения по адресам).</summary>
    private sealed class Api
    {
        private static Api? _instance;

        private readonly nint _keyCallBacks;
        private readonly nint _valueCallBacks;

        private Api()
        {
            var security = NativeLibrary.Load(SecurityLib);
            var cf = NativeLibrary.Load(CoreFoundationLib);
            Class = Value(security, "kSecClass");
            ClassGenericPassword = Value(security, "kSecClassGenericPassword");
            AttrService = Value(security, "kSecAttrService");
            AttrAccount = Value(security, "kSecAttrAccount");
            AttrLabel = Value(security, "kSecAttrLabel");
            ValueData = Value(security, "kSecValueData");
            ReturnData = Value(security, "kSecReturnData");
            MatchLimit = Value(security, "kSecMatchLimit");
            MatchLimitOne = Value(security, "kSecMatchLimitOne");
            True = Value(cf, "kCFBooleanTrue");
            _keyCallBacks = NativeLibrary.GetExport(cf, "kCFTypeDictionaryKeyCallBacks");
            _valueCallBacks = NativeLibrary.GetExport(cf, "kCFTypeDictionaryValueCallBacks");
            ServiceName = String(Service); // живут до конца процесса
            AccountName = String(Account);
        }

        public static Api Instance => _instance ??= new Api();

        public nint Class { get; }

        public nint ClassGenericPassword { get; }

        public nint AttrService { get; }

        public nint AttrAccount { get; }

        public nint AttrLabel { get; }

        public nint ValueData { get; }

        public nint ReturnData { get; }

        public nint MatchLimit { get; }

        public nint MatchLimitOne { get; }

        public nint True { get; }

        public nint ServiceName { get; }

        public nint AccountName { get; }

        public nint Query(bool returnData) => returnData
            ? Dictionary((Class, ClassGenericPassword), (AttrService, ServiceName), (AttrAccount, AccountName),
                (ReturnData, True), (MatchLimit, MatchLimitOne))
            : Dictionary((Class, ClassGenericPassword), (AttrService, ServiceName), (AttrAccount, AccountName));

        public nint String(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text + "\0");
            fixed (byte* p = bytes)
            {
                return CFStringCreateWithCString(0, p, Utf8Encoding);
            }
        }

        public nint Dictionary(params ReadOnlySpan<(nint Key, nint Value)> pairs)
        {
            var keys = stackalloc nint[pairs.Length];
            var values = stackalloc nint[pairs.Length];
            for (var i = 0; i < pairs.Length; i++)
            {
                keys[i] = pairs[i].Key;
                values[i] = pairs[i].Value;
            }

            return CFDictionaryCreate(0, keys, values, pairs.Length, _keyCallBacks, _valueCallBacks);
        }

        private static nint Value(nint library, string name) => *(nint*)NativeLibrary.GetExport(library, name);
    }
}
