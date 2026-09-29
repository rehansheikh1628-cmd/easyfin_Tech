using System.Net;
using System.Threading.Tasks;
using Accufex.Server.Data;
using Accufex.Server.Options;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Accufex.Server.Tests;

public class DevelopmentWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _dbName = "Accufex_DevTestDb_" + Guid.NewGuid();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureServices(services =>
        {
            services.AddDbContext<AccufexDbContext>(options =>
            {
                options.UseInMemoryDatabase(_dbName);
            });

            services.Configure<FileUploadOptions>(options =>
            {
                options.StorageRoot = Path.Combine(Path.GetTempPath(), "Accufex_DevTests_" + Guid.NewGuid().ToString("N"));
            });
        });
    }
}

public class SwaggerEndpointTests : IClassFixture<DevelopmentWebApplicationFactory>
{
    private readonly DevelopmentWebApplicationFactory _factory;

    public SwaggerEndpointTests(DevelopmentWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task OpenApiEndpoint_ReturnsSuccessAndValidJson()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"openapi\"", content);
        Assert.Contains("/api/", content);
    }

    [Fact]
    public async Task SwaggerUiEndpoint_ReturnsHtml()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/swagger/index.html");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("swagger-ui", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SwaggerRoot_RedirectsOrServesSwaggerUi()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/swagger");

        // UseSwaggerUI issues a 301/302 redirect from /swagger to /swagger/index.html or serves 200
        Assert.True(
            response.StatusCode == HttpStatusCode.MovedPermanently ||
            response.StatusCode == HttpStatusCode.Redirect ||
            response.StatusCode == HttpStatusCode.OK,
            $"Expected redirect or OK for /swagger, but got {response.StatusCode}");
    }
}
