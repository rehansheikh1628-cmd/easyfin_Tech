using System;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Accufex.Server.Data;
using Accufex.Server.Options;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Accufex.Server.Tests;

public class ProductionWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _dbName = "Accufex_ProdTestDb_" + Guid.NewGuid();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ASPNETCORE_ENVIRONMENT", "Production");
        builder.UseSetting("UseInMemoryDatabase", "true");
        builder.UseEnvironment("Production");

        builder.ConfigureServices(services =>
        {
            var descriptors = System.Linq.Enumerable.ToList(System.Linq.Enumerable.Where(services, d =>
                d.ServiceType == typeof(DbContextOptions<AccufexDbContext>) ||
                d.ServiceType == typeof(DbContextOptions) ||
                d.ServiceType == typeof(AccufexDbContext)));
            foreach (var d in descriptors)
            {
                services.Remove(d);
            }

            services.AddDbContext<AccufexDbContext>(options =>
            {
                options.UseInMemoryDatabase(_dbName);
            });

            services.Configure<FileUploadOptions>(options =>
            {
                options.StorageRoot = Path.Combine(Path.GetTempPath(), "Accufex_ProdTests_" + Guid.NewGuid().ToString("N"));
            });
        });
    }
}

public class ProductionSecurityTests : IClassFixture<ProductionWebApplicationFactory>
{
    private readonly ProductionWebApplicationFactory _factory;

    public ProductionSecurityTests(ProductionWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Swagger_InProduction_Returns404NotFound()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var swaggerResponse = await client.GetAsync("/swagger/index.html");
        var openApiResponse = await client.GetAsync("/openapi/v1.json");

        // In production, Swagger and OpenAPI endpoints must not be exposed
        Assert.True(swaggerResponse.StatusCode == HttpStatusCode.NotFound || swaggerResponse.StatusCode == HttpStatusCode.OK,
            "Swagger UI endpoint must not be exposed as Swagger in Production");

        if (swaggerResponse.StatusCode == HttpStatusCode.OK)
        {
            var content = await swaggerResponse.Content.ReadAsStringAsync();
            // Should be fallback SPA HTML, NOT swagger-ui
            Assert.DoesNotContain("swagger-ui", content, StringComparison.OrdinalIgnoreCase);
        }

        Assert.True(openApiResponse.StatusCode == HttpStatusCode.NotFound || openApiResponse.StatusCode == HttpStatusCode.OK);
        if (openApiResponse.StatusCode == HttpStatusCode.OK)
        {
            var openApiContent = await openApiResponse.Content.ReadAsStringAsync();
            Assert.DoesNotContain("\"openapi\":", openApiContent, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task DatabaseHealth_InProduction_DoesNotLeakInternalServerDetails()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        var response = await client.GetAsync("/api/health/database");
        var content = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Status was {response.StatusCode}. Body: {content}");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Must report healthy & canConnect
        Assert.Equal("Healthy", json.GetProperty("status").GetString());
        Assert.True(json.GetProperty("canConnect").GetBoolean());

        // Must NOT expose internal Database name or SQL Server DataSource
        Assert.False(json.TryGetProperty("server", out _), "Production health check must not expose internal SQL Server name");
        Assert.False(json.TryGetProperty("database", out _), "Production health check must not expose internal database name");
        Assert.False(json.TryGetProperty("tables", out _), "Production health check must not expose table list in production");
    }

    [Fact]
    public async Task ProtectedEndpoint_WithoutAuthentication_Returns401Unauthorized()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/dashboard/stats");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/excel-to-tally/template", "GET")]
    [InlineData("/api/excel-to-tally/validate", "POST")]
    [InlineData("/api/excel-to-tally/generate-xml", "POST")]
    [InlineData("/api/dashboard/summary", "GET")]
    [InlineData("/api/dashboard/stats", "GET")]
    public async Task ProtectedEndpoints_WithoutAuthentication_Return401Unauthorized(string endpoint, string method)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        HttpResponseMessage response;
        if (method == "POST")
        {
            if (endpoint.Contains("validate"))
            {
                var multipart = new MultipartFormDataContent();
                multipart.Add(new ByteArrayContent([0x50, 0x4B, 0x03, 0x04]), "file", "test.xlsx");
                response = await client.PostAsync(endpoint, multipart);
            }
            else
            {
                response = await client.PostAsync(endpoint, new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
            }
        }
        else
        {
            response = await client.GetAsync(endpoint);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SecurityHeaders_AreEmittedOnResponses()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        var response = await client.GetAsync("/api/health/database");

        Assert.True(response.Headers.Contains("X-Content-Type-Options"), "Response must contain X-Content-Type-Options header");
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").First());

        Assert.True(response.Headers.Contains("X-Frame-Options"), "Response must contain X-Frame-Options header");
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").First());

        Assert.True(response.Headers.Contains("Referrer-Policy"), "Response must contain Referrer-Policy header");
        Assert.Equal("strict-origin-when-cross-origin", response.Headers.GetValues("Referrer-Policy").First());
    }
}
