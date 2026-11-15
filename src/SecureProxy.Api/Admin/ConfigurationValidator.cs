using System.Text.RegularExpressions;
using SecureProxy.Api.Data;
using SecureProxy.Api.Proxy;

namespace SecureProxy.Api.Admin;

public sealed record ParameterRuleInput(
    string Name, bool IsRequired, string DataType, string Location,
    int? MaxLength, decimal? MinValue, decimal? MaxValue, string? Pattern);

/// <summary>
/// Checks configuration when it is written, so that a mistake is refused at the admin API
/// instead of surfacing later as "operation_misconfigured" on live requests. The request
/// pipeline still re-checks everything at execution time: this is an early warning, not the guard.
/// Each method returns null when the input is valid, otherwise a message for the administrator.
/// </summary>
public static partial class ConfigurationValidator
{
    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,99}$")]
    private static partial Regex KeyFormat();

    [GeneratedRegex("^[A-Za-z0-9_]{1,100}$")]
    private static partial Regex ParameterName();

    [GeneratedRegex("^[A-Za-z0-9-]{1,100}$")]
    private static partial Regex HeaderName();

    [GeneratedRegex(@"\{([A-Za-z0-9_]+)\}")]
    private static partial Regex Placeholder();

    public static bool IsValidKey(string? key) => key is not null && KeyFormat().IsMatch(key);

    public static string? ValidateDestination(
        string? key, string? name, string? baseUrl, int timeoutSeconds, long maxResponseBytes, string? allowedPorts, bool requireHttps)
    {
        if (!IsValidKey(key)) return "key must be lower-case letters, digits, '.', '_' or '-' (max 100).";
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200) return "name is required (max 200).";
        if (string.IsNullOrWhiteSpace(baseUrl) || baseUrl.Length > 500 || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
            return "baseUrl must be an absolute URL.";
        if (uri.Scheme is not ("http" or "https")) return "baseUrl must use http or https.";
        if (requireHttps && uri.Scheme != "https") return "baseUrl must use https.";
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return "baseUrl must not contain credentials, a query string or a fragment.";
        if (timeoutSeconds is < 1 or > 300) return "timeoutSeconds must be between 1 and 300.";
        if (maxResponseBytes < 1) return "maxResponseBytes must be positive.";
        foreach (var part in (allowedPorts ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (!int.TryParse(part, out var port) || port is < 1 or > 65535) return "allowedPorts must be a comma separated list of port numbers.";
        return null;
    }

    public static string? ValidateOperation(
        string? operationKey, string? httpMethod, string? pathTemplate, int maxRequestBytes, int? timeoutSeconds,
        bool requiresEncryption, bool hasEncryptionProfile)
    {
        if (!IsValidKey(operationKey)) return "operationKey must be lower-case letters, digits, '.', '_' or '-' (max 100).";
        if (httpMethod is null || !HttpMethodPolicy.IsAllowed(httpMethod)) return "httpMethod must be GET, POST, PUT, PATCH or DELETE.";
        if (maxRequestBytes < 1) return "maxRequestBytes must be positive.";
        if (timeoutSeconds is < 1 or > 300) return "timeoutSeconds must be between 1 and 300.";
        if (requiresEncryption && !hasEncryptionProfile) return "An operation that requires encryption needs an encryption profile.";
        return ValidatePathTemplate(pathTemplate);
    }

    public static string? ValidatePathTemplate(string? pathTemplate)
    {
        if (string.IsNullOrEmpty(pathTemplate) || pathTemplate.Length > 500 || !pathTemplate.StartsWith('/'))
            return "pathTemplate must start with '/'.";
        var literal = Placeholder().Replace(pathTemplate, "x");
        if (literal.Contains('{') || literal.Contains('}')) return "pathTemplate contains a malformed placeholder.";
        if (literal.Any(c => c is '?' or '#' or '\\' or '%' || char.IsWhiteSpace(c) || char.IsControl(c)))
            return "pathTemplate contains a character that is not allowed.";
        if (literal.Split('/').Any(segment => segment is "." or "..")) return "pathTemplate must not contain '.' or '..' segments.";
        return null;
    }

    public static string? ValidateRules(
        string httpMethod, string pathTemplate, IReadOnlyCollection<ParameterRuleInput> rules, IReadOnlyCollection<string> headers)
    {
        var placeholders = Placeholder().Matches(pathTemplate).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rule in rules)
        {
            if (rule.Name is null || !ParameterName().IsMatch(rule.Name)) return "A parameter name is missing or malformed.";
            if (!names.Add(rule.Name)) return $"Parameter '{rule.Name}' is defined more than once.";
            // Names only: Enum.TryParse would also accept numeric strings such as "1".
            if (rule.DataType is null || !Enum.GetNames<ParameterDataType>().Contains(rule.DataType))
                return $"Parameter '{rule.Name}': dataType must be String, Integer, Decimal, Boolean or Guid.";
            if (rule.Location is null || !Enum.GetNames<ParameterLocation>().Contains(rule.Location))
                return $"Parameter '{rule.Name}': location must be Path, Query or Body.";
            var location = Enum.Parse<ParameterLocation>(rule.Location);

            if (location == ParameterLocation.Path)
            {
                if (!placeholders.Contains(rule.Name)) return $"Path parameter '{rule.Name}' has no placeholder in pathTemplate.";
                if (!rule.IsRequired) return $"Path parameter '{rule.Name}' must be required.";
            }
            if (location == ParameterLocation.Body && !HttpMethodPolicy.HasBody(httpMethod))
                return $"Parameter '{rule.Name}': a body parameter needs POST, PUT or PATCH.";

            if (rule.MaxLength is < 1) return $"Parameter '{rule.Name}': maxLength must be positive.";
            if (rule.MinValue is { } min && rule.MaxValue is { } max && min > max) return $"Parameter '{rule.Name}': minValue is greater than maxValue.";
            if (!string.IsNullOrEmpty(rule.Pattern))
            {
                if (rule.Pattern.Length > 500) return $"Parameter '{rule.Name}': pattern is too long.";
                try { _ = new Regex(rule.Pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)); }
                catch (ArgumentException) { return $"Parameter '{rule.Name}': pattern is not a valid regular expression."; }
            }
        }

        foreach (var placeholder in placeholders)
            if (!rules.Any(r => r.Name == placeholder && r.Location == nameof(ParameterLocation.Path)))
                return $"Placeholder '{placeholder}' has no path parameter rule.";

        var seenHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in headers)
        {
            if (header is null || !HeaderName().IsMatch(header)) return "A header name is missing or malformed.";
            if (!seenHeaders.Add(header)) return $"Header '{header}' is listed more than once.";
            if (UpstreamRequestBuilder.IsNeverForwarded(header)) return $"Header '{header}' can never be forwarded.";
        }
        return null;
    }
}
