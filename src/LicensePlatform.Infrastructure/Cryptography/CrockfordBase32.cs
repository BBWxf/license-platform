using System.Text;

namespace LicensePlatform.Infrastructure.Cryptography;

internal static class CrockfordBase32
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return string.Empty;
        }

        var outputLength = checked((data.Length * 8 + 4) / 5);
        var output = new StringBuilder(outputLength);
        var buffer = 0;
        var bitsInBuffer = 0;

        foreach (var value in data)
        {
            buffer = (buffer << 8) | value;
            bitsInBuffer += 8;

            while (bitsInBuffer >= 5)
            {
                bitsInBuffer -= 5;
                output.Append(Alphabet[(buffer >> bitsInBuffer) & 31]);
                buffer &= (1 << bitsInBuffer) - 1;
            }
        }

        if (bitsInBuffer > 0)
        {
            output.Append(Alphabet[(buffer << (5 - bitsInBuffer)) & 31]);
        }

        return output.ToString();
    }

    public static string Normalize(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = new StringBuilder(value.Length);

        foreach (var input in value)
        {
            if (input is '-' or ' ' or '\t' or '\r' or '\n')
            {
                continue;
            }

            var character = char.ToUpperInvariant(input) switch
            {
                'O' => '0',
                'I' or 'L' => '1',
                var candidate => candidate
            };

            if (Alphabet.IndexOf(character) < 0)
            {
                throw new FormatException($"'{input}' is not a Crockford Base32 character.");
            }

            normalized.Append(character);
        }

        return normalized.ToString();
    }
}
