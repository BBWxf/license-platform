using LicensePlatform.Web.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Xunit;

namespace LicensePlatform.Web.Tests;

public sealed class ClientRequestSignatureFilterTests
{
    [Theory]
    [InlineData("activation")]
    [InlineData("refresh")]
    [InlineData("complete")]
    [InlineData("validation")]
    public async Task Supported_authenticated_requests_reach_next_once_and_replays_are_challenged(string kind)
    {
        using var request = new SignedClientRequest(kind);
        var filter = new ClientRequestSignatureFilter(request.Verifier);
        var invocation = new DefaultEndpointFilterInvocationContext(request.Context, "unrelated-argument", request.Request);
        var calls = 0;
        var expectedResult = new object();
        EndpointFilterDelegate next = context =>
        {
            Assert.Same(invocation, context);
            calls++;
            return ValueTask.FromResult<object?>(expectedResult);
        };

        Assert.Same(expectedResult, await filter.InvokeAsync(invocation, next));
        var replay = Assert.IsType<ProblemHttpResult>(await filter.InvokeAsync(invocation, next));

        Assert.Equal(1, calls);
        Assert.Equal(StatusCodes.Status401Unauthorized, replay.StatusCode);
        Assert.Equal("invalid_client_signature", replay.ProblemDetails.Extensions["code"]);
        Assert.Equal("Signature realm=\"license-client\"", request.Context.Response.Headers.WWWAuthenticate);
    }

    [Fact]
    public async Task Missing_supported_request_returns_400_without_running_verification_or_next()
    {
        using var request = new SignedClientRequest();
        var filter = new ClientRequestSignatureFilter(request.Verifier);
        var invocation = new DefaultEndpointFilterInvocationContext(request.Context, "unrelated-argument", new object());

        var result = Assert.IsType<ProblemHttpResult>(await filter.InvokeAsync(invocation, _ =>
            throw new InvalidOperationException("The endpoint must not run.")));

        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
        Assert.Equal("invalid_request", result.ProblemDetails.Extensions["code"]);
        Assert.Equal(0, request.Resolver.Calls);
        Assert.False(request.Context.Response.Headers.ContainsKey("WWW-Authenticate"));
    }

    [Fact]
    public async Task Invalid_signature_is_a_generic_401_with_a_request_identifier()
    {
        using var request = new SignedClientRequest();
        request.Context.TraceIdentifier = "test-request-id";
        request.Context.Request.Headers.Remove("X-Licensing-Signature");
        var filter = new ClientRequestSignatureFilter(request.Verifier);
        var invocation = new DefaultEndpointFilterInvocationContext(request.Context, request.Request);

        var result = Assert.IsType<ProblemHttpResult>(await filter.InvokeAsync(invocation, _ =>
            throw new InvalidOperationException("The endpoint must not run.")));

        Assert.Equal(StatusCodes.Status401Unauthorized, result.StatusCode);
        Assert.Equal("invalid_client_signature", result.ProblemDetails.Extensions["code"]);
        Assert.Equal("test-request-id", result.ProblemDetails.Extensions["requestId"]);
        Assert.Null(result.ProblemDetails.Detail);
        Assert.Equal(2, result.ProblemDetails.Extensions.Count);
    }
}
