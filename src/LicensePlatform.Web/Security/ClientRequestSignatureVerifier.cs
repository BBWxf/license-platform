using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using LicensePlatform.Application.Abstractions;
using LicensePlatform.Contracts.Challenges;
using LicensePlatform.Web.Middleware;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;

namespace LicensePlatform.Web.Security;

public sealed class ClientRequestSignatureFilter(ClientRequestSignatureVerifier verifier) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var request = context.Arguments.FirstOrDefault(argument => argument is
            CreateActivationChallengeRequest
            or CreateLeaseRefreshChallengeRequest
            or CompleteActivationChallengeRequest
            or ValidateLicenseRequest);
        if (request is null)
        {
            return ApiProblemResults.Create(
                context.HttpContext,
                StatusCodes.Status400BadRequest,
                "invalid_request",
                "请求格式或参数无效。");
        }

        if (!await verifier.VerifyAsync(
                context.HttpContext,
                request,
                context.HttpContext.RequestAborted))
        {
            context.HttpContext.Response.Headers.WWWAuthenticate = "Signature realm=\"license-client\"";
            return ApiProblemResults.Create(
                context.HttpContext,
                StatusCodes.Status401Unauthorized,
                "invalid_client_signature",
                "客户端请求认证失败。");
        }

        return await next(context);
    }
}

public sealed class ClientRequestSignatureVerifier(
    IClientRequestPublicKeyResolver publicKeyResolver,
    IClock clock,
    ClientRequestReplayCache replayCache,
    IConfiguration configuration)
{
    private const string ProtocolVersion = "licensing-v1";
    private const string InstallationHeader = "X-Licensing-Installation";
    private const string TimestampHeader = "X-Licensing-Timestamp";
    private const string NonceHeader = "X-Licensing-Nonce";
    private const string ContentHashHeader = "X-Licensing-Content-SHA256";
    private const string KeyIdHeader = "X-Licensing-Key-Id";
    private const string AlgorithmHeader = "X-Licensing-Algorithm";
    private const string SignatureHeader = "X-Licensing-Signature";

    private readonly int _allowedClockSkewSeconds = ReadClockSkew(configuration);

    public async Task<bool> VerifyAsync(
        HttpContext context,
        object request,
        CancellationToken cancellationToken)
    {
        if (!HttpMethods.IsPost(context.Request.Method)
            || !TryGetSingleHeader(context, InstallationHeader, 32, out var installation)
            || !Guid.TryParseExact(installation, "N", out var installationId)
            || installationId == Guid.Empty
            || !string.Equals(installationId.ToString("N"), installation, StringComparison.Ordinal)
            || !TryGetSingleHeader(context, TimestampHeader, 20, out var timestampText)
            || !long.TryParse(timestampText, NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp)
            || !string.Equals(
                timestampText,
                timestamp.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal)
            || !TryGetSingleHeader(context, NonceHeader, 64, out var nonceText)
            || !TryDecodeBase64Url(nonceText, expectedLength: 16, out var nonce)
            || !TryGetSingleHeader(context, ContentHashHeader, 64, out var contentHashText)
            || !TryDecodeBase64Url(contentHashText, expectedLength: 32, out var suppliedContentHash)
            || !TryGetSingleHeader(context, KeyIdHeader, 128, out var keyId)
            || !TryGetSingleHeader(context, AlgorithmHeader, 16, out var algorithm)
            || !string.Equals(algorithm, "ES256", StringComparison.Ordinal)
            || !TryGetSingleHeader(context, SignatureHeader, 128, out var signatureText)
            || !TryDecodeBase64Url(signatureText, expectedLength: 64, out var signature))
        {
            return false;
        }

        try
        {
            var nowUnixSeconds = clock.UtcNow.ToUnixTimeSeconds();
            if (timestamp < nowUnixSeconds - _allowedClockSkewSeconds
                || timestamp > nowUnixSeconds + _allowedClockSkewSeconds)
            {
                return false;
            }

            if (!context.Items.TryGetValue(ClientApiRequestBufferingMiddleware.BufferedBodyItemKey, out var item)
                || item is not byte[] body
                || body.Length == 0)
            {
                return false;
            }

            var actualContentHash = SHA256.HashData(body);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(actualContentHash, suppliedContentHash))
                {
                    return false;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(actualContentHash);
            }

            var publicKeyPem = await publicKeyResolver.ResolveAsync(request, cancellationToken);
            if (string.IsNullOrWhiteSpace(publicKeyPem)
                || publicKeyPem.Length > 2_048
                || publicKeyPem.Contains("PRIVATE KEY", StringComparison.Ordinal))
            {
                return false;
            }

            using var publicKey = ECDsa.Create();
            try
            {
                publicKey.ImportFromPem(publicKeyPem);
            }
            catch (Exception exception) when (exception is CryptographicException or ArgumentException)
            {
                return false;
            }

            var publicParameters = publicKey.ExportParameters(includePrivateParameters: false);
            if (publicKey.KeySize != 256
                || !string.Equals(
                    publicParameters.Curve.Oid.Value,
                    ECCurve.NamedCurves.nistP256.Oid.Value,
                    StringComparison.Ordinal)
                || !KeyIdMatches(publicKey, keyId))
            {
                return false;
            }

            var rawTarget = context.Features.Get<IHttpRequestFeature>()?.RawTarget;
            if (string.IsNullOrEmpty(rawTarget)
                || rawTarget[0] != '/'
                || rawTarget.Length > 2_048
                || rawTarget.Contains('#'))
            {
                return false;
            }

            var canonical = string.Join('\n',
                ProtocolVersion,
                "POST",
                rawTarget,
                timestampText,
                nonceText,
                contentHashText);
            var canonicalBytes = Encoding.UTF8.GetBytes(canonical);
            try
            {
                if (!publicKey.VerifyData(
                        canonicalBytes,
                        signature,
                        HashAlgorithmName.SHA256,
                        DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                {
                    return false;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(canonicalBytes);
            }

            return replayCache.TryRegister(
                keyId,
                nonceText,
                nowUnixSeconds,
                nowUnixSeconds + (_allowedClockSkewSeconds * 2L));
        }
        catch (Exception exception) when (exception is
            CryptographicException
            or ArgumentException
            or FormatException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(suppliedContentHash);
            CryptographicOperations.ZeroMemory(signature);
        }
    }

    private static bool KeyIdMatches(ECDsa publicKey, string suppliedKeyId)
    {
        var parameters = publicKey.ExportParameters(includePrivateParameters: false);
        if (parameters.Q.X is not { Length: 32 } x || parameters.Q.Y is not { Length: 32 } y)
        {
            return false;
        }

        try
        {
            var canonicalJwk = $"{{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"{EncodeBase64Url(x)}\",\"y\":\"{EncodeBase64Url(y)}\"}}";
            var thumbprint = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJwk));
            var suppliedBytes = Encoding.ASCII.GetBytes(suppliedKeyId);
            try
            {
                var expectedText = Encoding.ASCII.GetBytes(EncodeBase64Url(thumbprint));
                try
                {
                    return expectedText.Length == suppliedBytes.Length
                           && CryptographicOperations.FixedTimeEquals(expectedText, suppliedBytes);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(expectedText);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(thumbprint);
                CryptographicOperations.ZeroMemory(suppliedBytes);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(x);
            CryptographicOperations.ZeroMemory(y);
        }
    }

    private static bool TryGetSingleHeader(
        HttpContext context,
        string name,
        int maximumLength,
        out string value)
    {
        StringValues values = context.Request.Headers[name];
        if (values.Count != 1
            || string.IsNullOrWhiteSpace(values[0])
            || values[0]!.Length > maximumLength)
        {
            value = string.Empty;
            return false;
        }

        value = values[0]!;
        return true;
    }

    private static bool TryDecodeBase64Url(string value, int expectedLength, out byte[] bytes)
    {
        bytes = [];
        if (value.Contains('=') || value.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            return false;
        }

        var base64 = value.Replace('-', '+').Replace('_', '/');
        if (base64.Length % 4 == 1)
        {
            return false;
        }

        try
        {
            bytes = Convert.FromBase64String(
                base64.PadRight(base64.Length + ((4 - base64.Length % 4) % 4), '='));
            return bytes.Length == expectedLength
                   && string.Equals(EncodeBase64Url(bytes), value, StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }

    private static string EncodeBase64Url(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static int ReadClockSkew(IConfiguration configuration)
    {
        var value = configuration.GetValue<int?>("Security:ClientRequestClockSkewSeconds") ?? 300;
        if (value is < 30 or > 900)
        {
            throw new InvalidOperationException(
                "Security:ClientRequestClockSkewSeconds must be between 30 and 900.");
        }

        return value;
    }
}

public sealed class ClientRequestReplayCache
{
    private const int MaximumEntries = 100_000;
    private readonly ConcurrentDictionary<string, long> _entries = new(StringComparer.Ordinal);
    private long _operationCount;

    public bool TryRegister(
        string keyId,
        string nonce,
        long nowUnixSeconds,
        long expiresAtUnixSeconds)
    {
        if ((Interlocked.Increment(ref _operationCount) & 0xff) == 0
            || _entries.Count >= MaximumEntries)
        {
            RemoveExpired(nowUnixSeconds);
        }

        if (_entries.Count >= MaximumEntries)
        {
            return false;
        }

        var key = keyId + ":" + nonce;
        while (true)
        {
            if (!_entries.TryGetValue(key, out var existingExpiration))
            {
                return _entries.TryAdd(key, expiresAtUnixSeconds);
            }

            if (existingExpiration > nowUnixSeconds)
            {
                return false;
            }

            if (_entries.TryUpdate(key, expiresAtUnixSeconds, existingExpiration))
            {
                return true;
            }
        }
    }

    private void RemoveExpired(long nowUnixSeconds)
    {
        foreach (var entry in _entries)
        {
            if (entry.Value <= nowUnixSeconds)
            {
                ((ICollection<KeyValuePair<string, long>>)_entries).Remove(entry);
            }
        }
    }
}
