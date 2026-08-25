using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;

namespace API.LoadTests;

/// <summary>
/// Runs the API as a child process with the strategy and Redis endpoint the test asked for, then
/// checks that the process answering is the one it started.
/// <para>
/// The check is not paranoia. On a fixed port, a process left behind by an earlier run answers
/// <c>/health</c> exactly like a fresh one, so a suite that runs both strategies back to back can
/// silently measure the same strategy twice.
/// </para>
/// </summary>
public sealed class ApiProcess : IAsyncDisposable
{
    private readonly Process _process;

    private readonly StringBuilder _output;

    private ApiProcess(Process process, StringBuilder output) => (_process, _output) = (process, output);

    public static async Task<(string Url, ApiProcess? Process)> StartAsync(LoadTestOptions options)
    {
        if (options.ApiUrl is { } existing)
        {
            await VerifyAsync(existing, options);

            return (existing, null);
        }

        // A port nobody is on, so a leftover process cannot be mistaken for this one.
        var url = $"http://127.0.0.1:{FreePort()}";

        var start = new ProcessStartInfo("dotnet")
        {
            ArgumentList = { "run", "-c", "Release", "--project", ProjectPath(), "--no-launch-profile" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        start.Environment["ASPNETCORE_URLS"] = url;

        // Development is where the Jwt section lives, and ValidateOnStart would fail without it.
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["TokenRevocation__Strategy"] = options.Strategy.ToString();

        // Turns on the per-stage breakdown, so the run can say how much of a request the Redis
        // check actually is instead of reporting one opaque total.
        start.Environment["Diagnostics__Enabled"] = "true";

        // The same settings the load test mints tokens with. A mismatch here would fail every
        // request with a signature error instead of measuring anything.
        start.Environment["Jwt__Issuer"] = options.JwtIssuer;
        start.Environment["Jwt__Audience"] = options.JwtAudience;
        start.Environment["Jwt__SecurityKey"] = options.JwtSecurityKey;
        start.Environment["Jwt__ExpiryMinutes"] = options.TokenLifetimeMinutes.ToString();

        // Through the proxy, so the injected latency applies to the API's round trips and not to
        // the preload, which needs to move a hundred thousand keys quickly.
        start.Environment["ConnectionStrings__Redis"] = options.RedisProxy;

        // A rejected token logs at Information, and at a thousand requests a second that logging
        // would be a bigger cost than the thing under test.
        start.Environment["Logging__LogLevel__Default"] = "Warning";

        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the API.");

        // Kept rather than discarded: a failure to bind or a configuration error is only visible
        // here, and swallowing it turns a hard failure into a wrong number.
        var output = new StringBuilder();

        process.OutputDataReceived += (_, line) => output.AppendLine(line.Data);
        process.ErrorDataReceived += (_, line) => output.AppendLine(line.Data);

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var api = new ApiProcess(process, output);

        try
        {
            await VerifyAsync(url, options);
        }
        catch
        {
            Console.Error.WriteLine(api.Tail());

            await api.DisposeAsync();

            throw;
        }

        return (url, api);
    }

    private static int FreePort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

    private static string ProjectPath() =>
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "API", "API.csproj");

    /// <summary>Waits for the API, then confirms it is running the configuration this run asked for.</summary>
    private static async Task VerifyAsync(string url, LoadTestOptions options)
    {
        using var http = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(5) };

        // Generous, because the first run pays for a Release build of the API.
        var deadline = DateTime.UtcNow.AddSeconds(120);

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var info = await http.GetFromJsonAsync<ApiInfo>("/diagnostics/info");

                if (info is null)
                {
                    throw new InvalidOperationException($"The API at {url} did not report its configuration.");
                }

                if (!string.Equals(info.Strategy, options.Strategy.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"The API at {url} is running the {info.Strategy} strategy but this run asked for " +
                        $"{options.Strategy}. Something else is listening on that port (pid {info.ProcessId}).");
                }

                if (info.TokenLifetimeMinutes != options.TokenLifetimeMinutes)
                {
                    throw new InvalidOperationException(
                        $"The API at {url} has a {info.TokenLifetimeMinutes}-minute token lifetime but this run " +
                        $"asked for {options.TokenLifetimeMinutes} (pid {info.ProcessId}).");
                }

                return;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                // Not up yet.
            }

            await Task.Delay(500);
        }

        throw new TimeoutException($"The API at {url} never became ready.");
    }

    private string Tail(int lines = 25) =>
        string.Join(Environment.NewLine, _output
            .ToString()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .TakeLast(lines));

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            // `dotnet run` spawns the app as a grandchild, so the whole tree has to go.
            _process.Kill(entireProcessTree: true);
        }

        // Awaited, so the port is released before the next run tries to use one.
        await _process.WaitForExitAsync();

        _process.Dispose();
    }

    private sealed record ApiInfo(string Strategy, int ProcessId, int TokenLifetimeMinutes);
}
