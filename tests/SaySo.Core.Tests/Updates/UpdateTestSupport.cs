using System.Net;
using System.Security.Cryptography;
using System.Text;
using SaySo.Core.Updates;

namespace SaySo.Core.Tests.Updates;

/// <summary>Отвечает по адресу запроса; неизвестный адрес — 404. Запоминает запросы.</summary>
internal sealed class RoutingHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes = new(StringComparer.Ordinal);

    public List<(string Url, string UserAgent, string Accept)> Requests { get; } = [];

    public int Count(string url) => Requests.Count(r => r.Url == url);

    public void Route(string url, Func<HttpRequestMessage, HttpResponseMessage> response) => _routes[url] = response;

    public void Json(string url, string json, HttpStatusCode status = HttpStatusCode.OK) =>
        Route(url, _ => new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });

    public void Text(string url, string text) =>
        Route(url, _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8) });

    public void Bytes(string url, byte[] data) =>
        Route(url, _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) });

    public void Status(string url, HttpStatusCode status) => Route(url, _ => new HttpResponseMessage(status));

    public void Throw(string url, Exception e) => Route(url, _ => throw e);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!.AbsoluteUri;
        Requests.Add((url, request.Headers.UserAgent.ToString(), request.Headers.Accept.ToString()));
        await Task.Yield();
        return _routes.TryGetValue(url, out var response)
            ? response(request)
            : new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}

/// <summary>Содержимое без известной длины (в ответе нет Content-Length): размер проверяется по факту чтения.</summary>
internal sealed class UnknownLengthContent(byte[] data) : HttpContent
{
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(data, 0, data.Length);

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}

/// <summary>Поток, который отдаёт начало данных и обрывается, как соединение посреди тела ответа.</summary>
internal sealed class DroppingStream(byte[] prefix) : Stream
{
    private int _position;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_position >= prefix.Length)
        {
            throw new HttpIOException(HttpRequestError.ResponseEnded, "обрыв соединения");
        }

        var n = Math.Min(buffer.Length, prefix.Length - _position);
        prefix.AsSpan(_position, n).CopyTo(buffer);
        _position += n;
        return n;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Read(buffer.Span));

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal static class UpdateFixtures
{
    public const string SetupBase = "https://github.com/Denmurzik/SaySo/releases/download/";

    public static string SetupUrl(string version) => $"{SetupBase}v{version}/SaySo-Setup-{version}.exe";

    public static string ShaUrl(string version) => SetupUrl(version) + ".sha256";

    public static string Sha256Hex(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    public static byte[] SetupBytes(int length = 5000, byte seed = 7)
    {
        var bytes = new byte[length];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(seed + i * 31);
        }

        return bytes;
    }

    /// <summary>JSON релиза в виде, в каком его отдаёт GitHub (с лишними полями).</summary>
    public static string ReleaseJson(
        string tag,
        string version,
        bool withSetup = true,
        bool withSha = true,
        long size = 5000,
        string? body = "Заметки релиза",
        bool prerelease = false,
        bool draft = false,
        string htmlUrl = "https://github.com/Denmurzik/SaySo/releases/tag/TAG")
    {
        var assets = new List<string>();
        if (withSetup)
        {
            assets.Add(Asset($"SaySo-Setup-{version}.exe", size, SetupUrl(version)));
        }

        if (withSha)
        {
            assets.Add(Asset($"SaySo-Setup-{version}.exe.sha256", 91, ShaUrl(version)));
        }

        assets.Add(Asset($"SaySo-{version}-win-x64.zip", 123456, $"{SetupBase}v{version}/SaySo-{version}-win-x64.zip"));
        var bodyJson = body is null ? "null" : System.Text.Json.JsonSerializer.Serialize(body);
        return $$"""
            {
              "url": "https://api.github.com/repos/Denmurzik/SaySo/releases/1",
              "tag_name": "{{tag}}",
              "html_url": "{{htmlUrl.Replace("TAG", tag)}}",
              "name": "SaySo {{version}}",
              "draft": {{(draft ? "true" : "false")}},
              "prerelease": {{(prerelease ? "true" : "false")}},
              "body": {{bodyJson}},
              "author": { "login": "Denmurzik", "id": 1 },
              "assets": [{{string.Join(",", assets)}}]
            }
            """;
    }

    private static string Asset(string name, long size, string url) =>
        $$"""{"id":1,"name":"{{name}}","size":{{size}},"content_type":"application/octet-stream","browser_download_url":"{{url}}"}""";

    public static HttpClient Client(RoutingHandler handler) => new(handler);

    public static ReleaseInfo Release(string version = "0.2.0", byte[]? setup = null, bool installable = true)
    {
        setup ??= SetupBytes();
        return new ReleaseInfo(
            Version.Parse(version), "v" + version, "https://github.com/Denmurzik/SaySo/releases/tag/v" + version, "Заметки",
            installable ? SetupUrl(version) : null, installable ? setup.Length : 0, installable ? ShaUrl(version) : null);
    }
}
