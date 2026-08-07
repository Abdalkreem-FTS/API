namespace API;

public record LoginRequest(string Username, string Password);

public record TokenResponse(string AccessToken, string TokenType, DateTime ExpiresAt);

public record ValidateRequest(string Token);

public record TokenValidation(bool IsValid, string? Error);

public record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
