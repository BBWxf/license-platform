using LicensePlatform.Web.Security;
using Xunit;

namespace LicensePlatform.Web.Tests;

public sealed class ClientRequestReplayCacheTests
{
    [Fact]
    public void Nonces_are_scoped_to_the_key_and_reusable_only_at_expiration()
    {
        var cache = new ClientRequestReplayCache();

        Assert.True(cache.TryRegister("key-a", "nonce", 100, 200));
        Assert.False(cache.TryRegister("key-a", "nonce", 199, 299));
        Assert.True(cache.TryRegister("key-b", "nonce", 100, 200));
        Assert.True(cache.TryRegister("key-a", "other-nonce", 100, 200));
        Assert.True(cache.TryRegister("key-a", "nonce", 200, 300));
        Assert.False(cache.TryRegister("key-a", "nonce", 201, 301));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_registration_has_exactly_one_winner_even_when_replacing_an_expired_entry(bool expired)
    {
        var cache = new ClientRequestReplayCache();
        if (expired)
        {
            Assert.True(cache.TryRegister("key", "nonce", 1, 100));
        }

        var attempts = Enumerable.Range(0, 64)
            .Select(_ => Task.Run(() => cache.TryRegister("key", "nonce", 100, 200)));
        var results = await Task.WhenAll(attempts);

        Assert.Single(results, accepted => accepted);
    }

    [Fact]
    public void Capacity_fails_closed_then_recovers_after_expired_entries_are_pruned()
    {
        var cache = new ClientRequestReplayCache();
        for (var index = 0; index < 100_000; index++)
        {
            Assert.True(cache.TryRegister("key", index.ToString(System.Globalization.CultureInfo.InvariantCulture), 100, 200));
        }

        Assert.False(cache.TryRegister("key", "new-nonce", 199, 299));
        Assert.True(cache.TryRegister("key", "new-nonce", 200, 300));
        Assert.False(cache.TryRegister("key", "new-nonce", 200, 300));
    }
}
