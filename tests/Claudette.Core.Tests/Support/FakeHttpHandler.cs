using System.Net;

namespace Claudette.Core.Tests.Support;

/// <summary>Answers HTTP requests from a table of URLs, and keeps the requests. Nothing reaches the network.</summary>
public sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes = new(StringComparer.Ordinal);

    public List<HttpRequestMessage> Requests { get; } = [];

    public FakeHttpHandler On(string url, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _routes[url] = respond;
        return this;
    }

    public FakeHttpHandler OnJson(string url, string json) =>
        On(url, _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });

    public FakeHttpHandler OnBytes(string url, byte[] bytes) =>
        On(url, _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Requests)
        {
            Requests.Add(request);
        }
        var url = request.RequestUri!.ToString();
        return Task.FromResult(_routes.TryGetValue(url, out var respond)
            ? respond(request)
            : new HttpResponseMessage(HttpStatusCode.NotFound) { ReasonPhrase = "Not Found" });
    }
}
