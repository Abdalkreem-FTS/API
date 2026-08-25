using API.Contracts;
using NBomber.Contracts;
using NBomber.CSharp;
using NBomber.Http.CSharp;

namespace API.LoadTests;

/// <summary>
/// The three things a session does. Running them together at production ratios answers "what does
/// the system cost"; running one at full rate answers "what does this operation cost, with enough
/// samples for the tail to mean anything". Those are different questions and need different runs.
/// </summary>
public static class Scenarios
{
    public const string AuthenticatedRequest = "authenticated_request";

    public const string Login = "login";

    public const string Logout = "logout";

    private const int FailureBudget = 1_000_000;

    /// <summary>
    /// The failure cases establish what a strategy does when Redis is gone, which is a question about
    /// behaviour and not about throughput. Driving them at full rate buys no extra information and
    /// makes the generator hold data for hundreds of thousands of failed requests.
    /// </summary>
    private const int FailureModeMaxRate = 200;

    /// <summary>
    /// One client for the whole process. A ramp calls <see cref="Create"/> once per step, and a fresh
    /// client per step means a fresh connection pool per step: the old ones keep their sockets, and by
    /// the last step the generator runs out of connections and reports timeouts that belong to it
    /// rather than to the API.
    /// </summary>
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        // Well above any rate here, so the pool is never the thing that limits throughput.
        MaxConnectionsPerServer = 4_096,

        // Long enough that connections are reused across every step of a ramp.
        PooledConnectionLifetime = TimeSpan.FromMinutes(30),
    })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    public static ScenarioProps[] Create(
        string apiUrl,
        LoadTestOptions options,
        TokenPool pool,
        int? rateOverride = null,
        TimeSpan? durationOverride = null)
    {
        var client = Client;

        var forecast = $"{apiUrl}/api/weatherforecast";
        var tokens = $"{apiUrl}/api/tokens";

        var duration = durationOverride ?? options.Duration;

        ScenarioProps Request(int rate) => Scenario
            .Create(AuthenticatedRequest, async _ =>
            {
                var token = pool.WorkingSet[Random.Shared.Next(pool.WorkingSet.Length)];

                return await Http.Send(client, Http
                    .CreateRequest("GET", forecast)
                    .WithHeader("Authorization", $"Bearer {token}"));
            })
            .WithLoad(rate, duration);

        ScenarioProps LoginScenario(int rate) => Scenario
            .Create(Login, async _ => await Http.Send(client, Http
                .CreateRequest("POST", tokens)
                .WithJsonBody(new LoginRequest(options.Username, options.Password))))
            .WithLoad(rate, duration);

        ScenarioProps LogoutScenario(int rate) => Scenario
            .Create(Logout, async _ =>
            {
                var token = pool.NextForLogout();

                if (token is null)
                {
                    // The pool is sized off the rate and duration, so this only trips if a run was
                    // stretched. Failing loudly beats logging one token out twice and calling the
                    // resulting DEL-on-a-missing-key a logout.
                    return Response.Fail(statusCode: "pool-exhausted", message: "No unused token left to log out.");
                }

                return await Http.Send(client, Http
                    .CreateRequest("DELETE", tokens)
                    .WithHeader("Authorization", $"Bearer {token}"));
            })
            .WithLoad(rate, duration);

        var rate = options.Mode is LoadMode.Failure
            ? Math.Min(rateOverride ?? options.RequestsPerSecond, FailureModeMaxRate)
            : rateOverride ?? options.RequestsPerSecond;

        return options.Mode switch
        {
            LoadMode.Request or LoadMode.Ramp => [Request(rate)],
            LoadMode.Login => [LoginScenario(rate)],
            LoadMode.Logout => [LogoutScenario(rate)],

            // Failure mode drives all three. Logins alone would be misleading: the denylist does not
            // write on login, so a read-plus-login mix would never touch its write path at all and
            // would make it look immune to a Redis that has stopped accepting writes.
            LoadMode.Failure =>
            [
                Request(rate),
                LoginScenario(Math.Max(1, rate / 10)),
                LogoutScenario(Math.Max(1, rate / 10)),
            ],

            _ =>
            [
                Request(options.RequestsPerSecond),
                LoginScenario(options.LoginsPerSecond),
                LogoutScenario(options.LogoutsPerSecond),
            ],
        };
    }

    /// <summary>
    /// What the run asked this scenario to do, so the report can say whether the generator kept up.
    /// </summary>
    public static int RequestedRate(LoadTestOptions options, string scenario, int? rateOverride)
    {
        var rate = options.Mode is LoadMode.Failure
            ? Math.Min(rateOverride ?? options.RequestsPerSecond, FailureModeMaxRate)
            : rateOverride ?? options.RequestsPerSecond;

        if (options.Mode is LoadMode.Mix)
        {
            return scenario switch
            {
                Login => options.LoginsPerSecond,
                Logout => options.LogoutsPerSecond,
                _ => options.RequestsPerSecond,
            };
        }

        return options.Mode is LoadMode.Failure && scenario is Login or Logout ? Math.Max(1, rate / 10) : rate;
    }

    private static ScenarioProps WithLoad(this ScenarioProps scenario, int ratePerSecond, TimeSpan duration)
    {
        // NBomber's own warm-up phase would land in the server-side histograms, which are reset
        // from outside and cannot be reset between its phases. The runner does the warm-up as a
        // separate discarded session instead.
        scenario = scenario.WithoutWarmUp();

        // NBomber stops the whole session once total failures pass this, which silently truncates a
        // run rather than reporting it. The failure cases are supposed to fail, so the default of
        // 5000 is far too low. Not unlimited though: NBomber retains data per failed request, and
        // removing the bound entirely is enough to exhaust this process.
        scenario = scenario.WithMaxFailCount(FailureBudget);

        // Injected at a fixed rate rather than by a fixed number of virtual users: an open-loop
        // arrival process is what shows queueing and tail latency, and a closed loop hides both by
        // slowing its own request rate down whenever the server slows down.
        return scenario.WithLoadSimulations(Simulation.Inject(
            rate: ratePerSecond,
            interval: TimeSpan.FromSeconds(1),
            during: duration));
    }
}
