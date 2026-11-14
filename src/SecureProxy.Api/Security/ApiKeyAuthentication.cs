using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using SecureProxy.Api.Infrastructure;

namespace SecureProxy.Api.Security;

/// <summary>
/// Authenticates callers by an API key header. Only SHA-256 hashes of keys are configured;
/// comparison is constant-time and every configured key is always compared.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptionsMonitor<AuthOptions> auth)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";
    public const string AllowedOperationClaim = "allowed_operation";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var provided = Request.Headers[HeaderName].ToString();
        if (string.IsNullOrEmpty(provided) || provided.Length > 256)
            return Task.FromResult(AuthenticateResult.NoResult());

        var providedHash = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
        ApiKeyEntry? match = null;
        foreach (var entry in auth.CurrentValue.ApiKeys)
        {
            byte[] expected;
            try { expected = Convert.FromHexString(entry.Sha256); }
            catch (FormatException) { continue; }
            if (expected.Length == providedHash.Length && CryptographicOperations.FixedTimeEquals(expected, providedHash))
                match = entry;
        }

        if (match is null) return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, match.Name),
            new(ClaimTypes.Role, match.Role)
        };
        claims.AddRange(match.AllowedOperations.Select(o => new Claim(AllowedOperationClaim, o)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        ProxyErrors.WriteAsync(Context, 401, "unauthorized", "A valid API key is required.");

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        ProxyErrors.WriteAsync(Context, 403, "forbidden", "The caller is not permitted to use this endpoint.");
}

public static class CallerPermissions
{
    /// <summary>Deny by default: a caller may execute only operations listed for its key.</summary>
    public static bool MayExecute(ClaimsPrincipal user, string operationKey)
    {
        foreach (var claim in user.FindAll(ApiKeyAuthenticationHandler.AllowedOperationClaim))
        {
            var pattern = claim.Value;
            if (pattern == "*") return true;
            if (pattern.EndsWith('*') && operationKey.StartsWith(pattern[..^1], StringComparison.Ordinal)) return true;
            if (string.Equals(pattern, operationKey, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
