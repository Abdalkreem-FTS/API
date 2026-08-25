using API.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace API.Tests;

public sealed class TestApiFactory : WebApplicationFactory<Program>
{
    private StubTokenRevocationStore RevocationStore { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureServices(services => services.AddSingleton<ITokenRevocationStore>(RevocationStore));
}
