using System.Net;

namespace API.Tests;

public class HealthEndpointTests(TestApiFactory factory) : ControllerTestsBase(factory)
{
    [Fact]
    public async Task Get_DoesNotRequireAuthentication()
    {
        var response = await Client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
