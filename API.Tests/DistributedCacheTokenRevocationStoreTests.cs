using API.Authentication;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace API.Tests;

public class DistributedCacheTokenRevocationStoreTests
{
    [Fact]
    public async Task IsRevokedAsync_IsFalseForATokenNobodyRevoked()
    {
        var store = CreateStore();

        Assert.False(await store.IsRevokedAsync("some-jti"));
    }

    [Fact]
    public async Task RevokeAsync_RemembersALiveToken()
    {
        var store = CreateStore();

        await store.RevokeAsync("some-jti", DateTimeOffset.UtcNow.AddMinutes(15));

        Assert.True(await store.IsRevokedAsync("some-jti"));
    }

    [Fact]
    public async Task RevokeAsync_DoesNotStoreATokenThatHasAlreadyExpired()
    {
        var store = CreateStore();

        await store.RevokeAsync("some-jti", DateTimeOffset.UtcNow.AddMinutes(-1));

        Assert.False(await store.IsRevokedAsync("some-jti"));
    }

    [Fact]
    public async Task RevokeAsync_KeepsTokensApart()
    {
        var store = CreateStore();

        await store.RevokeAsync("first-jti", DateTimeOffset.UtcNow.AddMinutes(15));

        Assert.True(await store.IsRevokedAsync("first-jti"));
        Assert.False(await store.IsRevokedAsync("second-jti"));
    }

    private static DistributedCacheTokenRevocationStore CreateStore() =>
        new(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));
}
