using System;
using System.Collections.Generic;
using System.Linq;
using EasyFin_Tech.Server.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace EasyFin_Tech.Server.Configuration;

public static class ConfigurationValidator
{
    public const string PlaceholderServerTag = "YOUR_PROD_SQL_SERVER";

    /// <summary>
    /// Validates application configuration on startup.
    /// Fails fast in Production if critical connection strings, CORS settings, or storage credentials are missing.
    /// NEVER includes secret values in exception messages.
    /// </summary>
    public static void Validate(IConfiguration configuration, IHostEnvironment environment)
    {
        // Skip validation during automated test runs
        if (environment.IsEnvironment("Testing"))
        {
            return;
        }

        var errors = new List<string>();

        // 1. Database Connection Validation
        var useInMemory = configuration.GetValue<bool>("UseInMemoryDatabase");
        var connectionString = configuration.GetConnectionString("DefaultConnection");

        if (!useInMemory)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                errors.Add("Critical: ConnectionStrings:DefaultConnection is missing or empty.");
            }
            else if (environment.IsProduction())
            {
                if (connectionString.Contains(PlaceholderServerTag, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add("Critical: ConnectionStrings:DefaultConnection contains unconfigured placeholder 'YOUR_PROD_SQL_SERVER'. A real production SQL Server connection string must be configured via environment variable or secret provider.");
                }
            }
        }

        // 2. Storage Provider Configuration Validation
        var storageProvider = configuration.GetValue<string>($"{FileUploadOptions.SectionName}:StorageProvider") ?? "Local";
        if (!string.Equals(storageProvider, "Local", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(storageProvider, "AzureBlob", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"Invalid configuration: FileUpload:StorageProvider '{storageProvider}' is not supported. Must be 'Local' or 'AzureBlob'.");
        }

        if (string.Equals(storageProvider, "AzureBlob", StringComparison.OrdinalIgnoreCase))
        {
            var blobConn = configuration.GetValue<string>($"{FileUploadOptions.SectionName}:AzureBlob:ConnectionString");
            if (string.IsNullOrWhiteSpace(blobConn))
            {
                errors.Add("Critical: FileUpload:StorageProvider is set to 'AzureBlob' but FileUpload:AzureBlob:ConnectionString is missing or empty.");
            }
        }

        // 3. CORS Policy Validation (Production Only)
        if (environment.IsProduction())
        {
            var corsOrigins = configuration.GetSection($"{CorsOptions.SectionName}:AllowedOrigins").Get<string[]>() ?? [];
            if (corsOrigins.Length == 0 || corsOrigins.All(string.IsNullOrWhiteSpace))
            {
                errors.Add("Critical: Cors:AllowedOrigins is missing or empty in Production. At least one production frontend origin (e.g. https://...) must be configured.");
            }
            else if (corsOrigins.Any(o => o.Trim() == "*"))
            {
                errors.Add("Security Error: Wildcard origin '*' is strictly prohibited in Cors:AllowedOrigins when credential-bearing cookie authentication is enabled.");
            }
        }

        if (errors.Count > 0)
        {
            var aggregated = string.Join(Environment.NewLine, errors);
            throw new InvalidOperationException($"Application configuration validation failed:{Environment.NewLine}{aggregated}");
        }
    }
}
