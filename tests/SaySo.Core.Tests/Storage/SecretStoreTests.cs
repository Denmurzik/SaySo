using System.Text;
using SaySo.Core.Storage;

namespace SaySo.Core.Tests.Storage;

public class SecretStoreTests
{
    [Fact]
    public void Load_WhenMissing_ReturnsEmptySecrets()
    {
        using var dir = new TempDir();
        var s = new SecretStore(new AppPaths(dir.Path)).Load();
        Assert.Empty(s.GeminiApiKeys);
        Assert.Null(s.GroqApiKey);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips_AndFileIsNotPlaintext()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path);
        var store = new SecretStore(paths);
        store.Save(new Secrets { GeminiApiKeys = ["AIzaSyTEST-KEY-1", " AIzaSyTEST-KEY-2 ", "", "AIzaSyTEST-KEY-1"], GroqApiKey = "gsk_TEST" });

        var loaded = store.Load();
        Assert.Equal(["AIzaSyTEST-KEY-1", "AIzaSyTEST-KEY-2"], loaded.GeminiApiKeys);
        Assert.Equal("gsk_TEST", loaded.GroqApiKey);

        var raw = File.ReadAllBytes(paths.SecretsFile);
        Assert.DoesNotContain("AIzaSyTEST-KEY", Encoding.UTF8.GetString(raw));
        Assert.DoesNotContain("AIzaSyTEST-KEY", Encoding.Unicode.GetString(raw));
    }

    [Fact]
    public void Load_CorruptFile_ReturnsEmptySecrets()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path);
        File.WriteAllBytes(paths.SecretsFile, [1, 2, 3, 4]);
        Assert.Empty(new SecretStore(paths).Load().GeminiApiKeys);
    }
}
