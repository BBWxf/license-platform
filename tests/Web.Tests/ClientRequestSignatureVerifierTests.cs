using System.Globalization;
using System.Security.Cryptography;
using LicensePlatform.Web.Middleware;
using LicensePlatform.Web.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace LicensePlatform.Web.Tests;

public sealed class ClientRequestSignatureVerifierTests
{
    [Theory]
    [InlineData("activation")]
    [InlineData("refresh")]
    [InlineData("complete")]
    [InlineData("validation")]
    public async Task Valid_ES256_requests_verify_once_and_pass_the_request_to_key_resolution(string kind)
    {
        using var request = new SignedClientRequest(kind);

        Assert.True(await request.VerifyAsync());
        Assert.Same(request.Request, request.Resolver.LastRequest);
        Assert.False(await request.VerifyAsync());
    }

    [Theory]
    [InlineData(-300, true)]
    [InlineData(300, true)]
    [InlineData(-301, false)]
    [InlineData(301, false)]
    public async Task Default_clock_skew_has_inclusive_boundaries(int offsetSeconds, bool expected)
    {
        using var request = new SignedClientRequest();
        request.Context.Request.Headers["X-Licensing-Timestamp"] =
            (request.Clock.UtcNow.ToUnixTimeSeconds() + offsetSeconds).ToString(CultureInfo.InvariantCulture);
        request.Sign();

        Assert.Equal(expected, await request.VerifyAsync());
    }

    [Theory]
    [InlineData(30, 30, true)]
    [InlineData(30, -31, false)]
    [InlineData(900, -900, true)]
    [InlineData(900, 901, false)]
    public async Task Configured_clock_skew_is_used(int skew, int offsetSeconds, bool expected)
    {
        using var request = new SignedClientRequest(clockSkewSeconds: skew);
        request.Context.Request.Headers["X-Licensing-Timestamp"] =
            (request.Clock.UtcNow.ToUnixTimeSeconds() + offsetSeconds).ToString(CultureInfo.InvariantCulture);
        request.Sign();

        Assert.Equal(expected, await request.VerifyAsync());
    }

    [Theory]
    [InlineData(29)]
    [InlineData(901)]
    public void Unsafe_clock_skew_configuration_is_rejected(int skew)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Security:ClientRequestClockSkewSeconds"] = skew.ToString(CultureInfo.InvariantCulture)
        }).Build();

        Assert.Throws<InvalidOperationException>(() => new ClientRequestSignatureVerifier(
            new StubPublicKeyResolver(), new TestClock(), new ClientRequestReplayCache(), configuration));
    }

    public static TheoryData<string, string> InvalidHeaders => new()
    {
        { "X-Licensing-Installation", "" },
        { "X-Licensing-Installation", Guid.Empty.ToString("N") },
        { "X-Licensing-Installation", "ABCDEFABCDEF4ABC8ABCABCDEFABCDEF" },
        { "X-Licensing-Installation", "abcdefab-cdef-4abc-8abc-abcdefabcdef" },
        { "X-Licensing-Timestamp", "01759320000" },
        { "X-Licensing-Timestamp", "+1759320000" },
        { "X-Licensing-Timestamp", "-1" },
        { "X-Licensing-Timestamp", " 1759320000" },
        { "X-Licensing-Timestamp", "9223372036854775808" },
        { "X-Licensing-Nonce", "AA==" },
        { "X-Licensing-Nonce", new string('A', 21) + "B" },
        { "X-Licensing-Nonce", new string('A', 21) },
        { "X-Licensing-Nonce", new string('A', 22) + "=" },
        { "X-Licensing-Nonce", new string('A', 21) + "+" },
        { "X-Licensing-Content-SHA256", new string('A', 42) + "B" },
        { "X-Licensing-Content-SHA256", new string('A', 42) },
        { "X-Licensing-Key-Id", "" },
        { "X-Licensing-Key-Id", new string('a', 129) },
        { "X-Licensing-Key-Id", new string('a', 43) },
        { "X-Licensing-Algorithm", "es256" },
        { "X-Licensing-Algorithm", "RS256" },
        { "X-Licensing-Signature", new string('A', 86) },
        { "X-Licensing-Signature", new string('A', 85) },
        { "X-Licensing-Signature", new string('A', 85) + "B" }
    };

    [Theory]
    [MemberData(nameof(InvalidHeaders))]
    public async Task Malformed_or_incorrect_headers_fail_closed(string name, string value)
    {
        using var request = new SignedClientRequest();
        var original = request.Context.Request.Headers[name];
        request.Context.Request.Headers[name] = value;

        Assert.False(await request.VerifyAsync());

        request.Context.Request.Headers[name] = original;
        Assert.True(await request.VerifyAsync());
    }

    [Theory]
    [InlineData("X-Licensing-Installation")]
    [InlineData("X-Licensing-Timestamp")]
    [InlineData("X-Licensing-Nonce")]
    [InlineData("X-Licensing-Content-SHA256")]
    [InlineData("X-Licensing-Key-Id")]
    [InlineData("X-Licensing-Algorithm")]
    [InlineData("X-Licensing-Signature")]
    public async Task Every_authentication_header_must_be_present_exactly_once(string name)
    {
        using var request = new SignedClientRequest();
        var original = request.Context.Request.Headers[name].ToString();
        request.Context.Request.Headers.Remove(name);
        Assert.False(await request.VerifyAsync());

        request.Context.Request.Headers[name] = new StringValues([original, original]);
        Assert.False(await request.VerifyAsync());

        request.Context.Request.Headers[name] = original;
        Assert.True(await request.VerifyAsync());
    }

    [Fact]
    public async Task Body_tampering_does_not_consume_the_nonce()
    {
        using var request = new SignedClientRequest();
        var modifiedBody = (byte[])request.Body.Clone();
        modifiedBody[^1] ^= 1;
        request.Context.Items[ClientApiRequestBufferingMiddleware.BufferedBodyItemKey] = modifiedBody;

        Assert.False(await request.VerifyAsync());
        Assert.Equal(0, request.Resolver.Calls);

        request.Context.Items[ClientApiRequestBufferingMiddleware.BufferedBodyItemKey] = request.Body;
        Assert.True(await request.VerifyAsync());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("wrong-type")]
    public async Task Buffered_request_body_is_required(string bodyState)
    {
        using var request = new SignedClientRequest();
        request.Context.Items.Remove(ClientApiRequestBufferingMiddleware.BufferedBodyItemKey);
        if (bodyState != "missing")
        {
            request.Context.Items[ClientApiRequestBufferingMiddleware.BufferedBodyItemKey] =
                bodyState == "empty" ? Array.Empty<byte>() : "not-buffered-by-middleware";
        }

        Assert.False(await request.VerifyAsync());
        Assert.Equal(0, request.Resolver.Calls);
    }

    [Fact]
    public async Task Signature_covers_exact_escaped_raw_target_including_query_string()
    {
        using var request = new SignedClientRequest();
        var feature = request.Context.Features.Get<IHttpRequestFeature>()!;
        feature.RawTarget = "/api/v1/client/challenges/activation?next=%2f&value=a%20b";
        request.Sign();
        feature.RawTarget = "/api/v1/client/challenges/activation?next=%2F&value=a%20b";

        Assert.False(await request.VerifyAsync());

        feature.RawTarget = "/api/v1/client/challenges/activation?next=%2f&value=a%20b";
        Assert.True(await request.VerifyAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("api/v1/client/challenges/activation")]
    [InlineData("https://example.invalid/api/v1/client/challenges/activation")]
    [InlineData("/api/v1/client/challenges/activation#fragment")]
    public async Task Missing_or_ambiguous_raw_targets_are_rejected(string? rawTarget)
    {
        using var request = new SignedClientRequest();
        request.Context.Features.Get<IHttpRequestFeature>()!.RawTarget = rawTarget!;

        Assert.False(await request.VerifyAsync());
    }

    [Fact]
    public async Task Overlong_raw_target_is_rejected()
    {
        using var request = new SignedClientRequest();
        request.Context.Features.Get<IHttpRequestFeature>()!.RawTarget = "/" + new string('x', 2048);
        request.Sign();

        Assert.False(await request.VerifyAsync());
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Only_POST_is_authenticated(string method)
    {
        using var request = new SignedClientRequest();
        request.Context.Request.Method = method;

        Assert.False(await request.VerifyAsync());
        Assert.Equal(0, request.Resolver.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-public-key")]
    [InlineData("-----BEGIN PRIVATE KEY-----\ninvalid\n-----END PRIVATE KEY-----")]
    public async Task Missing_malformed_or_private_public_keys_fail_closed(string? publicKey)
    {
        using var request = new SignedClientRequest();
        request.Resolver.PublicKey = publicKey;

        Assert.False(await request.VerifyAsync());
    }

    [Fact]
    public async Task Wrong_curve_and_different_P256_keys_are_rejected()
    {
        using var request = new SignedClientRequest();
        using var wrongCurve = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        request.Resolver.PublicKey = wrongCurve.ExportSubjectPublicKeyInfoPem();
        Assert.False(await request.VerifyAsync());

        using var differentKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        request.Resolver.PublicKey = differentKey.ExportSubjectPublicKeyInfoPem();
        Assert.False(await request.VerifyAsync());
    }

    [Fact]
    public async Task Overlong_public_key_is_rejected()
    {
        using var request = new SignedClientRequest();
        request.Resolver.PublicKey = new string('x', 2049);

        Assert.False(await request.VerifyAsync());
    }

    [Fact]
    public async Task Persistence_failures_propagate_to_the_sanitizing_API_middleware()
    {
        using var request = new SignedClientRequest("complete");
        var failure = new InvalidOperationException("synthetic persistence failure");
        request.Resolver.Failure = failure;

        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => request.VerifyAsync()));
    }

    [Fact]
    public async Task Request_cancellation_is_passed_through_to_the_key_resolver()
    {
        using var request = new SignedClientRequest("validation");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        request.Context.RequestAborted = cancellation.Token;
        request.Resolver.Failure = new OperationCanceledException(cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.VerifyAsync());
        Assert.Equal(cancellation.Token, request.Resolver.LastCancellationToken);
    }
}
