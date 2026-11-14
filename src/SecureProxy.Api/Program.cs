using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SecureProxy.Api.Admin;
using SecureProxy.Api.Audit;
using SecureProxy.Api.Data;
using SecureProxy.Api.Development;
using SecureProxy.Api.Infrastructure;
using SecureProxy.Api.Proxy;
using SecureProxy.Api.Security;

var builder = WebApplication.CreateBuilder(args);

// Structured (JSON) logs with scopes, so every line carries the correlation id.
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o => o.IncludeScopes = true);

builder.Services.Configure<ProxyOptions>(builder.Configuration.GetSection(ProxyOptions.Section));
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.Section));

// Kestrel refuses oversized bodies before the application reads them.
builder.WebHost.ConfigureKestrel((context, kestrel) =>
{
    kestrel.AddServerHeader = false;
    kestrel.Limits.MaxRequestBodySize =
        context.Configuration.GetValue<int?>($"{ProxyOptions.Section}:{nameof(ProxyOptions.MaxEnvelopeBytes)}") ?? 1024 * 1024;
});

builder.Services.AddDbContextFactory<ProxyDbContext>((sp, o) =>
    o.UseSqlServer(sp.GetRequiredService<IConfiguration>().GetConnectionString("ProxyDb"),
        sql => sql.EnableRetryOnFailure(3)));

builder.Services.AddMemoryCache();
builder.Services.AddSingleton<IOperationConfigProvider, OperationConfigProvider>();
builder.Services.AddSingleton<ISecretKeyProvider, ConfigurationSecretKeyProvider>();
builder.Services.AddSingleton<IAuditWriter, AuditWriter>();
builder.Services.AddSingleton<DestinationGate>();
builder.Services.AddSingleton<ProxyExecutor>();

// One pooled handler for all upstream calls (IHttpClientFactory). Per-request timeouts are applied
// with a CancellationToken, so HttpClient.Timeout is disabled. No redirects (a redirect would let an
// upstream send the proxy to an unconfigured host), no cookies, no system proxy, no retries.
builder.Services.AddHttpClient(ProxyExecutor.UpstreamClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    });

builder.Services.AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null);
builder.Services.AddAuthorization(o =>
{
    o.AddPolicy("Caller", p => p.RequireAuthenticatedUser().RequireRole("caller", "admin"));
    o.AddPolicy("Admin", p => p.RequireAuthenticatedUser().RequireRole("admin"));
});

builder.Services.AddRateLimiter(o =>
{
    o.OnRejected = async (context, _) =>
        await ProxyErrors.WriteAsync(context.HttpContext, 429, "rate_limited", "Too many requests. Retry later.");
    o.AddPolicy("proxy", ctx =>
    {
        var limit = ctx.RequestServices.GetRequiredService<IOptions<ProxyOptions>>().Value.RateLimitPerSecond;
        return RateLimitPartition.GetFixedWindowLimiter(
            ctx.User.Identity?.Name ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = Math.Max(1, limit), Window = TimeSpan.FromSeconds(1), QueueLimit = 0 });
    });
});

var app = builder.Build();

// Last line of defence: no stack trace or exception text ever reaches a caller.
app.UseExceptionHandler(handler => handler.Run(async ctx =>
{
    var status = ctx.Features.Get<IExceptionHandlerFeature>()?.Error is BadHttpRequestException bad ? bad.StatusCode : 500;
    await ProxyErrors.WriteAsync(ctx, status, status == 500 ? "internal_error" : "invalid_request",
        status == 500 ? "The proxy could not process the request." : "The request could not be processed.");
}));

app.UseCorrelationId();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapPost("/api/proxy/execute", (HttpContext ctx, ProxyExecutor executor) => executor.ExecuteAsync(ctx))
    .RequireAuthorization("Caller")
    .RequireRateLimiting("proxy");

app.MapAdminEndpoints();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

if (app.Configuration.GetValue("Database:ApplyMigrationsOnStartup", false))
{
    var factory = app.Services.GetRequiredService<IDbContextFactory<ProxyDbContext>>();
    await using var db = await factory.CreateDbContextAsync();
    // Uses EF migrations when the project has them; otherwise creates the schema from the model.
    if (db.Database.GetMigrations().Any()) await db.Database.MigrateAsync();
    else await db.Database.EnsureCreatedAsync();
}

if (app.Environment.IsDevelopment())
{
    app.MapDevelopmentEndpoints();
    await DevelopmentSupport.SeedAsync(app.Services, app.Configuration["Development:SelfBaseUrl"] ?? "http://localhost:5080");
}

app.Run();

public partial class Program;
