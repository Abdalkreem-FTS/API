using System.Diagnostics;
using System.Security.Claims;
using API.Diagnostics;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace API.Authentication;

public static class JwtBearerEventHandlers
{
    public const string LoggerCategory = "API.Authentication.JwtBearer";

    private const string FailureDetailKey = "auth:failure-detail";

    /// <param name="timings">
    /// Null unless diagnostics are on, in which case signature validation is timed separately from
    /// the revocation lookup that follows it.
    /// </param>
    public static JwtBearerEvents Create(ILogger logger, RequestTimings? timings = null) => new()
    {
        OnMessageReceived = context =>
        {
            if (timings is not null)
            {
                context.HttpContext.Items[RequestTimingsMiddleware.ValidateStartedKey] = Stopwatch.GetTimestamp();
            }

            return Task.CompletedTask;
        },

        OnTokenValidated = async context =>
        {
            // Stamped before the revocation lookup, so this covers header parsing and the signature
            // check only.
            if (timings is not null &&
                context.HttpContext.Items[RequestTimingsMiddleware.ValidateStartedKey] is long started)
            {
                var elapsed = Stopwatch.GetElapsedTime(started);

                context.HttpContext.Items[RequestTimingsMiddleware.ValidateKey] = elapsed;

                timings.Record(RequestTimings.Validate, elapsed);
            }

            var tokenId = context.Principal?.FindFirstValue(JwtRegisteredClaimNames.Jti);

            if (string.IsNullOrEmpty(tokenId))
            {
                Reject(context.HttpContext, logger, "This token carries no 'jti', so it cannot be checked for revocation.");
                context.Fail("Missing 'jti' claim.");

                return;
            }

            var revoked = context.HttpContext.RequestServices.GetRequiredService<ITokenRevocationStore>();

            if (await revoked.IsRevokedAsync(tokenId, context.HttpContext.RequestAborted))
            {
                Reject(context.HttpContext, logger, "This token has been revoked.");
                context.Fail("Revoked token.");
            }
        },

        OnAuthenticationFailed = context =>
        {
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
                Detail = ChallengeDetail(context),
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

    private static string ChallengeDetail(JwtBearerChallengeContext context)
    {
        if (context.HttpContext.Items[FailureDetailKey] is string detail)
        {
            return detail;
        }

        return string.IsNullOrEmpty(context.ErrorDescription) ? "A valid bearer token is required." : context.ErrorDescription;
    }

    private static void Reject(HttpContext context, ILogger logger, string detail)
    {
        context.Items[FailureDetailKey] = detail;

        logger.LogInformation("Bearer token rejected: {Reason}", detail);
    }
}
