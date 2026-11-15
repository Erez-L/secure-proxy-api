using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using SecureProxy.Api.Infrastructure;
using SecureProxy.Api.Security;

namespace SecureProxy.Api.Proxy;

public static partial class UpstreamRequestBuilder
{
    [GeneratedRegex(@"\{([A-Za-z0-9_]+)\}")]
    private static partial Regex Placeholder();

    /// <summary>
    /// Headers that are never forwarded, even if an allowlist entry names them: hop-by-hop
    /// headers, headers the HTTP client owns, and the caller's credentials for the proxy itself.
    /// </summary>
    private static readonly HashSet<string> NeverForward = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization", "Proxy-Connection",
        "TE", "Trailer", "Transfer-Encoding", "Upgrade",
        "Host", "Content-Length", "Content-Type", "Expect",
        "Authorization", "Cookie", "Set-Cookie", ApiKeyAuthenticationHandler.HeaderName,
        "X-Forwarded-For", "X-Forwarded-Host", "X-Forwarded-Proto", "Forwarded"
    };

    /// <summary>True for headers that are never forwarded, whatever an allowlist says.</summary>
    public static bool IsNeverForwarded(string headerName) => NeverForward.Contains(headerName);

    /// <summary>
    /// Builds the upstream request from trusted configuration plus validated parameters.
    /// Any inconsistency in configuration fails closed with "operation_misconfigured".
    /// </summary>
    public static HttpRequestMessage Build(
        OperationSnapshot op, DestinationSnapshot destination, ValidatedParameters parameters,
        IHeaderDictionary incomingHeaders, string correlationId, bool requireHttps)
    {
        if (!HttpMethodPolicy.IsAllowed(op.HttpMethod)) throw ProxyErrors.Misconfigured();

        var uri = BuildUri(op, destination, parameters, requireHttps);
        var request = new HttpRequestMessage(new HttpMethod(op.HttpMethod), uri);

        if (HttpMethodPolicy.HasBody(op.HttpMethod))
        {
            request.Content = new StringContent(parameters.Body.ToJsonString(), Encoding.UTF8);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        }

        foreach (var name in op.AllowedHeaders)
        {
            if (NeverForward.Contains(name)) continue;
            if (!incomingHeaders.TryGetValue(name, out var values)) continue;
            request.Headers.TryAddWithoutValidation(name, (IEnumerable<string?>)values);
        }

        request.Headers.TryAddWithoutValidation(Correlation.HeaderName, correlationId);
        return request;
    }

    private static Uri BuildUri(OperationSnapshot op, DestinationSnapshot destination, ValidatedParameters parameters, bool requireHttps)
    {
        if (!Uri.TryCreate(destination.BaseUrl, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(baseUri.UserInfo) || !string.IsNullOrEmpty(baseUri.Query) || !string.IsNullOrEmpty(baseUri.Fragment))
            throw ProxyErrors.Misconfigured();
        if (requireHttps && baseUri.Scheme != "https") throw ProxyErrors.Misconfigured();

        var missing = false;
        var used = new HashSet<string>(StringComparer.Ordinal);
        var path = Placeholder().Replace(op.PathTemplate, m =>
        {
            var name = m.Groups[1].Value;
            used.Add(name);
            if (parameters.Path.TryGetValue(name, out var value)) return Uri.EscapeDataString(value);
            missing = true;
            return "";
        });
        // A path parameter that was accepted but has no placeholder would be dropped silently;
        // treat that as broken configuration instead of forwarding a different request.
        if (parameters.Path.Keys.Any(k => !used.Contains(k))) throw ProxyErrors.Misconfigured();
        if (missing || path.Contains('{') || path.Contains('}') || path.Contains('?') || path.Contains('#'))
            throw ProxyErrors.Misconfigured();

        var builder = new StringBuilder(baseUri.GetLeftPart(UriPartial.Path).TrimEnd('/'));
        builder.Append('/').Append(path.TrimStart('/'));
        for (var i = 0; i < parameters.Query.Count; i++)
        {
            builder.Append(i == 0 ? '?' : '&')
                .Append(Uri.EscapeDataString(parameters.Query[i].Key)).Append('=')
                .Append(Uri.EscapeDataString(parameters.Query[i].Value));
        }

        if (!Uri.TryCreate(builder.ToString(), UriKind.Absolute, out var target)) throw ProxyErrors.Misconfigured();

        // Final guard, after the runtime has normalised the URL (dot segments etc.): the request
        // must still point at the configured origin and stay underneath the configured base path.
        var basePath = baseUri.AbsolutePath.TrimEnd('/') + "/";
        var sameOrigin = target.Scheme == baseUri.Scheme &&
                         string.Equals(target.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase) &&
                         target.Port == baseUri.Port;
        var underBase = (target.AbsolutePath.TrimEnd('/') + "/").StartsWith(basePath, StringComparison.Ordinal);
        var portAllowed = destination.AllowedPorts.Count == 0 || destination.AllowedPorts.Contains(target.Port);
        if (!sameOrigin || !underBase || !portAllowed) throw ProxyErrors.Misconfigured();

        return target;
    }
}
