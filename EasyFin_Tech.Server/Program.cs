using EasyFin_Tech.Server.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")) && builder.Environment.EnvironmentName != "Testing")
{
#if DEBUG
    builder.Environment.EnvironmentName = "Development";
#else
    builder.Environment.EnvironmentName = "Production";
#endif
}

// Add services to the container.
builder.Services.Configure<EasyFin_Tech.Server.Options.FileUploadOptions>(
    builder.Configuration.GetSection(EasyFin_Tech.Server.Options.FileUploadOptions.SectionName));

// Configure upload limits (50MB) matching FileUploadOptions
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 52428800; // 50MB
});
builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.Limits.MaxRequestBodySize = 52428800; // 50MB
});

builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<EasyFin_Tech.Server.Services.IFileStorageService, EasyFin_Tech.Server.Services.LocalFileStorageService>();
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

if (!builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddDbContext<EasyFinDbContext>(options =>
        options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));
}

builder.Services.AddAuthentication(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "EasyFin_Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
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

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapStaticAssets();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}
else if (!app.Environment.IsEnvironment("Testing"))
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

// Database health and connectivity check
app.MapGet("/api/health/database", async (EasyFinDbContext dbContext) =>
    {
        try
        {
            var canConnect = await dbContext.Database.CanConnectAsync();
            if (!canConnect)
            {
                return Results.Problem("Cannot connect to SQL Server database.", statusCode: 503);
            }

            var result = new
            {
                Status = "Healthy",
                Database = dbContext.Database.GetDbConnection().Database,
                Server = dbContext.Database.GetDbConnection().DataSource,
                CanConnect = canConnect,
                Tables = new
                {
                    Users = await dbContext.Users.CountAsync(),
                    Clients = await dbContext.Clients.CountAsync(),
                    FinancialYears = await dbContext.FinancialYears.CountAsync(),
                    FileRecords = await dbContext.FileRecords.CountAsync(),
                    Transactions = await dbContext.Transactions.CountAsync(),
                    TransactionImportResults = await dbContext.TransactionImportResults.CountAsync(),
                    PdfProcessingResults = await dbContext.PdfProcessingResults.CountAsync()
                }
            };

            return Results.Ok(result);
        }
        catch (Exception ex)
        {
            return Results.Problem(detail: ex.Message, statusCode: 500, title: "Database Connectivity Error");
        }
    });

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.MapFallbackToFile("/index.html");

app.Run();

public partial class Program { }
