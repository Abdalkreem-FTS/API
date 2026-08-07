# Minimal ASP.NET Web API with JWT Authentication

A small .NET 10 minimal API showing how JWT authentication works: log in with a username and
password, get a token back, and use that token to reach a protected endpoint.

## Running it

```bash
cd API
dotnet run
```

The API listens on <http://localhost:5244>.

```bash
cd API.Tests
dotnet test        # 15 tests
```

### Demo accounts

| Username | Password       |
| -------- | -------------- |
| `alice`  | `Password123!` |
| `bob`    | `Password456!` |

## Endpoints

| Method | Route              | Auth        | Purpose                                    |
| ------ | ------------------ | ----------- | ------------------------------------------ |
| GET    | `/`                | anonymous   | Welcome message                            |
| POST   | `/auth/login`      | anonymous   | Username + password → token                |
| POST   | `/auth/validate`   | anonymous   | Checks a token and says why it is invalid   |
| GET    | `/weatherforecast` | `[Authorize]` | The protected endpoint                   |
