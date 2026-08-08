using Microsoft.Extensions.Caching.Distributed;

namespace API.Authentication;

public sealed class DistributedCacheTokenRevocationStore(IDistributedCache cache) : ITokenRevocationStore
{
    private static readonly TimeSpan ExpiryBuffer = TimeSpan.FromMinutes(1);

    private static readonly byte[] Revoked = [.. "1"u8];

    public Task RevokeAsync(string tokenId, DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
    {
        var remaining = expiresAt - DateTimeOffset.UtcNow;

        if (remaining <= TimeSpan.Zero)
        {
            return Task.CompletedTask;
        }

        return cache.SetAsync(
            Key(tokenId),
            Revoked,
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = remaining + ExpiryBuffer },
            cancellationToken);
    }

    public async Task<bool> IsRevokedAsync(string tokenId, CancellationToken cancellationToken = default) =>
        await cache.GetAsync(Key(tokenId), cancellationToken) is not null;

    private static string Key(string tokenId) => $"revoked-token:{tokenId}";
}
