using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using LicensePlatform.Domain.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LicensePlatform.Web.Middleware;

public static class ClientApiPaths
{
    public static readonly PathString Prefix = new("/api/v1/client");

    public static bool IsKnownEndpoint(PathString path)
    {
        return path.Equals(new PathString("/api/v1/client/challenges/activation"))
               || path.Equals(new PathString("/api/v1/client/challenges/refresh"))
               || path.Equals(new PathString("/api/v1/client/challenges/complete"))
               || path.Equals(new PathString("/api/v1/client/validations"));
    }
}

public static class ApiProblemResults
{
    public static IResult Create(HttpContext context, int statusCode, string code, string title)
    {
        return Results.Problem(new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Extensions =
            {
                ["code"] = code,
                ["requestId"] = context.TraceIdentifier
            }
        });
    }

    public static async Task WriteAsync(
        HttpContext context,
        int statusCode,
        string code,
        string title,
        CancellationToken cancellationToken)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        if (statusCode == StatusCodes.Status429TooManyRequests)
        {
            context.Response.Headers["Retry-After"] = "60";
        }

        await Create(context, statusCode, code, title).ExecuteAsync(context);
    }
}

public sealed class ClientApiRequestBufferingMiddleware(RequestDelegate next)
{
    public const int MaximumBodyBytes = 64 * 1024;
    public const string BufferedBodyItemKey = "LicensePlatform.ClientApi.BufferedBody";

    public async Task InvokeAsync(HttpContext context)
    {
        if (!ClientApiPaths.IsKnownEndpoint(context.Request.Path)
            || !HttpMethods.IsPost(context.Request.Method))
        {
            await next(context);
            return;
        }

        if (!string.Equals(
                context.Request.ContentType?.Split(';', 2)[0].Trim(),
                "application/json",
                StringComparison.OrdinalIgnoreCase))
        {
            await ApiProblemResults.WriteAsync(
                context,
                StatusCodes.Status415UnsupportedMediaType,
                "unsupported_media_type",
                "请求必须使用 application/json 媒体类型。",
                context.RequestAborted);
            return;
        }

        if (context.Request.ContentLength > MaximumBodyBytes)
        {
            await ApiProblemResults.WriteAsync(
                context,
                StatusCodes.Status413PayloadTooLarge,
                "request_too_large",
                "请求正文超过允许的大小。",
                context.RequestAborted);
            return;
        }

        context.Request.EnableBuffering(bufferThreshold: 30 * 1024, bufferLimit: MaximumBodyBytes + 1L);
        context.Request.Body.Position = 0;
        byte[]? bufferedBody = null;
        var readBuffer = ArrayPool<byte>.Shared.Rent(8 * 1024);
        try
        {
            using var output = new MemoryStream();
            while (true)
            {
                // Read only the remaining allowance plus one byte, so unknown-length requests
                // reach our 413 response rather than FileBufferingReadStream's IOException limit.
                var remaining = (int)Math.Min(readBuffer.Length, MaximumBodyBytes + 1L - output.Length);
                var read = await context.Request.Body.ReadAsync(readBuffer.AsMemory(0, remaining), context.RequestAborted);
                if (read == 0)
                {
                    break;
                }

                if (output.Length + read > MaximumBodyBytes)
                {
                    await ApiProblemResults.WriteAsync(
                        context,
                        StatusCodes.Status413PayloadTooLarge,
                        "request_too_large",
                        "请求正文超过允许的大小。",
                        context.RequestAborted);
                    return;
                }

                output.Write(readBuffer, 0, read);
            }

            bufferedBody = output.ToArray();
            context.Items[BufferedBodyItemKey] = bufferedBody;
            context.Request.Body.Position = 0;
            await next(context);
        }
        finally
        {
            context.Request.Body.Position = 0;
            if (bufferedBody is not null)
            {
                CryptographicOperations.ZeroMemory(bufferedBody);
            }

            CryptographicOperations.ZeroMemory(readBuffer);
            ArrayPool<byte>.Shared.Return(readBuffer);
        }
    }
}

public sealed class ClientApiExceptionHandlingMiddleware(
    RequestDelegate next,
    ILogger<ClientApiExceptionHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments(ClientApiPaths.Prefix))
        {
            await next(context);
            return;
        }

        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (!context.Response.HasStarted)
        {
            var (statusCode, code, title, logUnexpected) = exception switch
            {
                DomainException => (
                    StatusCodes.Status400BadRequest,
                    "request_rejected",
                    "请求未通过业务规则校验。",
                    false),
                BadHttpRequestException or JsonException or ArgumentException or FormatException or InvalidDataException => (
                    StatusCodes.Status400BadRequest,
                    "invalid_request",
                    "请求格式或参数无效。",
                    false),
                DbUpdateConcurrencyException or DbUpdateException
                {
                    InnerException: Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation }
                } => (
                    StatusCodes.Status409Conflict,
                    "conflict",
                    "请求与当前资源状态冲突。",
                    false),
                _ => (
                    StatusCodes.Status500InternalServerError,
                    "server_error",
                    "服务器暂时无法处理请求。",
                    true)
            };

            if (logUnexpected)
            {
                // Exception messages can contain request secrets or database credentials.
                logger.LogError("Unhandled client API failure for request {RequestId}.", context.TraceIdentifier);
            }

            await ApiProblemResults.WriteAsync(
                context,
                statusCode,
                code,
                title,
                context.RequestAborted);
        }
    }
}
