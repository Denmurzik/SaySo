using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using SaySo.Core.Storage;

namespace SaySo.Core.Tests;

/// <summary>На macOS тесты хранилищ шифруют ключом в памяти: Связку ключей пользователя (и CI) они не трогают.</summary>
internal static class TestKeychain
{
    [ModuleInitializer]
    internal static void UseInMemoryKey()
    {
        if (OperatingSystem.IsMacOS())
        {
            KeychainProtector.KeyOverride = RandomNumberGenerator.GetBytes(32);
        }
    }
}
