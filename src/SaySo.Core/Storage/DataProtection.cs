using System.Security.Cryptography;

namespace SaySo.Core.Storage;

/// <summary>
/// Шифрование файлов данных (ключи API, история) в области текущего пользователя: на Windows DPAPI, на macOS AES-256-GCM
/// с ключом из Связки ключей пользователя (<see cref="KeychainProtector"/>).
/// </summary>
public static class DataProtection
{
    public static byte[] Protect(ReadOnlySpan<byte> plain)
    {
#if WINDOWS
        return Dpapi.Protect(plain);
#else
        return OperatingSystem.IsMacOS()
            ? KeychainProtector.Protect(plain)
            : throw new PlatformNotSupportedException("Шифрование данных SaySo есть только на Windows и macOS.");
#endif
    }

    public static byte[] Unprotect(ReadOnlySpan<byte> cipher)
    {
#if WINDOWS
        return Dpapi.Unprotect(cipher);
#else
        return OperatingSystem.IsMacOS()
            ? KeychainProtector.Unprotect(cipher)
            : throw new PlatformNotSupportedException("Шифрование данных SaySo есть только на Windows и macOS.");
#endif
    }
}

/// <summary>
/// Ключ шифрования сейчас недоступен (macOS: пользователь отказал в доступе к Связке ключей, она заблокирована). Файл при
/// этом не повреждён: в карантин его не переносят, а просто не читают в этот раз.
/// </summary>
public sealed class DataProtectionUnavailableException(string message) : CryptographicException(message);
