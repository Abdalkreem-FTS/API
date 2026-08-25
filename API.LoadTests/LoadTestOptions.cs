using API.Authentication;

namespace API.LoadTests;

public enum LoadMode
{
    /// <summary>All three operations at once, at the ratio a real deployment sees them.</summary>
    Mix,

    /// <summary>One operation at the full rate, which is the only way to get enough samples for a trustworthy p99.</summary>
    Request,

    Login,

    Logout,

    /// <summary>Steps the rate up until the tail latency knees, to find where the thing actually breaks.</summary>
    Ramp,

    /// <summary>Redis unreachable, Redis stalled, Redis out of memory.</summary>
    Failure,
}

public sealed record LoadTestOptions
{
    public LoadMode Mode { get; init; } = LoadMode.Mix;

    public TokenRevocationStrategy Strategy { get; init; } = TokenRevocationStrategy.Denylist;

    /// <summary>
    /// How many independent runs to do. One run cannot tell you whether a difference between the
    /// strategies is real, because it does not measure the noise the difference has to beat.
    /// </summary>
    public int Repeat { get; init; } = 1;

    /// <summary>
    /// How many sessions are active across the whole system. This is the number that decides the
    /// comparison: the allowlist holds a key for every one of them, the denylist holds a key only
    /// for the <see cref="LogoutShare"/> that have logged out.
    /// </summary>
    public int Sessions { get; init; } = 100_000;

    /// <summary>
    /// The fraction of those sessions that logged out while their token still had time left. A
    /// revoked token stops costing the denylist anything once it expires on its own, so this is
    /// what bounds the denylist keyspace.
    /// </summary>
    public double LogoutShare { get; init; } = 0.01;

    /// <summary>How many of the preloaded sessions get a usable token for the load to drive requests with.</summary>
    public int WorkingSet { get; init; } = 2_000;

    public int RequestsPerSecond { get; init; } = 1_000;

    public int LoginsPerSecond { get; init; } = 10;

    public int LogoutsPerSecond { get; init; } = 1;

    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan WarmUp { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How often the keyspace is sampled during a run, which is how TTL churn becomes visible.</summary>
    public TimeSpan SampleInterval { get; init; } = TimeSpan.FromSeconds(2);

    public int RampFrom { get; init; } = 250;

    public int RampTo { get; init; } = 4_000;

    public int RampSteps { get; init; } = 8;

    public TimeSpan RampStep { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Latency injected on every Redis round trip, in milliseconds. 0 disables the toxic.</summary>
    public int RedisLatencyMs { get; init; }

    public int RedisJitterMs { get; init; }

    public string Redis { get; init; } = "localhost:6379";

    public string RedisProxy { get; init; } = "localhost:26379";

    public string Toxiproxy { get; init; } = "http://localhost:8474";

    public string? ApiUrl { get; init; }

    /// <summary>Where to write the machine-readable summary a suite script collects.</summary>
    public string? Output { get; init; }

    public string Username { get; init; } = "alice";

    public string Password { get; init; } = "Password123!";

    // Handed to the API as environment variables and used locally to mint tokens, so the two cannot
    // drift apart. A token minted here has to validate there.
    public string JwtIssuer { get; init; } = "fts-api";

    public string JwtAudience { get; init; } = "fts-api-clients";

    public string JwtSecurityKey { get; init; } = "rPwEKPbG/B/O1WUUNwBODEeLQLslizVJNKMddEASEoFHE8psptdieGzNUELnNc+g";

    /// <summary>
    /// Shorten this to watch the keyspace reach steady state within a run: TTL churn only becomes
    /// visible once the run outlasts the token lifetime, and nobody wants a 45-minute test.
    /// </summary>
    public int TokenLifetimeMinutes { get; init; } = 15;

    public JwtOptions Jwt => new()
    {
        Issuer = JwtIssuer,
        Audience = JwtAudience,
        SecurityKey = JwtSecurityKey,
        ExpiryMinutes = TokenLifetimeMinutes,
    };

    /// <summary>The rate the logout scenario runs at, which is the whole budget when logout is the mode.</summary>
    public int EffectiveLogoutRate => Mode is LoadMode.Logout ? RequestsPerSecond : LogoutsPerSecond;

    /// <summary>
    /// Sized so no invocation ever has to reuse a token. Logging the same token out twice measures
    /// an allowlist DEL against a key that is already gone, which is not a logout.
    /// </summary>
    public int LogoutPoolSize => Mode switch
    {
        // A ramp only drives authenticated requests, so it needs no logout tokens at all. Sizing
        // this off the ramp rate would mint hundreds of thousands of them for nothing.
        LoadMode.Ramp => 0,

        // Four cases, each of which needs its own unused tokens.
        LoadMode.Failure => (int)(Math.Max(1, RequestsPerSecond / 10) * RampStep.TotalSeconds * 6) + 64,
        _ => (int)(EffectiveLogoutRate * (Duration.TotalSeconds + WarmUp.TotalSeconds) * 1.5) + 16,
    };

    public static LoadTestOptions Parse(string[] args)
    {
        var options = new LoadTestOptions();

        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];

            var value = () => i + 1 < args.Length
                ? args[++i]
                : throw new ArgumentException($"'{argument}' needs a value.");

            options = argument switch
            {
                "--mode" => options with { Mode = Enum.Parse<LoadMode>(value(), ignoreCase: true) },
                "--strategy" => options with { Strategy = Enum.Parse<TokenRevocationStrategy>(value(), ignoreCase: true) },
                "--repeat" => options with { Repeat = int.Parse(value()) },
                "--sessions" => options with { Sessions = int.Parse(value()) },
                "--logout-share" => options with { LogoutShare = double.Parse(value()) },
                "--working-set" => options with { WorkingSet = int.Parse(value()) },
                "--token-lifetime" => options with { TokenLifetimeMinutes = int.Parse(value()) },
                "--rps" => options with { RequestsPerSecond = int.Parse(value()) },
                "--login-rps" => options with { LoginsPerSecond = int.Parse(value()) },
                "--logout-rps" => options with { LogoutsPerSecond = int.Parse(value()) },
                "--duration" => options with { Duration = TimeSpan.FromSeconds(int.Parse(value())) },
                "--warmup" => options with { WarmUp = TimeSpan.FromSeconds(int.Parse(value())) },
                "--sample-interval" => options with { SampleInterval = TimeSpan.FromSeconds(int.Parse(value())) },
                "--ramp-from" => options with { RampFrom = int.Parse(value()) },
                "--ramp-to" => options with { RampTo = int.Parse(value()) },
                "--ramp-steps" => options with { RampSteps = int.Parse(value()) },
                "--ramp-step-seconds" => options with { RampStep = TimeSpan.FromSeconds(int.Parse(value())) },
                "--redis-latency" => options with { RedisLatencyMs = int.Parse(value()) },
                "--redis-jitter" => options with { RedisJitterMs = int.Parse(value()) },
                "--redis" => options with { Redis = value() },
                "--redis-proxy" => options with { RedisProxy = value() },
                "--toxiproxy" => options with { Toxiproxy = value() },
                "--api-url" => options with { ApiUrl = value() },
                "--output" => options with { Output = value() },
                "--help" or "-h" => throw new HelpRequestedException(),
                var unknown => throw new ArgumentException($"Unknown argument '{unknown}'."),
            };
        }

        if (options.WorkingSet > options.Sessions)
        {
            throw new ArgumentException("--working-set cannot exceed --sessions.");
        }

        if (options.Repeat < 1)
        {
            throw new ArgumentException("--repeat must be at least 1.");
        }

        return options;
    }

    public const string Usage = """
        Usage: dotnet run -c Release --project API.LoadTests -- [options]

          --mode           mix | request | login | logout | ramp | failure  (default mix)
                             mix      all three operations at the ratio below
                             request  authenticated requests only, at --rps
                             login    logins only, at --rps
                             logout   logouts only, at --rps
                             ramp     step the rate up until the tail knees
                             failure  Redis unreachable, stalled, and out of memory
          --strategy       denylist | allowlist            (default denylist)
          --repeat         independent runs, for variance   (default 1)
          --sessions       active sessions to preload       (default 100000)
          --logout-share   fraction already logged out      (default 0.01)
          --working-set    usable tokens to mint            (default 2000)
          --token-lifetime token lifetime in minutes        (default 15)
          --rps            requests/second                  (default 1000)
          --login-rps      logins/second, mix mode          (default 10)
          --logout-rps     logouts/second, mix mode         (default 1)
          --duration       seconds at load                  (default 60)
          --warmup         seconds of warm-up               (default 10)
          --sample-interval keyspace sampling, seconds      (default 2)
          --ramp-from      first rate in a ramp             (default 250)
          --ramp-to        last rate in a ramp              (default 4000)
          --ramp-steps     steps in a ramp                  (default 8)
          --ramp-step-seconds seconds per ramp step         (default 15)
          --redis-latency  ms injected per round trip       (default 0, needs Toxiproxy)
          --redis-jitter   ms of jitter on that latency     (default 0)
          --redis          direct Redis, for preload/stats  (default localhost:6379)
          --redis-proxy    Redis the API connects through   (default localhost:26379)
          --toxiproxy      Toxiproxy admin API              (default http://localhost:8474)
          --api-url        target a running API instead of launching one
          --output         write a JSON summary here for the suite report
        """;
}

public sealed class HelpRequestedException : Exception;
