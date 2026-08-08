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
dotnet test                                  # 29 tests
dotnet test --filter 'Category!=Integration' # 22 tests, no Docker needed
```

Two tiers, split by whether Redis is actually part of what is being tested:

- The fast tier covers everything that never touches revocation — issuing tokens, claims,
  authorising the protected endpoint, health. It swaps Redis for the in-memory distributed
  cache, since those tests only need *a* cache to exist so the host boots.
- The `Integration` tier starts a real Redis with Testcontainers and owns **all** revocation
  testing.

The rule is: if an in-memory pass and a real-Redis failure would both be possible, the test
belongs in the integration tier.

To stop Redis when you are done:

```bash
docker compose down
```

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
mean something concrete: the token's `jti` goes onto a denylist in Redis, and
`OnTokenValidated` rejects anything it finds there.

Nothing ever sweeps that denylist. Each entry is written with a TTL equal to the token's
remaining lifetime plus a minute of slack for clock drift, so Redis evicts it by itself — and
once a token is past its own `exp`, the lifetime check rejects it without help. The store stays
bounded by how many tokens are revoked inside one expiry window rather than growing with every
login.

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
