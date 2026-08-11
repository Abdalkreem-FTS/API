using API.Authentication;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace API.Tests.Integration;

public sealed class RedisFixture : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder("redis:8-alpine").Build();

    private RedisApiFactory _denylist = null!;

    private RedisApiFactory _allowlist = null!;

    public IConnectionMultiplexer Connection { get; private set; } = null!;

    public RedisApiFactory FactoryFor(TokenRevocationStrategy strategy) =>
        strategy is TokenRevocationStrategy.Allowlist ? _allowlist : _denylist;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        var connectionString = _container.GetConnectionString();

        _denylist = new RedisApiFactory(connectionString, TokenRevocationStrategy.Denylist);
        _allowlist = new RedisApiFactory(connectionString, TokenRevocationStrategy.Allowlist);

        Connection = await ConnectionMultiplexer.ConnectAsync(connectionString);
    }

    public async Task DisposeAsync()
    {
        await _denylist.DisposeAsync();
        await _allowlist.DisposeAsync();
        await Connection.DisposeAsync();
        await _container.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class RedisCollection : ICollectionFixture<RedisFixture>
{
    public const string Name = "redis";
}
