using API.Authentication;
using API.Contracts;
using API.Models;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace API.Benchmarks;

[MemoryDiagnoser]
[ThreadingDiagnoser]
public class JwtTokenGeneratorLifetimeBenchmarks
{
    private const int Concurrency = 100;

    private static readonly User Alice = new(
        Guid.Parse("8f14e45f-ceea-467a-9575-2c1a1a4a2d1b"),
        "alice",
        "Password123!",
        ["admin"]);

    [Params(ServiceLifetime.Singleton, ServiceLifetime.Scoped)]
    public ServiceLifetime Lifetime { get; set; }

    private ServiceProvider _provider = null!;

    [GlobalSetup]
    public void Setup()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Issuer"] = "fts-api",
                ["Jwt:Audience"] = "fts-api-clients",
                ["Jwt:SecurityKey"] = "rPwEKPbG/B/O1WUUNwBODEeLQLslizVJNKMddEASEoFHE8psptdieGzNUELnNc+g",
                ["Jwt:ExpiryMinutes"] = "15",
            })
            .Build();

        var services = new ServiceCollection();

        services.AddOptions<JwtOptions>().Bind(configuration.GetSection(JwtOptions.SectionName));

        if (Lifetime is ServiceLifetime.Singleton)
        {
            services.AddSingleton<JwtTokenGenerator>();
        }
        else
        {
            services.AddScoped<JwtTokenGenerator>();
        }

        _provider = services.BuildServiceProvider();
    }

    [GlobalCleanup]
    public void Cleanup() => _provider.Dispose();

    [Benchmark(Baseline = true)]
    public TokenResponse CreateToken() => CreateTokenInItsOwnScope();

    [Benchmark(OperationsPerInvoke = Concurrency)]
    public void CreateTokensConcurrently() => Parallel.For(0, Concurrency, _ => CreateTokenInItsOwnScope());

    private TokenResponse CreateTokenInItsOwnScope()
    {
        using var scope = _provider.CreateScope();

        return scope.ServiceProvider.GetRequiredService<JwtTokenGenerator>().GenerateToken(Alice).Response;
    }
}
