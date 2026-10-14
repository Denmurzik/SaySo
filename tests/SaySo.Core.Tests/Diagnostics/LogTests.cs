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

    [Fact]
    public void Writes_WhileAnotherProcessHasTheFileOpenForWriting()
    {
        // Демон и окно настроек пишут в один журнал: открытый другим писателем файл не должен глотать строки.
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "sayso.log");
        File.WriteAllText(path, "старая строка\n");
        Log.FilePath = path;
        try
        {
            using var other = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            other.Write("из второго процесса\n"u8);
            other.Flush();
            Log.Info("из первого процесса"); // файл открыт другим писателем и не закрыт — раньше здесь был IOException
            Log.Info("ещё одна строка");
        }
        finally
        {
            Log.FilePath = null;
        }

        var text = File.ReadAllText(path);
        Assert.Contains("старая строка", text);
        Assert.Contains("из первого процесса", text);
        Assert.Contains("из второго процесса", text);
        Assert.Contains("ещё одна строка", text);
    }

    [Fact]
    public void Writes_WhileAnotherProcessReadsTheFile()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "sayso.log");
        File.WriteAllText(path, "");
        Log.FilePath = path;
        try
        {
            using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Log.Info("пока файл читают");
        }
        finally
        {
            Log.FilePath = null;
        }

        Assert.Contains("пока файл читают", File.ReadAllText(path));
    }

    [Fact]
    public void WritesUtf8WithoutBom()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "sayso.log");
        Log.FilePath = path;
        try
        {
            Log.Info("кириллица");
        }
        finally
        {
            Log.FilePath = null;
        }

        var bytes = File.ReadAllBytes(path);
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.StartsWith("20", System.Text.Encoding.UTF8.GetString(bytes)); // год, а не BOM
    }
}
