using SaySo.Core.Diagnostics;

namespace SaySo.Core.Tests.Diagnostics;

[Collection("Log")] // Log — статический: тесты не должны идти параллельно
public class LogTests
{
    [Fact]
    public void Disabled_WhenPathIsNull()
    {
        Log.FilePath = null;
        Log.Info("ничего не пишется"); // не бросает
    }

    [Fact]
    public void Writes_AndRotates()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "sayso.log");
        Log.FilePath = path;
        try
        {
            Log.Info("запуск");
            Log.Error("контекст", new InvalidOperationException("сбой"));
            var text = File.ReadAllText(path);
            Assert.Contains("INFO запуск", text);
            Assert.Contains("ERROR контекст: InvalidOperationException: сбой", text);

            File.WriteAllText(path, new string('x', (int)Log.MaxBytes + 10));
            Log.Info("после ротации");
            Assert.True(File.Exists(path + ".1"));
            Assert.Contains("после ротации", File.ReadAllText(path));
            Assert.True(new FileInfo(path).Length < 1000);
        }
        finally
        {
            Log.FilePath = null;
        }
    }
}
