using System.Collections.Concurrent;

namespace API.LoadTests;

/// <summary>
/// Tokens the scenarios draw on while the load is running.
/// <para>
/// Refreshes itself, because a run longer than the token lifetime outlives the tokens it started
/// with. Without this, a soak test measures nothing but expired-token rejections from the moment the
/// working set ages out.
/// </para>
/// </summary>
public sealed class TokenPool(string[] workingSet, IEnumerable<string> logoutQueue)
{
    private volatile string[] _workingSet = workingSet;

    private readonly ConcurrentQueue<string> _logout = new(logoutQueue);

    /// <summary>Tokens that stay logged in, drawn at random by the authenticated-request scenario.</summary>
    public string[] WorkingSet => _workingSet;

    public int LogoutRemaining => _logout.Count;

    /// <summary>
    /// Takes a token nothing else has claimed. Logging the same token out twice would measure an
    /// allowlist DEL against a key that is already gone, which is not a logout.
    /// </summary>
    public string? NextForLogout() => _logout.TryDequeue(out var token) ? token : null;

    /// <summary>
    /// Replaces the working set and the logout queue, on a period shorter than the token lifetime.
    /// <para>
    /// The queue is drained rather than topped up. Topping up by depth leaves a full queue of tokens
    /// that have since expired, and every logout drawn from it is rejected for expiry instead of
    /// measuring a logout.
    /// </para>
    /// </summary>
    /// <param name="logoutsPerWindow">Unused tokens to leave queued for the next window.</param>
    public async Task RefreshAsync(
        Func<int, Task<string[]>> mint,
        TimeSpan every,
        int logoutsPerWindow,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(every, cancellationToken);

                _workingSet = await mint(_workingSet.Length);

                var fresh = await mint(logoutsPerWindow);

                _logout.Clear();

                foreach (var token in fresh)
                {
                    _logout.Enqueue(token);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"  token refresh failed: {exception.Message}");
            }
        }
    }
}
