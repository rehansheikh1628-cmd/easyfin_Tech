using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using EasyFin_Tech.Server.DTOs;
using EasyFin_Tech.Server.Parsing.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace EasyFin_Tech.Server.Tests;

public class BankParsingSecurityTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public BankParsingSecurityTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static byte[] GenerateHdfcStatementPdf()
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PageSize.A4);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);

        page.AddText("Page No .: 1 Statement of account", 10, new PdfPoint(50, 800), font);
        page.AddText("Account Branch : DATTAWADI", 10, new PdfPoint(50, 780), font);
        page.AddText("Account No : 50200031189753", 10, new PdfPoint(50, 760), font);
        page.AddText("RTGS/NEFT IFSC : HDFC0004224", 10, new PdfPoint(50, 740), font);
        page.AddText("Statement From : 01/04/2024 To : 31/03/2025", 10, new PdfPoint(50, 720), font);

        // Columns
        page.AddText("Date", 10, new PdfPoint(40, 690), font);
        page.AddText("Narration", 10, new PdfPoint(150, 690), font);
        page.AddText("Chq./Ref.No.", 10, new PdfPoint(290, 690), font);
        page.AddText("Value Dt", 10, new PdfPoint(365, 690), font);
        page.AddText("Withdrawal Amt.", 10, new PdfPoint(420, 690), font);
        page.AddText("Deposit Amt.", 10, new PdfPoint(490, 690), font);
        page.AddText("Closing Balance", 10, new PdfPoint(565, 690), font);

        // Row
        page.AddText("01/04/24", 10, new PdfPoint(35, 660), font);
        page.AddText("SALARY CREDIT", 10, new PdfPoint(150, 660), font);
        page.AddText("REF0001", 10, new PdfPoint(290, 660), font);
        page.AddText("01/04/24", 10, new PdfPoint(365, 660), font);
        page.AddText("50,000.00", 10, new PdfPoint(490, 660), font);
        page.AddText("100,000.00", 10, new PdfPoint(565, 660), font);

        // Footer
        page.AddText("HDFC BANK LIMITED", 10, new PdfPoint(50, 50), font);
        page.AddText("*Closing balance includes funds earmarked", 10, new PdfPoint(50, 35), font);

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

        var email = $"{prefix}_{Guid.NewGuid():N}@sec-parse.test";
        var request = new RegisterRequest($"{prefix} Workspace", email, "SecurePass123!");
        var response = await client.PostAsJsonAsync("/api/auth/register", request, JsonOptions);
        Assert.True(response.IsSuccessStatusCode);

        var auth = await response.Content.ReadFromJsonAsync<AuthResponseDto>(JsonOptions);
        Assert.NotNull(auth);
        return (client, auth);
    }

    [Fact]
    public async Task AnonymousAccess_ParseAndTransactionsEndpoints_ReturnsUnauthorized()
    {
        var anonymousClient = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
            BaseAddress = new Uri("http://localhost")
        });

        var fakeId = Guid.NewGuid();

        var parseResp = await anonymousClient.PostAsync($"/api/statements/{fakeId}/parse", null);
        Assert.Equal(HttpStatusCode.Unauthorized, parseResp.StatusCode);

        var txResp = await anonymousClient.GetAsync($"/api/statements/{fakeId}/transactions");
        Assert.Equal(HttpStatusCode.Unauthorized, txResp.StatusCode);
    }

    [Fact]
    public async Task CrossUserAccess_UserCannotParseOrRetrieveAnotherUsersTransactions()
    {
        // 1. Register User A
        var (clientA, authA) = await RegisterUserAsync("UserA");

        // User A uploads an HDFC statement
        var pdfBytes = GenerateHdfcStatementPdf();
        var uploadContent = CreateMultipartPdf("HdfcStatement.pdf", pdfBytes);
        var uploadResp = await clientA.PostAsync("/api/statements/upload", uploadContent);
        Assert.True(uploadResp.IsSuccessStatusCode);

        var uploadResult = await uploadResp.Content.ReadFromJsonAsync<StatementUploadResponse>(JsonOptions);
        Assert.NotNull(uploadResult);
        var fileIdA = uploadResult.FileId;

        // User A extracts the statement
        var extractResp = await clientA.PostAsync($"/api/statements/{fileIdA}/extract", null);
        Assert.True(extractResp.IsSuccessStatusCode);

        // 2. Register User B
        var (clientB, authB) = await RegisterUserAsync("UserB");

        // 3. User B attempts to parse User A's statement -> Must be rejected (404 NotFound to prevent ID harvesting)
        var crossParseResp = await clientB.PostAsync($"/api/statements/{fileIdA}/parse", null);
        Assert.Equal(HttpStatusCode.NotFound, crossParseResp.StatusCode);

        // 4. User B attempts to get User A's transactions -> Must be rejected (404 NotFound)
        var crossTxResp = await clientB.GetAsync($"/api/statements/{fileIdA}/transactions");
        Assert.Equal(HttpStatusCode.NotFound, crossTxResp.StatusCode);

        // 5. User A parses their own statement -> Must succeed
        var parseRespA = await clientA.PostAsync($"/api/statements/{fileIdA}/parse", null);
        Assert.True(parseRespA.IsSuccessStatusCode);

        var parseResultA = await parseRespA.Content.ReadFromJsonAsync<BankParsingResult>(JsonOptions);
        Assert.NotNull(parseResultA);
        Assert.True(parseResultA.Success);
        Assert.Single(parseResultA.Transactions);

        // 6. User A retrieves transactions -> Succeeded
        var txRespA = await clientA.GetAsync($"/api/statements/{fileIdA}/transactions");
        Assert.True(txRespA.IsSuccessStatusCode);

        var txResultA = await txRespA.Content.ReadFromJsonAsync<BankParsingResult>(JsonOptions);
        Assert.NotNull(txResultA);
        Assert.Single(txResultA.Transactions);
        Assert.Equal("SALARY CREDIT", txResultA.Transactions[0].Description);

        // 7. User B STILL cannot view User A's transactions
        var crossTxResp2 = await clientB.GetAsync($"/api/statements/{fileIdA}/transactions");
        Assert.Equal(HttpStatusCode.NotFound, crossTxResp2.StatusCode);
    }
}
