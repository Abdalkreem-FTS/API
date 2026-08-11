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

## Benchmarks

[BenchmarkDotNet](https://github.com/dotnet/BenchmarkDotNet) measures the two choices that could
have gone either way. Redis has to be up for the first one:

```bash
docker compose up -d --wait
dotnet run -c Release --project API.Benchmarks -- --filter '*'
```

`-c Release` is not optional — BenchmarkDotNet refuses to measure a debug build. Narrow with
`--filter '*TokenRevocation*'` or `--filter '*Lifetime*'`, add `--job short` for a rough answer,
and read the reports in `BenchmarkDotNet.Artifacts/results/`.

Numbers are from a Core i7-13700H on .NET 10, against Redis in Docker on the same machine.

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
