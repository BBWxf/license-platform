using System.Text;
using System.Text.Json;
using LicensePlatform.Domain.Common;
using LicensePlatform.Web.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LicensePlatform.Web.Tests;

public sealed class ClientApiMiddlewareTests
{
    private const string SecretCanary = "synthetic-secret-must-not-appear";

    [Theory]
    [InlineData("domain", 400, "request_rejected", false)]
    [InlineData("json", 400, "invalid_request", false)]
    [InlineData("bad-http", 400, "invalid_request", false)]
    [InlineData("argument", 400, "invalid_request", false)]
    [InlineData("format", 400, "invalid_request", false)]
    [InlineData("invalid-data", 400, "invalid_request", false)]
    [InlineData("concurrency", 409, "conflict", false)]
    [InlineData("unique", 409, "conflict", false)]
    [InlineData("database", 500, "server_error", true)]
    [InlineData("unexpected", 500, "server_error", true)]
    public async Task Exceptions_are_mapped_without_leaking_payloads_to_responses_or_logs(
        string kind,
        int expectedStatus,
        string expectedCode,
        bool logged)
    {
        using var services = CreateServices();
        var context = CreateContext(services);
        context.TraceIdentifier = "regression-request-id";
        var logs = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var failure = CreateException(kind);
        var middleware = new ClientApiExceptionHandlingMiddleware(
            _ => Task.FromException(failure), loggerFactory.CreateLogger<ClientApiExceptionHandlingMiddleware>());

        await middleware.InvokeAsync(context);

        Assert.Equal(expectedStatus, context.Response.StatusCode);
        using var json = await ReadResponseAsync(context);
        Assert.Equal(expectedStatus, json.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(expectedCode, json.RootElement.GetProperty("code").GetString());
        Assert.Equal("regression-request-id", json.RootElement.GetProperty("requestId").GetString());
        Assert.DoesNotContain(SecretCanary, json.RootElement.GetRawText());
        if (logged)
        {
            var entry = Assert.Single(logs.Entries);
            Assert.Equal(LogLevel.Error, entry.Level);
            Assert.Null(entry.Exception);
            Assert.Contains("regression-request-id", entry.Message);
            Assert.DoesNotContain(SecretCanary, entry.Message);
        }
        else
        {
            Assert.Empty(logs.Entries);
        }
    }

    [Fact]
    public async Task Cancellation_of_the_request_is_not_rewritten_or_logged()
    {
        using var services = CreateServices();
        var context = CreateContext(services);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        context.RequestAborted = cancellation.Token;
        var failure = new OperationCanceledException(cancellation.Token);
        var logs = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var middleware = new ClientApiExceptionHandlingMiddleware(
            _ => Task.FromException(failure), loggerFactory.CreateLogger<ClientApiExceptionHandlingMiddleware>());

        Assert.Same(failure, await Assert.ThrowsAnyAsync<OperationCanceledException>(() => middleware.InvokeAsync(context)));
        Assert.Empty(logs.Entries);
        Assert.Equal(0, context.Response.Body.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Non_client_requests_and_started_responses_do_not_swallow_failures(bool responseStarted)
    {
        using var services = CreateServices();
        var context = CreateContext(services);
        if (responseStarted)
        {
            context.Features.Set<IHttpResponseFeature>(new StartedResponseFeature());
        }
        else
        {
            context.Request.Path = "/Account/Login";
        }

        using var loggerFactory = LoggerFactory.Create(_ => { });
        var failure = new InvalidOperationException("expected failure");
        var middleware = new ClientApiExceptionHandlingMiddleware(
            _ => Task.FromException(failure), loggerFactory.CreateLogger<ClientApiExceptionHandlingMiddleware>());

        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context)));
    }

    [Fact]
    public async Task Buffering_rewinds_the_body_and_erases_the_captured_bytes_after_next()
    {
        using var services = CreateServices();
        var context = CreateContext(services);
        var original = Encoding.UTF8.GetBytes("{\"test\":\"body\"}");
        context.Request.Body = new MemoryStream(original);
        byte[]? buffered = null;
        var calls = 0;
        var middleware = new ClientApiRequestBufferingMiddleware(async httpContext =>
        {
            calls++;
            buffered = Assert.IsType<byte[]>(httpContext.Items[ClientApiRequestBufferingMiddleware.BufferedBodyItemKey]);
            Assert.Equal(original, buffered);
            Assert.Equal(0, httpContext.Request.Body.Position);
            using var copy = new MemoryStream();
            await httpContext.Request.Body.CopyToAsync(copy);
            Assert.Equal(original, copy.ToArray());
        });

        await middleware.InvokeAsync(context);

        Assert.Equal(1, calls);
        Assert.NotNull(buffered);
        Assert.All(buffered, value => Assert.Equal((byte)0, value));
        Assert.Equal(0, context.Request.Body.Position);
    }

    [Fact]
    public async Task Buffer_is_erased_even_when_the_endpoint_throws()
    {
        using var services = CreateServices();
        var context = CreateContext(services);
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{}"));
        byte[]? buffered = null;
        var middleware = new ClientApiRequestBufferingMiddleware(httpContext =>
        {
            buffered = Assert.IsType<byte[]>(httpContext.Items[ClientApiRequestBufferingMiddleware.BufferedBodyItemKey]);
            throw new InvalidOperationException("expected endpoint failure");
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context));

        Assert.NotNull(buffered);
        Assert.All(buffered, value => Assert.Equal((byte)0, value));
        Assert.Equal(0, context.Request.Body.Position);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("text/plain")]
    [InlineData("application/problem+json")]
    public async Task Unsupported_media_type_is_rejected_before_the_endpoint(string? contentType)
    {
        using var services = CreateServices();
        var context = CreateContext(services);
        context.Request.ContentType = contentType;
        var middleware = new ClientApiRequestBufferingMiddleware(_ => throw new InvalidOperationException("Must not run."));

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status415UnsupportedMediaType, context.Response.StatusCode);
        using var json = await ReadResponseAsync(context);
        Assert.Equal("unsupported_media_type", json.RootElement.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Oversized_body_with_or_without_content_length_is_a_413_not_a_500(bool hasContentLength)
    {
        using var services = CreateServices();
        var context = CreateContext(services);
        var bytes = new byte[ClientApiRequestBufferingMiddleware.MaximumBodyBytes + 8192];
        context.Request.Body = new NonSeekableReadStream(bytes);
        context.Request.ContentLength = hasContentLength ? bytes.Length : null;
        var buffering = new ClientApiRequestBufferingMiddleware(_ => throw new InvalidOperationException("Must not run."));
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var exceptions = new ClientApiExceptionHandlingMiddleware(
            buffering.InvokeAsync, loggerFactory.CreateLogger<ClientApiExceptionHandlingMiddleware>());

        try
        {
            await exceptions.InvokeAsync(context);

            Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
            using var json = await ReadResponseAsync(context);
            Assert.Equal("request_too_large", json.RootElement.GetProperty("code").GetString());
        }
        finally
        {
            await context.Request.Body.DisposeAsync();
        }
    }

    [Fact]
    public async Task Maximum_body_size_and_JSON_content_type_parameters_are_accepted()
    {
        using var services = CreateServices();
        var context = CreateContext(services);
        context.Request.ContentType = "Application/Json; charset=utf-8";
        context.Request.Body = new NonSeekableReadStream(new byte[ClientApiRequestBufferingMiddleware.MaximumBodyBytes]);
        var calls = 0;
        var middleware = new ClientApiRequestBufferingMiddleware(httpContext =>
        {
            calls++;
            Assert.Equal(ClientApiRequestBufferingMiddleware.MaximumBodyBytes,
                Assert.IsType<byte[]>(httpContext.Items[ClientApiRequestBufferingMiddleware.BufferedBodyItemKey]).Length);
            return Task.CompletedTask;
        });

        try
        {
            await middleware.InvokeAsync(context);
            Assert.Equal(1, calls);
        }
        finally
        {
            await context.Request.Body.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("/api/v1/client/not-an-endpoint", "POST")]
    [InlineData("/api/v1/client/challenges/activation", "GET")]
    [InlineData("/Account/Login", "POST")]
    public async Task Buffering_does_not_intercept_other_routes_or_methods(string path, string method)
    {
        using var services = CreateServices();
        var context = CreateContext(services);
        context.Request.Path = path;
        context.Request.Method = method;
        context.Request.ContentType = "text/plain";
        var calls = 0;
        var middleware = new ClientApiRequestBufferingMiddleware(_ =>
        {
            calls++;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);

        Assert.Equal(1, calls);
        Assert.False(context.Items.ContainsKey(ClientApiRequestBufferingMiddleware.BufferedBodyItemKey));
    }

    private static ServiceProvider CreateServices() => new ServiceCollection()
        .AddLogging()
        .AddProblemDetails()
        .BuildServiceProvider();

    private static DefaultHttpContext CreateContext(IServiceProvider services)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Path = "/api/v1/client/challenges/activation";
        context.Request.Method = HttpMethods.Post;
        context.Request.ContentType = "application/json";
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<JsonDocument> ReadResponseAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return await JsonDocument.ParseAsync(context.Response.Body);
    }

    private static Exception CreateException(string kind) => kind switch
    {
        "domain" => new DomainException(SecretCanary),
        "json" => new JsonException(SecretCanary),
        "bad-http" => new BadHttpRequestException(SecretCanary),
        "argument" => new ArgumentException(SecretCanary),
        "format" => new FormatException(SecretCanary),
        "invalid-data" => new InvalidDataException(SecretCanary),
        "concurrency" => new DbUpdateConcurrencyException(SecretCanary),
        "unique" => new DbUpdateException(SecretCanary,
            new Npgsql.PostgresException(SecretCanary, "ERROR", "ERROR", Npgsql.PostgresErrorCodes.UniqueViolation)),
        "database" => new DbUpdateException(SecretCanary,
            new Npgsql.PostgresException(SecretCanary, "ERROR", "ERROR", Npgsql.PostgresErrorCodes.ConnectionFailure)),
        _ => new InvalidOperationException(SecretCanary, new IOException(SecretCanary))
    };

    private sealed class StartedResponseFeature : HttpResponseFeature
    {
        public override bool HasStarted => true;
    }

    private sealed class NonSeekableReadStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
