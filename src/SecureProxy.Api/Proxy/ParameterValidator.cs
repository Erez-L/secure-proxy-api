using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SecureProxy.Api.Data;
using SecureProxy.Api.Infrastructure;

namespace SecureProxy.Api.Proxy;

/// <summary>Validated parameters, already split by where they go in the upstream request.</summary>
public sealed class ValidatedParameters
{
    public Dictionary<string, string> Path { get; } = new(StringComparer.Ordinal);
    public List<KeyValuePair<string, string>> Query { get; } = new();
    public JsonObject Body { get; } = new();
}

public static class ParameterValidator
{
    private const int DefaultMaxLength = 256;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Validates caller parameters against the operation rules. Error messages name the
    /// offending parameter (already known to the caller) but never echo its value.
    /// </summary>
    public static ValidatedParameters Validate(OperationSnapshot op, JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object)
            throw ProxyErrors.InvalidParameters("parameters must be a JSON object.");

        var rules = op.Parameters.ToDictionary(r => r.Name, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new ValidatedParameters();
        var hasBody = HttpMethodPolicy.HasBody(op.HttpMethod);

        foreach (var property in parameters.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                throw ProxyErrors.InvalidParameters("A parameter was supplied more than once.");

            if (!rules.TryGetValue(property.Name, out var rule))
            {
                if (!op.AllowUnknownParameters)
                    throw ProxyErrors.InvalidParameters("An unknown parameter was supplied.");

                // Explicitly allowed unknown parameters are still constrained: scalar values with a
                // bounded length, and they can never be placed in the path.
                var loose = Canonical(property.Name, property.Value, ParameterDataType.String, lenient: true);
                CheckLength(property.Name, loose, DefaultMaxLength);
                if (hasBody) result.Body[property.Name] = JsonNode.Parse(property.Value.GetRawText());
                else result.Query.Add(new(property.Name, loose));
                continue;
            }

            var text = Canonical(rule.Name, property.Value, rule.DataType, lenient: false);
            CheckRule(rule, text);

            switch (rule.Location)
            {
                case ParameterLocation.Path:
                    EnsureSafePathSegment(rule.Name, text);
                    result.Path[rule.Name] = text;
                    break;
                case ParameterLocation.Query:
                    result.Query.Add(new(rule.Name, text));
                    break;
                default:
                    if (!hasBody) throw ProxyErrors.Misconfigured();
                    result.Body[rule.Name] = JsonNode.Parse(property.Value.GetRawText());
                    break;
            }
        }

        foreach (var rule in op.Parameters)
            if (rule.IsRequired && !seen.Contains(rule.Name))
                throw ProxyErrors.InvalidParameters($"Required parameter '{rule.Name}' is missing.");

        return result;
    }

    private static string Canonical(string name, JsonElement value, ParameterDataType type, bool lenient)
    {
        ProxyException Invalid() => ProxyErrors.InvalidParameters($"Parameter '{name}' has an invalid value.");

        if (lenient)
        {
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString()!,
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => throw Invalid()
            };
        }

        switch (type)
        {
            case ParameterDataType.String:
                if (value.ValueKind != JsonValueKind.String) throw Invalid();
                return value.GetString()!;
            case ParameterDataType.Integer:
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var l)) throw Invalid();
                return l.ToString(CultureInfo.InvariantCulture);
            case ParameterDataType.Decimal:
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var d)) throw Invalid();
                return d.ToString(CultureInfo.InvariantCulture);
            case ParameterDataType.Boolean:
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid();
                return value.GetBoolean() ? "true" : "false";
            case ParameterDataType.Guid:
                if (value.ValueKind != JsonValueKind.String || !Guid.TryParseExact(value.GetString(), "D", out var g)) throw Invalid();
                return g.ToString("D");
            default:
                throw ProxyErrors.Misconfigured();
        }
    }

    private static void CheckRule(ParameterRuleSnapshot rule, string text)
    {
        if (rule.DataType == ParameterDataType.String)
        {
            CheckLength(rule.Name, text, rule.MaxLength ?? DefaultMaxLength);
            if (!string.IsNullOrEmpty(rule.Pattern))
            {
                bool ok;
                try { ok = Regex.IsMatch(text, $"^(?:{rule.Pattern})$", RegexOptions.CultureInvariant, RegexTimeout); }
                catch (RegexMatchTimeoutException) { ok = false; }
                catch (ArgumentException) { throw ProxyErrors.Misconfigured(); }
                if (!ok) throw ProxyErrors.InvalidParameters($"Parameter '{rule.Name}' has an invalid value.");
            }
        }
        else if (rule.DataType is ParameterDataType.Integer or ParameterDataType.Decimal)
        {
            var number = decimal.Parse(text, CultureInfo.InvariantCulture);
            if ((rule.MinValue is { } min && number < min) || (rule.MaxValue is { } max && number > max))
                throw ProxyErrors.InvalidParameters($"Parameter '{rule.Name}' is out of range.");
        }
    }

    private static void CheckLength(string name, string text, int max)
    {
        if (text.Length > max)
            throw ProxyErrors.InvalidParameters($"Parameter '{name}' is too long.");
    }

    /// <summary>
    /// A path parameter must be exactly one inert path segment. Anything that could change the
    /// path structure is rejected outright (not "cleaned"), including encoded forms.
    /// </summary>
    private static void EnsureSafePathSegment(string name, string value)
    {
        var bad = value.Length == 0 || value is "." or ".." ||
                  value.Any(c => c is '/' or '\\' or '%' or '?' or '#' or ':' or '@' || char.IsControl(c) || char.IsWhiteSpace(c));
        if (bad) throw ProxyErrors.InvalidParameters($"Parameter '{name}' has an invalid value.");
    }
}

public static class HttpMethodPolicy
{
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal) { "GET", "POST", "PUT", "PATCH", "DELETE" };

    /// <summary>Only these methods may ever be forwarded, whatever the database says.</summary>
    public static bool IsAllowed(string method) => Allowed.Contains(method);

    public static bool HasBody(string method) => method is "POST" or "PUT" or "PATCH";
}
