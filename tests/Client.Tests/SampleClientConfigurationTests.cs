using System.Security.Cryptography;
using System.Text;
using Licensing.Client;
using Licensing.Samples;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Client.Tests;

public sealed class SampleClientConfigurationTests
{
    private const int MaximumFileBytes = 256 * 1024;
    private const string SafeConfigurationMessage = "客户端配置无效，请重新导出并检查公共配置。";
    private const string SafeAddressMessage = "服务器地址无效，请填写不含凭据、路径、查询或片段的 HTTP(S) 服务地址。";
    private const string SafeHttpMessage = "明文 HTTP 默认禁用，请明确确认后重试。";

    [Fact]
    public void ActualExportShape_ConvertsNativePublicSpkiAndKeepsSdkPolicyDefaults()
    {
        using var fixture = new ConfigurationFixture();
        var document = fixture.Export();
        document["productId"] = Guid.NewGuid().ToString("D");
        document["productName"] = "合成测试产品";
        document["offlineLeaseMinutes"] = 5;
        document["expectedIssuer"] = "ignored-public-information";
        document["additionalPublicInfo"] = new JObject { ["published"] = true, ["revision"] = 1.25e2 };
        var configuration = fixture.Load(document);
        var options = configuration.CreateOptions(configuration.ServerBaseUrl, false);
        var defaults = new LicenseClientOptions();

        Assert.Equal("TEST_PRODUCT", configuration.ProductCode);
        Assert.Equal("https://licensing.example", configuration.ServerBaseUrl);
        Assert.Equal("配置文件", configuration.Source);
        Assert.Equal(new Uri("https://licensing.example/"), options.ServiceBaseUri);
        Assert.Equal("TEST_PRODUCT", options.ProductCode);
        Assert.Equal("license-platform", options.ExpectedIssuer);
        Assert.Equal("TEST_PRODUCT", options.ExpectedAudience);
        Assert.Equal("Licensed sample device", options.DeviceHint);
        Assert.False(options.AllowInsecureHttp);
        Assert.Equal(defaults.OfflineLeaseLimit, options.OfflineLeaseLimit);
        Assert.Equal(defaults.RefreshInterval, options.RefreshInterval);
        Assert.Equal(defaults.ClockSkew, options.ClockSkew);
        Assert.Equal(defaults.ClockRollbackTolerance, options.ClockRollbackTolerance);
        Assert.Equal(defaults.RequestTimeout, options.RequestTimeout);
        Assert.Equal(defaults.MaxResponseBytes, options.MaxResponseBytes);
        Assert.Equal(defaults.ActivationChallengePath, options.ActivationChallengePath);
        Assert.Equal(defaults.ValidationPath, options.ValidationPath);
        Assert.Null(options.Logger);
        var key = Assert.Single(options.ServerVerificationKeys);
        Assert.Equal("original-kid:V1", key.Kid);
        Assert.Equal("EC", key.Kty);
        Assert.Equal("P-256", key.Crv);
        Assert.Equal("ES256", key.Alg);
        Assert.Equal("sig", key.Use);
        Assert.Equal(Encode(fixture.Parameters.Q.X!), key.X);
        Assert.Equal(Encode(fixture.Parameters.Q.Y!), key.Y);
    }

    [Fact]
    public void KeyRotation_RetainsActiveAndRetiredButNeverRevokedKeys()
    {
        using var fixture = new ConfigurationFixture();
        using var second = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var document = fixture.Export(
            fixture.SigningKey("Active.MixedCase", "ACTIVE"),
            fixture.SigningKey("Retired-Original", "RETIRED", second.ExportSubjectPublicKeyInfoPem()),
            fixture.SigningKey("revoked", "REVOKED"));
        var configuration = fixture.Load(document);
        var keys = configuration.CreateOptions(configuration.ServerBaseUrl, false).ServerVerificationKeys;

        Assert.Equal(new[] { "Active.MixedCase", "Retired-Original" }, keys.Select(key => key.Kid));
        Assert.Equal(Encode(second.ExportParameters(false).Q.X!), keys[1].X);
    }

    [Fact]
    public void CreateOptions_ReturnsIndependentMutableSdkKeyCopies()
    {
        using var fixture = new ConfigurationFixture();
        var configuration = fixture.Load();
        var first = configuration.CreateOptions(configuration.ServerBaseUrl, false);
        first.ServerVerificationKeys[0].Kid = "changed";
        first.ServerVerificationKeys[0].X = "changed";
        first.ServerVerificationKeys.Clear();
        first.ProductCode = "CHANGED";

        var second = configuration.CreateOptions(configuration.ServerBaseUrl, false);

        Assert.Equal("TEST_PRODUCT", second.ProductCode);
        Assert.Equal("original-kid:V1", Assert.Single(second.ServerVerificationKeys).Kid);
        Assert.Equal(Encode(fixture.Parameters.Q.X!), second.ServerVerificationKeys[0].X);
    }

    [Fact]
    public void InitialFile_TakesPrecedenceWithoutReadingAnyEnvironmentVariables()
    {
        using var fixture = new ConfigurationFixture();
        fixture.Write(fixture.Export());

        var configuration = SampleClientConfiguration.LoadInitial(fixture.Directory,
            _ => throw new InvalidOperationException("Environment must not be read."));

        Assert.NotNull(configuration);
        Assert.Equal("配置文件", configuration.Source);
    }

    [Fact]
    public void InvalidInitialFile_DoesNotFallBackToCompleteLegacyEnvironment()
    {
        using var fixture = new ConfigurationFixture();
        fixture.WriteText("not-json-synthetic-input");
        var reads = new List<string>();
        var environment = fixture.Environment();

        AssertSafeConfiguration(() => SampleClientConfiguration.LoadInitial(fixture.Directory, name =>
        {
            reads.Add(name);
            return environment.TryGetValue(name, out var value) ? value : null;
        }));

        Assert.Empty(reads);
    }

    [Fact]
    public void DirectoryAtInitialFilePath_IsAnErrorAndDoesNotReadEnvironment()
    {
        using var fixture = new ConfigurationFixture();
        System.IO.Directory.CreateDirectory(fixture.ConfigPath);
        var environmentWasRead = false;

        AssertSafeConfiguration(() => SampleClientConfiguration.LoadInitial(fixture.Directory, _ =>
        {
            environmentWasRead = true;
            return null;
        }));
        Assert.False(environmentWasRead);
    }

    [Fact]
    public void ExplicitlyChosenFile_OverridesInitialFileAndLegacyEnvironment()
    {
        using var fixture = new ConfigurationFixture();
        fixture.Write(fixture.Export());
        var initial = SampleClientConfiguration.LoadInitial(fixture.Directory, _ => null);
        var selected = fixture.Export(fixture.SigningKey("selected-kid"));
        selected["productCode"] = "chosen_product";
        selected["serverBaseUrl"] = "https://selected.example";
        var selectedPath = fixture.Write(selected, "selected-config.json");

        var configuration = SampleClientConfiguration.LoadFile(selectedPath);

        Assert.Equal("TEST_PRODUCT", initial!.ProductCode);
        Assert.Equal("CHOSEN_PRODUCT", configuration.ProductCode);
        Assert.Equal("https://selected.example", configuration.ServerBaseUrl);
        Assert.Equal("selected-kid", Assert.Single(configuration.CreateOptions(configuration.ServerBaseUrl, false).ServerVerificationKeys).Kid);
    }

    [Fact]
    public void NoPublicEnvironment_ReturnsNullAndNeverReadsSecretsOrHttpConsent()
    {
        using var fixture = new ConfigurationFixture();
        var reads = new List<string>();
        var unrelated = new Dictionary<string, string?>
        {
            ["LICENSE_KEY"] = "synthetic-never-consumed-input",
            ["LICENSE_ALLOW_INSECURE_HTTP"] = "true"
        };

        var configuration = SampleClientConfiguration.LoadInitial(fixture.Directory, name =>
        {
            reads.Add(name);
            return unrelated.TryGetValue(name, out var value) ? value : null;
        });

        Assert.Null(configuration);
        Assert.Equal(new[]
        {
            "LICENSING_BASE_URL", "LICENSING_PRODUCT_CODE", "LICENSING_SERVER_JWK", "LICENSING_ISSUER", "LICENSING_AUDIENCE"
        }, reads);
    }

    [Theory]
    [InlineData("LICENSING_BASE_URL")]
    [InlineData("LICENSING_PRODUCT_CODE")]
    [InlineData("LICENSING_SERVER_JWK")]
    [InlineData("LICENSING_ISSUER")]
    [InlineData("LICENSING_AUDIENCE")]
    public void AnyPartialEnvironment_IsAnError(string presentName)
    {
        using var fixture = new ConfigurationFixture();

        AssertSafeConfiguration(() => SampleClientConfiguration.LoadInitial(fixture.Directory,
            name => name == presentName ? "synthetic-public-value" : null));
    }

    [Fact]
    public void LegacySingleJwk_UsesDefaultsAndDoesNotInheritHttpConsent()
    {
        using var fixture = new ConfigurationFixture();
        var environment = fixture.Environment();
        environment["LICENSING_BASE_URL"] = "http://127.0.0.1:5080";
        environment["LICENSE_ALLOW_INSECURE_HTTP"] = "true";
        environment["LICENSE_KEY"] = "synthetic-never-consumed-input";
        var reads = new List<string>();

        var configuration = SampleClientConfiguration.LoadInitial(fixture.Directory, name =>
        {
            reads.Add(name);
            return environment.TryGetValue(name, out var value) ? value : null;
        })!;

        Assert.Equal("环境变量", configuration.Source);
        Assert.Equal("TEST_PRODUCT", configuration.ProductCode);
        Assert.Equal(SafeHttpMessage, Assert.Throws<SampleConfigurationException>(
            () => configuration.CreateOptions(configuration.ServerBaseUrl, false)).Message);
        var options = configuration.CreateOptions(configuration.ServerBaseUrl, true);
        Assert.True(options.AllowInsecureHttp);
        Assert.Equal("license-platform", options.ExpectedIssuer);
        Assert.Equal("TEST_PRODUCT", options.ExpectedAudience);
        Assert.Equal(Encode(fixture.Parameters.Q.X!), Assert.Single(options.ServerVerificationKeys).X);
        Assert.DoesNotContain("LICENSE_KEY", reads);
        Assert.DoesNotContain("LICENSE_ALLOW_INSECURE_HTTP", reads);
    }

    [Fact]
    public void LegacyJwks_RetainsIdentifiersAndOptionalIssuerAudience()
    {
        using var fixture = new ConfigurationFixture();
        var second = fixture.Jwk("Second.MixedCase");
        var jwks = new JObject { ["keys"] = new JArray(fixture.Jwk("First"), second) };
        var environment = fixture.Environment(jwks);
        environment["LICENSING_ISSUER"] = " custom-issuer ";
        environment["LICENSING_AUDIENCE"] = " custom-audience ";

        var configuration = fixture.LoadEnvironment(environment);
        var options = configuration.CreateOptions(configuration.ServerBaseUrl, false);

        Assert.Equal(new[] { "First", "Second.MixedCase" }, options.ServerVerificationKeys.Select(key => key.Kid));
        Assert.Equal("custom-issuer", options.ExpectedIssuer);
        Assert.Equal("custom-audience", options.ExpectedAudience);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("empty")]
    public void LegacyJwk_OptionalAlgorithmAndUseMatchSdkCompatibility(string representation)
    {
        using var fixture = new ConfigurationFixture();
        var jwk = fixture.Jwk();
        if (representation == "missing")
        {
            jwk.Remove("alg");
            jwk.Remove("use");
        }
        else
        {
            jwk["alg"] = representation == "null" ? JValue.CreateNull() : new JValue("");
            jwk["use"] = representation == "null" ? JValue.CreateNull() : new JValue("");
        }

        var configuration = fixture.LoadEnvironment(fixture.Environment(jwk));

        Assert.Single(configuration.CreateOptions(configuration.ServerBaseUrl, false).ServerVerificationKeys);
    }

    [Fact]
    public void EnvironmentReaderException_IsSanitizedWithoutAnInnerException()
    {
        using var fixture = new ConfigurationFixture();

        var error = AssertSafeConfiguration(() => SampleClientConfiguration.LoadInitial(fixture.Directory,
            _ => throw new IOException("synthetic-sensitive-error-input")));

        Assert.DoesNotContain("synthetic-sensitive-error-input", error.ToString());
    }

    [Theory]
    [InlineData("https://override.example", "https://override.example/")]
    [InlineData("https://override.example:8443/", "https://override.example:8443/")]
    [InlineData("HTTPS://OVERRIDE.EXAMPLE/", "https://override.example/")]
    [InlineData("https://127.0.0.1:8443", "https://127.0.0.1:8443/")]
    [InlineData("https://[::1]:8443", "https://[::1]:8443/")]
    public void ServerAddressOverride_AcceptsOnlyActualRootOrigins(string address, string expected)
    {
        using var fixture = new ConfigurationFixture();
        var configuration = fixture.Load();

        var options = configuration.CreateOptions(address, false);

        Assert.Equal(new Uri(expected), options.ServiceBaseUri);
        Assert.Equal("https://licensing.example", configuration.ServerBaseUrl);
        Assert.False(options.AllowInsecureHttp);
    }

    [Fact]
    public void ExportedServerIpPlaceholder_CanBeReplacedButCannotBeUsed()
    {
        using var fixture = new ConfigurationFixture();
        var document = fixture.Export();
        document["serverBaseUrl"] = "http://SERVER_IP";
        var configuration = fixture.Load(document);

        Assert.Equal("http://SERVER_IP", configuration.ServerBaseUrl);
        AssertSafeAddress(() => configuration.CreateOptions(configuration.ServerBaseUrl, true));
        Assert.Equal(new Uri("https://configured.example/"), configuration.CreateOptions("https://configured.example", false).ServiceBaseUri);
        Assert.Equal(new Uri("http://192.0.2.1:5080/"), configuration.CreateOptions("http://192.0.2.1:5080", true).ServiceBaseUri);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("licensing.example")]
    [InlineData("//licensing.example")]
    [InlineData("ftp://licensing.example")]
    [InlineData("https:licensing.example")]
    [InlineData("https:///licensing.example")]
    [InlineData("https://")]
    [InlineData("https://SERVER_IP")]
    [InlineData("https://server_ip.:8443/")]
    [InlineData("https://user:synthetic-password@licensing.example")]
    [InlineData("https://@licensing.example")]
    [InlineData("https://licensing.example/api")]
    [InlineData("https://licensing.example/a/..")]
    [InlineData("https://licensing.example/.")]
    [InlineData("https://licensing.example//")]
    [InlineData("https://licensing.example/%2e")]
    [InlineData("https://licensing.example?query")]
    [InlineData("https://licensing.example?")]
    [InlineData("https://licensing.example#fragment")]
    [InlineData("https://licensing.example#")]
    [InlineData("https://licensing.example\\api")]
    [InlineData(" https://licensing.example")]
    [InlineData("https://licensing.example ")]
    [InlineData("https://licensing.example\n")]
    [InlineData("https://licensing.example:")]
    [InlineData("https://licensing.example:0")]
    [InlineData("https://licensing.example:65536")]
    [InlineData("https://licensing.example:invalid")]
    [InlineData("https://%53ERVER_IP")]
    public void InvalidServerAddress_IsSanitizedEvenWhenHttpExplicitlyAllowed(string address)
    {
        using var fixture = new ConfigurationFixture();
        var configuration = fixture.Load();

        AssertSafeAddress(() => configuration.CreateOptions(address, true));
    }

    [Fact]
    public void NullOrOverlongServerAddress_IsASafeError()
    {
        using var fixture = new ConfigurationFixture();
        var configuration = fixture.Load();

        AssertSafeAddress(() => configuration.CreateOptions(null!, false));
        AssertSafeAddress(() => configuration.CreateOptions("https://" + new string('a', 2048), false));
    }

    [Fact]
    public void HttpRequiresExplicitConsentOnEveryOptionsCreation()
    {
        using var fixture = new ConfigurationFixture();
        var configuration = fixture.Load();

        var error = Assert.Throws<SampleConfigurationException>(() => configuration.CreateOptions("http://localhost:5080", false));
        Assert.Equal(SafeHttpMessage, error.Message);
        Assert.Null(error.InnerException);
        Assert.True(configuration.CreateOptions("http://localhost:5080", true).AllowInsecureHttp);
        Assert.Equal(SafeHttpMessage, Assert.Throws<SampleConfigurationException>(
            () => configuration.CreateOptions("http://localhost:5080", false)).Message);
    }

    [Theory]
    [InlineData("protocolVersion")]
    [InlineData("productCode")]
    [InlineData("serverBaseUrl")]
    [InlineData("signingKeys")]
    public void MissingExportFields_AreRejected(string property)
    {
        using var fixture = new ConfigurationFixture();
        var document = fixture.Export();
        document.Remove(property);

        AssertSafeConfiguration(() => fixture.Load(document));
    }

    [Theory]
    [InlineData("protocolVersion", "")]
    [InlineData("protocolVersion", "2")]
    [InlineData("protocolVersion", " 1")]
    [InlineData("productCode", "invalid product")]
    [InlineData("productCode", "synthetic/product")]
    [InlineData("productCode", "\nTEST")]
    [InlineData("productCode", "")]
    [InlineData("serverBaseUrl", "")]
    [InlineData("serverBaseUrl", "https://licensing.example\n")]
    public void InvalidExportFieldValues_AreRejected(string property, string value)
    {
        using var fixture = new ConfigurationFixture();
        var document = fixture.Export();
        document[property] = value;

        AssertSafeConfiguration(() => fixture.Load(document));
    }

    [Theory]
    [InlineData("protocolVersion")]
    [InlineData("productCode")]
    [InlineData("serverBaseUrl")]
    public void ExportStringFields_DoNotCoerceNumbersOrNull(string property)
    {
        using var fixture = new ConfigurationFixture();
        var document = fixture.Export();
        document[property] = 1;
        AssertSafeConfiguration(() => fixture.Load(document));
        document[property] = JValue.CreateNull();
        AssertSafeConfiguration(() => fixture.Load(document));
    }

    [Theory]
    [InlineData("productCode", 65)]
    [InlineData("serverBaseUrl", 2049)]
    [InlineData("protocolVersion", 9)]
    [InlineData("productName", 4097)]
    public void ExportFieldLengths_AreBounded(string property, int length)
    {
        using var fixture = new ConfigurationFixture();
        var document = fixture.Export();
        document[property] = new string('a', length);

        AssertSafeConfiguration(() => fixture.Load(document));
    }

    [Fact]
    public void JsonPropertyNameLength_IsBounded()
    {
        using var fixture = new ConfigurationFixture();
        var document = fixture.Export();
        document[new string('a', 129)] = "public";

        AssertSafeConfiguration(() => fixture.Load(document));
    }

    [Fact]
    public void EmptyWronglyTypedAndAllRevokedSigningKeys_AreRejected()
    {
        using var fixture = new ConfigurationFixture();
        var document = fixture.Export();
        document["signingKeys"] = new JArray();
        AssertSafeConfiguration(() => fixture.Load(document));
        document["signingKeys"] = new JObject();
        AssertSafeConfiguration(() => fixture.Load(document));
        document["signingKeys"] = new JArray(JValue.CreateNull());
        AssertSafeConfiguration(() => fixture.Load(document));
        AssertSafeConfiguration(() => fixture.Load(fixture.Export(fixture.SigningKey("revoked", "REVOKED"))));
    }

    [Fact]
    public void SigningKeyCount_IsBoundedIncludingRevokedKeys()
    {
        using var fixture = new ConfigurationFixture();
        var keys = Enumerable.Range(0, 32).Select(index => fixture.SigningKey("key-" + index)).ToArray();
        var configuration = fixture.Load(fixture.Export(keys));
        Assert.Equal(32, configuration.CreateOptions(configuration.ServerBaseUrl, false).ServerVerificationKeys.Count);

        var tooMany = keys.Concat(new[] { fixture.SigningKey("extra-revoked", "REVOKED") }).ToArray();
        AssertSafeConfiguration(() => fixture.Load(fixture.Export(tooMany)));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" padded")]
    [InlineData("padded ")]
    [InlineData("kid\nnewline")]
    [InlineData("kid\u0000nul")]
    public void InvalidKeyIdentifiers_AreRejected(string kid)
    {
        using var fixture = new ConfigurationFixture();

        AssertSafeConfiguration(() => fixture.Load(fixture.Export(fixture.SigningKey(kid))));
        AssertSafeConfiguration(() => fixture.LoadEnvironment(fixture.Environment(fixture.Jwk(kid))));
    }

    [Theory]
    [InlineData("opaque kid/with+punctuation")]
    [InlineData("kid\\segment")]
    [InlineData("中文标识")]
    [InlineData("kid\"quoted")]
    public void OpaquePublicKeyIdentifiers_ArePreservedWithoutInventingAnAlphabet(string kid)
    {
        using var fixture = new ConfigurationFixture();
        var fromFile = fixture.Load(fixture.Export(fixture.SigningKey(kid)));
        var fromLegacy = fixture.LoadEnvironment(fixture.Environment(fixture.Jwk(kid)));

        Assert.Equal(kid, Assert.Single(fromFile.CreateOptions(fromFile.ServerBaseUrl, false).ServerVerificationKeys).Kid);
        Assert.Equal(kid, Assert.Single(fromLegacy.CreateOptions(fromLegacy.ServerBaseUrl, false).ServerVerificationKeys).Kid);
    }

    [Theory]
    [InlineData("ACTIVE")]
    [InlineData("RETIRED")]
    [InlineData("REVOKED")]
    public void DuplicateKeyIdentifiers_AreRejectedRegardlessOfStatus(string duplicateStatus)
    {
        using var fixture = new ConfigurationFixture();

        AssertSafeConfiguration(() => fixture.Load(fixture.Export(
            fixture.SigningKey("same"), fixture.SigningKey("same", duplicateStatus))));
        AssertSafeConfiguration(() => fixture.LoadEnvironment(fixture.Environment(new JObject
        {
            ["keys"] = new JArray(fixture.Jwk("same"), fixture.Jwk("same"))
        })));
    }

    [Theory]
    [InlineData("kid")]
    [InlineData("alg")]
    [InlineData("pem")]
    [InlineData("status")]
    public void MissingSigningKeyFields_AreRejected(string property)
    {
        using var fixture = new ConfigurationFixture();
        var key = fixture.SigningKey();
        key.Remove(property);

        AssertSafeConfiguration(() => fixture.Load(fixture.Export(key)));
    }

    [Theory]
    [InlineData("alg", "RS256")]
    [InlineData("alg", "es256")]
    [InlineData("status", "active")]
    [InlineData("status", "retired")]
    [InlineData("status", "DISABLED")]
    [InlineData("status", "")]
    public void UnsupportedSigningAlgorithmOrStatus_AreRejected(string property, string value)
    {
        using var fixture = new ConfigurationFixture();
        var key = fixture.SigningKey();
        key[property] = value;

        AssertSafeConfiguration(() => fixture.Load(fixture.Export(key)));
    }

    [Theory]
    [InlineData("kid", 129)]
    [InlineData("alg", 17)]
    [InlineData("status", 17)]
    [InlineData("pem", 1025)]
    public void SigningKeyFieldLengths_AreBounded(string property, int length)
    {
        using var fixture = new ConfigurationFixture();
        var key = fixture.SigningKey();
        key[property] = new string('a', length);

        AssertSafeConfiguration(() => fixture.Load(fixture.Export(key)));
    }

    [Theory]
    [InlineData("prefix")]
    [InlineData("suffix")]
    [InlineData("two-blocks")]
    [InlineData("private-header")]
    [InlineData("ec-private-header")]
    [InlineData("wrong-case")]
    [InlineData("missing-footer")]
    [InlineData("body-space")]
    [InlineData("body-tab")]
    [InlineData("empty-body-line")]
    [InlineData("lone-carriage-return")]
    [InlineData("extra-trailing-newline")]
    [InlineData("noncanonical-base64")]
    public void MalformedOrNonPublicPem_IsRejected(string mutation)
    {
        using var fixture = new ConfigurationFixture();
        var pem = fixture.Pem.Replace("\r\n", "\n");
        var encoded = Convert.ToBase64String(fixture.Der);
        pem = mutation switch
        {
            "prefix" => "synthetic-input\n" + pem,
            "suffix" => pem + "\nsynthetic-input",
            "two-blocks" => pem + "\n" + pem,
            "private-header" => pem.Replace("PUBLIC KEY", "PRIVATE KEY"),
            "ec-private-header" => pem.Replace("PUBLIC KEY", "EC PRIVATE KEY"),
            "wrong-case" => pem.Replace("PUBLIC KEY", "public key"),
            "missing-footer" => pem.Substring(0, pem.IndexOf("-----END", StringComparison.Ordinal)),
            "body-space" => pem.Replace("-----BEGIN PUBLIC KEY-----\n", "-----BEGIN PUBLIC KEY-----\n "),
            "body-tab" => pem.Replace("-----BEGIN PUBLIC KEY-----\n", "-----BEGIN PUBLIC KEY-----\n\t"),
            "empty-body-line" => pem.Replace("-----BEGIN PUBLIC KEY-----\n", "-----BEGIN PUBLIC KEY-----\n\n"),
            "lone-carriage-return" => pem.Replace("\n", "\r"),
            "extra-trailing-newline" => pem.TrimEnd('\n') + "\n\n",
            "noncanonical-base64" => WrapPem(ChangeUnusedBase64Bits(encoded, encoded.Length - 3)),
            _ => throw new InvalidOperationException("Unknown test mutation.")
        };

        AssertSafeConfiguration(() => fixture.Load(fixture.Export(fixture.SigningKey(pem: pem))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PublicPem_AcceptsLfOrCrLfAndOneOptionalFinalNewline(bool windowsNewlines)
    {
        using var fixture = new ConfigurationFixture();
        var pem = fixture.Pem.Replace("\r\n", "\n").TrimEnd('\n') + "\n";
        if (windowsNewlines)
            pem = pem.Replace("\n", "\r\n");

        var configuration = fixture.Load(fixture.Export(fixture.SigningKey(pem: pem)));

        Assert.Equal(Encode(fixture.Parameters.Q.Y!), Assert.Single(
            configuration.CreateOptions(configuration.ServerBaseUrl, false).ServerVerificationKeys).Y);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(12)]
    [InlineData(22)]
    [InlineData(25)]
    [InlineData(26)]
    public void WrongSpkiTagLengthAlgorithmCurveOrPointEncoding_IsRejected(int byteIndex)
    {
        using var fixture = new ConfigurationFixture();
        var der = (byte[])fixture.Der.Clone();
        der[byteIndex] ^= 1;

        AssertSafeConfiguration(() => fixture.Load(fixture.Export(fixture.SigningKey(pem: WrapPem(Convert.ToBase64String(der))))));
    }

    [Fact]
    public void TruncatedOrTrailingDer_IsRejected()
    {
        using var fixture = new ConfigurationFixture();
        var truncated = fixture.Der.Take(90).ToArray();
        var trailing = fixture.Der.Concat(new byte[] { 0 }).ToArray();

        AssertSafeConfiguration(() => fixture.Load(fixture.Export(fixture.SigningKey(pem: WrapPem(Convert.ToBase64String(truncated))))));
        AssertSafeConfiguration(() => fixture.Load(fixture.Export(fixture.SigningKey(pem: WrapPem(Convert.ToBase64String(trailing))))));
    }

    [Fact]
    public void NativePointValidation_RejectsZeroCoordinatesInPemAndLegacyJwk()
    {
        using var fixture = new ConfigurationFixture();
        var der = (byte[])fixture.Der.Clone();
        Array.Clear(der, 27, 64);
        var jwk = fixture.Jwk();
        jwk["x"] = Encode(new byte[32]);
        jwk["y"] = Encode(new byte[32]);

        AssertSafeConfiguration(() => fixture.Load(fixture.Export(fixture.SigningKey(pem: WrapPem(Convert.ToBase64String(der))))));
        AssertSafeConfiguration(() => fixture.LoadEnvironment(fixture.Environment(jwk)));
    }

    [Fact]
    public void WrongCurveAndActualSyntheticPrivatePem_AreRejected()
    {
        using var fixture = new ConfigurationFixture();
        using var wrongCurve = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        using var syntheticPrivate = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        AssertSafeConfiguration(() => fixture.Load(fixture.Export(fixture.SigningKey(pem: wrongCurve.ExportSubjectPublicKeyInfoPem()))));
        AssertSafeConfiguration(() => fixture.Load(fixture.Export(fixture.SigningKey(pem: syntheticPrivate.ExportPkcs8PrivateKeyPem()))));
    }

    [Fact]
    public void RevokedMalformedKey_IsNotSilentlyIgnored()
    {
        using var fixture = new ConfigurationFixture();

        AssertSafeConfiguration(() => fixture.Load(fixture.Export(
            fixture.SigningKey("active"), fixture.SigningKey("revoked", "REVOKED", "synthetic-invalid-pem"))));
    }

    [Theory]
    [InlineData("d")]
    [InlineData("D")]
    [InlineData("p")]
    [InlineData("q")]
    [InlineData("dp")]
    [InlineData("dq")]
    [InlineData("qi")]
    [InlineData("oth")]
    [InlineData("k")]
    [InlineData("privateKey")]
    [InlineData("privateKeyPem")]
    public void PrivateKeyMembers_AreRejectedEvenWhenNull(string property)
    {
        using var fixture = new ConfigurationFixture();
        var jwk = fixture.Jwk();
        jwk[property] = JValue.CreateNull();
        AssertSafeConfiguration(() => fixture.LoadEnvironment(fixture.Environment(jwk)));
        var key = fixture.SigningKey();
        key[property] = "synthetic-sensitive-input";
        var error = AssertSafeConfiguration(() => fixture.Load(fixture.Export(key)));
        Assert.DoesNotContain("synthetic-sensitive-input", error.ToString());
    }

    [Theory]
    [InlineData("kty")]
    [InlineData("crv")]
    [InlineData("kid")]
    [InlineData("x")]
    [InlineData("y")]
    public void LegacyJwk_RequiresPublicKeyFields(string property)
    {
        using var fixture = new ConfigurationFixture();
        var jwk = fixture.Jwk();
        jwk.Remove(property);

        AssertSafeConfiguration(() => fixture.LoadEnvironment(fixture.Environment(jwk)));
    }

    [Theory]
    [InlineData("kty", "RSA")]
    [InlineData("crv", "P-384")]
    [InlineData("alg", "ES384")]
    [InlineData("use", "enc")]
    [InlineData("alg", " ES256")]
    public void LegacyJwk_RejectsWrongKeyAlgorithmCurveOrUse(string property, string value)
    {
        using var fixture = new ConfigurationFixture();
        var jwk = fixture.Jwk();
        jwk[property] = value;

        AssertSafeConfiguration(() => fixture.LoadEnvironment(fixture.Environment(jwk)));
    }

    [Theory]
    [InlineData("x")]
    [InlineData("y")]
    public void LegacyCoordinates_RejectWrongLengthPaddingAlphabetAndUnusedBits(string property)
    {
        using var fixture = new ConfigurationFixture();
        var good = fixture.Jwk()[property]!.Value<string>()!;
        foreach (var bad in new[]
        {
            good.Substring(1), good + "=", good + "A", "+" + good.Substring(1), "/" + good.Substring(1),
            " " + good.Substring(1), ChangeUnusedBase64Bits(good, good.Length - 1)
        })
        {
            var jwk = fixture.Jwk();
            jwk[property] = bad;
            AssertSafeConfiguration(() => fixture.LoadEnvironment(fixture.Environment(jwk)));
        }
    }

    [Fact]
    public void LegacyJwks_RejectsEmptyWrongTypeAndOverlongLists()
    {
        using var fixture = new ConfigurationFixture();
        foreach (var value in new JToken[] { new JArray(), new JObject(), JValue.CreateNull(), new JArray(1) })
            AssertSafeConfiguration(() => fixture.LoadEnvironment(fixture.Environment(new JObject { ["keys"] = value })));
        var tooMany = new JArray(Enumerable.Range(0, 33).Select(index => fixture.Jwk("key-" + index)));
        AssertSafeConfiguration(() => fixture.LoadEnvironment(fixture.Environment(new JObject { ["keys"] = tooMany })));
    }

    [Theory]
    [InlineData("LICENSING_PRODUCT_CODE", 65)]
    [InlineData("LICENSING_BASE_URL", 2049)]
    [InlineData("LICENSING_ISSUER", 257)]
    [InlineData("LICENSING_AUDIENCE", 257)]
    public void LegacyEnvironmentFieldLengths_AreBounded(string name, int length)
    {
        using var fixture = new ConfigurationFixture();
        var environment = fixture.Environment();
        environment[name] = new string('a', length);

        AssertSafeConfiguration(() => fixture.LoadEnvironment(environment));
    }

    [Fact]
    public void LegacyJwkJson_IsBoundedBeforeParsing()
    {
        using var fixture = new ConfigurationFixture();
        var environment = fixture.Environment();
        environment["LICENSING_SERVER_JWK"] = new string(' ', MaximumFileBytes + 1) + "{}";

        AssertSafeConfiguration(() => fixture.LoadEnvironment(environment));
    }

    [Fact]
    public void DuplicateProperties_AreRejectedIncludingNestedUnknownInformationAndEscapedNames()
    {
        using var fixture = new ConfigurationFixture();
        var json = fixture.Export().ToString(Formatting.None);
        foreach (var prefix in new[]
        {
            "\"productCode\":\"OTHER\",", "\"product\\u0043ode\":\"OTHER\",",
            "\"publicInformation\":{\"duplicate\":1,\"duplicate\":2},"
        })
            AssertSafeConfiguration(() => SampleClientConfiguration.LoadFile(fixture.WriteText("{" + prefix + json.Substring(1))));

        var jwk = fixture.Jwk().ToString(Formatting.None);
        var environment = fixture.Environment();
        environment["LICENSING_SERVER_JWK"] = "{\"kid\":\"first\"," + jwk.Substring(1);
        AssertSafeConfiguration(() => fixture.LoadEnvironment(environment));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("synthetic-trailing-input")]
    public void TrailingJsonContent_IsRejectedForFileAndLegacyEnvironment(string trailing)
    {
        using var fixture = new ConfigurationFixture();
        AssertSafeConfiguration(() => SampleClientConfiguration.LoadFile(
            fixture.WriteText(fixture.Export().ToString(Formatting.None) + "\n" + trailing)));
        var environment = fixture.Environment();
        environment["LICENSING_SERVER_JWK"] += "\n" + trailing;
        AssertSafeConfiguration(() => fixture.LoadEnvironment(environment));
    }

    [Theory]
    [InlineData("'single-quoted'")]
    [InlineData("undefined")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("0x10")]
    [InlineData("0123")]
    [InlineData("+1")]
    [InlineData(".5")]
    [InlineData("1.")]
    [InlineData("1e+")]
    [InlineData("\"bad\\'escape\"")]
    [InlineData("[1,]")]
    [InlineData("{\"a\":1,}")]
    [InlineData("{unquoted:1}")]
    [InlineData("/* comment */ 1")]
    public void JavaScriptJsonExtensions_AreRejectedEvenInUnknownFields(string value)
    {
        using var fixture = new ConfigurationFixture();
        var json = fixture.Export().ToString(Formatting.None);
        var malformed = "{\"additional\":" + value + "," + json.Substring(1);

        AssertSafeConfiguration(() => SampleClientConfiguration.LoadFile(fixture.WriteText(malformed)));
    }

    [Fact]
    public void TrailingCommaAndTrailingComment_AreRejected()
    {
        using var fixture = new ConfigurationFixture();
        var json = fixture.Export().ToString(Formatting.None);

        AssertSafeConfiguration(() => SampleClientConfiguration.LoadFile(fixture.WriteText(json.Substring(0, json.Length - 1) + ",}")));
        AssertSafeConfiguration(() => SampleClientConfiguration.LoadFile(fixture.WriteText(json + " // synthetic-comment")));
    }

    [Fact]
    public void JsonDepth_IsBoundedEvenInIgnoredFields()
    {
        using var fixture = new ConfigurationFixture();
        var document = fixture.Export();
        JToken nested = new JValue("public");
        for (var i = 0; i < 17; i++)
            nested = new JArray(nested);
        document["additionalInformation"] = nested;

        AssertSafeConfiguration(() => fixture.Load(document));
        var environment = fixture.Environment();
        environment["LICENSING_SERVER_JWK"] = document.ToString(Formatting.None);
        AssertSafeConfiguration(() => fixture.LoadEnvironment(environment));
    }

    [Fact]
    public void FileByteLimit_IsEnforcedAndExactLimitWithWhitespaceIsAccepted()
    {
        using var fixture = new ConfigurationFixture();
        var json = fixture.Export().ToString(Formatting.None);
        var bytes = Encoding.UTF8.GetBytes(json);
        var exact = new byte[MaximumFileBytes];
        Array.Fill(exact, (byte)' ');
        Buffer.BlockCopy(bytes, 0, exact, 0, bytes.Length);
        File.WriteAllBytes(fixture.ConfigPath, exact);
        Assert.Equal("TEST_PRODUCT", SampleClientConfiguration.LoadFile(fixture.ConfigPath).ProductCode);

        File.WriteAllBytes(fixture.ConfigPath, exact.Concat(new byte[] { (byte)' ' }).ToArray());
        AssertSafeConfiguration(() => SampleClientConfiguration.LoadFile(fixture.ConfigPath));
        File.WriteAllBytes(fixture.ConfigPath, Array.Empty<byte>());
        AssertSafeConfiguration(() => SampleClientConfiguration.LoadFile(fixture.ConfigPath));
    }

    [Fact]
    public void StrictUtf8_RejectsInvalidSequencesAndUtf16ButAcceptsOptionalUtf8Bom()
    {
        using var fixture = new ConfigurationFixture();
        var json = fixture.Export().ToString(Formatting.None);
        var encoded = Encoding.UTF8.GetBytes(json);
        File.WriteAllBytes(fixture.ConfigPath, new byte[] { 0xEF, 0xBB, 0xBF }.Concat(encoded).ToArray());
        Assert.Equal("TEST_PRODUCT", SampleClientConfiguration.LoadFile(fixture.ConfigPath).ProductCode);

        foreach (var invalid in new[]
        {
            new byte[] { 0xC0, 0xAF }, new byte[] { 0xED, 0xA0, 0x80 }, new byte[] { 0xF4, 0x90, 0x80, 0x80 },
            new byte[] { 0xE2, 0x82 }, new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes(json)).ToArray()
        })
        {
            File.WriteAllBytes(fixture.ConfigPath, invalid.Concat(encoded).ToArray());
            AssertSafeConfiguration(() => SampleClientConfiguration.LoadFile(fixture.ConfigPath));
        }
    }

    [Fact]
    public void MissingInvalidAndNullFilePaths_NeverExposeInputOrRawExceptions()
    {
        using var fixture = new ConfigurationFixture();
        var syntheticPath = Path.Combine(fixture.Directory, "synthetic-sensitive-input.json");
        var error = AssertSafeConfiguration(() => SampleClientConfiguration.LoadFile(syntheticPath));
        Assert.DoesNotContain("synthetic-sensitive-input", error.ToString());
        Assert.DoesNotContain(fixture.Directory, error.ToString());
        AssertSafeConfiguration(() => SampleClientConfiguration.LoadFile(null!));
        AssertSafeConfiguration(() => SampleClientConfiguration.LoadFile("\0synthetic-input"));
        AssertSafeConfiguration(() => SampleClientConfiguration.LoadInitial(null!, _ => null));
        AssertSafeConfiguration(() => SampleClientConfiguration.LoadInitial(fixture.Directory, null!));
    }

    private static SampleConfigurationException AssertSafeConfiguration(Action action)
    {
        var error = Assert.Throws<SampleConfigurationException>(action);
        Assert.Equal(SafeConfigurationMessage, error.Message);
        Assert.Null(error.InnerException);
        return error;
    }

    private static void AssertSafeAddress(Action action)
    {
        var error = Assert.Throws<SampleConfigurationException>(action);
        Assert.Equal(SafeAddressMessage, error.Message);
        Assert.Null(error.InnerException);
    }

    private static string Encode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string ChangeUnusedBase64Bits(string encoded, int index)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        var chars = encoded.ToCharArray();
        var standard = chars[index] == '-' ? '+' : chars[index] == '_' ? '/' : chars[index];
        var changed = alphabet[alphabet.IndexOf(standard) + 1];
        chars[index] = encoded.IndexOf('=') >= 0 ? changed : changed == '+' ? '-' : changed == '/' ? '_' : changed;
        return new string(chars);
    }

    private static string WrapPem(string encoded)
    {
        var builder = new StringBuilder("-----BEGIN PUBLIC KEY-----\n");
        for (var i = 0; i < encoded.Length; i += 64)
            builder.Append(encoded, i, Math.Min(64, encoded.Length - i)).Append('\n');
        return builder.Append("-----END PUBLIC KEY-----").ToString();
    }

    private sealed class ConfigurationFixture : IDisposable
    {
        public ConfigurationFixture()
        {
            Directory = Path.Combine(Path.GetTempPath(), "licensing-sample-configuration-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            Parameters = key.ExportParameters(false);
            Der = key.ExportSubjectPublicKeyInfo();
            Pem = key.ExportSubjectPublicKeyInfoPem();
        }

        public string Directory { get; }
        public string ConfigPath => Path.Combine(Directory, "client-config.json");
        public ECParameters Parameters { get; }
        public byte[] Der { get; }
        public string Pem { get; }

        public JObject SigningKey(string kid = "original-kid:V1", string status = "ACTIVE", string? pem = null)
        {
            return new JObject { ["kid"] = kid, ["alg"] = "ES256", ["pem"] = pem ?? Pem, ["status"] = status };
        }

        public JObject Export(params JObject[] keys)
        {
            return new JObject
            {
                ["protocolVersion"] = "1",
                ["productCode"] = "test_product",
                ["serverBaseUrl"] = "https://licensing.example",
                ["signingKeys"] = new JArray(keys.Length == 0 ? new[] { SigningKey() } : keys)
            };
        }

        public JObject Jwk(string kid = "original-kid:V1")
        {
            return new JObject
            {
                ["kty"] = "EC",
                ["crv"] = "P-256",
                ["alg"] = "ES256",
                ["use"] = "sig",
                ["kid"] = kid,
                ["x"] = Encode(Parameters.Q.X!),
                ["y"] = Encode(Parameters.Q.Y!)
            };
        }

        public string Write(JObject document, string name = "client-config.json")
        {
            return WriteText(document.ToString(Formatting.None), name);
        }

        public string WriteText(string text, string name = "client-config.json")
        {
            var path = Path.Combine(Directory, name);
            File.WriteAllText(path, text, new UTF8Encoding(false, true));
            return path;
        }

        public SampleClientConfiguration Load(JObject? document = null)
        {
            return SampleClientConfiguration.LoadFile(Write(document ?? Export()));
        }

        public Dictionary<string, string?> Environment(JObject? jwk = null)
        {
            return new Dictionary<string, string?>
            {
                ["LICENSING_BASE_URL"] = "https://licensing.example",
                ["LICENSING_PRODUCT_CODE"] = "test_product",
                ["LICENSING_SERVER_JWK"] = (jwk ?? Jwk()).ToString(Formatting.None)
            };
        }

        public SampleClientConfiguration LoadEnvironment(Dictionary<string, string?> environment)
        {
            // Isolate the legacy branch from files written by another assertion in the same test.
            var environmentDirectory = Path.Combine(Directory, "legacy-only");
            System.IO.Directory.CreateDirectory(environmentDirectory);
            return SampleClientConfiguration.LoadInitial(environmentDirectory,
                name => environment.TryGetValue(name, out var value) ? value : null)!;
        }

        public void Dispose()
        {
            System.IO.Directory.Delete(Directory, true);
        }
    }
}
