using StackExchange.Redis;
using Testcontainers.Redis;

namespace API.Tests.Integration;

public sealed class RedisFixture : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder("redis:8-alpine").Build();

    public RedisApiFactory Factory { get; private set; } = null!;

    public IConnectionMultiplexer Connection { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        var connectionString = _container.GetConnectionString();

        Factory = new RedisApiFactory(connectionString);
        Connection = await ConnectionMultiplexer.ConnectAsync(connectionString);
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await Connection.DisposeAsync();
        await _container.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class RedisCollection : ICollectionFixture<RedisFixture>
{
    public const string Name = "redis";
}
