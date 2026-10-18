using LicensePlatform.Web.Security;
using Xunit;

namespace LicensePlatform.Web.Tests;

public sealed class AdminPasswordHasherTests
{
    private static readonly string Salt = Convert.ToBase64String(new byte[16]);
    private static readonly string Digest = Convert.ToBase64String(new byte[32]);
    private readonly AdminPasswordHasher _hasher = new();

    [Fact]
    public async Task Hashes_are_salted_and_verify_only_the_original_unicode_password()
    {
        const string password = "Regression-only-密码-passphrase-123!";

        var first = await _hasher.HashAsync(password);
        var second = await _hasher.HashAsync(password);

        Assert.StartsWith("$argon2id$v=19$m=65536,t=3,p=2$", first);
        Assert.NotEqual(first, second);
        Assert.True(await _hasher.VerifyAsync(first, password));
        Assert.False(await _hasher.VerifyAsync(first, password + "x"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("              ")]
    [InlineData("too-short")]
    public async Task Hash_rejects_missing_blank_or_short_passwords(string? password)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _hasher.HashAsync(password!));
    }

    [Fact]
    public async Task Hash_rejects_passwords_over_the_maximum_length()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _hasher.HashAsync(new string('x', 513)));
    }

    [Theory]
    [InlineData(8192, 1, 1)]
    [InlineData(65536, 3, 2)]
    [InlineData(262144, 10, 8)]
    public async Task Accepted_parameter_bounds_observe_cancellation_before_derivation(int memory, int iterations, int lanes)
    {
        var encoded = Encode($"m={memory},t={iterations},p={lanes}");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _hasher.VerifyAsync(encoded, "regression-password", new CancellationToken(canceled: true)));
    }

    public static TheoryData<string> InvalidParameters => new()
    {
        "m=8191,t=3,p=2",
        "m=262145,t=3,p=2",
        "m=-65536,t=3,p=2",
        "m=2147483648,t=3,p=2",
        "m=65536,t=0,p=2",
        "m=65536,t=11,p=2",
        "m=65536,t=-1,p=2",
        "m=65536,t=3,p=0",
        "m=65536,t=3,p=9",
        "m=65536,t=3,p=-1",
        "m=not-a-number,t=3,p=2",
        "m=,t=3,p=2",
        "m=65536,t=3",
        "m=65536,t=3,p=2,extra=1",
        "m=65536,t=3,t=2",
        "t=3,m=65536,p=2",
        "M=65536,t=3,p=2",
        "m=+65536,t=3,p=2",
        "m= 65536,t=3,p=2",
        "m=65536,t=3 ,p=2"
    };

    [Theory]
    [MemberData(nameof(InvalidParameters))]
    public async Task Malformed_or_excessive_parameters_fail_without_starting_argon2(string parameters)
    {
        // Cancellation would throw if the parser allowed derivation, even for a bounded value.
        Assert.False(await _hasher.VerifyAsync(
            Encode(parameters), "regression-password", new CancellationToken(canceled: true)));
    }

    public static TheoryData<string?> InvalidEncodings => new()
    {
        null,
        "",
        " ",
        "not-an-argon2-hash",
        $"$argon2i$v=19$m=65536,t=3,p=2${Salt}${Digest}",
        $"$argon2id$v=16$m=65536,t=3,p=2${Salt}${Digest}",
        $"argon2id$v=19$m=65536,t=3,p=2${Salt}${Digest}",
        $"$$argon2id$v=19$m=65536,t=3,p=2${Salt}${Digest}",
        $"$argon2id$v=19$m=65536,t=3,p=2$${Salt}${Digest}",
        $"$argon2id$v=19$m=65536,t=3,p=2${Salt}${Digest}$",
        $"$argon2id$v=19$m=65536,t=3,p=2$not-base64!${Digest}",
        $"$argon2id$v=19$m=65536,t=3,p=2${Salt}$not-base64!",
        $"$argon2id$v=19$m=65536,t=3,p=2${Convert.ToBase64String(new byte[15])}${Digest}",
        $"$argon2id$v=19$m=65536,t=3,p=2${Convert.ToBase64String(new byte[65])}${Digest}",
        $"$argon2id$v=19$m=65536,t=3,p=2${Salt}${Convert.ToBase64String(new byte[31])}",
        $"$argon2id$v=19$m=65536,t=3,p=2${Salt}${Convert.ToBase64String(new byte[33])}"
    };

    [Theory]
    [MemberData(nameof(InvalidEncodings))]
    public async Task Malformed_hash_encodings_fail_closed_without_derivation(string? encoded)
    {
        Assert.False(await _hasher.VerifyAsync(
            encoded!, "regression-password", new CancellationToken(canceled: true)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Verify_rejects_missing_password_without_derivation(string? password)
    {
        Assert.False(await _hasher.VerifyAsync(
            Encode("m=65536,t=3,p=2"), password!, new CancellationToken(canceled: true)));
    }

    private static string Encode(string parameters) => $"$argon2id$v=19${parameters}${Salt}${Digest}";
}
