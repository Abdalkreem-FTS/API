namespace API.Models;

public record User(Guid Id, string Username, string Password, IReadOnlyList<string> Roles);
