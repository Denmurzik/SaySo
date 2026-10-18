namespace SaySo.Mac.Platform;

/// <summary>
/// Единственный экземпляр (аналог именованного мьютекса Windows): файл в личном $TMPDIR, открытый с FileShare.None — на
/// Unix это flock(LOCK_EX), который система снимает сама, даже если процесс упал. Рядом — PID владельца.
/// </summary>
internal sealed class InstanceLock : IDisposable
{
    private readonly FileStream _stream;
    private readonly string _pidPath;

    private InstanceLock(FileStream stream, string pidPath)
    {
        _stream = stream;
        _pidPath = pidPath;
    }

    /// <returns>Блокировка или null, если ею уже владеет другой процесс.</returns>
    public static InstanceLock? TryAcquire(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), $"SaySo.{name}.lock");
        try
        {
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var pidPath = Path.Combine(Path.GetTempPath(), $"SaySo.{name}.pid");
            File.WriteAllText(pidPath, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return new InstanceLock(stream, pidPath);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>PID процесса, владеющего блокировкой (0 — неизвестно).</summary>
    public static int OwnerPid(string name)
    {
        try
        {
            return int.TryParse(File.ReadAllText(Path.Combine(Path.GetTempPath(), $"SaySo.{name}.pid")), out var pid) ? pid : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    public void Dispose()
    {
        try
        {
            File.Delete(_pidPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        _stream.Dispose();
    }
}
