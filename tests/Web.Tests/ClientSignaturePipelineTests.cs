using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using LicensePlatform.Contracts.Challenges;
using LicensePlatform.Web.Middleware;
using LicensePlatform.Web.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LicensePlatform.Web.Tests;

public sealed class ClientSignaturePipelineTests
{
    [Theory]
    [InlineData("activation")]
    [InlineData("refresh")]
    [InlineData("complete")]
    [InlineData("validation")]
    public async Task JSON_binding_buffering_signature_filter_and_replay_work_together_over_HTTP(string kind)
    {
        using var signed = new SignedClientRequest(kind);
        var calls = 0;
        using var host = new HostBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureWebHost(web => web.UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddProblemDetails();
                })
                .Configure(app =>
                {
                    app.Use(async (context, next) =>
                    {
                        // TestServer omits Kestrel's RawTarget transport feature. These routes have
                        // no escaping/query; exact escaped-target verification is tested separately.
                        context.Features.Get<IHttpRequestFeature>()!.RawTarget = context.Request.Path.Value!;
                        await next();
                    });
                    app.UseRouting();
                    app.UseMiddleware<ClientApiExceptionHandlingMiddleware>();
                    app.UseMiddleware<ClientApiRequestBufferingMiddleware>();
                    app.UseEndpoints(endpoints =>
                    {
                        var group = endpoints.MapGroup(ClientApiPaths.Prefix)
                            .AddEndpointFilter(new ClientRequestSignatureFilter(signed.Verifier));
                        group.MapPost("/challenges/activation", (CreateActivationChallengeRequest request) => Accepted());
                        group.MapPost("/challenges/refresh", (CreateLeaseRefreshChallengeRequest request) => Accepted());
                        group.MapPost("/challenges/complete", (CompleteActivationChallengeRequest request) => Accepted());
                        group.MapPost("/validations", (ValidateLicenseRequest request) => Accepted());
                    });
                }))
            .Build();
        await host.StartAsync();
        using var client = host.GetTestClient();
        using var original = CreateMessage(signed);
        using var accepted = await client.SendAsync(original);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(1, calls);

        using var replay = CreateMessage(signed);
        using var rejected = await client.SendAsync(replay);
        await AssertSignatureFailureAsync(rejected);
        Assert.Equal(1, calls);

        using var modified = CreateMessage(signed, [.. signed.Body, (byte)' ']);
        using var tampered = await client.SendAsync(modified);
        await AssertSignatureFailureAsync(tampered);
        Assert.Equal(1, calls);
        await host.StopAsync();

        IResult Accepted()
        {
            calls++;
            return Results.Ok(new { accepted = true });
        }
    }

    private static HttpRequestMessage CreateMessage(SignedClientRequest signed, byte[]? body = null)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, signed.Context.Request.Path.Value);
        message.Content = new ByteArrayContent(body ?? signed.Body);
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        foreach (var header in signed.Context.Request.Headers)
        {
            if (header.Key.StartsWith("X-Licensing-", StringComparison.Ordinal))
            {
                message.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        }

        return message;
    }

    private static async Task AssertSignatureFailureAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("Signature realm=\"license-client\"", response.Headers.WwwAuthenticate.ToString());
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("invalid_client_signature", json.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("requestId").GetString()));
    }
}
