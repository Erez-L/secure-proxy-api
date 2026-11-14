using System.Text.RegularExpressions;

namespace SecureProxy.Api.Infrastructure;

public static partial class Correlation
{
    public const string HeaderName = "X-Correlation-ID";
    private const string ItemKey = "CorrelationId";

    [GeneratedRegex("^[A-Za-z0-9-]{8,64}$")]
    private static partial Regex ValidId();

    public static string Get(HttpContext ctx) =>
        ctx.Items.TryGetValue(ItemKey, out var v) && v is string s ? s : ctx.TraceIdentifier;

    /// <summary>
    /// Accepts a well-formed caller supplied correlation id (so traces can span systems),
    /// otherwise generates one. Arbitrary caller text is never written to logs.
    /// </summary>
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app) =>
        app.Use(async (ctx, next) =>
        {
            var supplied = ctx.Request.Headers[HeaderName].ToString();
            var id = ValidId().IsMatch(supplied) ? supplied : Guid.NewGuid().ToString("N");
            ctx.Items[ItemKey] = id;
            ctx.Response.Headers[HeaderName] = id;

            var logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("SecureProxy.Request");
            using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = id }))
            {
                await next();
            }
        });
}
