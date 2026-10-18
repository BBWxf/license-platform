using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Licensing.Client
{
    internal static class Base64Url
    {
        internal static string Encode(byte[] value)
        {
            return Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        internal static byte[] Decode(string value)
        {
            if (value == null)
                throw new ArgumentNullException(nameof(value));
            // JWS and JWK values use unpadded, canonical base64url, not permissive MIME base64.
            foreach (var character in value)
            {
                if (!((character >= 'A' && character <= 'Z') || (character >= 'a' && character <= 'z') ||
                    (character >= '0' && character <= '9') || character == '-' || character == '_'))
                    throw new FormatException("Invalid base64url character.");
            }
            var padded = value.Replace('-', '+').Replace('_', '/');
            switch (padded.Length % 4)
            {
                case 0: break;
                case 2: padded += "=="; break;
                case 3: padded += "="; break;
                default: throw new FormatException("Invalid base64url length.");
            }
            var decoded = Convert.FromBase64String(padded);
            if (!string.Equals(Encode(decoded), value, StringComparison.Ordinal))
                throw new FormatException("Non-canonical base64url encoding.");
            return decoded;
        }
    }

    internal static class CryptoUtilities
    {
        internal static byte[] Sha256(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return sha.ComputeHash(bytes);
        }

        internal static string Sha256Base64Url(string value)
        {
            return Base64Url.Encode(Sha256(Encoding.UTF8.GetBytes(value)));
        }

        internal static string Sha256Hex(string value)
        {
            var hash = Sha256(Encoding.UTF8.GetBytes(value));
            var builder = new StringBuilder(hash.Length * 2);
            foreach (var item in hash)
                builder.Append(item.ToString("x2"));
            return builder.ToString();
        }

        internal static ECDsa CreateEcdsa(InstallationKeyRecord record)
        {
            var key = ECDsa.Create();
            key.ImportParameters(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                D = Clone(record.D),
                Q = new ECPoint { X = Clone(record.X), Y = Clone(record.Y) }
            });
            return key;
        }

        internal static ECDsa CreateEcdsa(ServerJwk jwk)
        {
            ValidateServerJwk(jwk);
            var x = Base64Url.Decode(jwk.X);
            var y = Base64Url.Decode(jwk.Y);
            if (x.Length != 32 || y.Length != 32)
                throw new LicensingException(LicenseErrorCode.InvalidConfiguration, "An ES256 server JWK must have 32-byte x and y coordinates.");
            var key = ECDsa.Create();
            key.ImportParameters(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = x, Y = y }
            });
            return key;
        }

        internal static void ValidateServerJwk(ServerJwk key)
        {
            if (key == null)
                throw new LicensingException(LicenseErrorCode.InvalidConfiguration, "A server JWK cannot be null.");
            if (!string.Equals(key.Kty, "EC", StringComparison.Ordinal) ||
                !string.Equals(key.Crv, "P-256", StringComparison.Ordinal) ||
                (!string.IsNullOrEmpty(key.Alg) && !string.Equals(key.Alg, "ES256", StringComparison.Ordinal)) ||
                (!string.IsNullOrEmpty(key.Use) && !string.Equals(key.Use, "sig", StringComparison.Ordinal)))
                throw new LicensingException(LicenseErrorCode.InvalidConfiguration, "Server verification keys must be EC P-256 keys for ES256.");
            if (string.IsNullOrWhiteSpace(key.Kid) || string.IsNullOrWhiteSpace(key.X) || string.IsNullOrWhiteSpace(key.Y))
                throw new LicensingException(LicenseErrorCode.InvalidConfiguration, "Each server JWK must include kid, x, and y.");
        }

        internal static string ToPublicJwkThumbprint(byte[] x, byte[] y)
        {
            var canonical = "{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"" + Base64Url.Encode(x) + "\",\"y\":\"" + Base64Url.Encode(y) + "\"}";
            return Sha256Base64Url(canonical);
        }

        internal static PublicInstallationJwk ToPublicJwk(InstallationKeyRecord record)
        {
            return new PublicInstallationJwk
            {
                Kid = record.KeyId,
                X = Base64Url.Encode(record.X),
                Y = Base64Url.Encode(record.Y)
            };
        }

        internal static string ToSubjectPublicKeyInfoPem(InstallationKeyRecord record)
        {
            if (record.X == null || record.Y == null || record.X.Length != 32 || record.Y.Length != 32)
                throw new CryptographicException("A P-256 public key must contain 32-byte x and y coordinates.");

            // RFC 5480 SubjectPublicKeyInfo for id-ecPublicKey + prime256v1, followed by an uncompressed point.
            var prefix = new byte[]
            {
                0x30, 0x59, 0x30, 0x13, 0x06, 0x07, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x02, 0x01,
                0x06, 0x08, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x03, 0x01, 0x07,
                0x03, 0x42, 0x00, 0x04
            };
            var der = new byte[prefix.Length + 64];
            Buffer.BlockCopy(prefix, 0, der, 0, prefix.Length);
            Buffer.BlockCopy(record.X, 0, der, prefix.Length, 32);
            Buffer.BlockCopy(record.Y, 0, der, prefix.Length + 32, 32);
            var encoded = Convert.ToBase64String(der);
            var builder = new StringBuilder(encoded.Length + 64);
            builder.Append("-----BEGIN PUBLIC KEY-----\n");
            for (var offset = 0; offset < encoded.Length; offset += 64)
                builder.Append(encoded, offset, Math.Min(64, encoded.Length - offset)).Append('\n');
            builder.Append("-----END PUBLIC KEY-----");
            Array.Clear(der, 0, der.Length);
            return builder.ToString();
        }

        internal static byte[] SignJws(InstallationKeyRecord record, byte[] data)
        {
            using (var key = CreateEcdsa(record))
            {
                var hash = Sha256(data);
                var signature = key.SignHash(hash);
                return NormalizeToP1363(signature, 32);
            }
        }

        internal static bool VerifyJws(ECDsa key, byte[] data, byte[] p1363Signature)
        {
            if (p1363Signature.Length != 64)
                return false;
            var hash = Sha256(data);
            try
            {
                if (key.VerifyHash(hash, p1363Signature))
                    return true;
            }
            catch (CryptographicException)
            {
                // Some legacy providers only accept DER. Try it below.
            }

            try
            {
                return key.VerifyHash(hash, P1363ToDer(p1363Signature, 32));
            }
            catch (CryptographicException)
            {
                return false;
            }
        }

        internal static byte[] NormalizeToP1363(byte[] signature, int fieldSize)
        {
            if (signature.Length == fieldSize * 2)
                return signature;
            if (signature.Length > 0 && signature[0] == 0x30)
                return DerToP1363(signature, fieldSize);
            throw new CryptographicException("ECDSA provider returned an unsupported signature encoding.");
        }

        private static byte[] DerToP1363(byte[] der, int fieldSize)
        {
            var offset = 0;
            RequireByte(der, ref offset, 0x30);
            var sequenceLength = ReadLength(der, ref offset);
            if (sequenceLength != der.Length - offset)
                throw new CryptographicException("Invalid DER ECDSA sequence length.");
            RequireByte(der, ref offset, 0x02);
            var rLength = ReadLength(der, ref offset);
            var r = ReadInteger(der, ref offset, rLength, fieldSize);
            RequireByte(der, ref offset, 0x02);
            var sLength = ReadLength(der, ref offset);
            var s = ReadInteger(der, ref offset, sLength, fieldSize);
            if (offset != der.Length)
                throw new CryptographicException("Trailing data in DER ECDSA signature.");
            var raw = new byte[fieldSize * 2];
            Buffer.BlockCopy(r, 0, raw, 0, fieldSize);
            Buffer.BlockCopy(s, 0, raw, fieldSize, fieldSize);
            return raw;
        }

        private static byte[] P1363ToDer(byte[] raw, int fieldSize)
        {
            var r = EncodeInteger(raw, 0, fieldSize);
            var s = EncodeInteger(raw, fieldSize, fieldSize);
            var payloadLength = 2 + r.Length + 2 + s.Length;
            using (var stream = new MemoryStream())
            {
                stream.WriteByte(0x30);
                WriteLength(stream, payloadLength);
                stream.WriteByte(0x02);
                WriteLength(stream, r.Length);
                stream.Write(r, 0, r.Length);
                stream.WriteByte(0x02);
                WriteLength(stream, s.Length);
                stream.Write(s, 0, s.Length);
                return stream.ToArray();
            }
        }

        private static byte[] ReadInteger(byte[] source, ref int offset, int length, int fieldSize)
        {
            if (length <= 0 || offset + length > source.Length)
                throw new CryptographicException("Invalid DER ECDSA integer.");
            while (length > 1 && source[offset] == 0)
            {
                offset++;
                length--;
            }
            if (length > fieldSize)
                throw new CryptographicException("DER ECDSA integer is too large.");
            var result = new byte[fieldSize];
            Buffer.BlockCopy(source, offset, result, fieldSize - length, length);
            offset += length;
            return result;
        }

        private static byte[] EncodeInteger(byte[] source, int offset, int length)
        {
            var first = 0;
            while (first < length - 1 && source[offset + first] == 0)
                first++;
            var remaining = length - first;
            var prependZero = (source[offset + first] & 0x80) != 0;
            var result = new byte[remaining + (prependZero ? 1 : 0)];
            Buffer.BlockCopy(source, offset + first, result, prependZero ? 1 : 0, remaining);
            return result;
        }

        private static int ReadLength(byte[] source, ref int offset)
        {
            if (offset >= source.Length)
                throw new CryptographicException("Invalid DER length.");
            var value = source[offset++];
            if ((value & 0x80) == 0)
                return value;
            var count = value & 0x7f;
            if (count == 0 || count > 2 || offset + count > source.Length)
                throw new CryptographicException("Invalid DER length.");
            var length = 0;
            for (var i = 0; i < count; i++)
                length = (length << 8) | source[offset++];
            return length;
        }

        private static void WriteLength(Stream stream, int length)
        {
            if (length < 128)
            {
                stream.WriteByte((byte)length);
                return;
            }
            stream.WriteByte(0x81);
            stream.WriteByte((byte)length);
        }

        private static void RequireByte(byte[] source, ref int offset, byte expected)
        {
            if (offset >= source.Length || source[offset++] != expected)
                throw new CryptographicException("Invalid DER ECDSA signature.");
        }

        private static byte[] Clone(byte[] source) => source == null ? Array.Empty<byte>() : source.ToArray();
    }

    public sealed class PublicInstallationJwk
    {
        public string Kty { get; set; } = "EC";
        public string Crv { get; set; } = "P-256";
        public string X { get; set; } = string.Empty;
        public string Y { get; set; } = string.Empty;
        public string Kid { get; set; } = string.Empty;
        public string Alg { get; set; } = "ES256";
    }
}
