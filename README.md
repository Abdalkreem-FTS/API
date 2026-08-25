# Minimal ASP.NET Web API with JWT Authentication

A small .NET 10 minimal API showing how JWT authentication works: post your credentials, get a
token back, and use that token to reach a protected endpoint.

## Running it

Token revocation is backed by Redis, so start that first:

```bash
docker compose up -d --wait
```

```bash
dotnet run --project API
```

The API listens on <http://localhost:5244>.

## Tests

```bash
dotnet test                                  # 46 tests
dotnet test --filter 'Category!=Integration' # 29 tests, no Docker needed
```

Two tiers, split by whether Redis is actually part of what is being tested:

- The fast tier covers everything that never touches revocation — issuing tokens, claims,
  authorising the protected endpoint, health. It swaps Redis for the in-memory distributed
  cache, since those tests only need *a* cache to exist so the host boots.
- The `Integration` tier starts a real Redis with Testcontainers and owns **all** revocation
  testing. It runs the logout behaviour against both strategies and each strategy's key
  handling against its own.

The rule is: if an in-memory pass and a real-Redis failure would both be possible, the test
belongs in the integration tier.

To stop Redis when you are done:

```bash
docker compose down
```

## Running everything

### What you need

- .NET 10 SDK, Docker (with `docker compose`), and Python 3 for the report generator.
- Ports 6379, 8474 and 26379 free for Redis and Toxiproxy. The API takes an ephemeral port of its
  own per run, so nothing needs to be reserved for it.
- About 2 GB of free memory for the load generator at the default rates, and a machine you are not
  using for anything else while it runs.

Nothing needs starting by hand — the script brings up its own dependencies.

### One command

```bash
./scripts/run-suite.sh            # standard profile, about 35 minutes
./scripts/run-suite.sh quick      # smoke test, about 6 minutes
./scripts/run-suite.sh full       # more repeats, longer soak, higher ramp, about 90 minutes
```

Sixteen steps: both BenchmarkDotNet suites, then for each strategy the production mix, each
operation on its own, a saturation ramp, the failure cases and a soak. It brings up Redis and
Toxiproxy, flushes the keyspace, builds, and prints a step counter and progress bar as it goes;
NBomber draws its own live bar inside each run, and preloading a large session count has one too.

Start with `quick` on a machine you have not run this on before. It exercises every step and takes
six minutes, so a missing dependency or a busy port surfaces immediately rather than twenty minutes
in. Its numbers are not quotable — the run-to-run spread it reports for p99 is around 80%, which the
report states plainly — but its job is proving the harness works.

### What you get

Everything lands in `LoadTestReports/<profile>-<timestamp>/`:

```
REPORT.md              the comparison, assembled from every run
<strategy>-<mode>.json machine-readable results per run
logs/                  full console output per run
benchmarkdotnet/       BenchmarkDotNet's own reports
<strategy>-<mode>-N/   NBomber's own txt/md/csv reports
```

Rebuild `REPORT.md` from a finished directory without re-running anything — useful after changing
the report generator:

```bash
python3 scripts/build-report.py LoadTestReports/standard-20260812-133338 > report.md
```

### Reading it without being misled

The report is written to be read by someone who was not there for the run, so it states its own
limits. Three habits worth keeping:

- **Check the noise floor before believing a gap.** Every latency table prints the run-to-run spread
  beside the difference between strategies and labels it `real` or `indistinguishable`. A 5% gap
  under a 40% spread is not a finding.
- **Check the achieved rate.** Any scenario that came in under the rate it asked for is flagged, and
  its latency belongs to the load generator rather than to the API.
- **Check the keyspace arithmetic.** Under the allowlist the keyspace must gain exactly one key per
  login and lose one per logout. If it does not, something is wired wrong and no latency number from
  that run means anything. This is the cheapest guard there is against a run that quietly measures
  the wrong thing, and it has caught exactly that more than once.

### When something looks wrong

- **A run reports the wrong strategy and stops.** Working as intended: each run asserts through
  `/diagnostics/info` that the API it is talking to reports the strategy and token lifetime it asked
  for. The message names the process holding the port.
- **`--redis-latency` refuses to run.** Toxiproxy is not up. `docker compose up -d --wait`.
- **Redis refuses writes on a later run.** A previous failure-mode run left `maxmemory` set. The
  suite resets it, but a run killed midway may not have. `docker compose exec redis redis-cli config
  set maxmemory 0`.
- **A step fails.** The suite carries on and prints `!!` with the log path; the report is built from
  whatever succeeded. Re-run just that step with the same arguments the script used and point
  `--output` at the same directory, then rebuild the report.

The sections below cover running each piece on its own.

## Benchmarks

[BenchmarkDotNet](https://github.com/dotnet/BenchmarkDotNet) measures the two choices that could
have gone either way. Redis has to be up for the first one:

```bash
docker compose up -d --wait
docker compose exec redis redis-cli flushall
dotnet run -c Release --project API.Benchmarks -- --filter '*'
```

`-c Release` is not optional — BenchmarkDotNet refuses to measure a debug build. The flush is not
required either, but the benchmarks leave a key per issued token behind with an hour on its TTL,
so runs stack up in Redis memory until you clear them.

Narrow with `--filter '*TokenRevocationBenchmarks*'` (one token at a time),
`--filter '*ConcurrentTokenRevocation*'` (a hundred in flight) or `--filter '*Lifetime*'`, and read
the reports in `BenchmarkDotNet.Artifacts/results/`.

Numbers are from a Core i7-13700H on .NET 10, against Redis in Docker on the same machine.

### Reading the revocation results

Both strategies make exactly **one** Redis round trip per authenticated request, so the
`CheckOnEveryRequest` rows come out close to identical and the loopback round trip dominates both.
Latency is not the axis that separates them. What the numbers do show is login: the denylist's
`IssueAsync` is a no-op that never touches Redis, so its `Login` row measures a returned
`Task.CompletedTask` and nothing else. Read that as *zero round trips*, not as a speedup ratio —
the ratio is an artifact of comparing a method call against a network hop.

Two things the benchmarks deliberately do, both of which an earlier version got wrong:

- **Every measured operation gets its own token id, freshly issued in `[IterationSetup]`.** Revoking
  one id repeatedly would have the allowlist `DEL` a key that the previous invocation already
  removed — the cheapest command Redis has, and a state that never occurs in production — while the
  denylist kept paying for a real `SET`. That made the allowlist logout look free.
- **The invocation count is pinned** (`[InvocationCount]` with an unroll factor of 1) so
  `[IterationSetup]` knows exactly how many tokens to issue up front. Do not override it with a
  custom job that raises it, or the token pool runs dry mid-iteration.

And one thing they cannot show: `[MemoryDiagnoser]`'s `Allocated` column is **managed heap in the
benchmark process**, not Redis memory. The allowlist's real cost is one live key per *active token*
for that token's whole lifetime, against the denylist's one key per *logout* — a difference in
Redis footprint that no column here reports. That is what the load tests below are for.

## Load tests

BenchmarkDotNet is closed-loop: it runs one operation at a time, as fast as it can, against a
keyspace that starts empty. Production is open-loop, runs a *mix* of operations, and reaches a
steady state where the two strategies hold wildly different numbers of keys. `API.LoadTests` covers
that gap, using [NBomber](https://nbomber.com) over real HTTP against a real Kestrel process.

```bash
docker compose up -d --wait
dotnet run -c Release --project API.LoadTests -- --help
```

The run launches the API itself with the matching `TokenRevocation:Strategy`, so the two cannot
disagree about what is being measured; pass `--api-url` to target an instance you started yourself.
Reports land in `LoadTestReports/`.

What it does differently from the benchmarks:

- **Reaches steady state first.** `--sessions` sets how many sessions are active system-wide, and
  `--logout-share` how many of them ended early. The preloader drives the real store
  implementations, so the allowlist ends up with a key per active token and the denylist with a key
  only per logout — then reports `DBSIZE`, `used_memory`, per-key `MEMORY USAGE` and the
  fragmentation ratio either side of the run. This is the measurement that decides the design, and
  no latency column can substitute for it.
- **Breaks a request into stages.** With `Diagnostics:Enabled` the API records signature validation,
  the store call and the rest of the pipeline separately, and the run prints each one's share of the
  mean. This is what turns "2.5 ms per request" into "the Redis check is 95% of it".
- **Injects real network latency.** `--redis-latency` and `--redis-jitter` drive a Toxiproxy toxic
  in front of Redis, turning a sub-millisecond loopback hop into the 0.5–2 ms a managed instance one
  availability zone away costs.
- **Reports tail latency at a fixed arrival rate.** Requests are injected open-loop, so a slow server
  produces a queue and a visible p99 instead of quietly lowering its own request rate. Every scenario
  reports achieved rate against requested, and flags itself when it falls short — below the requested
  rate, the latency being reported is partly the generator's own queue and describes nothing under
  test.

### The modes answer different questions

`--mode mix` runs all three operations at `--rps` : `--login-rps` : `--logout-rps` (1000 : 10 : 1 by
default) and answers *what does the system cost*. It is the only honest way to weigh login, which is
the one operation where the strategies differ in round trips — at that ratio the denylist's free
login applies to 1% of traffic.

`--mode request | login | logout` runs one operation at the full `--rps`. Necessary because the mix
starves the rare operations of samples: at one logout per second a 60-second run yields 60 of them,
and a p99 over 60 samples is a single request. Percentiles come from these runs; ratios come from the
mix.

`--repeat N` does N independent runs and prints median, min, max and spread. A single run cannot tell
you whether a gap between the strategies is real, because it never measures the noise the gap has to
beat.

`--mode ramp` steps from `--ramp-from` to `--ramp-to` and prints the rate at which the tail knees,
which is also where you find out whether the single StackExchange.Redis multiplexer is the limit.

`--mode failure` is the one that decides things. See below.

### Watching steady state actually happen

The preloader stamps a keyspace into place; it does not prove the keyspace would *stay* there. TTL
churn only shows up once a run outlasts the token lifetime, so shorten the lifetime rather than
running for an hour:

```bash
dotnet run -c Release --project API.LoadTests -- \
  --mode mix --strategy allowlist --token-lifetime 1 --duration 240 --sample-interval 5
```

The keyspace is sampled throughout and reported as a range, so a run that never reached steady state
is distinguishable from one that did.

### Failure modes

```bash
dotnet run -c Release --project API.LoadTests -- --mode failure --strategy denylist
dotnet run -c Release --project API.LoadTests -- --mode failure --strategy allowlist
```

Four cases, driving reads *and* both write paths: healthy, Redis unreachable, Redis accepting
connections but never answering, and Redis at `maxmemory` under `noeviction`. All three operations
run in every case — a read-plus-login mix would never touch the denylist's write path and would make
it look immune to a Redis that has stopped accepting writes.

The first thing this establishes is that **neither strategy fails open or fails closed**. Redis
exceptions propagate out of `IsRevokedAsync`, past the bearer events, to the top of the pipeline, so
an unreachable or stalled Redis produces `500` on every authenticated request under both strategies.
Redis availability is currently a hard requirement for serving any authenticated request, and that is
a property of the code rather than of either strategy — no `catch` in either store decides what an
unanswerable revocation question should mean.

Where they do differ is what survives:

| | Denylist | Allowlist |
| --- | --- | --- |
| Redis unreachable / stalled — read path | `500` | `500` |
| Redis unreachable / stalled — login | works, never writes | `500` |
| Redis unreachable / stalled — logout | `500` | `500` |
| At `maxmemory` — read path | works | works |
| At `maxmemory` — login | works, never writes | mostly `500` |
| At `maxmemory` — logout | `500` | works, `DEL` frees memory |

Under memory exhaustion the two break in opposite directions, and the direction is the argument. The
denylist keeps letting people in but can no longer record a logout, so a revoked token stays valid
until it expires — a security failure. The allowlist keeps honouring revocations but can no longer
issue a token, so nobody new can log in — an availability failure. The allowlist also partly relieves
its own pressure, because every logout is a `DEL`, which is why some logins get through.

Reach `maxmemory` deliberately with a low cap:

```bash
REDIS_MAXMEMORY=64mb docker compose up -d --wait
```

Redis also runs with the append-only log on, because a real deployment would and every write has to
pay for it. There is still no volume mounted, so the log dies with the container.

## Configuration

The `Jwt` section is bound through the options pattern and validated with
`ValidateOnStart()`, so a missing or malformed key fails the app at startup rather than on the
first request.

| Key                     | Notes                                       |
| ----------------------- | ------------------------------------------- |
| `Jwt:Issuer`            | required                                    |
| `Jwt:Audience`          | required                                    |
| `Jwt:SecurityKey`       | required, at least 32 characters for HS256  |
| `Jwt:ExpiryMinutes`     | 1–1440, defaults to 15                      |
| `ConnectionStrings:Redis` | required — backs the revocation list      |
| `TokenRevocation:Strategy` | `Denylist` or `Allowlist`, defaults to `Denylist` |

A missing Redis connection string fails startup rather than falling back to an in-process
store, because a silent fallback would quietly stop honouring logouts.

Options are injected as `IOptionsMonitor<JwtOptions>` rather than a startup snapshot, so the
signing key can be rotated without restarting the app.

The development key lives in `appsettings.Development.json`.

### Demo accounts

| Username | Password       | Roles   |
| -------- | -------------- | ------- |
| `alice`  | `Password123!` | `admin` |
| `bob`    | `Password456!` | `user`  |

## Endpoints

| Method | Route                  | Auth          | Purpose                     |
| ------ | ---------------------- | ------------- | --------------------------- |
| GET    | `/health`              | anonymous     | Liveness check              |
| POST   | `/api/tokens`          | anonymous     | Credentials → access token  |
| DELETE | `/api/tokens`          | `[Authorize]` | Log out — revokes the token |
| GET    | `/api/weatherforecast` | `[Authorize]` | The protected endpoint      |

Creating a token is a `POST` to the token collection rather than a `/login` verb, and logging
out is a `DELETE` rather than a `/logout` verb, so routes name resources and the HTTP method
supplies the action. `DELETE /api/tokens` revokes whichever token the caller presented in the
`Authorization` header, so nobody can revoke a token they are not holding.

## Logging out

A signed JWT stays valid until its `exp` no matter what the server thinks, so logout has to
mean something concrete. Both options here keep `jti` values in Redis and have
`OnTokenValidated` consult them on every request. What they disagree about is which tokens go
on the list.

|                            | Denylist (blacklist)                   | Allowlist (whitelist)   |
| -------------------------- | -------------------------------------- | ----------------------- |
| Holds                      | the tokens that logged out             | the tokens still logged in |
| Login                      | writes nothing                         | writes the `jti`        |
| Logout                     | writes the `jti`                       | deletes the `jti`       |
| A request is rejected when | the `jti` is **there**                 | the `jti` is **missing** |
| Keys held                  | one per logout inside an expiry window | one per live session    |
| If Redis loses the data    | logged-out tokens work again           | everybody is logged out |

Neither list is ever swept. Both get a TTL of the token's remaining lifetime plus a minute of
slack for clock drift — the denylist so Redis drops the entry once it stops mattering, the
allowlist because the entry has to outlive the token it stands for, or a token still inside its
own lifetime starts reading as revoked.

The last row is the real decision. The denylist fails open: flush Redis and every logged-out
token works again until it expires. The allowlist fails closed: flush Redis and everyone logs in
again, which is an outage rather than a security hole. The allowlist is also the only one that
can say how many sessions a user has open, or end all of them at once.

Pick one with `TokenRevocation:Strategy`, which defaults to `Denylist`:

```bash
dotnet run --project API -- --TokenRevocation:Strategy=Allowlist
```

Switching an already-running deployment to `Allowlist` logs everyone out: tokens issued before
the switch have no entry, and no entry means revoked.

## Tokens

Each token carries:

| Claim  | Purpose                                                      |
| ------ | ------------------------------------------------------------ |
| `sub`  | Stable user id — usernames can change, ids should not         |
| `jti`  | Unique id for this token, which a revocation list keys on     |
| `name` | Display name, surfaced as `User.Identity.Name`                |
| `role` | Drives `[Authorize(Roles = "...")]`                           |
| `iat`  | Issued at, so tokens older than a password change can be cut  |
| `nbf`  | Not before                                                    |
| `exp`  | Expiry                                                        |

Tokens are issued and validated with `JsonWebTokenHandler`, the same handler the JWT bearer
middleware uses.

## Failed authentication

`JwtBearerEvents` replaces the framework's empty `401` with an `application/problem+json` body
and logs the reason the token was rejected. An expired token additionally comes back with an
`x-token-expired` header, so a client can tell "refresh me" apart from "your token is broken".
