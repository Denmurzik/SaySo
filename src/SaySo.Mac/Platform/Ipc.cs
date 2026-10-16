using System.Net.Sockets;
using System.Text;
using SaySo.Core.Diagnostics;
using SaySo.Core.Platform;
using SaySo.Mac.Interop;

namespace SaySo.Mac.Platform;

/// <summary>
/// IPC «окно настроек → демон» на macOS: Unix-сокет в личном временном каталоге пользователя ($TMPDIR, права 0700),
/// одно сообщение <see cref="IpcProtocol"/> строкой на соединение. Аналог зарегистрированных оконных сообщений Windows.
/// </summary>
internal static class Ipc
{
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(1);

    public static string SocketPath => Path.Combine(Path.GetTempPath(), "SaySo.daemon.sock");

    public static bool IsDaemonRunning() => Send(null);

    /// <returns>false — демон не запущен или не ответил.</returns>
    public static bool Send(string? message)
    {
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.SendTimeout = (int)SendTimeout.TotalMilliseconds;
            var connect = socket.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath));
            if (!connect.Wait(SendTimeout))
            {
                return false;
            }

            if (message is not null)
            {
                socket.Send(Encoding.UTF8.GetBytes(message + "\n"));
            }

            socket.Shutdown(SocketShutdown.Both);
            return true;
        }
        catch (Exception e) when (e is SocketException or IOException or AggregateException or ObjectDisposedException)
        {
            return false;
        }
    }
}

/// <summary>
/// Приём сообщений демоном: асинхронный accept (блокирующий accept на своём потоке на macOS не прерывается закрытием
/// сокета, и Dispose завис бы), сообщения из <see cref="IpcProtocol.AllMessages"/> — на главный поток.
/// </summary>
internal sealed class IpcServer : IDisposable
{
    private const int MaxMessageBytes = 256;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(1);

    private readonly Socket _listener;
    private readonly Action<string> _onMessage;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    /// <remarks>Вызывать, только владея блокировкой единственного экземпляра: оставшийся от упавшего демона сокет удаляется.</remarks>
    public IpcServer(Action<string> onMessage)
    {
        _onMessage = onMessage;
        var path = Ipc.SocketPath;
        File.Delete(path);
        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(path));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        _listener.Listen(8);
        _ = Task.Run(AcceptLoopAsync);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        _listener.Dispose();
        try
        {
            File.Delete(Ipc.SocketPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error("Удаление сокета IPC", e);
        }
    }

    private async Task AcceptLoopAsync()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _listener.AcceptAsync(token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                if (!token.IsCancellationRequested)
                {
                    Log.Error("IPC: приём соединения", e);
                }

                return;
            }

            _ = ReadAsync(client, token);
        }
    }

    private async Task ReadAsync(Socket client, CancellationToken token)
    {
        using (client)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(ReadTimeout);
                var buffer = new byte[MaxMessageBytes];
                var length = 0;
                int read;
                while (length < buffer.Length
                       && (read = await client.ReceiveAsync(buffer.AsMemory(length), SocketFlags.None, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    length += read;
                }

                var message = Encoding.UTF8.GetString(buffer, 0, length).Trim();
                if (IpcProtocol.AllMessages.Contains(message))
                {
                    Dispatch.Post(() => _onMessage(message));
                }
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                // клиент отключился или молчит — сообщение просто не дошло
            }
        }
    }
}
