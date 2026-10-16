using System.Security.Cryptography;
using System.Text.Json;
using SaySo.Core.Diagnostics;
using SaySo.Core.Json;

namespace SaySo.Core.Storage;

/// <summary>Ключи API (secrets.bin, зашифровано DPAPI на Windows, ключом из Связки ключей на macOS).</summary>
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
        var cipher = StoreFile.TryReadAllBytes(paths.SecretsFile, "SecretStore");
        if (cipher is null)
        {
            return new Secrets();
        }

        try
        {
            var plain = DataProtection.Unprotect(cipher);
            try
            {
                return (JsonSerializer.Deserialize(plain, SaySoJsonContext.Default.Secrets) ?? new Secrets()).Clean();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
        catch (DataProtectionUnavailableException e)
        {
            // Связка ключей не отдала ключ (отказ в доступе): файл цел, в карантин не переносим.
            Log.Error("SecretStore", e);
            return new Secrets();
        }
        catch (Exception e) when (e is CryptographicException or JsonException)
        {
            // Повреждённый файл: копия в secrets.bin.bad; ключи придётся ввести заново.
            StoreFile.Quarantine(paths.SecretsFile, "SecretStore", e);
            return new Secrets();
        }
    }

    public void Save(Secrets secrets)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(secrets.Clean(), SaySoJsonContext.Default.Secrets);
        try
        {
            AtomicFile.WriteAllBytes(paths.SecretsFile, DataProtection.Protect(json));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
        }
    }
}
