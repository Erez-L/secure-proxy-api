using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace SecureProxy.Api.Infrastructure;

// Shapes used only to describe the request body of the proxy endpoint in the API description.
// The endpoint itself reads and validates the raw body (see ProxyExecutor).
public sealed record ExecuteRequestDocument(string ClientId, ExecuteJsonDocument Json);

public sealed record ExecuteJsonDocument(string UrlId, Dictionary<string, object>? Parameters, EncryptedPayloadDocument? Encrypted);

public sealed record EncryptedPayloadDocument(string Nonce, string Ciphertext, string Tag);

public static class OpenApiSetup
{
    public static IServiceCollection AddProxyOpenApi(this IServiceCollection services)
    {
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "Secure Proxy API",
                Version = "v1",
                Description = "Forwards explicitly configured operations to predefined destinations."
            });
            options.OperationFilter<ExecuteRequestBodyFilter>();
            options.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Header,
                Name = "X-Api-Key",
                Description = "API key of the caller."
            });
            options.AddSecurityRequirement(new OpenApiSecurityRequirement
            {
                {
                    new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "ApiKey" } },
                    Array.Empty<string>()
                }
            });
        });
        return services;
    }

    public static RouteHandlerBuilder DescribeExecuteEndpoint(this RouteHandlerBuilder endpoint) => endpoint
        .WithTags("Proxy")
        .WithName(ExecuteRequestBodyFilter.OperationName)
        .Produces(StatusCodes.Status200OK)
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces<ErrorResponse>(StatusCodes.Status401Unauthorized)
        .Produces<ErrorResponse>(StatusCodes.Status403Forbidden)
        .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
        .Produces<ErrorResponse>(StatusCodes.Status413PayloadTooLarge)
        .Produces<ErrorResponse>(StatusCodes.Status429TooManyRequests)
        .Produces<ErrorResponse>(StatusCodes.Status502BadGateway)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable)
        .Produces<ErrorResponse>(StatusCodes.Status504GatewayTimeout);
}

/// <summary>
/// Describes the request body of the proxy endpoint in the API description. This is done with a
/// filter rather than with endpoint metadata on purpose: "accepts" metadata would make the
/// framework reject other content types by itself, before the proxy pipeline runs, and that
/// rejection would then be neither audited nor returned in the proxy's own error format.
/// </summary>
public sealed class ExecuteRequestBodyFilter : IOperationFilter
{
    public const string OperationName = "ExecuteOperation";

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (operation.OperationId != OperationName) return;
        operation.RequestBody = new OpenApiRequestBody
        {
            Required = true,
            Content =
            {
                ["application/json"] = new OpenApiMediaType
                {
                    Schema = context.SchemaGenerator.GenerateSchema(typeof(ExecuteRequestDocument), context.SchemaRepository)
                }
            }
        };
    }
}
