using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace API.Tests;

public class HealthEndpointTests(WebApplicationFactory<Program> factory) : ControllerTestsBase(factory)
{
    [Fact]
    public async Task Get_DoesNotRequireAuthentication()
    {
        var response = await Client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
