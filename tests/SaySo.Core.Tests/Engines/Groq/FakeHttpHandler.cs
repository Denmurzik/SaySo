using System.Net;
using System.Text;

namespace SaySo.Core.Tests.Engines.Groq;

/// <summary>Отвечает по сценарию и запоминает запросы (с прочитанным телом).</summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();

    public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

    public void Enqueue(HttpStatusCode status, string json, Action<HttpResponseMessage>? configure = null) =>
        _responses.Enqueue(_ =>
        {
            var r = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            configure?.Invoke(r);
            return r;
        });

    public void EnqueueException(Exception e) => _responses.Enqueue(_ => throw e);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        Requests.Add((request, body));
        return _responses.Dequeue()(request);
    }
}
