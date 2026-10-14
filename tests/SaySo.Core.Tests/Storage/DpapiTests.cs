using System.Security.Cryptography;
using SaySo.Core.Storage;

namespace SaySo.Core.Tests.Storage;

public class DpapiTests
{
    [Fact]
    public void ProtectThenUnprotect_ReturnsOriginal()
    {
        var plain = "секретный ключ AIza-123"u8.ToArray();
        var cipher = Dpapi.Protect(plain);
        Assert.NotEqual(plain, cipher);
        Assert.Equal(plain, Dpapi.Unprotect(cipher));
    }

    [Fact]
    public void ProtectThenUnprotect_EmptyInput_RoundTrips()
    {
        var cipher = Dpapi.Protect(ReadOnlySpan<byte>.Empty);
        Assert.Empty(Dpapi.Unprotect(cipher));
    }

    [Fact]
    public void Unprotect_TamperedData_ThrowsCryptographicException()
    {
        var cipher = Dpapi.Protect("данные"u8);
        cipher[^1] ^= 0xFF;
        Assert.Throws<CryptographicException>(() => Dpapi.Unprotect(cipher));
    }
}
