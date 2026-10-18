using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Licensing.Client;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Licensing.Samples
{
    internal sealed class SampleConfigurationException : Exception
    {
        private SampleConfigurationException(string message) : base(message) { }

        internal static SampleConfigurationException InvalidConfiguration()
        {
            return new SampleConfigurationException("客户端配置无效，请重新导出并检查公共配置。");
        }

        internal static SampleConfigurationException InvalidAddress()
        {
            return new SampleConfigurationException("服务器地址无效，请填写不含凭据、路径、查询或片段的 HTTP(S) 服务地址。");
        }

        internal static SampleConfigurationException HttpNotAllowed()
        {
            return new SampleConfigurationException("明文 HTTP 默认禁用，请明确确认后重试。");
        }
    }

    // Linked into each sample: only public configuration belongs here, never cards or credentials.
    internal sealed class SampleClientConfiguration
    {
        private const int MaxConfigurationBytes = 256 * 1024;
        private const int MaxJsonDepth = 16;
        private const int MaxKeys = 32;
        private const int MaxJsonStringLength = 4096;
        private const int MaxAddressLength = 2048;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly byte[] PublicKeyPrefix =
        {
            // 26-byte SPKI header, then the uncompressed-point marker (27 bytes in total).
            0x30, 0x59, 0x30, 0x13, 0x06, 0x07, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x02, 0x01,
            0x06, 0x08, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x03, 0x01, 0x07, 0x03, 0x42, 0x00, 0x04
        };
        private readonly ServerJwk[] _keys;
        private readonly string _issuer;
        private readonly string _audience;

        private SampleClientConfiguration(string productCode, string serverBaseUrl, string source,
            ServerJwk[] keys, string issuer, string? audience)
        {
            ProductCode = productCode;
            ServerBaseUrl = serverBaseUrl;
            Source = source;
            _keys = keys;
            _issuer = issuer;
            _audience = audience ?? productCode;
        }

        public string ProductCode { get; }
        public string ServerBaseUrl { get; }
        public string Source { get; }

        public static SampleClientConfiguration LoadFile(string path)
        {
            try
            {
                var root = ParseJson(ReadConfigurationFile(path));
                if (!string.Equals(RequiredString(root, "protocolVersion", 8), "1", StringComparison.Ordinal))
                    throw SampleConfigurationException.InvalidConfiguration();
                var productCode = ValidateProductCode(RequiredString(root, "productCode", 64));
                var address = ValidateText(RequiredString(root, "serverBaseUrl", MaxAddressLength), MaxAddressLength);
                var items = root["signingKeys"] as JArray;
                if (items == null || items.Count == 0 || items.Count > MaxKeys)
                    throw SampleConfigurationException.InvalidConfiguration();

                var keys = new List<ServerJwk>();
                var identifiers = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in items)
                {
                    var key = item as JObject;
                    if (key == null)
                        throw SampleConfigurationException.InvalidConfiguration();
                    RejectPrivateMaterial(key);
                    var kid = ValidateKid(RequiredString(key, "kid", 128));
                    if (!identifiers.Add(kid) || RequiredString(key, "alg", 16) != "ES256")
                        throw SampleConfigurationException.InvalidConfiguration();
                    var status = RequiredString(key, "status", 16);
                    if (status != "ACTIVE" && status != "RETIRED" && status != "REVOKED")
                        throw SampleConfigurationException.InvalidConfiguration();
                    // Even excluded records must be well-formed public keys.
                    var publicKey = ReadPublicKeyPem(kid, RequiredString(key, "pem", 1024));
                    if (status != "REVOKED")
                        keys.Add(publicKey);
                }
                if (keys.Count == 0)
                    throw SampleConfigurationException.InvalidConfiguration();
                // Exported lease durations and other informational fields do not change SDK policy.
                return new SampleClientConfiguration(productCode, address, "配置文件", keys.ToArray(), "license-platform", null);
            }
            catch (SampleConfigurationException) { throw; }
            catch (Exception)
            {
                // No raw parser, filesystem or provider exception (including its inner exception) reaches the UI.
                throw SampleConfigurationException.InvalidConfiguration();
            }
        }

        public static SampleClientConfiguration? LoadInitial(string appDirectory, Func<string, string?> readEnvironment)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(appDirectory))
                    throw SampleConfigurationException.InvalidConfiguration();
                var path = Path.Combine(appDirectory, "client-config.json");
                var exists = true;
                try { File.GetAttributes(path); }
                catch (FileNotFoundException) { exists = false; }
                catch (DirectoryNotFoundException) { exists = false; }
                // An inaccessible, malformed or directory-valued candidate is an error, not an environment fallback.
                if (exists)
                    return LoadFile(path);
                if (readEnvironment == null)
                    throw SampleConfigurationException.InvalidConfiguration();

                var address = readEnvironment("LICENSING_BASE_URL");
                var productCode = readEnvironment("LICENSING_PRODUCT_CODE");
                var jwk = readEnvironment("LICENSING_SERVER_JWK");
                var issuer = readEnvironment("LICENSING_ISSUER");
                var audience = readEnvironment("LICENSING_AUDIENCE");
                if ((address != null && address.Length > MaxAddressLength) ||
                    (productCode != null && productCode.Length > 64) || (jwk != null && jwk.Length > MaxConfigurationBytes) ||
                    (issuer != null && issuer.Length > 256) || (audience != null && audience.Length > 256))
                    throw SampleConfigurationException.InvalidConfiguration();
                if (string.IsNullOrWhiteSpace(address) && string.IsNullOrWhiteSpace(productCode) &&
                    string.IsNullOrWhiteSpace(jwk) && string.IsNullOrWhiteSpace(issuer) && string.IsNullOrWhiteSpace(audience))
                    return null;
                if (string.IsNullOrWhiteSpace(address) || string.IsNullOrWhiteSpace(productCode) || string.IsNullOrWhiteSpace(jwk))
                    throw SampleConfigurationException.InvalidConfiguration();

                return new SampleClientConfiguration(
                    ValidateProductCode(productCode!), ValidateText(address!, MaxAddressLength), "环境变量",
                    ReadLegacyKeys(jwk!),
                    string.IsNullOrWhiteSpace(issuer) ? "license-platform" : ValidateText(issuer!, 256).Trim(),
                    string.IsNullOrWhiteSpace(audience) ? null : ValidateText(audience!, 256).Trim());
            }
            catch (SampleConfigurationException) { throw; }
            catch (Exception)
            {
                throw SampleConfigurationException.InvalidConfiguration();
            }
        }

        public LicenseClientOptions CreateOptions(string serverBaseUrl, bool allowInsecureHttp)
        {
            var uri = ValidateOrigin(serverBaseUrl);
            if (uri.Scheme == Uri.UriSchemeHttp && !allowInsecureHttp)
                throw SampleConfigurationException.HttpNotAllowed();
            var options = new LicenseClientOptions
            {
                ServiceBaseUri = uri,
                ProductCode = ProductCode,
                DeviceHint = "Licensed sample device",
                ExpectedIssuer = _issuer,
                ExpectedAudience = _audience,
                AllowInsecureHttp = allowInsecureHttp
            };
            foreach (var key in _keys)
            {
                // A caller may mutate its options without changing future options from this configuration.
                options.ServerVerificationKeys.Add(new ServerJwk
                {
                    Kid = key.Kid,
                    Kty = key.Kty,
                    Crv = key.Crv,
                    Alg = key.Alg,
                    Use = key.Use,
                    X = key.X,
                    Y = key.Y
                });
            }
            return options;
        }

        private static string ReadConfigurationFile(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length == 0 || stream.Length > MaxConfigurationBytes)
                    throw SampleConfigurationException.InvalidConfiguration();
                var bytes = new byte[MaxConfigurationBytes + 1];
                var count = 0;
                while (count < bytes.Length)
                {
                    var read = stream.Read(bytes, count, bytes.Length - count);
                    if (read == 0)
                        break;
                    count += read;
                }
                if (count == 0 || count > MaxConfigurationBytes)
                    throw SampleConfigurationException.InvalidConfiguration();
                var offset = count >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
                return StrictUtf8.GetString(bytes, offset, count - offset);
            }
        }

        private static JObject ParseJson(string json)
        {
            if (json.Length == 0 || json.Length > MaxConfigurationBytes || StrictUtf8.GetByteCount(json) > MaxConfigurationBytes)
                throw SampleConfigurationException.InvalidConfiguration();
            ValidateJsonLexemes(json);
            using (var input = new StringReader(json))
            using (var reader = new JsonTextReader(input)
            {
                DateParseHandling = DateParseHandling.None,
                MaxDepth = MaxJsonDepth,
                SupportMultipleContent = true
            })
            {
                var root = JToken.Load(reader, new JsonLoadSettings
                {
                    DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
                    CommentHandling = CommentHandling.Load
                }) as JObject;
                if (root == null || reader.Read())
                    throw SampleConfigurationException.InvalidConfiguration();
                ValidateJsonStrings(root);
                return root;
            }
        }

        // Newtonsoft deliberately accepts JavaScript extensions. This bounded lexical pass restricts
        // it to JSON (including strict numbers/escapes and no comments, single quotes or trailing commas).
        // JsonTextReader remains responsible for grammar, nesting and duplicate properties.
        private static void ValidateJsonLexemes(string json)
        {
            var previous = '\0';
            for (var i = 0; i < json.Length; i++)
            {
                var c = json[i];
                if (IsJsonWhitespace(c))
                    continue;
                if (c == '"')
                {
                    var closed = false;
                    while (++i < json.Length)
                    {
                        c = json[i];
                        if (c == '"') { closed = true; break; }
                        if (c < 0x20)
                            throw SampleConfigurationException.InvalidConfiguration();
                        if (c != '\\')
                            continue;
                        if (++i >= json.Length)
                            throw SampleConfigurationException.InvalidConfiguration();
                        c = json[i];
                        if (c == 'u')
                        {
                            for (var digit = 0; digit < 4; digit++)
                            {
                                if (++i >= json.Length || !IsHex(json[i]))
                                    throw SampleConfigurationException.InvalidConfiguration();
                            }
                        }
                        else if (c != '"' && c != '\\' && c != '/' && c != 'b' && c != 'f' && c != 'n' && c != 'r' && c != 't')
                            throw SampleConfigurationException.InvalidConfiguration();
                    }
                    if (!closed)
                        throw SampleConfigurationException.InvalidConfiguration();
                    previous = 's';
                }
                else if (c == '-' || IsDigit(c))
                {
                    if (c == '-' && (++i >= json.Length || !IsDigit(json[i])))
                        throw SampleConfigurationException.InvalidConfiguration();
                    if (json[i] == '0')
                        i++;
                    else
                    {
                        if (json[i] < '1' || json[i] > '9')
                            throw SampleConfigurationException.InvalidConfiguration();
                        while (i < json.Length && IsDigit(json[i])) i++;
                    }
                    if (i < json.Length && json[i] == '.')
                    {
                        if (++i >= json.Length || !IsDigit(json[i]))
                            throw SampleConfigurationException.InvalidConfiguration();
                        while (i < json.Length && IsDigit(json[i])) i++;
                    }
                    if (i < json.Length && (json[i] == 'e' || json[i] == 'E'))
                    {
                        i++;
                        if (i < json.Length && (json[i] == '+' || json[i] == '-')) i++;
                        if (i >= json.Length || !IsDigit(json[i]))
                            throw SampleConfigurationException.InvalidConfiguration();
                        while (i < json.Length && IsDigit(json[i])) i++;
                    }
                    RequireJsonDelimiter(json, i);
                    i--;
                    previous = 'n';
                }
                else if (c == 't' || c == 'f' || c == 'n')
                {
                    var literal = c == 't' ? "true" : c == 'f' ? "false" : "null";
                    if (i + literal.Length > json.Length || string.CompareOrdinal(json, i, literal, 0, literal.Length) != 0)
                        throw SampleConfigurationException.InvalidConfiguration();
                    i += literal.Length;
                    RequireJsonDelimiter(json, i);
                    i--;
                    previous = 'v';
                }
                else if (c == '{' || c == '[' || c == '}' || c == ']' || c == ':' || c == ',')
                {
                    if ((c == '}' || c == ']') && previous == ',')
                        throw SampleConfigurationException.InvalidConfiguration();
                    previous = c;
                }
                else
                    throw SampleConfigurationException.InvalidConfiguration();
            }
        }

        private static void RequireJsonDelimiter(string json, int index)
        {
            if (index < json.Length && !IsJsonWhitespace(json[index]) && json[index] != ',' && json[index] != ']' && json[index] != '}')
                throw SampleConfigurationException.InvalidConfiguration();
        }

        private static bool IsDigit(char c) { return c >= '0' && c <= '9'; }
        private static bool IsHex(char c) { return IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'); }
        private static bool IsJsonWhitespace(char c) { return c == ' ' || c == '\t' || c == '\r' || c == '\n'; }

        private static void ValidateJsonStrings(JToken token)
        {
            var obj = token as JObject;
            if (obj != null)
            {
                foreach (var property in obj.Properties())
                {
                    if (property.Name.Length > 128)
                        throw SampleConfigurationException.InvalidConfiguration();
                    ValidateJsonStrings(property.Value);
                }
            }
            else if (token is JArray array)
            {
                foreach (var value in array)
                    ValidateJsonStrings(value);
            }
            else if (token.Type == JTokenType.String && token.Value<string>()!.Length > MaxJsonStringLength)
                throw SampleConfigurationException.InvalidConfiguration();
        }

        private static string RequiredString(JObject obj, string name, int maxLength)
        {
            var value = obj[name];
            if (value == null || value.Type != JTokenType.String)
                throw SampleConfigurationException.InvalidConfiguration();
            var text = value.Value<string>()!;
            if (string.IsNullOrWhiteSpace(text) || text.Length > maxLength)
                throw SampleConfigurationException.InvalidConfiguration();
            return text;
        }

        private static string ValidateText(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength)
                throw SampleConfigurationException.InvalidConfiguration();
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (char.IsControl(c) || char.IsLowSurrogate(c))
                    throw SampleConfigurationException.InvalidConfiguration();
                if (char.IsHighSurrogate(c) && (++i >= value.Length || !char.IsLowSurrogate(value[i])))
                    throw SampleConfigurationException.InvalidConfiguration();
            }
            return value;
        }

        private static string ValidateProductCode(string value)
        {
            value = ValidateText(value, 64).Trim().ToUpperInvariant();
            foreach (var c in value)
                if (!IsAsciiLetterOrDigit(c) && c != '-' && c != '_')
                    throw SampleConfigurationException.InvalidConfiguration();
            return value;
        }

        private static string ValidateKid(string value)
        {
            ValidateText(value, 128);
            // The protocol treats kid as opaque. Preserve it, rather than inventing a token alphabet
            // or silently trimming identifiers and changing the signed token's key lookup.
            if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
                throw SampleConfigurationException.InvalidConfiguration();
            return value;
        }

        private static bool IsAsciiLetterOrDigit(char c)
        {
            return IsDigit(c) || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
        }

        private static void RejectPrivateMaterial(JObject key)
        {
            foreach (var property in key.Properties())
            {
                switch (property.Name.ToLowerInvariant())
                {
                    case "d":
                    case "p":
                    case "q":
                    case "dp":
                    case "dq":
                    case "qi":
                    case "oth":
                    case "k":
                    case "privatekey":
                    case "privatekeypem":
                    case "privateparameters":
                        throw SampleConfigurationException.InvalidConfiguration();
                }
            }
        }

        private static ServerJwk ReadPublicKeyPem(string kid, string pem)
        {
            const string begin = "-----BEGIN PUBLIC KEY-----\n";
            const string end = "-----END PUBLIC KEY-----";
            pem = pem.Replace("\r\n", "\n");
            if (pem.EndsWith("\n", StringComparison.Ordinal))
                pem = pem.Substring(0, pem.Length - 1);
            if (!pem.StartsWith(begin, StringComparison.Ordinal) || !pem.EndsWith("\n" + end, StringComparison.Ordinal))
                throw SampleConfigurationException.InvalidConfiguration();
            var body = pem.Substring(begin.Length, pem.Length - begin.Length - end.Length - 1);
            var encoded = new StringBuilder();
            foreach (var line in body.Split('\n'))
            {
                if (line.Length == 0 || line.Length > 64)
                    throw SampleConfigurationException.InvalidConfiguration();
                foreach (var c in line)
                    if (!IsAsciiLetterOrDigit(c) && c != '+' && c != '/' && c != '=')
                        throw SampleConfigurationException.InvalidConfiguration();
                encoded.Append(line);
            }
            var text = encoded.ToString();
            var der = Convert.FromBase64String(text);
            if (der.Length != 91 || Convert.ToBase64String(der) != text)
                throw SampleConfigurationException.InvalidConfiguration();
            for (var i = 0; i < PublicKeyPrefix.Length; i++)
                if (der[i] != PublicKeyPrefix[i])
                    throw SampleConfigurationException.InvalidConfiguration();
            var x = new byte[32];
            var y = new byte[32];
            Buffer.BlockCopy(der, PublicKeyPrefix.Length, x, 0, x.Length);
            Buffer.BlockCopy(der, PublicKeyPrefix.Length + x.Length, y, 0, y.Length);
            ValidatePoint(x, y);
            return new ServerJwk { Kid = kid, Kty = "EC", Crv = "P-256", Alg = "ES256", Use = "sig", X = EncodeBase64Url(x), Y = EncodeBase64Url(y) };
        }

        private static ServerJwk[] ReadLegacyKeys(string json)
        {
            var root = ParseJson(json);
            RejectPrivateMaterial(root);
            var items = root["keys"];
            if (items == null)
                return new[] { ReadLegacyKey(root) };
            var array = items as JArray;
            if (array == null || array.Count == 0 || array.Count > MaxKeys)
                throw SampleConfigurationException.InvalidConfiguration();
            var keys = new List<ServerJwk>();
            var identifiers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in array)
            {
                var obj = item as JObject;
                if (obj == null)
                    throw SampleConfigurationException.InvalidConfiguration();
                var key = ReadLegacyKey(obj);
                if (!identifiers.Add(key.Kid))
                    throw SampleConfigurationException.InvalidConfiguration();
                keys.Add(key);
            }
            return keys.ToArray();
        }

        private static ServerJwk ReadLegacyKey(JObject obj)
        {
            RejectPrivateMaterial(obj);
            if (RequiredString(obj, "kty", 16) != "EC" || RequiredString(obj, "crv", 16) != "P-256" ||
                !IsOptionalKeyValue(obj["alg"], "ES256") || !IsOptionalKeyValue(obj["use"], "sig"))
                throw SampleConfigurationException.InvalidConfiguration();
            var kid = ValidateKid(RequiredString(obj, "kid", 128));
            var xText = RequiredString(obj, "x", 43);
            var yText = RequiredString(obj, "y", 43);
            var x = DecodeCoordinate(xText);
            var y = DecodeCoordinate(yText);
            ValidatePoint(x, y);
            return new ServerJwk { Kid = kid, Kty = "EC", Crv = "P-256", Alg = "ES256", Use = "sig", X = xText, Y = yText };
        }

        private static bool IsOptionalKeyValue(JToken? token, string expected)
        {
            return token == null || token.Type == JTokenType.Null ||
                (token.Type == JTokenType.String && (token.Value<string>() == string.Empty || token.Value<string>() == expected));
        }

        private static byte[] DecodeCoordinate(string text)
        {
            if (text.Length != 43)
                throw SampleConfigurationException.InvalidConfiguration();
            foreach (var c in text)
                if (!IsAsciiLetterOrDigit(c) && c != '-' && c != '_')
                    throw SampleConfigurationException.InvalidConfiguration();
            var bytes = Convert.FromBase64String(text.Replace('-', '+').Replace('_', '/') + "=");
            if (bytes.Length != 32 || EncodeBase64Url(bytes) != text)
                throw SampleConfigurationException.InvalidConfiguration();
            return bytes;
        }

        private static string EncodeBase64Url(byte[] value)
        {
            return Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static void ValidatePoint(byte[] x, byte[] y)
        {
            using (var key = ECDsa.Create())
            {
                key.ImportParameters(new ECParameters
                {
                    Curve = ECCurve.NamedCurves.nistP256,
                    Q = new ECPoint { X = x, Y = y }
                });
                // Import through the native provider, rather than only checking lengths/OIDs.
                var actual = key.ExportParameters(false);
                if (!EqualBytes(x, actual.Q.X) || !EqualBytes(y, actual.Q.Y))
                    throw SampleConfigurationException.InvalidConfiguration();
            }
        }

        private static bool EqualBytes(byte[] left, byte[]? right)
        {
            if (right == null || left.Length != right.Length)
                return false;
            for (var i = 0; i < left.Length; i++)
                if (left[i] != right[i])
                    return false;
            return true;
        }

        private static Uri ValidateOrigin(string address)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(address) || address.Length > MaxAddressLength)
                    throw SampleConfigurationException.InvalidAddress();
                foreach (var c in address)
                    if (char.IsWhiteSpace(c) || char.IsControl(c) || c == '\\' || c == '%' || c == '@' || c == '?' || c == '#')
                        throw SampleConfigurationException.InvalidAddress();
                var schemeLength = address.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? 8 :
                    address.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? 7 : 0;
                if (schemeLength == 0)
                    throw SampleConfigurationException.InvalidAddress();
                var pathIndex = address.IndexOf('/', schemeLength);
                if (pathIndex >= 0 && pathIndex != address.Length - 1)
                    throw SampleConfigurationException.InvalidAddress();
                var authority = address.Substring(schemeLength, (pathIndex < 0 ? address.Length : pathIndex) - schemeLength);
                if (authority.Length == 0 || authority.EndsWith(":", StringComparison.Ordinal))
                    throw SampleConfigurationException.InvalidAddress();
                if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri == null ||
                    (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
                    !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/" ||
                    !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
                    Uri.CheckHostName(uri.Host) == UriHostNameType.Unknown || uri.Port <= 0 ||
                    string.Equals(uri.Host.TrimEnd('.'), "SERVER_IP", StringComparison.OrdinalIgnoreCase))
                    throw SampleConfigurationException.InvalidAddress();
                return uri;
            }
            catch (SampleConfigurationException) { throw; }
            catch (Exception)
            {
                throw SampleConfigurationException.InvalidAddress();
            }
        }
    }
}
