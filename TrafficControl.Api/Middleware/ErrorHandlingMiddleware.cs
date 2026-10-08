using Microsoft.AspNetCore.Mvc;
using TrafficControl.Application;

namespace TrafficControl.Api.Middleware;

/// <summary>Maps application exceptions to proper HTTP status codes. Unknown errors become a generic 500 (no internals leaked).</summary>
public sealed class ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> log)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex) when (!context.Response.HasStarted)
        {
            switch (ex)
            {
                case JunctionNotFoundException:
                    await Write(context, StatusCodes.Status404NotFound, "Junction not found", ex.Message);
                    break;
                case JunctionAlreadyExistsException:
                    await Write(context, StatusCodes.Status409Conflict, "Junction already exists", ex.Message);
                    break;
                case RequestValidationException:
                    await Write(context, StatusCodes.Status400BadRequest, "Invalid request", ex.Message);
                    break;
                default:
                    log.LogError(ex, "Unhandled error for {Method} {Path}", context.Request.Method, context.Request.Path);
                    await Write(context, StatusCodes.Status500InternalServerError, "Unexpected error", "The request could not be processed.");
                    break;
            }
        }
    }

    private static Task Write(HttpContext context, int status, string title, string detail)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new ProblemDetails { Status = status, Title = title, Detail = detail });
    }
}
