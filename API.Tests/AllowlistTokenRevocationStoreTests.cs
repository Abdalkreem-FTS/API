using API.Authentication;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace API.Tests;

public class AllowlistTokenRevocationStoreTests
{
    [Fact]
    public async Task IsRevokedAsync_IsTrueForATokenThatWasNeverIssued()
    {
        var store = CreateStore();

        Assert.True(await store.IsRevokedAsync("some-jti"));
    }

    [Fact]
    public async Task IssueAsync_MakesTheTokenValid()
    {
        var store = CreateStore();

        await store.IssueAsync("some-jti", DateTimeOffset.UtcNow.AddMinutes(15));

        Assert.False(await store.IsRevokedAsync("some-jti"));
    }

    [Fact]
    public async Task IssueAsync_DoesNotStoreATokenThatHasAlreadyExpired()
    {
        var store = CreateStore();

        await store.IssueAsync("some-jti", DateTimeOffset.UtcNow.AddMinutes(-1));

        Assert.True(await store.IsRevokedAsync("some-jti"));
    }

    [Fact]
    public async Task RevokeAsync_ForgetsALiveToken()
    {
        var store = CreateStore();

        await store.IssueAsync("some-jti", DateTimeOffset.UtcNow.AddMinutes(15));
        await store.RevokeAsync("some-jti", DateTimeOffset.UtcNow.AddMinutes(15));

        Assert.True(await store.IsRevokedAsync("some-jti"));
    }

    [Fact]
    public async Task RevokeAsync_KeepsTokensApart()
    {
        var store = CreateStore();

        await store.IssueAsync("first-jti", DateTimeOffset.UtcNow.AddMinutes(15));
        await store.IssueAsync("second-jti", DateTimeOffset.UtcNow.AddMinutes(15));

        await store.RevokeAsync("first-jti", DateTimeOffset.UtcNow.AddMinutes(15));

        Assert.True(await store.IsRevokedAsync("first-jti"));
        Assert.False(await store.IsRevokedAsync("second-jti"));
    }

    private static AllowlistTokenRevocationStore CreateStore() =>
        new(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));
}
