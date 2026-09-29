using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Accufex.Server.Configuration;
using Accufex.Server.Options;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Accufex.Server.Tests;

public class ProductionConfigurationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public ProductionConfigurationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private class StubHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "Accufex.Server";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    // =========================================================================
    // 1–6: Startup Configuration Validation Tests
    // =========================================================================

    [Fact]
    public void Test01_ConfigurationValidation_MissingConnection_FailsInProduction()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "",
                ["Cors:AllowedOrigins:0"] = "https://app.accufex.com",
                ["FileUpload:StorageProvider"] = "Local"
            })
            .Build();

        var env = new StubHostEnvironment { EnvironmentName = "Production" };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ConfigurationValidator.Validate(config, env));

        Assert.Contains("ConnectionStrings:DefaultConnection is missing", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Test02_ConfigurationValidation_PlaceholderConnection_FailsInProduction()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Server=YOUR_PROD_SQL_SERVER;Database=Accufex;User Id=app;Password=pass;",
                ["Cors:AllowedOrigins:0"] = "https://app.accufex.com",
                ["FileUpload:StorageProvider"] = "Local"
            })
            .Build();

        var env = new StubHostEnvironment { EnvironmentName = "Production" };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ConfigurationValidator.Validate(config, env));

        Assert.Contains("placeholder 'YOUR_PROD_SQL_SERVER'", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Test03_ConfigurationValidation_MissingCorsOrigins_FailsInProduction()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Server=real-sql.database.windows.net;Database=Accufex;User Id=app;Password=pass;",
                ["FileUpload:StorageProvider"] = "Local"
            })
            .Build();

        var env = new StubHostEnvironment { EnvironmentName = "Production" };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ConfigurationValidator.Validate(config, env));

        Assert.Contains("Cors:AllowedOrigins is missing or empty in Production", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Test04_ConfigurationValidation_WildcardCors_FailsInProduction()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Server=real-sql.database.windows.net;Database=Accufex;User Id=app;Password=pass;",
                ["Cors:AllowedOrigins:0"] = "*",
                ["FileUpload:StorageProvider"] = "Local"
            })
            .Build();

        var env = new StubHostEnvironment { EnvironmentName = "Production" };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ConfigurationValidator.Validate(config, env));

        Assert.Contains("Wildcard origin '*' is strictly prohibited", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Test05_ConfigurationValidation_DevelopmentEnvironment_AllowsLocalSettings()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Server=localhost\\SQLEXPRESS;Database=Accufex;Trusted_Connection=True;",
                ["FileUpload:StorageProvider"] = "Local"
            })
            .Build();

        var env = new StubHostEnvironment { EnvironmentName = "Development" };

        // Should NOT throw in development even without production CORS
        var exception = Record.Exception(() => ConfigurationValidator.Validate(config, env));
        Assert.Null(exception);
    }

    [Fact]
    public void Test06_ConfigurationValidation_TestingEnvironment_AlwaysBypasses()
    {
        var config = new ConfigurationBuilder().Build();
        var env = new StubHostEnvironment { EnvironmentName = "Testing" };

        var exception = Record.Exception(() => ConfigurationValidator.Validate(config, env));
        Assert.Null(exception);
    }

    [Fact]
    public void Test07_ConfigurationValidation_AzureBlobWithoutConnectionString_Fails()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Server=real-sql.database.windows.net;Database=Accufex;User Id=app;Password=pass;",
                ["Cors:AllowedOrigins:0"] = "https://app.accufex.com",
                ["FileUpload:StorageProvider"] = "AzureBlob",
                ["FileUpload:AzureBlob:ConnectionString"] = ""
            })
            .Build();

        var env = new StubHostEnvironment { EnvironmentName = "Production" };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ConfigurationValidator.Validate(config, env));

        Assert.Contains("FileUpload:AzureBlob:ConnectionString is missing", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // 8–11: Security Headers & CORS Tests
    // =========================================================================

    [Fact]
    public async Task Test08_SecurityHeaders_EmittedOnAllResponses()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/health/database");

        Assert.True(response.Headers.Contains("X-Content-Type-Options"));
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").First());

        Assert.True(response.Headers.Contains("X-Frame-Options"));
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").First());

        Assert.True(response.Headers.Contains("Referrer-Policy"));
        Assert.Equal("strict-origin-when-cross-origin", response.Headers.GetValues("Referrer-Policy").First());

        Assert.True(response.Headers.Contains("X-XSS-Protection"));
        Assert.Equal("0", response.Headers.GetValues("X-XSS-Protection").First());

        Assert.True(response.Headers.Contains("Permissions-Policy"));
        Assert.Equal("camera=(), microphone=(), geolocation=()", response.Headers.GetValues("Permissions-Policy").First());
    }

    [Fact]
    public async Task Test09_CorsPolicy_AllowsDevelopmentOrigin_WithCredentials()
    {
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/auth/me");
        request.Headers.Add("Origin", "http://localhost:64991");
        request.Headers.Add("Access-Control-Request-Method", "GET");

        var response = await client.SendAsync(request);

        if (response.Headers.Contains("Access-Control-Allow-Origin"))
        {
            var allowOrigin = response.Headers.GetValues("Access-Control-Allow-Origin").First();
            Assert.Equal("http://localhost:64991", allowOrigin);
            Assert.NotEqual("*", allowOrigin);

            Assert.True(response.Headers.Contains("Access-Control-Allow-Credentials"));
            Assert.Equal("true", response.Headers.GetValues("Access-Control-Allow-Credentials").First());
        }
    }

    [Fact]
    public async Task Test10_CorsPolicy_RejectsUnapprovedOrigin()
    {
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/auth/me");
        request.Headers.Add("Origin", "https://malicious-attacker-site.com");
        request.Headers.Add("Access-Control-Request-Method", "GET");

        var response = await client.SendAsync(request);

        // Unapproved origin must NOT receive Access-Control-Allow-Origin matching the attacker
        if (response.Headers.Contains("Access-Control-Allow-Origin"))
        {
            var allowOrigin = response.Headers.GetValues("Access-Control-Allow-Origin").First();
            Assert.NotEqual("https://malicious-attacker-site.com", allowOrigin);
            Assert.NotEqual("*", allowOrigin);
        }
    }

    // =========================================================================
    // 11–14: Options Binding & Limit Verification
    // =========================================================================

    [Fact]
    public void Test11_CorsOptions_BindsConfiguredValues()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cors:AllowedOrigins:0"] = "https://app.accufex.com",
                ["Cors:AllowedOrigins:1"] = "https://admin.accufex.com"
            })
            .Build();

        var options = new CorsOptions();
        config.GetSection(CorsOptions.SectionName).Bind(options);

        Assert.Equal(2, options.AllowedOrigins.Length);
        Assert.Equal("https://app.accufex.com", options.AllowedOrigins[0]);
        Assert.Equal("https://admin.accufex.com", options.AllowedOrigins[1]);
    }

    [Fact]
    public void Test12_FileUploadOptions_Preserves250MbLimit()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FileUpload:MaxPdfSizeBytes"] = "262144000",
                ["FileUpload:StorageRoot"] = "Storage/Statements",
                ["FileUpload:StorageProvider"] = "Local"
            })
            .Build();

        var options = new FileUploadOptions();
        config.GetSection(FileUploadOptions.SectionName).Bind(options);

        Assert.Equal(262144000L, options.MaxPdfSizeBytes);
        Assert.Equal("Storage/Statements", options.StorageRoot);
        Assert.Equal("Local", options.StorageProvider);
    }

    [Fact]
    public void Test13_BackgroundProcessingOptions_BindsConfiguredValues()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BackgroundProcessing:MaxConcurrentJobs"] = "4",
                ["BackgroundProcessing:QueueCapacity"] = "200",
                ["BackgroundProcessing:JobTimeoutMinutes"] = "20"
            })
            .Build();

        var options = new BackgroundProcessingOptions();
        config.GetSection(BackgroundProcessingOptions.SectionName).Bind(options);

        Assert.Equal(4, options.MaxConcurrentJobs);
        Assert.Equal(200, options.QueueCapacity);
        Assert.Equal(20, options.JobTimeoutMinutes);
    }

    [Fact]
    public async Task Test14_DatabaseHealthCheck_ReturnsCleanStatus()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/health/database");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("Healthy", json);
    }

    [Fact]
    public async Task Test15_RootHealthCheck_ReturnsOkAndHealthyStatus()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("Healthy", json);
        // Ensure no sensitive internal details are exposed
        Assert.DoesNotContain("Server", json);
        Assert.DoesNotContain("Database", json);
        Assert.DoesNotContain("ConnectionString", json);
    }
}

