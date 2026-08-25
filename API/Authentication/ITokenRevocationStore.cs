namespace API.Authentication;

public interface ITokenRevocationStore
{
    Task IssueAsync(string tokenId, DateTimeOffset expiresAt, CancellationToken cancellationToken = default);

    Task RevokeAsync(string tokenId, DateTimeOffset expiresAt, CancellationToken cancellationToken = default);

    Task<bool> IsRevokedAsync(string tokenId, CancellationToken cancellationToken = default);
}
