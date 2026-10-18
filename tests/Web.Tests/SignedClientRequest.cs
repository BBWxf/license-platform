using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LicensePlatform.Contracts.Challenges;
using LicensePlatform.Contracts.Licenses;
using LicensePlatform.Web.Middleware;
using LicensePlatform.Web.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;

namespace LicensePlatform.Web.Tests;

internal sealed class SignedClientRequest : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public SignedClientRequest(string kind = "activation", int? clockSkewSeconds = null)
    {
        var fingerprint = new DeviceFingerprintDto("test-machine", "test-board", "test-volume");
        var publicKey = _key.ExportSubjectPublicKeyInfoPem();
        (string Path, object Payload) route = kind switch
        {
            "activation" => ("/api/v1/client/challenges/activation", new CreateActivationChallengeRequest(
                "test-product", "test-card", fingerprint, "test-device", publicKey)),
            "refresh" => ("/api/v1/client/challenges/refresh", new CreateLeaseRefreshChallengeRequest(
                Guid.NewGuid(), 1, fingerprint, publicKey)),
            "complete" => ("/api/v1/client/challenges/complete", new CompleteActivationChallengeRequest(
                "test-challenge-token", fingerprint, "test-proof")),
            "validation" => ("/api/v1/client/validations", new ValidateLicenseRequest(Guid.NewGuid(), 1, fingerprint)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        Request = route.Payload;
        Resolver = new StubPublicKeyResolver { PublicKey = publicKey };
        var settings = new Dictionary<string, string?>();
        if (clockSkewSeconds.HasValue)
        {
            settings["Security:ClientRequestClockSkewSeconds"] = clockSkewSeconds.Value.ToString(CultureInfo.InvariantCulture);
        }

        Verifier = new ClientRequestSignatureVerifier(
            Resolver, Clock, ReplayCache, new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        Context.Request.Method = HttpMethods.Post;
        Context.Request.Path = route.Path;
        Context.Request.ContentType = "application/json";
        Context.Features.Get<IHttpRequestFeature>()!.RawTarget = route.Path;
        Body = JsonSerializer.SerializeToUtf8Bytes(Request, Request.GetType());
        Context.Items[ClientApiRequestBufferingMiddleware.BufferedBodyItemKey] = Body;
        Context.Request.Headers["X-Licensing-Installation"] = Guid.NewGuid().ToString("N");
        Context.Request.Headers["X-Licensing-Timestamp"] = Clock.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        Context.Request.Headers["X-Licensing-Nonce"] = Base64Url(RandomNumberGenerator.GetBytes(16));
        Context.Request.Headers["X-Licensing-Content-SHA256"] = Base64Url(SHA256.HashData(Body));
        Context.Request.Headers["X-Licensing-Key-Id"] = Thumbprint(_key);
        Context.Request.Headers["X-Licensing-Algorithm"] = "ES256";
        Sign();
    }

    public DefaultHttpContext Context { get; } = new();
    public TestClock Clock { get; } = new();
    public ClientRequestReplayCache ReplayCache { get; } = new();
    public StubPublicKeyResolver Resolver { get; }
    public ClientRequestSignatureVerifier Verifier { get; }
    public object Request { get; }
    public byte[] Body { get; }

    public Task<bool> VerifyAsync() => Verifier.VerifyAsync(Context, Request, Context.RequestAborted);

    public void Sign(string? rawTarget = null)
    {
        var headers = Context.Request.Headers;
        var canonical = string.Join('\n',
            "licensing-v1",
            "POST",
            rawTarget ?? Context.Features.Get<IHttpRequestFeature>()!.RawTarget,
            headers["X-Licensing-Timestamp"].ToString(),
            headers["X-Licensing-Nonce"].ToString(),
            headers["X-Licensing-Content-SHA256"].ToString());
        headers["X-Licensing-Signature"] = Base64Url(_key.SignData(
            Encoding.UTF8.GetBytes(canonical), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    public void Dispose() => _key.Dispose();

    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Thumbprint(ECDsa key)
    {
        var parameters = key.ExportParameters(includePrivateParameters: false);
        var jwk = $"{{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"{Base64Url(parameters.Q.X!)}\",\"y\":\"{Base64Url(parameters.Q.Y!)}\"}}";
        return Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(jwk)));
    }
}

internal sealed class StubPublicKeyResolver : IClientRequestPublicKeyResolver
{
    public string? PublicKey { get; set; }
    public Exception? Failure { get; set; }
    public int Calls { get; private set; }
    public object? LastRequest { get; private set; }
    public CancellationToken LastCancellationToken { get; private set; }

    public Task<string?> ResolveAsync(object request, CancellationToken cancellationToken)
    {
        Calls++;
        LastRequest = request;
        LastCancellationToken = cancellationToken;
        return Failure is null ? Task.FromResult(PublicKey) : Task.FromException<string?>(Failure);
    }
}
