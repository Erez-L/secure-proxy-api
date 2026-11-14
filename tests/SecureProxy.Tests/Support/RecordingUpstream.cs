using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace SecureProxy.Tests.Support;

public sealed record RecordedRequest(
    HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string? Body);

/// <summary>
/// The mock destination. It replaces the primary handler of the proxy's upstream HttpClient,
/// so it sees exactly what would have gone on the wire, and it records every request.
/// Tests assert on <see cref="Requests"/> to prove a rejected request never left the proxy.
/// </summary>
public sealed class RecordingUpstream : HttpMessageHandler
{
    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Responder { get; set; } =
        (_, _) => Task.FromResult(Json(HttpStatusCode.OK, """{"ok":true}"""));

    public static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var headers = request.Headers
            .Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        Requests.Enqueue(new RecordedRequest(request.Method, request.RequestUri!, headers, body));
        return await Responder(request, ct);
    }

    // The HttpClientFactory owns handler lifetime; the test owns this instance.
    protected override void Dispose(bool disposing) { }
}
