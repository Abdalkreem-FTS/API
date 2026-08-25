using API.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace API.Tests.Integration;

public sealed class RedisApiFactory(string redisConnectionString, TokenRevocationStrategy strategy) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder
        .UseSetting("ConnectionStrings:Redis", redisConnectionString)
        .UseSetting("TokenRevocation:Strategy", strategy.ToString());
}
