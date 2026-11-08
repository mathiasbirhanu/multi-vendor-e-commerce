using Microsoft.AspNetCore.Mvc;
using MarketHub.Shared;

namespace MarketHub.Api;

/// <summary>Maps exceptions to RFC 7807 ProblemDetails responses.</summary>
public sealed class GlobalExceptionMiddleware(RequestDelegate next, ILogger<GlobalExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        try { await next(ctx); }
        catch (Exception ex)
        {
            var (status, title) = ex switch
            {
                NotFoundException => (StatusCodes.Status404NotFound, "Resource not found"),
                DomainException => (StatusCodes.Status400BadRequest, "Domain rule violation"),
                GatewayNotConfiguredException => (StatusCodes.Status503ServiceUnavailable, "Payment gateway not configured"),
                _ => (StatusCodes.Status500InternalServerError, "Internal server error"),
            };
            if (status == 500) logger.LogError(ex, "Unhandled exception on {Path}", ctx.Request.Path);
            else logger.LogWarning("{Type} on {Path}: {Message}", ex.GetType().Name, ctx.Request.Path, ex.Message);

            var problem = new ProblemDetails
            {
                Status = status,
                Title = title,
                Detail = status == 500 ? "An unexpected error occurred. The incident has been logged." : ex.Message,
                Instance = ctx.Request.Path,
            };
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/problem+json";
            await ctx.Response.WriteAsJsonAsync(problem);
        }
    }
}

/// <summary>Maps Result/PagedResult to HTTP responses.</summary>
public static class ResultExtensions
{
    public static IResult ToHttp(this Result result) => result.IsSuccess
        ? Results.Ok()
        : Results.BadRequest(new { error = result.Errors.FirstOrDefault()?.Message ?? "Request failed" });

    public static IResult ToHttp<T>(this Result<T> result) => result.IsSuccess
        ? Results.Ok(result.Value)
        : Results.BadRequest(new { error = result.Errors.FirstOrDefault()?.Message ?? "Request failed" });

    public static IResult Created<T>(this Result<T> result, string uri) => result.IsSuccess
        ? Results.Created(uri, result.Value)
        : Results.BadRequest(new { error = result.Errors.FirstOrDefault()?.Message });
}
