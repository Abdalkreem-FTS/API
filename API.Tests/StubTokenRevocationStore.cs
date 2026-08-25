using API.Authentication;

namespace API.Tests;

public sealed class StubTokenRevocationStore : ITokenRevocationStore
{
    private readonly Lock _gate = new();

    private readonly HashSet<string> _revoked = [];

    private readonly List<Revocation> _revocations = [];

    public IReadOnlyList<Revocation> Revocations
    {
        get
        {
            lock (_gate)
            {
                return [.. _revocations];
            }
        }
    }

    public Task RevokeAsync(string tokenId, DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _revocations.Add(new Revocation(tokenId, expiresAt));
            _revoked.Add(tokenId);
        }

        return Task.CompletedTask;
    }

    public Task<bool> IsRevokedAsync(string tokenId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_revoked.Contains(tokenId));
        }
    }

    public readonly record struct Revocation(string TokenId, DateTimeOffset ExpiresAt);
}
