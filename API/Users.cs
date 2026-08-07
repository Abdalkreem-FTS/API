namespace API;

public record User(string Username, string Password);

public static class Users
{
    private static readonly User[] All =
    [
        new("alice", "Password123!"),
        new("bob", "Password456!"),
    ];

    public static User? Find(string username, string password) => All.FirstOrDefault(user => user.Username.Equals(username, StringComparison.OrdinalIgnoreCase) && user.Password == password);
}
