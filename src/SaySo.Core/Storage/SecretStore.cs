using System.Security.Cryptography;
using System.Text.Json;
using SaySo.Core.Json;

namespace SaySo.Core.Storage;

/// <summary>Ключи API (secrets.bin, зашифровано DPAPI).</summary>
public sealed class Secrets
{
    /// <summary>Ключи Gemini в порядке приоритета; при исчерпании квоты SaySo переключается на следующий.</summary>
    public List<string> GeminiApiKeys { get; set; } = [];

    public string? GroqApiKey { get; set; }

    internal Secrets Clean()
    {
        GeminiApiKeys = (GeminiApiKeys ?? [])
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        GroqApiKey = string.IsNullOrWhiteSpace(GroqApiKey) ? null : GroqApiKey.Trim();
        return this;
    }
}

public sealed class SecretStore(AppPaths paths)
{
    public Secrets Load()
    {
        if (!File.Exists(paths.SecretsFile))
        {
            return new Secrets();
        }

        try
        {
            var plain = Dpapi.Unprotect(File.ReadAllBytes(paths.SecretsFile));
            try
            {
                return (JsonSerializer.Deserialize(plain, SaySoJsonContext.Default.Secrets) ?? new Secrets()).Clean();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
        catch (Exception e) when (e is CryptographicException or JsonException or IOException)
        {
            return new Secrets();
        }
    }

    public void Save(Secrets secrets)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(secrets.Clean(), SaySoJsonContext.Default.Secrets);
        try
        {
            AtomicFile.WriteAllBytes(paths.SecretsFile, Dpapi.Protect(json));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
        }
    }
}
