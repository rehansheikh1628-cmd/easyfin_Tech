using EasyFin_Tech.Server.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")) &&
    string.IsNullOrEmpty(builder.Configuration["ASPNETCORE_ENVIRONMENT"]) &&
    builder.Environment.EnvironmentName != "Testing")
{
#if DEBUG
    builder.Environment.EnvironmentName = "Development";
#else
    builder.Environment.EnvironmentName = "Production";
#endif
}
else if (!string.IsNullOrEmpty(builder.Configuration["ASPNETCORE_ENVIRONMENT"]))
{
    builder.Environment.EnvironmentName = builder.Configuration["ASPNETCORE_ENVIRONMENT"]!;
}

// Add services to the container.
builder.Services.Configure<EasyFin_Tech.Server.Options.FileUploadOptions>(
    builder.Configuration.GetSection(EasyFin_Tech.Server.Options.FileUploadOptions.SectionName));
builder.Services.Configure<EasyFin_Tech.Server.Options.BackgroundProcessingOptions>(
    builder.Configuration.GetSection(EasyFin_Tech.Server.Options.BackgroundProcessingOptions.SectionName));
builder.Services.Configure<EasyFin_Tech.Server.Options.CorsOptions>(
    builder.Configuration.GetSection(EasyFin_Tech.Server.Options.CorsOptions.SectionName));

// Configure CORS policy with strict origin enforcement and credentials support
const string corsPolicyName = "EasyFin_CorsPolicy";
builder.Services.AddCors(options =>
{
    options.AddPolicy(corsPolicyName, policy =>
    {
        var configuredOrigins = builder.Configuration
            .GetSection($"{EasyFin_Tech.Server.Options.CorsOptions.SectionName}:AllowedOrigins")
            .Get<string[]>() ?? [];

        if (builder.Environment.IsDevelopment())
        {
            // In development, ensure local Angular dev servers are always allowed
            var devOrigins = new HashSet<string>(configuredOrigins, StringComparer.OrdinalIgnoreCase)
            {
                "http://localhost:64991",
                "https://localhost:64991"
            };
            policy.WithOrigins([.. devOrigins])
                  .AllowAnyHeader()
                  .AllowAnyMethod()
                  .AllowCredentials()
                  .WithExposedHeaders("Content-Disposition");
        }
        else
        {
            // In production, strictly enforce configured origins without wildcards
            var prodOrigins = configuredOrigins
                .Where(o => !string.IsNullOrWhiteSpace(o) && o.Trim() != "*")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (prodOrigins.Length > 0)
            {
                policy.WithOrigins(prodOrigins)
                      .AllowAnyHeader()
                      .AllowAnyMethod()
                      .AllowCredentials()
                      .WithExposedHeaders("Content-Disposition");
            }
        }
    });
});

// Configure Forwarded Headers for reverse proxy / load balancer deployments
builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor |
                               Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

// Configure upload limits (default 250MB to support 100+ MB bank statements) matching FileUploadOptions
var maxUploadBytes = builder.Configuration.GetValue<long?>($"{EasyFin_Tech.Server.Options.FileUploadOptions.SectionName}:MaxPdfSizeBytes") ?? 262144000L;

builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = maxUploadBytes;
});
builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.Limits.MaxRequestBodySize = maxUploadBytes;
    serverOptions.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(5);
});

builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<EasyFin_Tech.Server.Services.IFileStorageService, EasyFin_Tech.Server.Services.LocalFileStorageService>();
builder.Services.AddSingleton<EasyFin_Tech.Server.Services.IStatementProcessingQueue, EasyFin_Tech.Server.Services.StatementProcessingQueue>();
builder.Services.AddHostedService<EasyFin_Tech.Server.Services.StatementProcessingBackgroundWorker>();
builder.Services.AddScoped<EasyFin_Tech.Server.Services.IPasswordHasher, EasyFin_Tech.Server.Services.PasswordHasher>();
builder.Services.AddScoped<EasyFin_Tech.Server.Services.ICurrentUserService, EasyFin_Tech.Server.Services.CurrentUserService>();
builder.Services.AddScoped<EasyFin_Tech.Server.Services.IStatementService, EasyFin_Tech.Server.Services.StatementService>();
builder.Services.AddScoped<EasyFin_Tech.Server.Services.IPdfExtractionService, EasyFin_Tech.Server.Services.PdfExtractionService>();
builder.Services.AddScoped<EasyFin_Tech.Server.Parsing.Interfaces.IBankDetector, EasyFin_Tech.Server.Parsing.BankDetector>();
builder.Services.AddScoped<EasyFin_Tech.Server.Parsing.Interfaces.IBankStatementParser, EasyFin_Tech.Server.Parsing.Parsers.HdfcStatementParser>();
builder.Services.AddScoped<EasyFin_Tech.Server.Parsing.Interfaces.IBankStatementParser, EasyFin_Tech.Server.Parsing.Parsers.YesBankStatementParser>();
builder.Services.AddScoped<EasyFin_Tech.Server.Parsing.Interfaces.IBankStatementParser, EasyFin_Tech.Server.Parsing.Parsers.AxisStatementParser>();
builder.Services.AddScoped<EasyFin_Tech.Server.Parsing.Interfaces.IBankStatementParser, EasyFin_Tech.Server.Parsing.Parsers.CentralBankStatementParser>();
builder.Services.AddScoped<EasyFin_Tech.Server.Parsing.Interfaces.IBankStatementParser, EasyFin_Tech.Server.Parsing.Parsers.ICICIStatementParser>();
builder.Services.AddScoped<EasyFin_Tech.Server.Parsing.Interfaces.IBankStatementParser, EasyFin_Tech.Server.Parsing.Parsers.ICICIStatementParserV2>();
builder.Services.AddScoped<EasyFin_Tech.Server.Parsing.Interfaces.IBankStatementParser, EasyFin_Tech.Server.Parsing.Parsers.SBIStatementParser>();
builder.Services.AddScoped<EasyFin_Tech.Server.Parsing.Interfaces.IBankStatementParser, EasyFin_Tech.Server.Parsing.Parsers.BOIStatementParser>();
builder.Services.AddScoped<EasyFin_Tech.Server.Parsing.Interfaces.IBankStatementParser, EasyFin_Tech.Server.Parsing.Parsers.KotakStatementParser>();
builder.Services.AddScoped<EasyFin_Tech.Server.Parsing.Interfaces.IBankParserRegistry, EasyFin_Tech.Server.Parsing.BankParserRegistry>();
builder.Services.AddScoped<EasyFin_Tech.Server.Parsing.Interfaces.IBankParsingService, EasyFin_Tech.Server.Parsing.BankParsingService>();
builder.Services.AddScoped<EasyFin_Tech.Server.Validation.Services.ITransactionCorrectionStore, EasyFin_Tech.Server.Validation.Services.TransactionCorrectionStore>();
builder.Services.AddScoped<EasyFin_Tech.Server.Validation.Services.ITransactionValidationService, EasyFin_Tech.Server.Validation.Services.TransactionValidationService>();
builder.Services.AddScoped<EasyFin_Tech.Server.Export.Interfaces.IExcelExportService, EasyFin_Tech.Server.Export.Services.ExcelExportService>();
builder.Services.AddScoped<EasyFin_Tech.Server.ExcelToTally.Interfaces.IExcelReaderService, EasyFin_Tech.Server.ExcelToTally.Services.ClosedXmlExcelReaderService>();
builder.Services.AddScoped<EasyFin_Tech.Server.ExcelToTally.Interfaces.IExcelToTallyValidationService, EasyFin_Tech.Server.ExcelToTally.Services.ExcelToTallyValidationService>();
builder.Services.AddScoped<EasyFin_Tech.Server.ExcelToTally.Interfaces.IExcelTemplateService, EasyFin_Tech.Server.ExcelToTally.Services.ExcelTemplateService>();
builder.Services.AddScoped<EasyFin_Tech.Server.ExcelToTally.Interfaces.ITallyXmlGenerator, EasyFin_Tech.Server.ExcelToTally.Services.TallyXmlGeneratorService>();

if (!builder.Environment.IsEnvironment("Testing") && !builder.Configuration.GetValue<bool>("UseInMemoryDatabase"))
{
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
    builder.Services.AddDbContext<EasyFinDbContext>(options =>
        options.UseSqlServer(connectionString, sqlOptions =>
        {
            sqlOptions.EnableRetryOnFailure(
                maxRetryCount: 5,
                maxRetryDelay: TimeSpan.FromSeconds(30),
                errorNumbersToAdd: null);
            sqlOptions.CommandTimeout(60);
        }));
}

builder.Services.AddAuthentication(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "EasyFin_Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = builder.Environment.IsProduction()
            ? CookieSecurePolicy.Always
            : CookieSecurePolicy.SameAsRequest;
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });

builder.Services.AddAuthorization();

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// Validate critical configuration on startup (fails fast in Production on invalid/missing config)
EasyFin_Tech.Server.Configuration.ConfigurationValidator.Validate(builder.Configuration, builder.Environment);

var app = builder.Build();

app.UseForwardedHeaders();

// Security Headers Middleware: Protect against clickjacking, MIME-sniffing, XSS, and cross-origin leaks
app.Use(async (context, next) =>
{
    context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    context.Response.Headers.Append("X-Frame-Options", "DENY");
    context.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");
    context.Response.Headers.Append("X-XSS-Protection", "0");
    context.Response.Headers.Append("Permissions-Policy", "camera=(), microphone=(), geolocation=()");
    await next();
});

app.UseCors(corsPolicyName);

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapStaticAssets();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/openapi/v1.json", "EasyFin Tech API v1");
        options.RoutePrefix = "swagger";
        options.DocumentTitle = "EasyFin Tech API - Swagger UI";
        options.DisplayRequestDuration();
        options.EnablePersistAuthorization();
    });

    // Automatically launch Microsoft Edge with both Main App and Swagger UI in one browser window
    if (!AppDomain.CurrentDomain.FriendlyName.Contains("testhost", StringComparison.OrdinalIgnoreCase) &&
        !app.Environment.IsEnvironment("Testing"))
    {
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(3500);
                try
                {
                    string[] potentialPaths =
                    [
                        @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                        @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
                        "msedge"
                    ];

                    string edgeExe = "msedge";
                    foreach (var path in potentialPaths)
                    {
                        if (File.Exists(path))
                        {
                            edgeExe = path;
                            break;
                        }
                    }

                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = edgeExe,
                        Arguments = "\"https://localhost:64991\" \"https://localhost:7059/swagger\"",
                        UseShellExecute = true
                    };
                    System.Diagnostics.Process.Start(psi);
                }
                catch
                {
                    // Ignore launch failure
                }
            });
        });
    }
}
else if (!app.Environment.IsEnvironment("Testing"))
{
    app.UseExceptionHandler(errorApp =>
    {
        errorApp.Run(async context =>
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new { error = "An internal server error occurred." });
        });
    });
    app.UseHsts();
    app.UseHttpsRedirection();
}

// Liveness & health check probe endpoint (sanitized, zero-dependency)
app.MapGet("/health", () => Results.Ok(new { status = "Healthy" }));

// Database health and connectivity check
app.MapGet("/api/health/database", async (EasyFinDbContext dbContext, IWebHostEnvironment env) =>
    {
        try
        {
            var canConnect = dbContext.Database.IsRelational()
                ? await dbContext.Database.CanConnectAsync()
                : true;
            if (!canConnect)
            {
                return Results.Problem("Cannot connect to SQL Server database.", statusCode: 503);
            }

            if (env.IsDevelopment())
            {
                var isRelational = dbContext.Database.IsRelational();
                var result = new
                {
                    Status = "Healthy",
                    Database = isRelational ? dbContext.Database.GetDbConnection().Database : "InMemory",
                    Server = isRelational ? dbContext.Database.GetDbConnection().DataSource : "InMemory",
                    CanConnect = canConnect,
                    Tables = new
                    {
                        Users = await dbContext.Users.CountAsync(),
                        Clients = await dbContext.Clients.IgnoreQueryFilters().CountAsync(),
                        FinancialYears = await dbContext.FinancialYears.IgnoreQueryFilters().CountAsync(),
                        FileRecords = await dbContext.FileRecords.IgnoreQueryFilters().CountAsync(),
                        Transactions = await dbContext.Transactions.IgnoreQueryFilters().CountAsync(),
                        TransactionImportResults = await dbContext.TransactionImportResults.IgnoreQueryFilters().CountAsync(),
                        PdfProcessingResults = await dbContext.PdfProcessingResults.IgnoreQueryFilters().CountAsync()
                    }
                };

                return Results.Ok(result);
            }

            // Production sanitized response: no internal server, database, or connection leak
            return Results.Ok(new
            {
                Status = "Healthy",
                CanConnect = true
            });
        }
        catch (Exception ex)
        {
            if (env.IsDevelopment())
            {
                return Results.Problem(detail: ex.Message, statusCode: 500, title: "Database Connectivity Error");
            }
            return Results.Problem(detail: "Database connectivity error.", statusCode: 500, title: "Database Error");
        }
    });

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.MapFallbackToFile("/index.html");

app.Run();

public partial class Program { }
