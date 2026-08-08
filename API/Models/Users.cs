namespace API.Models;

public static class Users
{
    private static readonly User[] All =
    [
        new(Guid.Parse("8f14e45f-ceea-467a-9575-2c1a1a4a2d1b"), "alice", "Password123!", ["admin"]),
        new(Guid.Parse("c9f0f895-fb98-4e64-a1d9-9b0f0a3d5e77"), "bob", "Password456!", ["user"]),
    ];

    public static User? Find(string username, string password) =>
        All.FirstOrDefault(user =>
            user.Username.Equals(username, StringComparison.OrdinalIgnoreCase)
            && user.Password == password);
}
