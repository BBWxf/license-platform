namespace LicensePlatform.Web.Middleware;

public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
        headers["Content-Security-Policy"] =
            "default-src 'self'; base-uri 'self'; frame-ancestors 'none'; " +
            "object-src 'none'; img-src 'self' data:; font-src 'self'; " +
            "style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; " +
            "form-action 'self'; connect-src 'self'";

        await next(context);
    }
}
