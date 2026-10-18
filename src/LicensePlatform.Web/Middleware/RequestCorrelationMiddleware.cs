using System.Diagnostics;

namespace LicensePlatform.Web.Middleware;

public sealed class RequestCorrelationMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Request-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        var requestId = NormalizeRequestId(context.Request.Headers[HeaderName].FirstOrDefault());
        context.TraceIdentifier = requestId;
        Activity.Current?.SetTag("request.id", requestId);
        context.Response.Headers[HeaderName] = requestId;
        await next(context);
    }

    private static string NormalizeRequestId(string? value)
    {
        return Guid.TryParse(value, out var id)
            ? id.ToString("D")
            : Guid.NewGuid().ToString("D");
    }
}
