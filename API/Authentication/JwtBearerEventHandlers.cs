using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;

namespace API.Authentication;

public static class JwtBearerEventHandlers
{
    private const string LoggerCategory = "API.Authentication.JwtBearer";

    public static JwtBearerEvents Create() => new()
    {
        OnAuthenticationFailed = context =>
        {
            var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(LoggerCategory);

            if (context.Exception is SecurityTokenExpiredException)
            {
                context.Response.Headers.Append("x-token-expired", "true");

                logger.LogInformation("Bearer token rejected: expired.");
            }
            else
            {
                logger.LogWarning(context.Exception, "Bearer token rejected.");
            }

            return Task.CompletedTask;
        },

        OnChallenge = async context =>
        {
            context.HandleResponse();

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/problem+json";

            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Status = StatusCodes.Status401Unauthorized,
                Title = "Unauthorized",
                Detail = context.ErrorDescription ?? "A valid bearer token is required.",
            });
        },

        OnForbidden = async context =>
        {
            context.Response.ContentType = "application/problem+json";

            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Forbidden",
                Detail = "You do not have permission to access this resource.",
            });
        },
    };
}
