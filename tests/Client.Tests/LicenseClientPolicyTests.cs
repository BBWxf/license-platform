using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Licensing.Client;
using Licensing.Client.Windows;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Client.Tests;

public sealed class LicenseClientPolicyTests
{
    private static readonly Guid TestLicenseId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task Startup_ActivatesAndCachesSignedLease()
    {
        using var environment = new TestEnvironment();
        var result = await environment.CreateClient().StartAsync("license-A");

        Assert.True(result.Success, result.Error?.ToString());
        Assert.False(result.UsedOfflineFallback);
        Assert.Equal(2, environment.Handler.RequestCount);
        Assert.NotNull(environment.StateStore.Record);
        Assert.DoesNotContain("license-A", JsonConvert.SerializeObject(environment.StateStore.Record));
        Assert.Equal(TestLicenseId.ToString("D"), result.Lease!.LicenseId);
    }

    [Fact]
    public async Task Startup_WithCachedStateDoesNotRequireLicenseKey()
    {
        using var environment = new TestEnvironment();
        Assert.True((await environment.CreateClient().StartAsync("license-A")).Success);
        var activationRequestCount = environment.Handler.RequestCount;

        environment.Clock.UtcNow = environment.Clock.UtcNow.AddMinutes(20);
        var result = await environment.CreateClient().StartAsync();

        Assert.True(result.Success, result.Error?.ToString());
        Assert.False(result.UsedOfflineFallback);
        Assert.Equal(activationRequestCount + 1, environment.Handler.RequestCount);
        Assert.False(environment.Handler.Requests.Last().Body.Contains("license-A", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Startup_WithoutCacheRequestsOneTimeActivation()
    {
        using var environment = new TestEnvironment();

        var result = await environment.CreateClient().StartAsync();

        Assert.False(result.Success);
        Assert.Equal(LicenseErrorCode.ActivationRequired, result.Error!.Code);
        Assert.Equal(0, environment.Handler.RequestCount);
    }

    [Fact]
    public async Task Startup_NetworkFailureUsesOnlyStillValidSignedCache()
    {
        using var environment = new TestEnvironment();
        Assert.True((await environment.CreateClient().StartAsync("license-A")).Success);

        environment.Handler.Mode = HandlerMode.NetworkFailure;
        environment.Clock.UtcNow = environment.Clock.UtcNow.AddMinutes(20);
        var result = await environment.CreateClient().StartAsync("license-A");

        Assert.True(result.Success, result.Error?.ToString());
        Assert.True(result.UsedOfflineFallback);
    }

    [Fact]
    public async Task Startup_Http5xxUsesOnlyStillValidSignedCache()
    {
        using var environment = new TestEnvironment();
        Assert.True((await environment.CreateClient().StartAsync("license-A")).Success);

        environment.Handler.Mode = HandlerMode.ServerFailure;
        var result = await environment.CreateClient().StartAsync("license-A");

        Assert.True(result.Success, result.Error?.ToString());
        Assert.True(result.UsedOfflineFallback);
    }

    [Fact]
    public async Task LeaseLongerThanSixHoursIsRejected()
    {
        using var environment = new TestEnvironment();
        environment.Handler.Mode = HandlerMode.OverlongLease;

        var result = await environment.CreateClient().StartAsync("license-A");

        Assert.False(result.Success);
        Assert.Equal(LicenseErrorCode.LeaseLifetimeExceeded, result.Error!.Code);
        Assert.Null(environment.StateStore.Record);
    }

    [Fact]
    public async Task Startup_BusinessRejectionClearsCacheAndDoesNotFallback()
    {
        using var environment = new TestEnvironment();
        Assert.True((await environment.CreateClient().StartAsync("license-A")).Success);

        environment.Handler.Mode = HandlerMode.BusinessRejection;
        var result = await environment.CreateClient().StartAsync("license-A");

        Assert.False(result.Success);
        Assert.Equal(LicenseErrorCode.BusinessRejected, result.Error!.Code);
        Assert.Null(environment.StateStore.Record);
        Assert.Equal(1, environment.StateStore.ClearCount);
    }

    [Fact]
    public async Task RuntimeCallBeforeTwoHoursUsesCacheWithoutHttp()
    {
        using var environment = new TestEnvironment();
        var client = environment.CreateClient();
        Assert.True((await client.StartAsync("license-A")).Success);
        var requestsAfterActivation = environment.Handler.RequestCount;

        environment.Clock.UtcNow = environment.Clock.UtcNow.AddMinutes(90);
        var result = await client.GetLeaseAsync("license-A");

        Assert.True(result.Success, result.Error?.ToString());
        Assert.False(result.UsedOfflineFallback);
        Assert.Equal(requestsAfterActivation, environment.Handler.RequestCount);
    }

    [Fact]
    public async Task RuntimeCallAtTwoHoursRefreshesOnline()
    {
        using var environment = new TestEnvironment();
        var client = environment.CreateClient();
        Assert.True((await client.StartAsync("license-A")).Success);
        var requestsAfterActivation = environment.Handler.RequestCount;

        environment.Clock.UtcNow = environment.Clock.UtcNow.AddHours(2).AddMinutes(1);
        var result = await client.GetLeaseAsync("license-A");

        Assert.True(result.Success, result.Error?.ToString());
        Assert.Equal(requestsAfterActivation + 3, environment.Handler.RequestCount);
    }

    [Fact]
    public async Task ClockRollbackBlocksOfflineFallback()
    {
        using var environment = new TestEnvironment();
        var client = environment.CreateClient();
        Assert.True((await client.StartAsync("license-A")).Success);

        var issuedAt = environment.Clock.UtcNow;
        environment.Clock.UtcNow = issuedAt.AddHours(1);
        Assert.True((await client.GetLeaseAsync("license-A")).Success);

        environment.Clock.UtcNow = issuedAt.AddMinutes(20);
        environment.Handler.Mode = HandlerMode.NetworkFailure;
        var result = await environment.CreateClient().StartAsync("license-A");

        Assert.False(result.Success);
        Assert.Equal(LicenseErrorCode.TrustedTimeRollback, result.Error!.Code);
    }

    [Fact]
    public async Task OneChangedHardwareComponentStillAllowsTwoOfThreeOfflineMatch()
    {
        using var environment = new TestEnvironment();
        Assert.True((await environment.CreateClient().StartAsync("license-A")).Success);

        environment.FingerprintProvider.Value = TestFingerprint("a", "b", "changed-c");
        environment.Handler.Mode = HandlerMode.NetworkFailure;
        var result = await environment.CreateClient().StartAsync("license-A");

        Assert.True(result.Success, result.Error?.ToString());
        Assert.True(result.UsedOfflineFallback);
    }

    [Fact]
    public async Task HttpRequestsCarryInstallationKeySignatures()
    {
        using var environment = new TestEnvironment();
        var client = environment.CreateClient();
        Assert.True((await client.StartAsync("license-A")).Success);
        environment.Clock.UtcNow = environment.Clock.UtcNow.AddHours(2).AddMinutes(1);
        Assert.True((await client.GetLeaseAsync("license-A")).Success);

        Assert.Contains(environment.Handler.Requests, request => request.PathAndQuery == "/api/v1/client/challenges/activation");
        Assert.Contains(environment.Handler.Requests, request => request.PathAndQuery == "/api/v1/client/challenges/refresh");
        Assert.Contains(environment.Handler.Requests, request => request.PathAndQuery == "/api/v1/client/challenges/complete");
        Assert.Contains(environment.Handler.Requests, request => request.PathAndQuery == "/api/v1/client/validations");
        Assert.All(environment.Handler.Requests.Where(request => request.PathAndQuery != "/api/v1/client/challenges/activation"), request =>
            Assert.False(request.Body.Contains("license-A", StringComparison.Ordinal), "Refresh and validation requests must not resubmit the license key."));

        Assert.All(environment.Handler.Requests, request =>
        {
            Assert.Equal("ES256", request.Headers["X-Licensing-Algorithm"]);
            Assert.False(string.IsNullOrWhiteSpace(request.Headers["X-Licensing-Signature"]));
            Assert.False(string.IsNullOrWhiteSpace(request.Headers["X-Licensing-Nonce"]));
            Assert.False(string.IsNullOrWhiteSpace(request.Headers["X-Licensing-Content-SHA256"]));
            Assert.Equal(Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(request.Body))), request.Headers["X-Licensing-Content-SHA256"]);

            var canonical = string.Join("\n", new[]
            {
                "licensing-v1",
                request.Method,
                request.PathAndQuery,
                request.Headers["X-Licensing-Timestamp"],
                request.Headers["X-Licensing-Nonce"],
                request.Headers["X-Licensing-Content-SHA256"]
            });
            var installation = environment.KeyStore.Record!;
            using var publicKey = ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = installation.X, Y = installation.Y }
            });
            Assert.True(publicKey.VerifyData(
                Encoding.UTF8.GetBytes(canonical),
                Base64UrlDecode(request.Headers["X-Licensing-Signature"]),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        });
    }

    [Fact]
    public void Constructor_ForciblyDisablesBuiltInAutomaticRedirects()
    {
        using var environment = new TestEnvironment();
        using var handler = new HttpClientHandler { AllowAutoRedirect = true };
        using var client = new LicenseClient(
            handler,
            environment.CreateOptions(),
            environment.FingerprintProvider,
            environment.KeyStore,
            environment.StateStore,
            environment.Clock);

        Assert.False(handler.AllowAutoRedirect);
    }

    [Fact]
    public async Task Startup_RedirectResponseDoesNotUseOfflineCache()
    {
        using var environment = new TestEnvironment();
        using var activationClient = environment.CreateClient();
        Assert.True((await activationClient.StartAsync("license-A")).Success);

        environment.Handler.Mode = HandlerMode.Redirect;
        using var validationClient = environment.CreateClient();
        var result = await validationClient.StartAsync();

        Assert.False(result.Success);
        Assert.Equal(LicenseErrorCode.InvalidResponse, result.Error!.Code);
        Assert.NotNull(environment.StateStore.Record);
    }

    [Fact]
    public async Task Startup_RejectsCrossOriginResponseFromCustomHandler()
    {
        using var environment = new TestEnvironment();
        using var activationClient = environment.CreateClient();
        Assert.True((await activationClient.StartAsync("license-A")).Success);

        environment.Handler.Mode = HandlerMode.CrossOriginResponse;
        using var validationClient = environment.CreateClient();
        var result = await validationClient.StartAsync();

        Assert.False(result.Success);
        Assert.Equal(LicenseErrorCode.InvalidResponse, result.Error!.Code);
        Assert.NotNull(environment.StateStore.Record);
    }

    [Fact]
    public async Task DisposingClientDoesNotDisposeCallerOwnedHandlerOrOtherClients()
    {
        using var environment = new TestEnvironment();
        using var outer = new PassThroughHandler(new PassThroughHandler(environment.Handler));
        var first = environment.CreateClient(handler: outer);
        using var second = environment.CreateClient(handler: outer);
        Assert.True((await first.StartAsync("license-A")).Success);

        first.Dispose();
        first.Dispose();
        var result = await second.StartAsync();

        Assert.True(result.Success, result.Error?.ToString());
        Assert.Equal(0, outer.DisposeCount);
        Assert.Equal(0, environment.Handler.DisposeCount);
        outer.Dispose();
        Assert.Equal(1, environment.Handler.DisposeCount);
    }

    [Fact]
    public void ConstructorDisablesRedirectsThroughNestedDelegatingHandlers()
    {
        using var environment = new TestEnvironment();
        using var primary = new HttpClientHandler { AllowAutoRedirect = true };
        using var outer = new PassThroughHandler(new PassThroughHandler(primary));
        using var client = environment.CreateClient(handler: outer);

        Assert.False(primary.AllowAutoRedirect);
        Assert.Equal(0, outer.DisposeCount);
    }

    [Fact]
    public void ConstructorRejectsIncompleteHandlerChainWithoutTakingOwnership()
    {
        using var environment = new TestEnvironment();
        using var outer = new PassThroughHandler();

        var exception = Assert.Throws<LicensingException>(() => environment.CreateClient(handler: outer));

        Assert.Equal(LicenseErrorCode.InvalidConfiguration, exception.Code);
        Assert.Equal(0, outer.DisposeCount);
        Assert.Equal(0, environment.Handler.DisposeCount);
    }

    [Theory]
    [InlineData("segments", LicenseErrorCode.LeaseMalformed)]
    [InlineData("empty-segment", LicenseErrorCode.LeaseMalformed)]
    [InlineData("invalid-base64", LicenseErrorCode.LeaseMalformed)]
    [InlineData("padded-signature", LicenseErrorCode.LeaseMalformed)]
    [InlineData("whitespace-signature", LicenseErrorCode.LeaseMalformed)]
    [InlineData("noncanonical-signature", LicenseErrorCode.LeaseMalformed)]
    [InlineData("short-signature", LicenseErrorCode.InvalidServerSignature)]
    [InlineData("tampered-payload", LicenseErrorCode.InvalidServerSignature)]
    [InlineData("duplicate-header", LicenseErrorCode.LeaseMalformed)]
    [InlineData("duplicate-payload", LicenseErrorCode.LeaseMalformed)]
    [InlineData("invalid-header-utf8", LicenseErrorCode.LeaseMalformed)]
    [InlineData("invalid-payload-utf8", LicenseErrorCode.LeaseMalformed)]
    [InlineData("algorithm-none", LicenseErrorCode.UnsupportedAlgorithm)]
    [InlineData("algorithm-object", LicenseErrorCode.LeaseMalformed)]
    [InlineData("kid-object", LicenseErrorCode.LeaseMalformed)]
    [InlineData("unknown-key", LicenseErrorCode.UnknownSigningKey)]
    [InlineData("critical-header", LicenseErrorCode.UnsupportedAlgorithm)]
    [InlineData("unencoded-payload", LicenseErrorCode.UnsupportedAlgorithm)]
    [InlineData("invalid-b64-header", LicenseErrorCode.UnsupportedAlgorithm)]
    [InlineData("issuer-object", LicenseErrorCode.LeaseMalformed)]
    [InlineData("wrong-issuer", LicenseErrorCode.LeaseMalformed)]
    [InlineData("wrong-audience", LicenseErrorCode.LeaseMalformed)]
    [InlineData("binding-overflow", LicenseErrorCode.LeaseMalformed)]
    [InlineData("binding-string", LicenseErrorCode.LeaseMalformed)]
    [InlineData("future-lease", LicenseErrorCode.LeaseNotYetValid)]
    [InlineData("expired-lease", LicenseErrorCode.LeaseExpired)]
    [InlineData("wrong-installation", LicenseErrorCode.LeaseBindingMismatch)]
    [InlineData("wrong-product", LicenseErrorCode.LeaseBindingMismatch)]
    public async Task InvalidJwsIsRejectedWithoutCaching(string scenario, LicenseErrorCode expected)
    {
        using var environment = new TestEnvironment();
        switch (scenario)
        {
            case "segments": environment.TokenTransform = _ => "one.two"; break;
            case "empty-segment": environment.TokenTransform = _ => "one..three"; break;
            case "invalid-base64": environment.TokenTransform = token => "!" + token; break;
            case "padded-signature": environment.TokenTransform = token => token + "=="; break;
            case "whitespace-signature": environment.TokenTransform = token => token + "\n   "; break;
            case "noncanonical-signature":
                environment.TokenTransform = token => token[..^1] + "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_"[
                    "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_".IndexOf(token[^1]) + 1];
                break;
            case "short-signature": environment.TokenTransform = token => token[..(token.LastIndexOf('.') + 1)] + "AA"; break;
            case "tampered-payload":
                environment.TokenTransform = token =>
                {
                    var parts = token.Split('.');
                    var payload = JObject.Parse(Encoding.UTF8.GetString(Base64UrlDecode(parts[1])));
                    payload["device_id"] = "tampered";
                    parts[1] = Base64Url(Encoding.UTF8.GetBytes(payload.ToString(Formatting.None)));
                    return string.Join(".", parts);
                };
                break;
            case "duplicate-header": environment.HeaderBytesTransform = _ => Encoding.UTF8.GetBytes("{\"alg\":\"ES256\",\"alg\":\"none\",\"kid\":\"server-1\"}"); break;
            case "duplicate-payload": environment.PayloadBytesTransform = _ => Encoding.UTF8.GetBytes("{\"iss\":\"test-issuer\",\"iss\":\"other\"}"); break;
            case "invalid-header-utf8": environment.HeaderBytesTransform = bytes => InvalidUtf8Property(bytes); break;
            case "invalid-payload-utf8": environment.PayloadBytesTransform = bytes => InvalidUtf8Property(bytes); break;
            case "algorithm-none": environment.HeaderMutation = header => header["alg"] = "none"; break;
            case "algorithm-object": environment.HeaderMutation = header => header["alg"] = new JObject(); break;
            case "kid-object": environment.HeaderMutation = header => header["kid"] = new JObject(); break;
            case "unknown-key": environment.HeaderMutation = header => header["kid"] = "unknown"; break;
            case "critical-header": environment.HeaderMutation = header => header["crit"] = new JArray("custom"); break;
            case "unencoded-payload": environment.HeaderMutation = header => header["b64"] = false; break;
            case "invalid-b64-header": environment.HeaderMutation = header => header["b64"] = "true"; break;
            case "issuer-object": environment.PayloadMutation = payload => payload["iss"] = new JObject(); break;
            case "wrong-issuer": environment.PayloadMutation = payload => payload["iss"] = "untrusted"; break;
            case "wrong-audience": environment.PayloadMutation = payload => payload["aud"] = "other-product"; break;
            case "binding-overflow": environment.PayloadMutation = payload => payload["binding_version"] = JToken.Parse("9223372036854775808"); break;
            case "binding-string": environment.PayloadMutation = payload => payload["binding_version"] = "1"; break;
            case "future-lease": environment.PayloadMutation = payload => payload["nbf"] = environment.Clock.UtcNow.AddMinutes(10).ToUnixTimeSeconds(); break;
            case "expired-lease":
                environment.PayloadMutation = payload =>
                {
                    payload["iat"] = environment.Clock.UtcNow.AddHours(-7).ToUnixTimeSeconds();
                    payload["nbf"] = payload["iat"];
                    payload["exp"] = environment.Clock.UtcNow.AddHours(-1).ToUnixTimeSeconds();
                };
                break;
            case "wrong-installation": environment.PayloadMutation = payload => payload["installation_key_thumbprint"] = "other-installation"; break;
            case "wrong-product": environment.PayloadMutation = payload => payload["product_code"] = "OTHER"; break;
            default: throw new ArgumentException("Unknown scenario", nameof(scenario));
        }
        using var client = environment.CreateClient();

        var result = await client.StartAsync("license-A");

        Assert.False(result.Success);
        Assert.False(result.UsedOfflineFallback);
        Assert.Equal(expected, result.Error!.Code);
        Assert.Null(environment.StateStore.Record);
    }

    [Fact]
    public async Task RemovedRevokedKeyCannotAuthorizeCachedLeaseEvenWhenOffline()
    {
        using var environment = new TestEnvironment();
        using var first = environment.CreateClient();
        Assert.True((await first.StartAsync("license-A")).Success);
        environment.Handler.Mode = HandlerMode.NetworkFailure;
        var requests = environment.Handler.RequestCount;
        var updatedTrust = environment.CreateOptions();
        // Revocation is distributed by a controlled trust-set update excluding the old kid.
        updatedTrust.ServerVerificationKeys[0].Kid = "replacement-key";
        using var afterRevocation = environment.CreateClient(options: updatedTrust);

        var result = await afterRevocation.StartAsync();

        Assert.False(result.Success);
        Assert.False(result.UsedOfflineFallback);
        Assert.Equal(LicenseErrorCode.UnknownSigningKey, result.Error!.Code);
        Assert.Equal(requests, environment.Handler.RequestCount);
    }

    [Fact]
    public async Task RetainedRetiredKeyStillValidatesHistoricalLease()
    {
        using var environment = new TestEnvironment();
        using var first = environment.CreateClient();
        Assert.True((await first.StartAsync("license-A")).Success);
        var updatedTrust = environment.CreateOptions();
        var replacement = environment.CreateOptions().ServerVerificationKeys[0];
        replacement.Kid = "replacement-key";
        updatedTrust.ServerVerificationKeys.Add(replacement);
        environment.Handler.Mode = HandlerMode.NetworkFailure;
        using var afterRotation = environment.CreateClient(options: updatedTrust);

        var result = await afterRotation.StartAsync();

        Assert.True(result.Success, result.Error?.ToString());
        Assert.True(result.UsedOfflineFallback);
        Assert.Equal("server-1", result.Lease!.KeyId);
    }

    [Fact]
    public async Task StartupTimeoutUsesValidCacheButCallerCancellationDoesNot()
    {
        using var environment = new TestEnvironment();
        using var activation = environment.CreateClient();
        Assert.True((await activation.StartAsync("license-A")).Success);
        var options = environment.CreateOptions();
        options.RequestTimeout = TimeSpan.FromSeconds(1);
        environment.Handler.Mode = HandlerMode.WaitForCancellation;
        using var client = environment.CreateClient(options: options);

        var timeout = await client.StartAsync();
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var cancellation = await client.StartAsync(cancelled.Token);

        Assert.True(timeout.Success, timeout.Error?.ToString());
        Assert.True(timeout.UsedOfflineFallback);
        Assert.False(cancellation.Success);
        Assert.False(cancellation.UsedOfflineFallback);
        Assert.Equal(LicenseErrorCode.OperationCancelled, cancellation.Error!.Code);
    }

    [Fact]
    public async Task ExpiredCacheCannotFallbackAfterNetworkFailure()
    {
        using var environment = new TestEnvironment();
        using var client = environment.CreateClient();
        Assert.True((await client.StartAsync("license-A")).Success);
        environment.Clock.UtcNow = environment.Clock.UtcNow.AddHours(6).AddMinutes(3);
        environment.Handler.Mode = HandlerMode.NetworkFailure;

        var result = await client.StartAsync();

        Assert.False(result.Success);
        Assert.False(result.UsedOfflineFallback);
        Assert.Equal(LicenseErrorCode.NetworkUnavailable, result.Error!.Code);
    }

    [Fact]
    public async Task CorruptDpapiStateReturnsStorageFailureWithoutNetworkOrFallback()
    {
        using var environment = new TestEnvironment();
        var directory = Path.Combine(Path.GetTempPath(), "Licensing.Client.Tests", Guid.NewGuid().ToString("N"));
        var stateStore = new DpapiLicenseStateStore(new WindowsStorageOptions("Client.Policy.Tests") { DirectoryPath = directory });
        try
        {
            using var first = environment.CreateClient(stateStore: stateStore);
            Assert.True((await first.StartAsync("license-A")).Success);
            var path = Path.Combine(directory, "lease-cache.v1.bin");
            var bytes = await File.ReadAllBytesAsync(path);
            bytes[^1] ^= 0xff;
            await File.WriteAllBytesAsync(path, bytes);
            var requests = environment.Handler.RequestCount;
            environment.Handler.Mode = HandlerMode.NetworkFailure;
            using var next = environment.CreateClient(stateStore: stateStore);

            var result = await next.StartAsync();

            Assert.False(result.Success);
            Assert.False(result.UsedOfflineFallback);
            Assert.Equal(LicenseErrorCode.StorageFailure, result.Error!.Code);
            Assert.Equal(requests, environment.Handler.RequestCount);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
            Assert.Equal(LicenseErrorCode.ActivationRequired, (await next.ClearAsync()).Error!.Code);
            Assert.Null(await stateStore.LoadAsync(CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PersistedDpapiObservationBlocksRollbackAcrossClientInstances()
    {
        using var environment = new TestEnvironment();
        var directory = Path.Combine(Path.GetTempPath(), "Licensing.Client.Tests", Guid.NewGuid().ToString("N"));
        var options = new WindowsStorageOptions("Client.Policy.Tests") { DirectoryPath = directory };
        try
        {
            using var first = environment.CreateClient(stateStore: new DpapiLicenseStateStore(options));
            Assert.True((await first.StartAsync("license-A")).Success);
            var issuedAt = environment.Clock.UtcNow;
            environment.Clock.UtcNow = issuedAt.AddHours(1);
            Assert.True((await first.GetLeaseAsync()).Success);
            environment.Clock.UtcNow = issuedAt.AddMinutes(10);
            environment.Handler.Mode = HandlerMode.NetworkFailure;
            var requests = environment.Handler.RequestCount;
            using var next = environment.CreateClient(stateStore: new DpapiLicenseStateStore(options));

            var result = await next.StartAsync();

            Assert.False(result.Success);
            Assert.False(result.UsedOfflineFallback);
            Assert.Equal(LicenseErrorCode.TrustedTimeRollback, result.Error!.Code);
            Assert.Equal(requests + 1, environment.Handler.RequestCount);
            var persisted = (await new DpapiLicenseStateStore(options).LoadAsync(CancellationToken.None))!;
            Assert.Equal(issuedAt.AddHours(1), persisted.LastObservedUtc);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static byte[] InvalidUtf8Property(byte[] json)
    {
        // Keep JSON syntactically valid for replacement decoders, but insert a non-UTF-8 byte in a string.
        return json[..^1].Concat(Encoding.ASCII.GetBytes(",\"extra\":\"")).Concat(new byte[] { 0xff })
            .Concat(Encoding.ASCII.GetBytes("\"}")).ToArray();
    }

    private sealed class PassThroughHandler : DelegatingHandler
    {
        internal PassThroughHandler() { }
        internal PassThroughHandler(HttpMessageHandler inner) : base(inner) { }
        internal int DisposeCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            base.SendAsync(request, cancellationToken);
        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposeCount++;
            base.Dispose(disposing);
        }
    }

    private static MachineFingerprint TestFingerprint(string one, string two, string three) =>
        new("windows-hardware-v1", "digest-" + one + two + three, new Dictionary<string, string>
        {
            ["machineGuid"] = one,
            ["smbiosUuid"] = two,
            ["systemVolume"] = three
        }, 2);

    private sealed class TestEnvironment : IDisposable
    {
        private readonly ECDsa _serverKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        internal TestEnvironment()
        {
            Clock = new MutableClock { UtcNow = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero) };
            FingerprintProvider = new MutableFingerprintProvider { Value = TestFingerprint("a", "b", "c") };
            KeyStore = new MemoryKeyStore();
            StateStore = new MemoryStateStore();
            Handler = new LicensingHandler(this);
        }

        internal MutableClock Clock { get; }
        internal MutableFingerprintProvider FingerprintProvider { get; }
        internal MemoryKeyStore KeyStore { get; }
        internal MemoryStateStore StateStore { get; }
        internal LicensingHandler Handler { get; }

        internal Action<JObject>? HeaderMutation { get; set; }
        internal Action<JObject>? PayloadMutation { get; set; }
        internal Func<byte[], byte[]>? HeaderBytesTransform { get; set; }
        internal Func<byte[], byte[]>? PayloadBytesTransform { get; set; }
        internal Func<string, string>? TokenTransform { get; set; }

        internal LicenseClient CreateClient(
            HttpMessageHandler? handler = null,
            LicenseClientOptions? options = null,
            ILicenseStateStore? stateStore = null) =>
            new(handler ?? Handler, options ?? CreateOptions(), FingerprintProvider, KeyStore, stateStore ?? StateStore, Clock);

        internal LicenseClientOptions CreateOptions()
        {
            var parameters = _serverKey.ExportParameters(false);
            var options = new LicenseClientOptions
            {
                ServiceBaseUri = new Uri("https://licensing.test"),
                ProductCode = "TEST-PRODUCT",
                ExpectedIssuer = "test-issuer",
                ExpectedAudience = "test-client"
            };
            options.ServerVerificationKeys.Add(new ServerJwk
            {
                Kid = "server-1",
                X = Base64Url(parameters.Q.X!),
                Y = Base64Url(parameters.Q.Y!)
            });
            return options;
        }

        internal HttpResponseMessage ChallengeResponse(int purpose)
        {
            return JsonResponse(new
            {
                challengeToken = "challenge-" + purpose,
                nonce = Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes("server-nonce-" + purpose))),
                purpose,
                expiresAtUtc = Clock.UtcNow.AddMinutes(5)
            });
        }

        internal HttpResponseMessage LeaseResponse()
        {
            var keyId = KeyStore.Record!.KeyId;
            var now = Clock.UtcNow;
            var expiresAtUtc = now.AddHours(Handler.Mode == HandlerMode.OverlongLease ? 7 : 6);
            var header = new JObject
            {
                ["alg"] = "ES256",
                ["kid"] = "server-1",
                ["typ"] = "license-lease+jwt"
            };
            var payload = new JObject
            {
                ["iss"] = "test-issuer",
                ["aud"] = "test-client",
                ["sub"] = TestLicenseId.ToString("D"),
                ["product_code"] = "TEST-PRODUCT",
                ["device_id"] = "device-001",
                ["installation_key_thumbprint"] = keyId,
                ["binding_version"] = 1,
                ["status"] = "active",
                ["features"] = new JObject { ["sample"] = true },
                ["iat"] = now.ToUnixTimeSeconds(),
                ["nbf"] = now.ToUnixTimeSeconds(),
                ["exp"] = expiresAtUtc.ToUnixTimeSeconds()
            };
            HeaderMutation?.Invoke(header);
            PayloadMutation?.Invoke(payload);
            var headerBytes = Encoding.UTF8.GetBytes(header.ToString(Formatting.None));
            var payloadBytes = Encoding.UTF8.GetBytes(payload.ToString(Formatting.None));
            var encodedHeader = Base64Url(HeaderBytesTransform?.Invoke(headerBytes) ?? headerBytes);
            var encodedPayload = Base64Url(PayloadBytesTransform?.Invoke(payloadBytes) ?? payloadBytes);
            var input = Encoding.ASCII.GetBytes(encodedHeader + "." + encodedPayload);
            var signature = _serverKey.SignData(input, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            var token = encodedHeader + "." + encodedPayload + "." + Base64Url(signature);
            return JsonResponse(new
            {
                licenseKeyId = TestLicenseId,
                activatedAtUtc = now,
                entitlementExpiresAtUtc = now.AddDays(30),
                bindingVersion = 1,
                lease = new
                {
                    compactToken = TokenTransform?.Invoke(token) ?? token,
                    keyId = "server-1",
                    algorithm = "ES256",
                    expiresAtUtc
                }
            });
        }

        internal HttpResponseMessage ValidationResponse()
        {
            return JsonResponse(new
            {
                isValid = true,
                failureCode = (string?)null,
                entitlementExpiresAtUtc = Clock.UtcNow.AddDays(30),
                currentBindingVersion = 1
            });
        }

        private HttpResponseMessage JsonResponse(object value)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonConvert.SerializeObject(value), Encoding.UTF8, "application/json")
            };
            response.Headers.Date = Clock.UtcNow;
            return response;
        }

        public void Dispose()
        {
            Handler.Dispose();
            _serverKey.Dispose();
        }
    }

    private enum HandlerMode
    {
        Normal,
        NetworkFailure,
        ServerFailure,
        BusinessRejection,
        Redirect,
        CrossOriginResponse,
        OverlongLease,
        WaitForCancellation
    }

    private sealed class LicensingHandler : HttpMessageHandler
    {
        private readonly TestEnvironment _environment;

        internal LicensingHandler(TestEnvironment environment) => _environment = environment;
        internal HandlerMode Mode { get; set; }
        internal int RequestCount { get; private set; }
        internal List<CapturedRequest> Requests { get; } = new();
        internal int DisposeCount { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposeCount++;
            base.Dispose(disposing);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(DisposeCount > 0, this);
            RequestCount++;
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(
                request.Method.Method,
                request.RequestUri!.PathAndQuery,
                body,
                request.Headers.ToDictionary(pair => pair.Key, pair => string.Join(",", pair.Value), StringComparer.OrdinalIgnoreCase)));

            if (Mode == HandlerMode.WaitForCancellation)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (Mode == HandlerMode.NetworkFailure)
                throw new HttpRequestException("offline");
            if (Mode == HandlerMode.BusinessRejection)
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("{\"code\":\"LICENSE_REVOKED\",\"message\":\"revoked\"}", Encoding.UTF8, "application/json")
                };
            }
            if (Mode == HandlerMode.ServerFailure)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("{\"code\":\"TEMPORARY\"}", Encoding.UTF8, "application/json")
                };
            }
            if (Mode == HandlerMode.Redirect)
            {
                return new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
                {
                    Content = new StringContent(string.Empty),
                    Headers = { Location = new Uri("https://redirected.test/capture") }
                };
            }
            if (Mode == HandlerMode.CrossOriginResponse)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json"),
                    RequestMessage = new HttpRequestMessage(
                        request.Method,
                        "https://different-origin.test/capture")
                };
            }

            _ = JObject.Parse(body);
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/client/challenges/activation" => _environment.ChallengeResponse(1),
                "/api/v1/client/challenges/refresh" => _environment.ChallengeResponse(2),
                "/api/v1/client/challenges/complete" => _environment.LeaseResponse(),
                "/api/v1/client/validations" => _environment.ValidationResponse(),
                _ => throw new InvalidOperationException("Unexpected licensing path: " + request.RequestUri.AbsolutePath)
            };
        }
    }

    private sealed record CapturedRequest(string Method, string PathAndQuery, string Body, IReadOnlyDictionary<string, string> Headers);

    private sealed class MutableClock : ILicenseClock
    {
        public DateTimeOffset UtcNow { get; set; }
    }

    private sealed class MutableFingerprintProvider : IMachineFingerprintProvider
    {
        public MachineFingerprint Value { get; set; } = null!;
        public Task<MachineFingerprint> GetAsync(CancellationToken cancellationToken) => Task.FromResult(Value);
    }

    private sealed class MemoryKeyStore : IInstallationKeyStore
    {
        internal InstallationKeyRecord? Record { get; private set; }
        public Task<InstallationKeyRecord?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Record);
        public Task SaveAsync(InstallationKeyRecord record, CancellationToken cancellationToken)
        {
            Record ??= record;
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryStateStore : ILicenseStateStore
    {
        internal LicenseStateRecord? Record { get; private set; }
        internal int ClearCount { get; private set; }
        public Task<LicenseStateRecord?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Record);
        public Task SaveAsync(LicenseStateRecord record, CancellationToken cancellationToken)
        {
            Record = record;
            return Task.CompletedTask;
        }
        public Task ClearAsync(CancellationToken cancellationToken)
        {
            Record = null;
            ClearCount++;
            return Task.CompletedTask;
        }
    }

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += padded.Length % 4 == 2 ? "==" : padded.Length % 4 == 3 ? "=" : string.Empty;
        return Convert.FromBase64String(padded);
    }
}
