namespace API.Contracts;

public record TokenResponse(string AccessToken, string TokenType, DateTime ExpiresAt);
