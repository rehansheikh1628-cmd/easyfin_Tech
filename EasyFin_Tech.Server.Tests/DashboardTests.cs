using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using EasyFin_Tech.Server.DTOs;
using Microsoft.AspNetCore.Mvc.Testing;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace EasyFin_Tech.Server.Tests;

public class DashboardTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public DashboardTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static byte[] GenerateSamplePdf()
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PageSize.A4);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        page.AddText("Page 1 Sample Statement", 12, new PdfPoint(50, 800), font);
        page.AddText("Account No: 50200031189753", 10, new PdfPoint(50, 780), font);
        page.AddText("HDFC BANK LIMITED", 10, new PdfPoint(50, 50), font);
        return builder.Build();
    }

    private static MultipartFormDataContent CreateMultipartPdf(string fileName, byte[] bytes)
    {
        var multipart = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        multipart.Add(fileContent, "file", fileName);
        return multipart;
    }

    private async Task<(HttpClient Client, AuthResponseDto Auth)> RegisterUserAsync(string prefix)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            BaseAddress = new Uri("http://localhost")
        });

        var email = $"{prefix}_{Guid.NewGuid():N}@dashboard-test.com";
        var request = new RegisterRequest($"{prefix} Workspace", email, "SecurePass123!");
        var response = await client.PostAsJsonAsync("/api/auth/register", request, JsonOptions);
        Assert.True(response.IsSuccessStatusCode);

        var auth = await response.Content.ReadFromJsonAsync<AuthResponseDto>(JsonOptions);
        Assert.NotNull(auth);
        return (client, auth);
    }

    [Fact]
    public async Task AnonymousAccess_DashboardEndpoints_ReturnsUnauthorized()
    {
        var anonymousClient = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
            BaseAddress = new Uri("http://localhost")
        });

        var statsResp = await anonymousClient.GetAsync("/api/dashboard/stats");
        Assert.Equal(HttpStatusCode.Unauthorized, statsResp.StatusCode);

        var summaryResp = await anonymousClient.GetAsync("/api/dashboard/summary");
        Assert.Equal(HttpStatusCode.Unauthorized, summaryResp.StatusCode);
    }

    [Fact]
    public async Task EmptyState_NewUser_ReturnsZeroCountsAndEmptyRecentList()
    {
        var (client, _) = await RegisterUserAsync("EmptyUser");

        var response = await client.GetAsync("/api/dashboard/stats");
        Assert.True(response.IsSuccessStatusCode);

        var dto = await response.Content.ReadFromJsonAsync<DashboardSummaryDto>(JsonOptions);
        Assert.NotNull(dto);

        Assert.Equal(0, dto.TotalStatements);
        Assert.Equal(0, dto.CompletedStatements);
        Assert.Equal(0, dto.ProcessingStatements);
        Assert.Equal(0, dto.FailedStatements);
        Assert.Equal(0, dto.TotalTransactions);
        Assert.NotNull(dto.RecentStatements);
        Assert.Empty(dto.RecentStatements);
        Assert.True(dto.CanConnect);
    }

    [Fact]
    public async Task RealData_UserWithUploadedStatement_ReflectsAccurateStatistics()
    {
        var (client, _) = await RegisterUserAsync("ActiveUser");

        // Upload a statement
        var pdfBytes = GenerateSamplePdf();
        var uploadContent = CreateMultipartPdf("March_Statement.pdf", pdfBytes);
        var uploadResp = await client.PostAsync("/api/statements/upload", uploadContent);
        Assert.True(uploadResp.IsSuccessStatusCode);

        // Query dashboard summary
        var response = await client.GetAsync("/api/dashboard/summary");
        Assert.True(response.IsSuccessStatusCode);

        var dto = await response.Content.ReadFromJsonAsync<DashboardSummaryDto>(JsonOptions);
        Assert.NotNull(dto);

        Assert.Equal(1, dto.TotalStatements);
        Assert.Equal(1, dto.ProcessingStatements); // Newly uploaded statement is ReadyForProcessing
        Assert.Equal(0, dto.CompletedStatements);
        Assert.Equal(0, dto.FailedStatements);

        Assert.Single(dto.RecentStatements);
        var recent = dto.RecentStatements[0];
        Assert.Equal("March_Statement.pdf", recent.OriginalFileName);
        Assert.Equal("ReadyForProcessing", recent.Status);
        Assert.Equal(0, recent.ProcessingStatus);
        Assert.True(recent.FileSizeBytes > 0);
        Assert.False(string.IsNullOrWhiteSpace(recent.FileSizeFormatted));
    }

    [Fact]
    public async Task UserIsolation_UserACannotSeeUserBStatementsOrStatistics()
    {
        // 1. User A registers and uploads a statement
        var (clientA, _) = await RegisterUserAsync("UserA");
        var pdfBytes = GenerateSamplePdf();
        var uploadContent = CreateMultipartPdf("Confidential_A.pdf", pdfBytes);
        var uploadResp = await clientA.PostAsync("/api/statements/upload", uploadContent);
        Assert.True(uploadResp.IsSuccessStatusCode);

        // 2. User B registers (zero uploads)
        var (clientB, _) = await RegisterUserAsync("UserB");

        // 3. User B queries dashboard
        var responseB = await clientB.GetAsync("/api/dashboard/stats");
        Assert.True(responseB.IsSuccessStatusCode);
        var dtoB = await responseB.Content.ReadFromJsonAsync<DashboardSummaryDto>(JsonOptions);
        Assert.NotNull(dtoB);

        // User B must see 0 files and no trace of User A's statement
        Assert.Equal(0, dtoB.TotalStatements);
        Assert.Empty(dtoB.RecentStatements);

        // 4. User A queries dashboard
        var responseA = await clientA.GetAsync("/api/dashboard/stats");
        Assert.True(responseA.IsSuccessStatusCode);
        var dtoA = await responseA.Content.ReadFromJsonAsync<DashboardSummaryDto>(JsonOptions);
        Assert.NotNull(dtoA);

        // User A must see their 1 statement
        Assert.Equal(1, dtoA.TotalStatements);
        Assert.Single(dtoA.RecentStatements);
        Assert.Equal("Confidential_A.pdf", dtoA.RecentStatements[0].OriginalFileName);
    }
}
