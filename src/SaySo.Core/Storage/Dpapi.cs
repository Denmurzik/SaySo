using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace SaySo.Core.Storage;

/// <summary>Шифрование DPAPI в области текущего пользователя Windows.</summary>
public static unsafe partial class Dpapi
{
    private const uint CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    // Дополнительная энтропия привязывает данные к SaySo (другие программы того же пользователя
    // не расшифруют их «случайно» без этого значения).
    private static ReadOnlySpan<byte> Entropy => "SaySo.v1"u8;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public uint cbData;
        public byte* pbData;
    }

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(DataBlob* pDataIn, char* szDataDescr, DataBlob* pOptionalEntropy,
        void* pvReserved, void* pPromptStruct, uint dwFlags, DataBlob* pDataOut);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(DataBlob* pDataIn, char** ppszDataDescr, DataBlob* pOptionalEntropy,
        void* pvReserved, void* pPromptStruct, uint dwFlags, DataBlob* pDataOut);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint hMem);

    public static byte[] Protect(ReadOnlySpan<byte> plain)
    {
        // У пустого span нет адреса — подставляем однобайтовый буфер с длиной 0.
        byte dummy = 0;
        fixed (byte* pPlainFixed = plain)
        fixed (byte* pEntropy = Entropy)
        {
            var pPlain = plain.IsEmpty ? &dummy : pPlainFixed;
            var input = new DataBlob { cbData = (uint)plain.Length, pbData = pPlain };
            var entropy = new DataBlob { cbData = (uint)Entropy.Length, pbData = pEntropy };
            DataBlob output = default;
            if (!CryptProtectData(&input, null, &entropy, null, null, CRYPTPROTECT_UI_FORBIDDEN, &output))
            {
                throw new CryptographicException(Marshal.GetLastPInvokeError());
            }

            try
            {
                return new ReadOnlySpan<byte>(output.pbData, (int)output.cbData).ToArray();
            }
            finally
            {
                LocalFree((nint)output.pbData);
            }
        }
    }

    public static byte[] Unprotect(ReadOnlySpan<byte> cipher)
    {
        if (cipher.IsEmpty)
        {
            throw new CryptographicException("Пустые зашифрованные данные.");
        }

        fixed (byte* pCipher = cipher)
        fixed (byte* pEntropy = Entropy)
        {
            var input = new DataBlob { cbData = (uint)cipher.Length, pbData = pCipher };
            var entropy = new DataBlob { cbData = (uint)Entropy.Length, pbData = pEntropy };
            DataBlob output = default;
            if (!CryptUnprotectData(&input, null, &entropy, null, null, CRYPTPROTECT_UI_FORBIDDEN, &output))
            {
                throw new CryptographicException(Marshal.GetLastPInvokeError());
            }

            try
            {
                var result = new ReadOnlySpan<byte>(output.pbData, (int)output.cbData).ToArray();
                new Span<byte>(output.pbData, (int)output.cbData).Clear();
                return result;
            }
            finally
            {
                LocalFree((nint)output.pbData);
            }
        }
    }
}
