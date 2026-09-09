using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EasyFin_Tech.Server.Data;
using EasyFin_Tech.Server.DTOs;
using EasyFin_Tech.Server.Models;
using EasyFin_Tech.Server.Parsing;
using EasyFin_Tech.Server.Parsing.Interfaces;
using EasyFin_Tech.Server.Parsing.Models;
using EasyFin_Tech.Server.Parsing.Parsers;
using EasyFin_Tech.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using ClosedXML.Excel;
using EasyFin_Tech.Server.Export.Services;
using EasyFin_Tech.Server.Validation.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace EasyFin_Tech.Server.Tests;

public class HdfcParserTests
{
    private readonly ITestOutputHelper _output;
    private readonly Mock<ILogger<BankDetector>> _mockDetectorLogger;
    private readonly Mock<ILogger<HdfcStatementParser>> _mockParserLogger;
    private readonly Mock<ILogger<BankParsingService>> _mockServiceLogger;
    private readonly BankDetector _detector;
    private readonly HdfcStatementParser _parser;
    private readonly BankParserRegistry _registry;

    public HdfcParserTests(ITestOutputHelper output)
    {
        _output = output;
        _mockDetectorLogger = new Mock<ILogger<BankDetector>>();
        _mockParserLogger = new Mock<ILogger<HdfcStatementParser>>();
        _mockServiceLogger = new Mock<ILogger<BankParsingService>>();

        _detector = new BankDetector(_mockDetectorLogger.Object);
        _parser = new HdfcStatementParser(_mockParserLogger.Object);
        _registry = new BankParserRegistry([_parser]);
    }

    private static PdfExtractionResult BuildSyntheticHdfcExtraction(Action<List<PdfPageResult>> configurePages)
    {
        var pages = new List<PdfPageResult>();
        configurePages(pages);

        return new PdfExtractionResult
        {
            FileId = Guid.NewGuid(),
            OriginalFileName = "HDFC_Statement.pdf",
            PageCount = pages.Count,
            ExtractionStatus = "DigitalTextExtracted",
            HasUsableText = true,
            PdfType = "DigitalWithText",
            Pages = pages
        };
    }

    private static PdfPageResult CreatePage(int pageNumber, List<PdfCandidateRow> rows)
    {
        return new PdfPageResult
        {
            PageNumber = pageNumber,
            Width = 638.0,
            Height = 842.0,
            HasUsableText = true,
            CandidateRows = rows,
            RawText = string.Join("\n", rows.Select(r => r.RawLineText))
        };
    }

    private static PdfCandidateRow CreateRow(int pageNumber, double y, List<(string Text, double X)> tokens)
    {
        var frags = tokens.Select((t, i) => new PdfTextBlock
        {
            Text = t.Text,
            X = t.X,
            Y = y,
            Width = t.Text.Length * 6.0,
            Height = 10.0,
            PageNumber = pageNumber,
            ReadingOrderIndex = i + 1
        }).ToList();

        return new PdfCandidateRow
        {
            PageNumber = pageNumber,
            Y = y,
            Height = 12.0,
            RawLineText = string.Join(" ", tokens.Select(t => t.Text)),
            LineFragments = frags
        };
    }

    private static List<PdfCandidateRow> StandardHdfcHeaderRows(int pageNumber)
    {
        return
        [
            CreateRow(pageNumber, 20.0, [("Page", 35.0), ("No", 60.0), (".:", 75.0), (pageNumber.ToString(), 90.0), ("Statement", 120.0), ("of", 175.0), ("account", 190.0)]),
            CreateRow(pageNumber, 50.0, [("Account", 35.0), ("Branch", 75.0), (":", 115.0), ("DATTAWADI", 125.0)]),
            CreateRow(pageNumber, 75.0, [("Cust", 35.0), ("ID", 60.0), (":", 75.0), ("114556272", 85.0)]),
            CreateRow(pageNumber, 100.0, [("Account", 35.0), ("No", 75.0), (":", 95.0), ("50200031189753", 105.0)]),
            CreateRow(pageNumber, 125.0, [("RTGS/NEFT", 35.0), ("IFSC", 90.0), (":", 115.0), ("HDFC0004224", 125.0)]),
            CreateRow(pageNumber, 150.0, [("Branch", 35.0), ("Code", 70.0), (":", 100.0), ("4224", 110.0)]),
            CreateRow(pageNumber, 175.0, [("Statement", 35.0), ("From", 85.0), (":", 115.0), ("01/04/2024", 125.0), ("To", 190.0), (":", 210.0), ("31/03/2025", 220.0)]),
            CreateRow(pageNumber, 200.0, [
                ("Date", 40.0),
                ("Narration", 150.0),
                ("Chq./Ref.No.", 290.0),
                ("Value", 360.0), ("Dt", 385.0),
                ("Withdrawal", 410.0), ("Amt.", 450.0),
                ("Deposit", 490.0), ("Amt.", 525.0),
                ("Closing", 565.0), ("Balance", 595.0)
            ])
        ];
    }

    private static List<PdfCandidateRow> StandardHdfcFooterRows(int pageNumber)
    {
        return
        [
            CreateRow(pageNumber, 790.0, [("HDFC", 35.0), ("BANK", 65.0), ("LIMITED", 100.0)]),
            CreateRow(pageNumber, 800.0, [("*Closing", 35.0), ("balance", 80.0), ("includes", 120.0), ("funds", 160.0), ("earmarked", 190.0)]),
            CreateRow(pageNumber, 810.0, [("Contents", 35.0), ("of", 80.0), ("this", 95.0), ("statement", 120.0), ("will", 170.0), ("be", 190.0), ("considered", 205.0)]),
            CreateRow(pageNumber, 830.0, [("Registered", 35.0), ("Office", 90.0), ("Address:", 125.0), ("HDFC", 175.0), ("Bank", 205.0), ("House", 235.0)])
        ];
    }

    #region Mandatory 18 Test Scenarios

    [Fact]
    public void Test_01_HdfcBankDetection()
    {
        var extraction = BuildSyntheticHdfcExtraction(pages =>
        {
            var rows = StandardHdfcHeaderRows(1);
            pages.Add(CreatePage(1, rows));
        });

        var result = _detector.DetectBank(extraction);

        Assert.True(result.IsSupported);
        Assert.Equal(BankType.Hdfc, result.DetectedBank);
        Assert.Equal("HDFC Bank", result.BankName);
        Assert.Equal("50200031189753", result.AccountNumber);
        Assert.True(result.Confidence >= 0.95);
    }

    [Fact]
    public void Test_02_OneNormalTransaction()
    {
        var extraction = BuildSyntheticHdfcExtraction(pages =>
        {
            var rows = StandardHdfcHeaderRows(1);
            rows.Add(CreateRow(1, 230.0, [
                ("01/04/24", 35.0),
                ("IMPS-TRANSFER-VINAYAK", 75.0),
                ("0000409212048837", 295.0),
                ("01/04/24", 365.0),
                ("3,587.00", 520.0),
                ("1,417,194.45", 585.0)
            ]));
            rows.AddRange(StandardHdfcFooterRows(1));
            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        var parsed = _parser.Parse(extraction, detection);

        Assert.True(parsed.Success);
        Assert.Single(parsed.Transactions);

        var tx = parsed.Transactions[0];
        Assert.Equal(new DateTime(2024, 4, 1), tx.TransactionDate);
        Assert.Equal("IMPS-TRANSFER-VINAYAK", tx.Description);
        Assert.Equal("0000409212048837", tx.Reference);
        Assert.Equal(new DateTime(2024, 4, 1), tx.ValueDate);
        Assert.Null(tx.Debit);
        Assert.Equal(3587.00m, tx.Credit);
        Assert.Equal(3587.00m, tx.Amount);
        Assert.Equal(1417194.45m, tx.Balance);
        Assert.Equal("Credit", tx.TransactionType);
        Assert.False(tx.NeedsReview);
    }

    [Fact]
    public void Test_03_MultipleTransactions()
    {
        var extraction = BuildSyntheticHdfcExtraction(pages =>
        {
            var rows = StandardHdfcHeaderRows(1);
            rows.Add(CreateRow(1, 230.0, [
                ("01/04/24", 35.0), ("SALARY CREDIT", 75.0), ("REF001", 295.0), ("01/04/24", 365.0), ("50,000.00", 520.0), ("100,000.00", 585.0)
            ]));
            rows.Add(CreateRow(1, 250.0, [
                ("02/04/24", 35.0), ("OFFICE RENT", 75.0), ("REF002", 295.0), ("02/04/24", 365.0), ("20,000.00", 430.0), ("80,000.00", 585.0)
            ]));
            rows.Add(CreateRow(1, 270.0, [
                ("03/04/24", 35.0), ("UTILITY BILL", 75.0), ("REF003", 295.0), ("03/04/24", 365.0), ("5,000.00", 430.0), ("75,000.00", 585.0)
            ]));
            rows.AddRange(StandardHdfcFooterRows(1));
            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        var parsed = _parser.Parse(extraction, detection);

        Assert.True(parsed.Success);
        Assert.Equal(3, parsed.Transactions.Count);
        Assert.Equal(3, parsed.ProcessedCount);

        // Verify running balance calculation holds
        Assert.Equal(1.0, parsed.Transactions[1].Confidence);
        Assert.Equal(1.0, parsed.Transactions[2].Confidence);
    }

    [Fact]
    public void Test_04_DebitTransaction()
    {
        var extraction = BuildSyntheticHdfcExtraction(pages =>
        {
            var rows = StandardHdfcHeaderRows(1);
            rows.Add(CreateRow(1, 230.0, [
                ("05/04/24", 35.0), ("SUPPLIER PAYMENT", 75.0), ("CHQ100", 295.0), ("05/04/24", 365.0), ("12,500.00", 430.0), ("87,500.00", 585.0)
            ]));
            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        var parsed = _parser.Parse(extraction, detection);

        Assert.Single(parsed.Transactions);
        var tx = parsed.Transactions[0];
        Assert.Equal(12500.00m, tx.Debit);
        Assert.Null(tx.Credit);
        Assert.Equal(12500.00m, tx.Amount);
        Assert.Equal("Debit", tx.TransactionType);
    }

    [Fact]
    public void Test_05_CreditTransaction()
    {
        var extraction = BuildSyntheticHdfcExtraction(pages =>
        {
            var rows = StandardHdfcHeaderRows(1);
            rows.Add(CreateRow(1, 230.0, [
                ("06/04/24", 35.0), ("CLIENT RECEIPT", 75.0), ("NEFT99", 295.0), ("06/04/24", 365.0), ("45,000.00", 520.0), ("132,500.00", 585.0)
            ]));
            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        var parsed = _parser.Parse(extraction, detection);

        Assert.Single(parsed.Transactions);
        var tx = parsed.Transactions[0];
        Assert.Null(tx.Debit);
        Assert.Equal(45000.00m, tx.Credit);
        Assert.Equal(45000.00m, tx.Amount);
        Assert.Equal("Credit", tx.TransactionType);
    }

    [Fact]
    public void Test_06_ClosingBalanceSegregation()
    {
        var extraction = BuildSyntheticHdfcExtraction(pages =>
        {
            var rows = StandardHdfcHeaderRows(1);
            rows.Add(CreateRow(1, 230.0, [
                ("07/04/24", 35.0), ("TX SAMPLE", 75.0), ("REF1", 295.0), ("07/04/24", 365.0), ("100.00", 430.0), ("5,432.10", 585.0)
            ]));
            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        var parsed = _parser.Parse(extraction, detection);

        var tx = parsed.Transactions[0];
        Assert.Equal(100.00m, tx.Debit);
        Assert.Equal(5432.10m, tx.Balance);
        Assert.NotEqual(tx.Debit, tx.Balance);
    }

    [Fact]
    public void Test_07_MultiLineNarration()
    {
        var extraction = BuildSyntheticHdfcExtraction(pages =>
        {
            var rows = StandardHdfcHeaderRows(1);
            // Main row
            rows.Add(CreateRow(1, 230.0, [
                ("08/04/24", 35.0),
                ("NEFT CR-UBIN0534927-D M ENTERPRISES-SWIP", 75.0),
                ("0000001338512502", 295.0),
                ("08/04/24", 365.0),
                ("30,963.00", 520.0),
                ("1,448,157.45", 585.0)
            ]));
            // Continuation line 1 (no date, narration column only)
            rows.Add(CreateRow(1, 245.0, [
                ("E FIRE-001338512502", 75.0)
            ]));
            // Continuation line 2
            rows.Add(CreateRow(1, 260.0, [
                ("INVOICE 2024-04-A", 75.0)
            ]));
            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        var parsed = _parser.Parse(extraction, detection);

        Assert.Single(parsed.Transactions);
        var tx = parsed.Transactions[0];
        Assert.Equal("NEFT CR-UBIN0534927-D M ENTERPRISES-SWIP E FIRE-001338512502 INVOICE 2024-04-A", tx.Description);
    }

    [Fact]
    public void Test_08_RepeatedPageHeaderRemoval()
    {
        var extraction = BuildSyntheticHdfcExtraction(pages =>
        {
            // Page 1
            var p1Rows = StandardHdfcHeaderRows(1);
            p1Rows.Add(CreateRow(1, 230.0, [
                ("01/04/24", 35.0), ("PAGE 1 TX", 75.0), ("REF1", 295.0), ("01/04/24", 365.0), ("1,000.00", 520.0), ("50,000.00", 585.0)
            ]));
            pages.Add(CreatePage(1, p1Rows));

            // Page 2 repeats the customer header
            var p2Rows = StandardHdfcHeaderRows(2);
            p2Rows.Add(CreateRow(2, 230.0, [
                ("02/04/24", 35.0), ("PAGE 2 TX", 75.0), ("REF2", 295.0), ("02/04/24", 365.0), ("2,000.00", 520.0), ("52,000.00", 585.0)
            ]));
            pages.Add(CreatePage(2, p2Rows));
        });

        var detection = _detector.DetectBank(extraction);
        var parsed = _parser.Parse(extraction, detection);

        Assert.Equal(2, parsed.Transactions.Count);
        Assert.DoesNotContain(parsed.Transactions, t => t.Description.Contains("Statement of account"));
        Assert.DoesNotContain(parsed.Transactions, t => t.Description.Contains("Account Branch"));
    }

    [Fact]
    public void Test_09_PageFooterRemoval()
    {
        var extraction = BuildSyntheticHdfcExtraction(pages =>
        {
            var rows = StandardHdfcHeaderRows(1);
            rows.Add(CreateRow(1, 230.0, [
                ("01/04/24", 35.0), ("REGULAR TX", 75.0), ("REF1", 295.0), ("01/04/24", 365.0), ("500.00", 520.0), ("10,000.00", 585.0)
            ]));
            rows.AddRange(StandardHdfcFooterRows(1));
            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        var parsed = _parser.Parse(extraction, detection);

        Assert.Single(parsed.Transactions);
        Assert.DoesNotContain(parsed.Transactions, t => t.Description.Contains("HDFC BANK LIMITED"));
        Assert.DoesNotContain(parsed.Transactions, t => t.Description.Contains("Registered Office"));
    }

    [Fact]
    public void Test_10_OpeningBalanceAndSummaryFiltering()
    {
        var extraction = BuildSyntheticHdfcExtraction(pages =>
        {
            var rows = StandardHdfcHeaderRows(1);
            rows.Add(CreateRow(1, 230.0, [
                ("01/04/24", 35.0), ("VALID TX", 75.0), ("REF1", 295.0), ("01/04/24", 365.0), ("1,000.00", 520.0), ("10,000.00", 585.0)
            ]));
            // Statement summary section
            rows.Add(CreateRow(1, 350.0, [("STATEMENT", 50.0), ("SUMMARY", 110.0), (":-", 155.0)]));
            rows.Add(CreateRow(1, 370.0, [
                ("Opening", 50.0), ("Balance", 90.0), ("Dr", 140.0), ("Count", 160.0), ("Cr", 195.0), ("Count", 215.0), ("Debits", 250.0), ("Credits", 295.0), ("Closing", 345.0), ("Bal", 385.0)
            ]));
            rows.Add(CreateRow(1, 390.0, [
                ("9,000.00", 50.0), ("0", 140.0), ("1", 195.0), ("0.00", 250.0), ("1,000.00", 295.0), ("10,000.00", 345.0)
            ]));
            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        var parsed = _parser.Parse(extraction, detection);

        Assert.Single(parsed.Transactions);
        Assert.Equal("VALID TX", parsed.Transactions[0].Description);
    }

    [Fact]
    public void Test_11_PageBreakTransactionContinuation()
    {
        var extraction = BuildSyntheticHdfcExtraction(pages =>
        {
            // Page 1: Transaction starts at the bottom
            var p1Rows = StandardHdfcHeaderRows(1);
            p1Rows.Add(CreateRow(1, 750.0, [
                ("09/04/25", 35.0),
                ("UPI-MR MOHAMMAD ILYAS", 75.0),
                ("0000102902276061", 295.0),
                ("09/04/25", 365.0),
                ("1,000.00", 430.0),
                ("23,748.47", 585.0)
            ]));
            p1Rows.Add(CreateRow(1, 765.0, [
                ("AB-MOHAMMADILYAS12", 75.0)
            ]));
            p1Rows.AddRange(StandardHdfcFooterRows(1));
            pages.Add(CreatePage(1, p1Rows));

            // Page 2: Header repeats, then narration continues before the next transaction starts
            var p2Rows = StandardHdfcHeaderRows(2);
            p2Rows.Add(CreateRow(2, 215.0, [
                ("02@OKSBI-CBIN0283572-102902276061-UPI", 75.0)
            ]));
            p2Rows.Add(CreateRow(2, 235.0, [
                ("10/04/25", 35.0),
                ("UPI-SHAHNAZ ABDUL MUJIB", 75.0),
                ("0000102929422269", 295.0),
                ("10/04/25", 365.0),
                ("20,000.00", 430.0),
                ("3,748.47", 585.0)
            ]));
            pages.Add(CreatePage(2, p2Rows));
        });

        var detection = _detector.DetectBank(extraction);
        var parsed = _parser.Parse(extraction, detection);

        Assert.Equal(2, parsed.Transactions.Count);

        var firstTx = parsed.Transactions[0];
        Assert.Equal("UPI-MR MOHAMMAD ILYAS AB-MOHAMMADILYAS12 02@OKSBI-CBIN0283572-102902276061-UPI", firstTx.Description);
        Assert.Equal(1000.00m, firstTx.Debit);
        Assert.Equal(23748.47m, firstTx.Balance);

        var secondTx = parsed.Transactions[1];
        Assert.Equal("UPI-SHAHNAZ ABDUL MUJIB", secondTx.Description);
        Assert.Equal(20000.00m, secondTx.Debit);
    }

    [Fact]
    public void Test_12_BlankDebitCreditCells()
    {
        var extraction = BuildSyntheticHdfcExtraction(pages =>
        {
            var rows = StandardHdfcHeaderRows(1);
            // Debit row: credit cell is empty
            rows.Add(CreateRow(1, 230.0, [
                ("11/04/24", 35.0), ("PURCHASE", 75.0), ("REF1", 295.0), ("11/04/24", 365.0), ("450.00", 430.0), ("10,000.00", 585.0)
            ]));
            // Credit row: debit cell is empty
            rows.Add(CreateRow(1, 250.0, [
                ("12/04/24", 35.0), ("INTEREST", 75.0), ("REF2", 295.0), ("12/04/24", 365.0), ("125.00", 520.0), ("10,125.00", 585.0)
            ]));
            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        var parsed = _parser.Parse(extraction, detection);

        Assert.Equal(2, parsed.Transactions.Count);
        Assert.Equal(450.00m, parsed.Transactions[0].Debit);
        Assert.Null(parsed.Transactions[0].Credit);

        Assert.Null(parsed.Transactions[1].Debit);
        Assert.Equal(125.00m, parsed.Transactions[1].Credit);
    }

    [Fact]
    public void Test_13_ReferenceChequeNumberExtraction()
    {
        var extraction = BuildSyntheticHdfcExtraction(pages =>
        {
            var rows = StandardHdfcHeaderRows(1);
            rows.Add(CreateRow(1, 230.0, [
                ("15/04/24", 35.0), ("CHQ CLEARING", 75.0), ("0000000000000758", 295.0), ("15/04/24", 365.0), ("21,000.00", 430.0), ("50,000.00", 585.0)
            ]));
            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        var parsed = _parser.Parse(extraction, detection);

        Assert.Equal("0000000000000758", parsed.Transactions[0].Reference);
    }

    [Fact]
    public void Test_14_DateParsing()
    {
        var extraction = BuildSyntheticHdfcExtraction(pages =>
        {
            var rows = StandardHdfcHeaderRows(1);
            rows.Add(CreateRow(1, 230.0, [
                ("05/11/24", 35.0), ("NOVEMBER TX", 75.0), ("REF1", 295.0), ("06/11/24", 365.0), ("1,000.00", 430.0), ("20,000.00", 585.0)
            ]));
            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        var parsed = _parser.Parse(extraction, detection);

        var tx = parsed.Transactions[0];
        Assert.Equal(new DateTime(2024, 11, 5), tx.TransactionDate);
        Assert.Equal(new DateTime(2024, 11, 6), tx.ValueDate);
    }

    [Fact]
    public void Test_15_AmountParsing()
    {
        var extraction = BuildSyntheticHdfcExtraction(pages =>
        {
            var rows = StandardHdfcHeaderRows(1);
            rows.Add(CreateRow(1, 230.0, [
                ("20/04/24", 35.0), ("LARGE SUM", 75.0), ("REF1", 295.0), ("20/04/24", 365.0), ("1,234,567.89", 520.0), ("2,345,678.90", 585.0)
            ]));
            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        var parsed = _parser.Parse(extraction, detection);

        var tx = parsed.Transactions[0];
        Assert.Equal(1234567.89m, tx.Credit);
        Assert.Equal(2345678.90m, tx.Balance);
    }

    [Fact]
    public void Test_16_MalformedOrAmbiguousRowMarkedForReview()
    {
        var extraction = BuildSyntheticHdfcExtraction(pages =>
        {
            var rows = StandardHdfcHeaderRows(1);
            // Row with mismatching balance calculation
            rows.Add(CreateRow(1, 230.0, [
                ("21/04/24", 35.0), ("FIRST ROW", 75.0), ("REF1", 295.0), ("21/04/24", 365.0), ("1,000.00", 520.0), ("10,000.00", 585.0)
            ]));
            rows.Add(CreateRow(1, 250.0, [
                ("22/04/24", 35.0), ("SECOND ROW BAD BAL", 75.0), ("REF2", 295.0), ("22/04/24", 365.0), ("500.00", 520.0), ("99,999.00", 585.0) // Expected 10,500
            ]));
            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        var parsed = _parser.Parse(extraction, detection);

        Assert.Equal(2, parsed.Transactions.Count);
        Assert.True(parsed.Transactions[1].NeedsReview);
        Assert.Contains("Running balance mismatch", parsed.Transactions[1].ReviewWarnings[0]);
    }

    [Fact]
    public void Test_17_UnsupportedNonHdfcDocumentNotClassifiedAsHdfc()
    {
        var extraction = new PdfExtractionResult
        {
            FileId = Guid.NewGuid(),
            OriginalFileName = "Invoice_2026.pdf",
            PageCount = 1,
            ExtractionStatus = "DigitalTextExtracted",
            HasUsableText = true,
            Pages =
            [
                new PdfPageResult
                {
                    PageNumber = 1,
                    RawText = "INVOICE #10294\nVendor: Global Tech Services\nTotal Amount Due: $450.00\nPayment terms: Net 30"
                }
            ]
        };

        var detection = _detector.DetectBank(extraction);

        Assert.False(detection.IsSupported);
        Assert.Equal(BankType.Unknown, detection.DetectedBank);
        Assert.False(_parser.CanParse(detection));
    }

    [Fact]
    public async Task Test_18_SafeIdempotency_ReprocessingReplacesWithoutDuplication()
    {
        var dbOptions = new DbContextOptionsBuilder<EasyFinDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var dbContext = new EasyFinDbContext(dbOptions);

        var userId = Guid.NewGuid();
        var client = new Client
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = "Test Client",
            ContactPerson = "Tester",
            Email = "tester@easyfin.local",
            Phone = "1234567890",
            BusinessName = "Tester Co",
            BusinessType = "Corporate",
            Address = "Nagpur",
            TaxId = "TAX123",
            CreatedAt = DateTime.UtcNow
        };
        var year = new FinancialYear
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            DisplayName = "2024-25",
            StartDate = new DateTime(2024, 4, 1),
            EndDate = new DateTime(2025, 3, 31),
            Status = 1,
            CreatedAt = DateTime.UtcNow
        };
        var fileRecord = new FileRecord
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            FinancialYearId = year.Id,
            OriginalFileName = "HdfcStatement.pdf",
            StoredFileName = "safe_file.pdf",
            Extension = ".pdf",
            ContentType = "application/pdf",
            SizeBytes = 1024,
            UploadedAt = DateTime.UtcNow,
            ProcessingStatus = 2
        };

        dbContext.Clients.Add(client);
        dbContext.FinancialYears.Add(year);
        dbContext.FileRecords.Add(fileRecord);
        await dbContext.SaveChangesAsync();

        var syntheticExtraction = BuildSyntheticHdfcExtraction(pages =>
        {
            var rows = StandardHdfcHeaderRows(1);
            rows.Add(CreateRow(1, 230.0, [
                ("01/04/24", 35.0), ("FIRST PARSE TX", 75.0), ("REF1", 295.0), ("01/04/24", 365.0), ("1,000.00", 520.0), ("10,000.00", 585.0)
            ]));
            pages.Add(CreatePage(1, rows));
        });

        var mockPdfService = new Mock<IPdfExtractionService>();
        mockPdfService.Setup(p => p.GetExtractionResultAsync(fileRecord.Id, userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(syntheticExtraction);

        var service = new BankParsingService(dbContext, mockPdfService.Object, _detector, _registry, _mockServiceLogger.Object);

        // 1. Initial parse
        var (success1, err1, result1) = await service.ParseAndPersistStatementAsync(fileRecord.Id, userId);
        Assert.True(success1, err1);
        Assert.Equal(1, await dbContext.Transactions.CountAsync(t => t.SourceFileId == fileRecord.Id));

        // 2. Safe re-parsing with updated transaction
        var updatedExtraction = BuildSyntheticHdfcExtraction(pages =>
        {
            var rows = StandardHdfcHeaderRows(1);
            rows.Add(CreateRow(1, 230.0, [
                ("01/04/24", 35.0), ("RE-PARSED TX 1", 75.0), ("REF1", 295.0), ("01/04/24", 365.0), ("2,000.00", 520.0), ("12,000.00", 585.0)
            ]));
            rows.Add(CreateRow(1, 250.0, [
                ("02/04/24", 35.0), ("RE-PARSED TX 2", 75.0), ("REF2", 295.0), ("02/04/24", 365.0), ("1,000.00", 430.0), ("11,000.00", 585.0)
            ]));
            pages.Add(CreatePage(1, rows));
        });

        mockPdfService.Setup(p => p.GetExtractionResultAsync(fileRecord.Id, userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(updatedExtraction);

        var (success2, err2, result2) = await service.ParseAndPersistStatementAsync(fileRecord.Id, userId);
        Assert.True(success2, err2);

        // Crucial: exactly 2 transactions must exist (not 1 + 2 = 3)
        var persistedTransactions = await dbContext.Transactions
            .Where(t => t.SourceFileId == fileRecord.Id)
            .OrderBy(t => t.TransactionDate)
            .ToListAsync();
        Assert.Equal(2, persistedTransactions.Count);
        Assert.Equal("RE-PARSED TX 1", persistedTransactions[0].Description);
        Assert.Equal("RE-PARSED TX 2", persistedTransactions[1].Description);

        // Check import result
        var import = await dbContext.TransactionImportResults.FirstOrDefaultAsync(r => r.SourceFileId == fileRecord.Id);
        Assert.NotNull(import);
        Assert.Equal(2, import.ProcessedCount);
        Assert.Equal("Completed", import.Status);
    }

    [Fact]
    public async Task Test_18b_SafeIdempotency_FailedReparsePreservesPreviousTransactions()
    {
        var dbOptions = new DbContextOptionsBuilder<EasyFinDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var dbContext = new EasyFinDbContext(dbOptions);

        var userId = Guid.NewGuid();
        var client = new Client
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = "Test Client",
            ContactPerson = "Tester",
            Email = "tester@easyfin.local",
            Phone = "1234567890",
            BusinessName = "Tester Co",
            BusinessType = "Corporate",
            Address = "Nagpur",
            TaxId = "TAX123",
            CreatedAt = DateTime.UtcNow
        };
        var year = new FinancialYear
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            DisplayName = "2024-25",
            StartDate = new DateTime(2024, 4, 1),
            EndDate = new DateTime(2025, 3, 31),
            Status = 1,
            CreatedAt = DateTime.UtcNow
        };
        var fileRecord = new FileRecord
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            FinancialYearId = year.Id,
            OriginalFileName = "HdfcStatement.pdf",
            StoredFileName = "safe_file.pdf",
            Extension = ".pdf",
            ContentType = "application/pdf",
            SizeBytes = 1024,
            UploadedAt = DateTime.UtcNow,
            ProcessingStatus = 2
        };

        dbContext.Clients.Add(client);
        dbContext.FinancialYears.Add(year);
        dbContext.FileRecords.Add(fileRecord);
        await dbContext.SaveChangesAsync();

        var initialExtraction = BuildSyntheticHdfcExtraction(pages =>
        {
            var rows = StandardHdfcHeaderRows(1);
            rows.Add(CreateRow(1, 230.0, [
                ("01/04/24", 35.0), ("PRESERVED ORIGINAL TX", 75.0), ("REF1", 295.0), ("01/04/24", 365.0), ("5,000.00", 520.0), ("20,000.00", 585.0)
            ]));
            pages.Add(CreatePage(1, rows));
        });

        var mockPdfService = new Mock<IPdfExtractionService>();
        mockPdfService.Setup(p => p.GetExtractionResultAsync(fileRecord.Id, userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(initialExtraction);

        var service = new BankParsingService(dbContext, mockPdfService.Object, _detector, _registry, _mockServiceLogger.Object);

        // 1. Initial successful parse
        var (s1, e1, r1) = await service.ParseAndPersistStatementAsync(fileRecord.Id, userId);
        Assert.True(s1);
        Assert.Equal(1, await dbContext.Transactions.CountAsync(t => t.SourceFileId == fileRecord.Id));

        // 2. Second parse fails because extraction is empty or non-HDFC
        var failedExtraction = new PdfExtractionResult
        {
            FileId = fileRecord.Id,
            HasUsableText = false, // Scanned / unusable text
            PageCount = 1,
            Pages = []
        };
        mockPdfService.Setup(p => p.GetExtractionResultAsync(fileRecord.Id, userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(failedExtraction);

        var (s2, e2, r2) = await service.ParseAndPersistStatementAsync(fileRecord.Id, userId);
        Assert.False(s2);

        // Required Correction 1: existing transaction must be preserved untouched!
        var existingTx = await dbContext.Transactions.Where(t => t.SourceFileId == fileRecord.Id).ToListAsync();
        Assert.Single(existingTx);
        Assert.Equal("PRESERVED ORIGINAL TX", existingTx[0].Description);
    }

    #endregion

    #region Real PDF Full Pipeline Integration Tests

    [Fact]
    public void RealPdf_Hdfc8753_FullPipelineReconciliationAndTransactionVerification()
    {
        string path = @"E:\Bank Statements\New 07-08-2026\hdfc bank 8753 01.04.24 to 31.3.25.pdf";
        if (!File.Exists(path))
        {
            _output.WriteLine($"[SKIPPED] File not found: {path}");
            return;
        }

        // 1. Digital PDF extraction (simulate Phase 3 pipeline)
        using var doc = PdfDocument.Open(path);
        Assert.Equal(47, doc.NumberOfPages);

        var pages = new List<PdfPageResult>(doc.NumberOfPages);
        for (int p = 1; p <= doc.NumberOfPages; p++)
        {
            var pdfPage = doc.GetPage(p);
            var words = pdfPage.GetWords().ToList();

            // Group into visual lines
            var lines = words
                .GroupBy(w => Math.Round((pdfPage.Height - w.BoundingBox.Top) / 4.0) * 4.0)
                .OrderBy(g => g.Key)
                .Select((g, idx) =>
                {
                    var frags = g.OrderBy(w => w.BoundingBox.Left).Select((w, fIdx) => new PdfTextBlock
                    {
                        Text = w.Text,
                        X = Math.Round(w.BoundingBox.Left, 2),
                        Y = Math.Round(pdfPage.Height - w.BoundingBox.Top, 2),
                        Width = Math.Round(w.BoundingBox.Width, 2),
                        Height = Math.Round(w.BoundingBox.Height, 2),
                        PageNumber = p,
                        ReadingOrderIndex = fIdx + 1
                    }).ToList();

                    return new PdfCandidateRow
                    {
                        RowIndex = idx + 1,
                        PageNumber = p,
                        Y = g.Key,
                        Height = 12.0,
                        RawLineText = string.Join(" ", frags.Select(f => f.Text)),
                        LineFragments = frags
                    };
                }).ToList();

            pages.Add(new PdfPageResult
            {
                PageNumber = p,
                Width = pdfPage.Width,
                Height = pdfPage.Height,
                CandidateRows = lines,
                RawText = string.Join("\n", lines.Select(l => l.RawLineText)),
                HasUsableText = true
            });
        }

        var extraction = new PdfExtractionResult
        {
            FileId = Guid.NewGuid(),
            OriginalFileName = Path.GetFileName(path),
            PageCount = doc.NumberOfPages,
            HasUsableText = true,
            PdfType = "DigitalWithText",
            Pages = pages
        };

        // 2. Bank Detection
        var detection = _detector.DetectBank(extraction);
        Assert.True(detection.IsSupported);
        Assert.Equal(BankType.Hdfc, detection.DetectedBank);
        Assert.Equal("50200031189753", detection.AccountNumber);
        _output.WriteLine($"Detected Bank: {detection.BankName}, Account: {detection.AccountNumber}, Confidence: {detection.Confidence:P0}");

        // 3. Bank Statement Parsing
        var result = _parser.Parse(extraction, detection);
        Assert.True(result.Success);

        // Required Correction 2: Verification of all multifaceted properties
        _output.WriteLine($"Total Transactions Parsed: {result.Transactions.Count}");
        Assert.Equal(772, result.Transactions.Count);

        // Verify First Transaction
        var firstTx = result.Transactions[0];
        Assert.Equal(new DateTime(2024, 4, 1), firstTx.TransactionDate);
        Assert.Equal("IMPS-409212048837-VINAYAK SALES AGENCIES -AUBL-XXXXXXXXXXXX4312-MONEY TRANSFER", firstTx.Description);
        Assert.Equal("0000409212048837", firstTx.Reference);
        Assert.Equal(new DateTime(2024, 4, 1), firstTx.ValueDate);
        Assert.Null(firstTx.Debit);
        Assert.Equal(3587.00m, firstTx.Credit);
        Assert.Equal(1417194.45m, firstTx.Balance);

        // Verify Middle Transaction (Tx 350)
        var midTx = result.Transactions[349];
        Assert.True(midTx.TransactionDate.Year == 2024 || midTx.TransactionDate.Year == 2025);
        Assert.True(midTx.Amount > 0);
        Assert.True(midTx.Balance.HasValue);

        // Verify Last Transaction
        var lastTx = result.Transactions[^1];
        Assert.Equal(new DateTime(2025, 3, 30), lastTx.TransactionDate);
        Assert.Equal(166482.74m, lastTx.Balance);

        // Verify Debits and Credits exist
        int debitCount = result.Transactions.Count(t => t.TransactionType == "Debit");
        int creditCount = result.Transactions.Count(t => t.TransactionType == "Credit");
        Assert.True(debitCount > 200, $"Debit count: {debitCount}");
        Assert.True(creditCount > 200, $"Credit count: {creditCount}");

        // Verify 100% Running Balance Reconciliation
        int balanceMatches = 0;
        for (int i = 1; i < result.Transactions.Count; i++)
        {
            var prev = result.Transactions[i - 1];
            var curr = result.Transactions[i];
            if (prev.Balance.HasValue && curr.Balance.HasValue)
            {
                decimal expected = prev.Balance.Value - (curr.Debit ?? 0m) + (curr.Credit ?? 0m);
                Assert.True(Math.Abs(expected - curr.Balance.Value) <= 0.01m,
                    $"Balance mismatch at #{i + 1}: expected {expected}, actual {curr.Balance.Value}");
                balanceMatches++;
            }
        }

        Assert.Equal(771, balanceMatches);
        _output.WriteLine($"Verified {balanceMatches}/771 running balance transitions with 0 mismatches.");
    }

    [Fact]
    public void RealPdf_HdfcPass134173633_FullPipelineAndPageBoundaryVerification()
    {
        string path = @"E:\Bank Statements\HDFC Pass-134173633_unlocked.pdf";
        if (!File.Exists(path))
        {
            _output.WriteLine($"[SKIPPED] File not found: {path}");
            return;
        }

        using var doc = PdfDocument.Open(path);
        Assert.Equal(65, doc.NumberOfPages);

        var pages = new List<PdfPageResult>(doc.NumberOfPages);
        for (int p = 1; p <= doc.NumberOfPages; p++)
        {
            var pdfPage = doc.GetPage(p);
            var words = pdfPage.GetWords().ToList();

            var lines = words
                .GroupBy(w => Math.Round((pdfPage.Height - w.BoundingBox.Top) / 4.0) * 4.0)
                .OrderBy(g => g.Key)
                .Select((g, idx) =>
                {
                    var frags = g.OrderBy(w => w.BoundingBox.Left).Select((w, fIdx) => new PdfTextBlock
                    {
                        Text = w.Text,
                        X = Math.Round(w.BoundingBox.Left, 2),
                        Y = Math.Round(pdfPage.Height - w.BoundingBox.Top, 2),
                        Width = Math.Round(w.BoundingBox.Width, 2),
                        Height = Math.Round(w.BoundingBox.Height, 2),
                        PageNumber = p,
                        ReadingOrderIndex = fIdx + 1
                    }).ToList();

                    return new PdfCandidateRow
                    {
                        RowIndex = idx + 1,
                        PageNumber = p,
                        Y = g.Key,
                        Height = 12.0,
                        RawLineText = string.Join(" ", frags.Select(f => f.Text)),
                        LineFragments = frags
                    };
                }).ToList();

            pages.Add(new PdfPageResult
            {
                PageNumber = p,
                Width = pdfPage.Width,
                Height = pdfPage.Height,
                CandidateRows = lines,
                RawText = string.Join("\n", lines.Select(l => l.RawLineText)),
                HasUsableText = true
            });
        }

        var extraction = new PdfExtractionResult
        {
            FileId = Guid.NewGuid(),
            OriginalFileName = Path.GetFileName(path),
            PageCount = doc.NumberOfPages,
            HasUsableText = true,
            PdfType = "DigitalWithText",
            Pages = pages
        };

        var detection = _detector.DetectBank(extraction);
        Assert.True(detection.IsSupported);
        Assert.Equal(BankType.Hdfc, detection.DetectedBank);
        Assert.Equal("50100314749013", detection.AccountNumber);

        var result = _parser.Parse(extraction, detection);
        Assert.True(result.Success);

        _output.WriteLine($"Total Transactions Parsed: {result.Transactions.Count}");
        Assert.Equal(757, result.Transactions.Count);

        // Verify First Transaction
        var firstTx = result.Transactions[0];
        Assert.Equal(new DateTime(2025, 4, 1), firstTx.TransactionDate);
        Assert.Equal("UPI-DHANWANTARI MEDICAL -Q225169968@YBL- YESB0YBLUPI-102446005590-UPI", firstTx.Description);
        Assert.Equal("0000102446005590", firstTx.Reference);
        Assert.Equal(78.00m, firstTx.Debit);
        Assert.Equal(41553.47m, firstTx.Balance);

        // Verify Page Boundary Transaction (Page 1 -> Page 2 transition at index 11)
        var page1EndTx = result.Transactions.FirstOrDefault(t => t.SourcePageNumber == 1 && t.Description.Contains("AB-MOHAMMADILYAS12"));
        Assert.NotNull(page1EndTx);
        Assert.Equal("UPI-MR MOHAMMAD ILYAS AB-MOHAMMADILYAS12 02@OKSBI-CBIN0283572-102902276061-UPI", page1EndTx.Description);
        Assert.DoesNotContain("SHEIKH", page1EndTx.Description);
        Assert.DoesNotContain("SOCIETY", page1EndTx.Description);
        _output.WriteLine($"Cross-page boundary transaction successfully reconstructed without customer header leaks: '{page1EndTx.Description}'");

        // Verify 100% Running Balance Reconciliation
        int balanceMatches = 0;
        for (int i = 1; i < result.Transactions.Count; i++)
        {
            var prev = result.Transactions[i - 1];
            var curr = result.Transactions[i];
            if (prev.Balance.HasValue && curr.Balance.HasValue)
            {
                decimal expected = prev.Balance.Value - (curr.Debit ?? 0m) + (curr.Credit ?? 0m);
                Assert.True(Math.Abs(expected - curr.Balance.Value) <= 0.01m,
                    $"Balance mismatch at #{i + 1}: expected {expected}, actual {curr.Balance.Value}");
                balanceMatches++;
            }
        }

        Assert.Equal(756, balanceMatches);
        _output.WriteLine($"Verified {balanceMatches}/756 running balance transitions with 0 mismatches.");
    }

    [Fact]
    public void ScanAllPageTransitions_BothRealStatements()
    {
        string[] paths =
        [
            @"E:\Bank Statements\New 07-08-2026\hdfc bank 8753 01.04.24 to 31.3.25.pdf",
            @"E:\Bank Statements\HDFC Pass-134173633_unlocked.pdf"
        ];

        foreach (var path in paths)
        {
            if (!File.Exists(path)) continue;
            var fileName = Path.GetFileName(path);
            _output.WriteLine($"\n=======================================================");
            _output.WriteLine($"ANALYZING CROSS-PAGE CONTINUATIONS: {fileName}");
            _output.WriteLine($"=======================================================");

            using var doc = PdfDocument.Open(path);
            var pages = new List<PdfPageResult>(doc.NumberOfPages);
            for (int p = 1; p <= doc.NumberOfPages; p++)
            {
                var pdfPage = doc.GetPage(p);
                var words = pdfPage.GetWords().ToList();
                var lines = words
                    .GroupBy(w => Math.Round((pdfPage.Height - w.BoundingBox.Top) / 4.0) * 4.0)
                    .OrderBy(g => g.Key)
                    .Select((g, idx) =>
                    {
                        var frags = g.OrderBy(w => w.BoundingBox.Left).Select((w, fIdx) => new PdfTextBlock
                        {
                            Text = w.Text,
                            X = Math.Round(w.BoundingBox.Left, 2),
                            Y = Math.Round(pdfPage.Height - w.BoundingBox.Top, 2),
                            Width = Math.Round(w.BoundingBox.Width, 2),
                            Height = Math.Round(w.BoundingBox.Height, 2),
                            PageNumber = p,
                            ReadingOrderIndex = fIdx + 1
                        }).ToList();

                        return new PdfCandidateRow
                        {
                            RowIndex = idx + 1,
                            PageNumber = p,
                            Y = g.Key,
                            Height = 12.0,
                            RawLineText = string.Join(" ", frags.Select(f => f.Text)),
                            LineFragments = frags
                        };
                    }).ToList();

                pages.Add(new PdfPageResult
                {
                    PageNumber = p,
                    Width = pdfPage.Width,
                    Height = pdfPage.Height,
                    CandidateRows = lines,
                    RawText = string.Join("\n", lines.Select(l => l.RawLineText)),
                    HasUsableText = true
                });
            }

            var extraction = new PdfExtractionResult
            {
                FileId = Guid.NewGuid(),
                OriginalFileName = fileName,
                PageCount = doc.NumberOfPages,
                HasUsableText = true,
                PdfType = "DigitalWithText",
                Pages = pages
            };

            var detection = _detector.DetectBank(extraction);
            var result = _parser.Parse(extraction, detection);

            _output.WriteLine($"Parsed {result.Transactions.Count} transactions.");

            // Find transactions that spanned page boundaries
            // A transaction has a cross-page continuation if a line from page p (before the first tx date on page p)
            // was appended to lastTxPrevPage.Description / RawSourceText.
            int continuationCount = 0;
            for (int p = 2; p <= doc.NumberOfPages; p++)
            {
                var page = pages[p - 1];

                // Find the last transaction that started on a previous page
                var lastTxPrevPage = result.Transactions.LastOrDefault(t => t.SourcePageNumber < p);
                if (lastTxPrevPage == null) continue;

                // Find the first transaction date line on page p
                var firstDateLine = page.CandidateRows.FirstOrDefault(r =>
                {
                    var ft = r.LineFragments.OrderBy(f => f.X).FirstOrDefault();
                    return ft != null && ft.X < 58.0 && System.Text.RegularExpressions.Regex.IsMatch(ft.Text.Trim(), @"^\d{2}/\d{2}/\d{2,4}$");
                });

                var limitY = firstDateLine != null ? firstDateLine.Y : 9999.0;

                // Find lines on page p before limitY that are in the Narration or Reference column and not header/footer/metadata
                var appendedLinesOnPageP = new List<string>();
                foreach (var r in page.CandidateRows.Where(r => r.Y < limitY))
                {
                    var trimmed = r.RawLineText.Trim();
                    if (string.IsNullOrWhiteSpace(trimmed)) continue;

                    // Check if this line's text or fragments are present in lastTxPrevPage.RawSourceText or Description
                    // and not metadata/header
                    var narrFrags = r.LineFragments.Where(f => f.X >= 48.0 && f.X < 270.0 && !string.IsNullOrWhiteSpace(f.Text)).Select(f => f.Text.Trim()).ToList();
                    var narrText = string.Join(" ", narrFrags);
                    if (!string.IsNullOrWhiteSpace(narrText) && lastTxPrevPage.Description.Contains(narrText))
                    {
                        appendedLinesOnPageP.Add(trimmed);
                    }
                    else
                    {
                        var refFrags = r.LineFragments.Where(f => f.X >= 270.0 && f.X < 355.0 && !string.IsNullOrWhiteSpace(f.Text)).Select(f => f.Text.Trim()).ToList();
                        var refText = string.Join(" ", refFrags);
                        if (!string.IsNullOrWhiteSpace(refText) && lastTxPrevPage.Reference != null && lastTxPrevPage.Reference.Contains(refText))
                        {
                            appendedLinesOnPageP.Add(trimmed);
                        }
                    }
                }

                if (appendedLinesOnPageP.Count > 0)
                {
                    continuationCount++;
                    var fragsCombined = string.Join(" | ", appendedLinesOnPageP);
                    _output.WriteLine($"[Continuation #{continuationCount}] Page {lastTxPrevPage.SourcePageNumber} -> Page {p}:");
                    _output.WriteLine($"   Previous Tx: Date={lastTxPrevPage.TransactionDate:dd/MM/yy}, Ref='{lastTxPrevPage.Reference}', Debit={lastTxPrevPage.Debit}, Credit={lastTxPrevPage.Credit}, Bal={lastTxPrevPage.Balance}");
                    _output.WriteLine($"   Continuation Fragment(s) on Page {p}: {fragsCombined}");
                    _output.WriteLine($"   Final Merged Narration: '{lastTxPrevPage.Description}'");
                }
            }

            _output.WriteLine($"\n>>> EXACT CROSS-PAGE CONTINUATION CASES for {fileName}: {continuationCount} <<<");
        }
    }

    private static (double DateMaxX, double NarrationMinX, double RefMinX) HdfcStatementParser_CalibrateBounds(PdfExtractionResult extraction)
    {
        return (58.0, 58.0, 270.0);
    }

    #endregion

    #region Targeted Cross-Page Narration Continuation Regression Tests (Tests 1 through 9, Screenshot Case, and Excel Export)

    [Fact]
    public void CrossPageTest_01_CompleteFinancialFieldsWithPartialNarrationContinuedOnNextPage()
    {
        // TEST 1: Previous transaction ends Page N with complete financial fields but partial narration.
        // Page N+1 has continuation before next transaction date.
        // Expected: continuation appended to previous transaction; financial fields preserved.
        var extraction = BuildSyntheticHdfcExtraction(pages =>
        {
            // Page 1: Transaction starts with complete financial fields and partial narration
            var page1Rows = StandardHdfcHeaderRows(1);
            page1Rows.Add(CreateRow(1, 230.0, [
                ("26/03/26", 35.0),
                ("UPI-ABDUL MUJIB ABDUL", 75.0),
                ("0000608549247059", 295.0),
                ("26/03/26", 365.0),
                ("100,000.00", 430.0),
                ("244,408.08", 585.0)
            ]));
            page1Rows.Add(CreateRow(1, 245.0, [
                ("SA-MUJIBAS@OKSBI-I", 75.0)
            ]));
            pages.Add(CreatePage(1, page1Rows));

            // Page 2: Repeated header, table header, continuation fragment, next transaction
            var page2Rows = StandardHdfcHeaderRows(2);
            page2Rows.Add(CreateRow(2, 232.0, [
                ("BKL0000041-608549247059-ST", 75.0)
            ]));
            page2Rows.Add(CreateRow(2, 250.0, [
                ("27/03/26", 35.0),
                ("UPI-ABIZER MULLA HUSAIN", 75.0),
                ("0000645222459999", 295.0),
                ("27/03/26", 365.0),
                ("790.00", 430.0),
                ("245,198.08", 585.0)
            ]));
            pages.Add(CreatePage(2, page2Rows));
        });

        var detection = _detector.DetectBank(extraction);
        var result = _parser.Parse(extraction, detection);

        Assert.True(result.Success);
        Assert.Equal(2, result.Transactions.Count);

        var tx1 = result.Transactions[0];
        Assert.Equal(new DateTime(2026, 3, 26), tx1.TransactionDate);
        Assert.Equal("UPI-ABDUL MUJIB ABDUL SA-MUJIBAS@OKSBI-I BKL0000041-608549247059-ST", tx1.Description);
        Assert.Equal(100000.00m, tx1.Debit);
        Assert.Equal(244408.08m, tx1.Balance);

        var tx2 = result.Transactions[1];
        Assert.Equal(new DateTime(2026, 3, 27), tx2.TransactionDate);
        Assert.Equal("UPI-ABIZER MULLA HUSAIN", tx2.Description);
        Assert.Equal(790.00m, tx2.Debit);
    }

    [Fact]
    public void CrossPageTest_02_RepeatedCustomerMetadataIgnoredBeforeContinuation()
    {
        // TEST 2: Repeated HDFC metadata appears before continuation.
        // Expected: metadata ignored, continuation retained and appended.
        var extraction = BuildSyntheticHdfcExtraction(pages =>
        {
            var p1Rows = StandardHdfcHeaderRows(1);
            p1Rows.Add(CreateRow(1, 230.0, [
                ("10/05/26", 35.0), ("UPI-PAYMENT TO VENDOR", 75.0), ("REF999", 295.0), ("10/05/26", 365.0), ("500.00", 430.0), ("10,000.00", 585.0)
            ]));
            pages.Add(CreatePage(1, p1Rows));

            var p2Rows = new List<PdfCandidateRow>
            {
                CreateRow(2, 20.0, [("Page", 35.0), ("No", 60.0), (".:", 75.0), ("2", 90.0)]),
                CreateRow(2, 40.0, [("Account", 35.0), ("Branch", 75.0), (":", 115.0), ("ANDHERI", 125.0)]),
                CreateRow(2, 60.0, [("Customer", 35.0), ("Name", 85.0), (":", 120.0), ("DR", 130.0), ("ARUN", 155.0), ("SHARMA", 195.0)]),
                CreateRow(2, 80.0, [("Address", 35.0), (":", 80.0), ("ANDHERI", 90.0), ("WEST", 145.0), ("MUMBAI", 185.0)]),
                CreateRow(2, 100.0, [("City", 35.0), (":", 65.0), ("MUMBAI", 75.0), ("State", 130.0), (":", 165.0), ("MAHARASHTRA", 175.0)]),
                CreateRow(2, 120.0, [("Phone", 35.0), (":", 70.0), ("9876543210", 80.0), ("Email", 150.0), (":", 185.0), ("arun@example.com", 195.0)]),
                CreateRow(2, 140.0, [("Cust", 35.0), ("ID", 60.0), (":", 75.0), ("114556272", 85.0), ("Account", 150.0), ("No", 195.0), (":", 215.0), ("50200031189753", 225.0)]),
                CreateRow(2, 160.0, [("RTGS/NEFT", 35.0), ("IFSC", 90.0), (":", 115.0), ("HDFC0001234", 125.0), ("MICR", 200.0), (":", 235.0), ("400240010", 245.0)]),
                CreateRow(2, 180.0, [("Nomination", 35.0), ("Registered", 95.0), ("Account", 160.0), ("Type", 205.0), ("SAVINGS", 235.0)]),
                CreateRow(2, 200.0, [("Statement", 35.0), ("From", 85.0), ("01/04/2026", 115.0), ("To", 180.0), ("31/03/2027", 200.0)]),
                CreateRow(2, 225.0, [("FRAG-AFTER-METADATA-XYZ", 75.0)]),
                CreateRow(2, 245.0, [("11/05/26", 35.0), ("UPI-NEXT PERSON", 75.0), ("REF111", 295.0), ("11/05/26", 365.0), ("250.00", 430.0), ("9,750.00", 585.0)])
            };
            pages.Add(CreatePage(2, p2Rows));
        });

        var detection = _detector.DetectBank(extraction);
        var result = _parser.Parse(extraction, detection);

        Assert.Equal(2, result.Transactions.Count);
        var tx1 = result.Transactions[0];
        Assert.Equal("UPI-PAYMENT TO VENDOR FRAG-AFTER-METADATA-XYZ", tx1.Description);
        Assert.DoesNotContain("ARUN", tx1.Description);
        Assert.DoesNotContain("SHARMA", tx1.Description);
        Assert.DoesNotContain("ANDHERI", tx1.Description);
        Assert.DoesNotContain("MUMBAI", tx1.Description);
        Assert.DoesNotContain("SAVINGS", tx1.Description);
    }

    [Fact]
    public void CrossPageTest_03_RepeatedTableHeaderIgnoredBeforeContinuation()
    {
        // TEST 3: Repeated HDFC table header appears before continuation.
        // Expected: header ignored; continuation retained.
        var extraction = BuildSyntheticHdfcExtraction(pages =>
        {
            var p1Rows = StandardHdfcHeaderRows(1);
            p1Rows.Add(CreateRow(1, 230.0, [
                ("15/06/26", 35.0), ("NEFT DR-CORP SUPPLIES", 75.0), ("N123456", 295.0), ("15/06/26", 365.0), ("10,000.00", 430.0), ("80,000.00", 585.0)
            ]));
            pages.Add(CreatePage(1, p1Rows));

            var p2Rows = new List<PdfCandidateRow>
            {
                CreateRow(2, 20.0, [("Page", 35.0), ("No", 60.0), (".:", 75.0), ("2", 90.0)]),
                CreateRow(2, 200.0, [
                    ("Date", 40.0), ("Narration", 150.0), ("Chq./Ref.No.", 290.0), ("Value", 360.0), ("Dt", 385.0),
                    ("Withdrawal", 410.0), ("Amt.", 450.0), ("Deposit", 490.0), ("Amt.", 525.0), ("Closing", 565.0), ("Balance", 595.0)
                ]),
                CreateRow(2, 225.0, [("INV-2026-987654-FINAL SETTLEMENT", 75.0)]),
                CreateRow(2, 245.0, [("16/06/26", 35.0), ("SUBSEQUENT PAYMENT", 75.0), ("N654321", 295.0), ("16/06/26", 365.0), ("5,000.00", 430.0), ("75,000.00", 585.0)])
            };
            pages.Add(CreatePage(2, p2Rows));
        });

        var detection = _detector.DetectBank(extraction);
        var result = _parser.Parse(extraction, detection);

        Assert.Equal(2, result.Transactions.Count);
        var tx1 = result.Transactions[0];
        Assert.Equal("NEFT DR-CORP SUPPLIES INV-2026-987654-FINAL SETTLEMENT", tx1.Description);
        Assert.DoesNotContain("Narration", tx1.Description);
        Assert.DoesNotContain("Withdrawal", tx1.Description);
        Assert.DoesNotContain("Closing", tx1.Description);
        Assert.DoesNotContain("Balance", tx1.Description);
    }

    [Fact]
    public void CrossPageTest_04_ContinuationFollowedByNewTransactionDateFinalizesPrevious()
    {
        // TEST 4: Continuation followed by new transaction date.
        // Expected: continuation attaches to previous transaction; new date creates next transaction.
        var extraction = BuildSyntheticHdfcExtraction(pages =>
        {
            var p1Rows = StandardHdfcHeaderRows(1);
            p1Rows.Add(CreateRow(1, 230.0, [
                ("20/07/26", 35.0), ("PARTIAL DESC", 75.0), ("REF555", 295.0), ("20/07/26", 365.0), ("1,000.00", 430.0), ("15,000.00", 585.0)
            ]));
            pages.Add(CreatePage(1, p1Rows));

            var p2Rows = StandardHdfcHeaderRows(2);
            p2Rows.Add(CreateRow(2, 230.0, [("REMAINING DESC", 75.0)]));
            p2Rows.Add(CreateRow(2, 250.0, [("21/07/26", 35.0), ("NEW TX", 75.0), ("REF666", 295.0), ("21/07/26", 365.0), ("500.00", 430.0), ("14,500.00", 585.0)]));
            pages.Add(CreatePage(2, p2Rows));
        });

        var detection = _detector.DetectBank(extraction);
        var result = _parser.Parse(extraction, detection);

        Assert.Equal(2, result.Transactions.Count);
        Assert.Equal("PARTIAL DESC REMAINING DESC", result.Transactions[0].Description);
        Assert.Equal("NEW TX", result.Transactions[1].Description);
        Assert.Equal(new DateTime(2026, 7, 21), result.Transactions[1].TransactionDate);
    }

    [Fact]
    public void CrossPageTest_05_TrulyCompleteTransactionDirectlyFollowedByNewTransactionOnNextPage()
    {
        // TEST 5: Previous transaction is truly complete and next page begins directly with a new transaction.
        // Expected: no false continuation.
        var extraction = BuildSyntheticHdfcExtraction(pages =>
        {
            var p1Rows = StandardHdfcHeaderRows(1);
            p1Rows.Add(CreateRow(1, 230.0, [
                ("01/08/26", 35.0), ("FULL DESCRIPTION HERE", 75.0), ("REF1", 295.0), ("01/08/26", 365.0), ("200.00", 430.0), ("5,000.00", 585.0)
            ]));
            pages.Add(CreatePage(1, p1Rows));

            var p2Rows = StandardHdfcHeaderRows(2);
            p2Rows.Add(CreateRow(2, 230.0, [
                ("02/08/26", 35.0), ("ANOTHER FULL DESCRIPTION", 75.0), ("REF2", 295.0), ("02/08/26", 365.0), ("300.00", 430.0), ("4,700.00", 585.0)
            ]));
            pages.Add(CreatePage(2, p2Rows));
        });

        var detection = _detector.DetectBank(extraction);
        var result = _parser.Parse(extraction, detection);

        Assert.Equal(2, result.Transactions.Count);
        Assert.Equal("FULL DESCRIPTION HERE", result.Transactions[0].Description);
        Assert.Equal("ANOTHER FULL DESCRIPTION", result.Transactions[1].Description);
    }

    [Fact]
    public void CrossPageTest_06_MultipleConsecutivePageBoundaries_AllContinuationFragmentsPreserved()
    {
        // TEST 6: Multiple page transitions contain continuation text (Page 1 -> Page 2 -> Page 3).
        // Expected: all continuation fragments preserved across arbitrarily many pages.
        var extraction = BuildSyntheticHdfcExtraction(pages =>
        {
            // Page 1: Tx starts
            var p1Rows = StandardHdfcHeaderRows(1);
            p1Rows.Add(CreateRow(1, 230.0, [
                ("01/09/26", 35.0), ("INITIAL NARRATION", 75.0), ("REF123", 295.0), ("01/09/26", 365.0), ("500.00", 430.0), ("10,000.00", 585.0)
            ]));
            pages.Add(CreatePage(1, p1Rows));

            // Page 2: Continuation part 2 only (no new transaction)
            var p2Rows = StandardHdfcHeaderRows(2);
            p2Rows.Add(CreateRow(2, 230.0, [("CONTINUATION PART TWO", 75.0)]));
            pages.Add(CreatePage(2, p2Rows));

            // Page 3: Continuation part 3, then new transaction
            var p3Rows = StandardHdfcHeaderRows(3);
            p3Rows.Add(CreateRow(3, 230.0, [("CONTINUATION PART THREE", 75.0)]));
            p3Rows.Add(CreateRow(3, 250.0, [
                ("02/09/26", 35.0), ("NEXT TRANSACTION", 75.0), ("REF456", 295.0), ("02/09/26", 365.0), ("100.00", 430.0), ("9,900.00", 585.0)
            ]));
            pages.Add(CreatePage(3, p3Rows));
        });

        var detection = _detector.DetectBank(extraction);
        var result = _parser.Parse(extraction, detection);

        Assert.Equal(2, result.Transactions.Count);
        var tx1 = result.Transactions[0];
        Assert.Equal("INITIAL NARRATION CONTINUATION PART TWO CONTINUATION PART THREE", tx1.Description);
        Assert.Equal(500.00m, tx1.Debit);
        Assert.Equal(10000.00m, tx1.Balance);

        var tx2 = result.Transactions[1];
        Assert.Equal(new DateTime(2026, 9, 2), tx2.TransactionDate);
        Assert.Equal("NEXT TRANSACTION", tx2.Description);
    }

    [Fact]
    public void CrossPageTest_07_ContinuationContainsNumericLookingIdentifiers()
    {
        // TEST 7: Continuation contains numeric-looking identifiers.
        // Expected: still treated as continuation if not a valid Date-column transaction start.
        var extraction = BuildSyntheticHdfcExtraction(pages =>
        {
            var p1Rows = StandardHdfcHeaderRows(1);
            p1Rows.Add(CreateRow(1, 230.0, [
                ("10/10/26", 35.0), ("UPI-SHOPPING", 75.0), ("REF10", 295.0), ("10/10/26", 365.0), ("1,200.00", 430.0), ("8,800.00", 585.0)
            ]));
            pages.Add(CreatePage(1, p1Rows));

            var p2Rows = StandardHdfcHeaderRows(2);
            // Numeric-looking identifier in Narration column (X = 75.0)
            p2Rows.Add(CreateRow(2, 230.0, [("0000608549247059-9988-ST-1234", 75.0)]));
            p2Rows.Add(CreateRow(2, 250.0, [
                ("11/10/26", 35.0), ("FOLLOWING TX", 75.0), ("REF11", 295.0), ("11/10/26", 365.0), ("100.00", 430.0), ("8,700.00", 585.0)
            ]));
            pages.Add(CreatePage(2, p2Rows));
        });

        var detection = _detector.DetectBank(extraction);
        var result = _parser.Parse(extraction, detection);

        Assert.Equal(2, result.Transactions.Count);
        Assert.Equal("UPI-SHOPPING 0000608549247059-9988-ST-1234", result.Transactions[0].Description);
        Assert.Equal("FOLLOWING TX", result.Transactions[1].Description);
    }

    [Fact]
    public void CrossPageTest_08_FooterOnPreviousPageNotAppended()
    {
        // TEST 8: Footer on previous page.
        // Expected: footer is not appended to pending transaction; continuation on next page is retained.
        var extraction = BuildSyntheticHdfcExtraction(pages =>
        {
            var p1Rows = StandardHdfcHeaderRows(1);
            p1Rows.Add(CreateRow(1, 230.0, [
                ("01/11/26", 35.0), ("PRIMARY NARRATION", 75.0), ("REF88", 295.0), ("01/11/26", 365.0), ("750.00", 430.0), ("20,000.00", 585.0)
            ]));
            p1Rows.AddRange(StandardHdfcFooterRows(1));
            pages.Add(CreatePage(1, p1Rows));

            var p2Rows = StandardHdfcHeaderRows(2);
            p2Rows.Add(CreateRow(2, 230.0, [("LEGITIMATE CROSS-PAGE NARRATION", 75.0)]));
            p2Rows.Add(CreateRow(2, 250.0, [
                ("02/11/26", 35.0), ("NEXT ROW", 75.0), ("REF89", 295.0), ("02/11/26", 365.0), ("250.00", 430.0), ("19,750.00", 585.0)
            ]));
            pages.Add(CreatePage(2, p2Rows));
        });

        var detection = _detector.DetectBank(extraction);
        var result = _parser.Parse(extraction, detection);

        Assert.Equal(2, result.Transactions.Count);
        var tx1 = result.Transactions[0];
        Assert.Equal("PRIMARY NARRATION LEGITIMATE CROSS-PAGE NARRATION", tx1.Description);
        Assert.DoesNotContain("HDFC BANK LIMITED", tx1.Description);
        Assert.DoesNotContain("Closing balance includes funds", tx1.Description);
        Assert.DoesNotContain("Registered Office", tx1.Description);
    }

    [Fact]
    public void CrossPageTest_09_ReferenceContinuationAcrossPageBoundary_SeparatedByGeometry()
    {
        // TEST 9: Reference continuation across page boundary.
        // Expected: reference remains separate from narration where coordinate geometry proves it belongs to reference.
        var extraction = BuildSyntheticHdfcExtraction(pages =>
        {
            var p1Rows = StandardHdfcHeaderRows(1);
            p1Rows.Add(CreateRow(1, 230.0, [
                ("01/12/26", 35.0), ("UPI-VENDOR PAYMENT", 75.0), ("PARTIAL-REF-1", 295.0), ("01/12/26", 365.0), ("1,000.00", 430.0), ("30,000.00", 585.0)
            ]));
            pages.Add(CreatePage(1, p1Rows));

            var p2Rows = StandardHdfcHeaderRows(2);
            // Reference continuation fragment at X = 295.0 (Ref column interval [270, 355))
            p2Rows.Add(CreateRow(2, 225.0, [("EXTENDED-REF-2", 295.0)]));
            // Narration continuation fragment at X = 75.0 (Narration column interval [58, 270))
            p2Rows.Add(CreateRow(2, 235.0, [("ADDITIONAL NARRATION FRAGMENT", 75.0)]));
            p2Rows.Add(CreateRow(2, 255.0, [
                ("02/12/26", 35.0), ("NEXT TRANSACTION", 75.0), ("NEWREF", 295.0), ("02/12/26", 365.0), ("500.00", 430.0), ("29,500.00", 585.0)
            ]));
            pages.Add(CreatePage(2, p2Rows));
        });

        var detection = _detector.DetectBank(extraction);
        var result = _parser.Parse(extraction, detection);

        Assert.Equal(2, result.Transactions.Count);
        var tx1 = result.Transactions[0];
        Assert.Equal("UPI-VENDOR PAYMENT ADDITIONAL NARRATION FRAGMENT", tx1.Description);
        Assert.Equal("PARTIAL-REF-1 EXTENDED-REF-2", tx1.Reference);
        Assert.DoesNotContain("EXTENDED-REF-2", tx1.Description);
        Assert.DoesNotContain("ADDITIONAL NARRATION FRAGMENT", tx1.Reference);
    }

    [Fact]
    public void RealPdf_ExactScreenshotCase_Page63To64_AbdulMujibContinuation()
    {
        // SECTION 19: Exact real case verification from HDFC Pass-134173633_unlocked.pdf
        // Page 63 ends with: 26/03/26 UPI-ABDUL MUJIB ABDUL SA-MUJIBAS@OKSBI-I [ref/value dt/amount/balance]
        // Page 64 continuation: BKL0000041-608549247059-ST
        // Page 64 next transaction: 27/03/26 UPI-ABIZER MULLA HUSAIN
        // Expected single previous narration: UPI-ABDUL MUJIB ABDUL SA-MUJIBAS@OKSBI-I BKL0000041-608549247059-ST
        string path = @"E:\Bank Statements\HDFC Pass-134173633_unlocked.pdf";
        if (!File.Exists(path))
        {
            _output.WriteLine($"[SKIPPED] File not found: {path}");
            return;
        }

        using var doc = PdfDocument.Open(path);
        var pages = new List<PdfPageResult>(doc.NumberOfPages);
        for (int p = 1; p <= doc.NumberOfPages; p++)
        {
            var pdfPage = doc.GetPage(p);
            var words = pdfPage.GetWords().ToList();
            var lines = words
                .GroupBy(w => Math.Round((pdfPage.Height - w.BoundingBox.Top) / 4.0) * 4.0)
                .OrderBy(g => g.Key)
                .Select((g, idx) =>
                {
                    var frags = g.OrderBy(w => w.BoundingBox.Left).Select((w, fIdx) => new PdfTextBlock
                    {
                        Text = w.Text,
                        X = Math.Round(w.BoundingBox.Left, 2),
                        Y = Math.Round(pdfPage.Height - w.BoundingBox.Top, 2),
                        Width = Math.Round(w.BoundingBox.Width, 2),
                        Height = Math.Round(w.BoundingBox.Height, 2),
                        PageNumber = p,
                        ReadingOrderIndex = fIdx + 1
                    }).ToList();

                    return new PdfCandidateRow
                    {
                        RowIndex = idx + 1,
                        PageNumber = p,
                        Y = g.Key,
                        Height = 12.0,
                        RawLineText = string.Join(" ", frags.Select(f => f.Text)),
                        LineFragments = frags
                    };
                }).ToList();

            pages.Add(new PdfPageResult
            {
                PageNumber = p,
                Width = pdfPage.Width,
                Height = pdfPage.Height,
                CandidateRows = lines,
                RawText = string.Join("\n", lines.Select(l => l.RawLineText)),
                HasUsableText = true
            });
        }

        var extraction = new PdfExtractionResult
        {
            FileId = Guid.NewGuid(),
            OriginalFileName = Path.GetFileName(path),
            PageCount = doc.NumberOfPages,
            HasUsableText = true,
            PdfType = "DigitalWithText",
            Pages = pages
        };

        var detection = _detector.DetectBank(extraction);
        var result = _parser.Parse(extraction, detection);

        Assert.True(result.Success);
        Assert.Equal(757, result.Transactions.Count);

        // Find the transaction from Page 63
        var tx = result.Transactions.FirstOrDefault(t => t.SourcePageNumber == 63 && t.TransactionDate == new DateTime(2026, 3, 26) && t.Debit == 100000.00m);
        Assert.NotNull(tx);

        Assert.Equal("UPI-ABDUL MUJIB ABDUL SA-MUJIBAS@OKSBI-I BKL0000041-608549247059-ST", tx.Description);
        Assert.Equal("0000608549247059", tx.Reference);
        Assert.Equal(100000.00m, tx.Debit);
        Assert.Equal(244408.08m, tx.Balance);
        Assert.True(tx.Balance.HasValue);

        // Verify the next transaction starts cleanly on 27/03/26
        int txIndex = result.Transactions.IndexOf(tx);
        Assert.True(txIndex >= 0 && txIndex < result.Transactions.Count - 1);
        var nextTx = result.Transactions[txIndex + 1];
        Assert.Equal(new DateTime(2026, 3, 27), nextTx.TransactionDate);
        Assert.Contains("UPI-ABIZER MULLA HUSAIN", nextTx.Description);
        Assert.Equal(790.00m, nextTx.Credit);
        Assert.Null(nextTx.Debit);
        Assert.Equal(245198.08m, nextTx.Balance);

        _output.WriteLine($"[VERIFIED] Screenshot case: '{tx.Description}'");
        _output.WriteLine($"[VERIFIED] Following transaction: '{nextTx.Description}' ({nextTx.TransactionDate:dd/MM/yy})");
    }

    [Fact]
    public async Task ExcelExport_HdfcCrossPageTransaction_DescriptionContainsFullMergedNarration()
    {
        // SECTION 20: Excel regression verification
        // Verify that the affected HDFC transaction automatically exports with the complete narration in the Excel Description column.
        string path = @"E:\Bank Statements\HDFC Pass-134173633_unlocked.pdf";
        if (!File.Exists(path))
        {
            _output.WriteLine($"[SKIPPED] File not found: {path}");
            return;
        }

        using var doc = PdfDocument.Open(path);
        var pages = new List<PdfPageResult>(doc.NumberOfPages);
        for (int p = 1; p <= doc.NumberOfPages; p++)
        {
            var pdfPage = doc.GetPage(p);
            var words = pdfPage.GetWords().ToList();
            var lines = words
                .GroupBy(w => Math.Round((pdfPage.Height - w.BoundingBox.Top) / 4.0) * 4.0)
                .OrderBy(g => g.Key)
                .Select((g, idx) =>
                {
                    var frags = g.OrderBy(w => w.BoundingBox.Left).Select((w, fIdx) => new PdfTextBlock
                    {
                        Text = w.Text,
                        X = Math.Round(w.BoundingBox.Left, 2),
                        Y = Math.Round(pdfPage.Height - w.BoundingBox.Top, 2),
                        Width = Math.Round(w.BoundingBox.Width, 2),
                        Height = Math.Round(w.BoundingBox.Height, 2),
                        PageNumber = p,
                        ReadingOrderIndex = fIdx + 1
                    }).ToList();

                    return new PdfCandidateRow
                    {
                        RowIndex = idx + 1,
                        PageNumber = p,
                        Y = g.Key,
                        Height = 12.0,
                        RawLineText = string.Join(" ", frags.Select(f => f.Text)),
                        LineFragments = frags
                    };
                }).ToList();

            pages.Add(new PdfPageResult
            {
                PageNumber = p,
                Width = pdfPage.Width,
                Height = pdfPage.Height,
                CandidateRows = lines,
                RawText = string.Join("\n", lines.Select(l => l.RawLineText)),
                HasUsableText = true
            });
        }

        var extraction = new PdfExtractionResult
        {
            FileId = Guid.NewGuid(),
            OriginalFileName = Path.GetFileName(path),
            PageCount = doc.NumberOfPages,
            HasUsableText = true,
            PdfType = "DigitalWithText",
            Pages = pages
        };

        var detection = _detector.DetectBank(extraction);
        var result = _parser.Parse(extraction, detection);

        // Convert to review DTOs as consumed by ExcelExportService
        var reviewDtos = result.Transactions.Select((t, idx) => new TransactionReviewDto
        {
            Id = t.Id,
            SourceFileId = extraction.FileId,
            TransactionDate = t.TransactionDate,
            ValueDate = t.ValueDate,
            Description = t.Description,
            Reference = t.Reference,
            TransactionType = t.TransactionType,
            Debit = t.Debit,
            Credit = t.Credit,
            Balance = t.Balance,
            ValidationStatus = "VALID",
            BankCode = t.BankCode,
            BankName = t.BankName,
            SourcePageNumber = t.SourcePageNumber,
            SourceLineIndex = t.SourceLineIndex
        }).ToList();

        var fileRecord = new FileRecord
        {
            Id = extraction.FileId,
            OriginalFileName = "HDFC Pass-134173633_unlocked.pdf",
            UploadedAt = DateTime.UtcNow
        };
        var validationService = new TransactionValidationService();
        var summary = validationService.ComputeSummary(fileRecord.Id, 1, "HDFC Bank", "HDFC-v1", reviewDtos);

        var excelService = new ExcelExportService(NullLogger<ExcelExportService>.Instance);
        var fileBytes = await excelService.GenerateStatementWorkbookAsync(fileRecord, summary, reviewDtos);

        Assert.NotNull(fileBytes);
        Assert.True(fileBytes.Length > 0);

        // Inspect workbook with ClosedXML
        using var ms = new MemoryStream(fileBytes);
        using var workbook = new XLWorkbook(ms);
        var worksheet = workbook.Worksheet("Transactions");
        Assert.NotNull(worksheet);

        // Find the row for 26/03/2026 with description containing "ABDUL MUJIB"
        IXLRow? matchingRow = null;
        var lastRow = worksheet.LastRowUsed()?.RowNumber() ?? 0;
        for (int r = 2; r <= lastRow; r++)
        {
            var row = worksheet.Row(r);
            var desc = row.Cell(4).GetString(); // Column 4 is Particulars / Narration
            if (desc.Contains("ABDUL MUJIB") && desc.Contains("BKL0000041-608549247059-ST"))
            {
                matchingRow = row;
                break;
            }
        }

        Assert.NotNull(matchingRow);
        var exportedDesc = matchingRow.Cell(4).GetString();
        Assert.Equal("UPI-ABDUL MUJIB ABDUL SA-MUJIBAS@OKSBI-I BKL0000041-608549247059-ST", exportedDesc);
        _output.WriteLine($"[EXCEL VERIFIED] Description cell: '{exportedDesc}'");
    }

    #endregion

    #region Section 16 - Generic HDFC Parsing Tests (Scenarios A through K)

    [Fact]
    public void ScenarioA_DifferentCustomerMetadata_ParsedSuccessfullyAndMetadataExcluded()
    {
        // Different customer name, account number, branch, address, statement period
        var extraction = BuildSyntheticHdfcExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>
            {
                CreateRow(1, 20.0, [("Page", 35.0), ("No", 60.0), (".:", 75.0), ("1", 90.0), ("Statement", 120.0), ("of", 175.0), ("account", 190.0)]),
                CreateRow(1, 40.0, [("DR", 35.0), ("ARUN", 60.0), ("KUMAR", 100.0), ("SHARMA", 150.0)]),
                CreateRow(1, 55.0, [("FLAT", 35.0), ("402", 70.0), ("SKYLINE", 100.0), ("TOWERS", 150.0)]),
                CreateRow(1, 70.0, [("KORAMANGALA", 35.0), ("BENGALURU", 120.0), ("KARNATAKA", 190.0), ("560034", 260.0)]),
                CreateRow(1, 85.0, [("Phone", 35.0), ("no", 70.0), (".:", 90.0), ("9876543210", 110.0), ("Email", 200.0), (":", 230.0), ("arun.sharma@example.com", 240.0)]),
                CreateRow(1, 100.0, [("Account", 35.0), ("Branch", 75.0), (":", 115.0), ("KORAMANGALA", 125.0), ("BANGALORE", 210.0)]),
                CreateRow(1, 115.0, [("Cust", 35.0), ("ID", 60.0), (":", 75.0), ("998877665", 85.0), ("Account", 180.0), ("Type", 220.0), (":", 250.0), ("SAVINGS", 260.0)]),
                CreateRow(1, 130.0, [("Account", 35.0), ("No", 75.0), (":", 95.0), ("50100987654321", 105.0)]),
                CreateRow(1, 145.0, [("RTGS/NEFT", 35.0), ("IFSC", 90.0), (":", 115.0), ("HDFC0000053", 125.0), ("MICR", 220.0), (":", 250.0), ("560240002", 260.0)]),
                CreateRow(1, 160.0, [("Statement", 35.0), ("From", 85.0), (":", 115.0), ("01/01/2026", 125.0), ("To", 190.0), (":", 210.0), ("31/01/2026", 220.0)]),
                CreateRow(1, 180.0, [
                    ("Date", 40.0),
                    ("Narration", 150.0),
                    ("Chq./Ref.No.", 290.0),
                    ("Value", 360.0), ("Dt", 385.0),
                    ("Withdrawal", 410.0), ("Amt.", 450.0),
                    ("Deposit", 490.0), ("Amt.", 525.0),
                    ("Closing", 565.0), ("Balance", 595.0)
                ]),
                CreateRow(1, 210.0, [
                    ("05/01/26", 35.0),
                    ("NETBANKING TRANSFER CONSULTING FEES", 75.0),
                    ("N0052619472019", 295.0),
                    ("05/01/26", 365.0),
                    ("150,000.00", 515.0),
                    ("450,000.00", 580.0)
                ]),
                CreateRow(1, 790.0, [("HDFC", 35.0), ("BANK", 65.0), ("LIMITED", 100.0)]),
                CreateRow(1, 805.0, [("Closing", 35.0), ("balance", 75.0), ("includes", 115.0), ("funds", 155.0), ("earmarked", 185.0)])
            };
            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        Assert.True(detection.IsSupported);
        Assert.Equal("50100987654321", detection.AccountNumber);

        var parsed = _parser.Parse(extraction, detection);
        Assert.True(parsed.Success);
        Assert.Single(parsed.Transactions);
        var tx = parsed.Transactions[0];
        Assert.Equal("NETBANKING TRANSFER CONSULTING FEES", tx.Description);
        Assert.Equal(150000.00m, tx.Credit);
        Assert.Equal(450000.00m, tx.Balance);
        Assert.DoesNotContain("SHARMA", tx.Description);
        Assert.DoesNotContain("KORAMANGALA", tx.Description);
    }

    [Fact]
    public void ScenarioB_DifferentTransactionValues_ParsesDatesAmountsAndReferences()
    {
        var extraction = BuildSyntheticHdfcExtraction(pages =>
        {
            var rows = StandardHdfcHeaderRows(1);
            rows.Add(CreateRow(1, 230.0, [
                ("15/05/26", 35.0),
                ("VENDOR INVOICE PAYMENT INV-2026-991", 75.0),
                ("CMS2938472910", 295.0),
                ("16/05/26", 365.0),
                ("12,345.67", 430.0),
                ("87,654.33", 585.0)
            ]));
            rows.AddRange(StandardHdfcFooterRows(1));
            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        var parsed = _parser.Parse(extraction, detection);

        Assert.True(parsed.Success);
        Assert.Single(parsed.Transactions);
        var tx = parsed.Transactions[0];
        Assert.Equal(new DateTime(2026, 5, 15), tx.TransactionDate);
        Assert.Equal(new DateTime(2026, 5, 16), tx.ValueDate);
        Assert.Equal(12345.67m, tx.Debit);
        Assert.Null(tx.Credit);
        Assert.Equal(87654.33m, tx.Balance);
        Assert.Equal("CMS2938472910", tx.Reference);
    }

    [Fact]
    public void ScenarioC_DifferentNarrationLengths_ReconstructedCorrectly()
    {
        var extraction = BuildSyntheticHdfcExtraction(pages =>
        {
            var rows = StandardHdfcHeaderRows(1);
            // 1. Short narration (3 chars)
            rows.Add(CreateRow(1, 230.0, [
                ("01/06/26", 35.0),
                ("ATM", 75.0),
                ("00001", 295.0),
                ("01/06/26", 365.0),
                ("500.00", 435.0),
                ("99,500.00", 585.0)
            ]));
            // 2. One-line standard narration
            rows.Add(CreateRow(1, 260.0, [
                ("02/06/26", 35.0),
                ("SALARY CREDIT TECH CORP", 75.0),
                ("SAL009988", 295.0),
                ("02/06/26", 365.0),
                ("75,000.00", 515.0),
                ("174,500.00", 585.0)
            ]));
            // 3. Multi-line wrapped narration (2 lines)
            rows.Add(CreateRow(1, 290.0, [
                ("03/06/26", 35.0),
                ("UPI-MERCHANT PAYMENT STORE 1", 75.0),
                ("UPI991122", 295.0),
                ("03/06/26", 365.0),
                ("1,250.00", 435.0),
                ("173,250.00", 585.0)
            ]));
            rows.Add(CreateRow(1, 305.0, [
                ("-TRANSACTION ID 992837198273", 75.0)
            ]));
            // 4. Long wrapped narration (4 lines)
            rows.Add(CreateRow(1, 330.0, [
                ("04/06/26", 35.0),
                ("RTGS DR-CORP CLIENT SERVICES", 75.0),
                ("RTGS11223344", 295.0),
                ("04/06/26", 365.0),
                ("50,000.00", 435.0),
                ("123,250.00", 585.0)
            ]));
            rows.Add(CreateRow(1, 345.0, [
                ("LINE TWO OF NARRATION BENEFICIARY PVT LTD", 75.0)
            ]));
            rows.Add(CreateRow(1, 360.0, [
                ("LINE THREE INVOICE BATCH NO 88921", 75.0)
            ]));
            rows.Add(CreateRow(1, 375.0, [
                ("LINE FOUR SETTLEMENT FINALIZED", 75.0)
            ]));

            rows.AddRange(StandardHdfcFooterRows(1));
            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        var parsed = _parser.Parse(extraction, detection);

        Assert.True(parsed.Success);
        Assert.Equal(4, parsed.Transactions.Count);

        Assert.Equal("ATM", parsed.Transactions[0].Description);
        Assert.Equal("SALARY CREDIT TECH CORP", parsed.Transactions[1].Description);
        Assert.Equal("UPI-MERCHANT PAYMENT STORE 1 -TRANSACTION ID 992837198273", parsed.Transactions[2].Description);
        Assert.Equal("RTGS DR-CORP CLIENT SERVICES LINE TWO OF NARRATION BENEFICIARY PVT LTD LINE THREE INVOICE BATCH NO 88921 LINE FOUR SETTLEMENT FINALIZED", parsed.Transactions[3].Description);
    }

    [Fact]
    public void ScenarioD_DifferentTransactionCounts_SmallAndLargerStatements()
    {
        // Test a statement with 3 transactions and another with 25 transactions
        var smallExtraction = BuildSyntheticHdfcExtraction(pages =>
        {
            var rows = StandardHdfcHeaderRows(1);
            for (int i = 1; i <= 3; i++)
            {
                rows.Add(CreateRow(1, 200.0 + (i * 30.0), [
                    ($"0{i}/07/26", 35.0),
                    ($"TRANSACTION TEST #{i}", 75.0),
                    ($"REF{i:D4}", 295.0),
                    ($"0{i}/07/26", 365.0),
                    ($"{i * 100}.00", 435.0),
                    ($"{10000 - (i * 100)}.00", 585.0)
                ]));
            }
            rows.AddRange(StandardHdfcFooterRows(1));
            pages.Add(CreatePage(1, rows));
        });

        var smallResult = _parser.Parse(smallExtraction, _detector.DetectBank(smallExtraction));
        Assert.True(smallResult.Success);
        Assert.Equal(3, smallResult.Transactions.Count);

        var largerExtraction = BuildSyntheticHdfcExtraction(pages =>
        {
            var rows = StandardHdfcHeaderRows(1);
            for (int i = 1; i <= 25; i++)
            {
                rows.Add(CreateRow(1, 200.0 + (i * 15.0), [
                    ("10/07/26", 35.0),
                    ($"LARGE STATEMENT BATCH #{i}", 75.0),
                    ($"REF{i:D6}", 295.0),
                    ("10/07/26", 365.0),
                    ("50.00", 435.0),
                    ($"{50000 - (i * 50)}.00", 585.0)
                ]));
            }
            rows.AddRange(StandardHdfcFooterRows(1));
            pages.Add(CreatePage(1, rows));
        });

        var largerResult = _parser.Parse(largerExtraction, _detector.DetectBank(largerExtraction));
        Assert.True(largerResult.Success);
        Assert.Equal(25, largerResult.Transactions.Count);
    }

    [Fact]
    public void ScenarioE_PageContinuation_TransactionContinuesAcrossPages()
    {
        var extraction = BuildSyntheticHdfcExtraction(pages =>
        {
            // Page 1: Starts a transaction near the bottom with partial narration
            var p1Rows = StandardHdfcHeaderRows(1);
            p1Rows.Add(CreateRow(1, 750.0, [
                ("15/08/26", 35.0),
                ("UPI-ACME CORP SERVICES INITIAL PART", 75.0),
                ("000099881122", 295.0),
                ("15/08/26", 365.0),
                ("3,450.00", 435.0),
                ("82,100.00", 585.0)
            ]));
            p1Rows.AddRange(StandardHdfcFooterRows(1));
            pages.Add(CreatePage(1, p1Rows));

            // Page 2: Header rows, then continuation of the same transaction, then another transaction
            var p2Rows = StandardHdfcHeaderRows(2);
            p2Rows.Add(CreateRow(2, 230.0, [
                ("CONTINUATION SECOND PART FROM PREVIOUS PAGE", 75.0)
            ]));
            p2Rows.Add(CreateRow(2, 260.0, [
                ("16/08/26", 35.0),
                ("NEXT INDEPENDENT TRANSACTION", 75.0),
                ("000099881123", 295.0),
                ("16/08/26", 365.0),
                ("1,000.00", 515.0),
                ("83,100.00", 585.0)
            ]));
            p2Rows.AddRange(StandardHdfcFooterRows(2));
            pages.Add(CreatePage(2, p2Rows));
        });

        var detection = _detector.DetectBank(extraction);
        var parsed = _parser.Parse(extraction, detection);

        Assert.True(parsed.Success);
        Assert.Equal(2, parsed.Transactions.Count);

        var crossTx = parsed.Transactions[0];
        Assert.Equal("UPI-ACME CORP SERVICES INITIAL PART CONTINUATION SECOND PART FROM PREVIOUS PAGE", crossTx.Description);
        Assert.Equal(3450.00m, crossTx.Debit);
        Assert.Equal(82100.00m, crossTx.Balance);
        Assert.DoesNotContain("DATTAWADI", crossTx.Description);
        Assert.DoesNotContain("Cust ID", crossTx.Description);
    }

    [Fact]
    public void ScenarioF_RepeatedPageHeaders_ExcludedFromNarration()
    {
        var extraction = BuildSyntheticHdfcExtraction(pages =>
        {
            var p1Rows = StandardHdfcHeaderRows(1);
            p1Rows.Add(CreateRow(1, 230.0, [
                ("20/08/26", 35.0),
                ("STANDARD ROW BEFORE BREAK", 75.0),
                ("REF01", 295.0),
                ("20/08/26", 365.0),
                ("100.00", 435.0),
                ("5,000.00", 585.0)
            ]));
            pages.Add(CreatePage(1, p1Rows));

            var p2Rows = StandardHdfcHeaderRows(2);
            p2Rows.Add(CreateRow(2, 230.0, [
                ("21/08/26", 35.0),
                ("STANDARD ROW AFTER BREAK", 75.0),
                ("REF02", 295.0),
                ("21/08/26", 365.0),
                ("200.00", 435.0),
                ("4,800.00", 585.0)
            ]));
            pages.Add(CreatePage(2, p2Rows));
        });

        var detection = _detector.DetectBank(extraction);
        var parsed = _parser.Parse(extraction, detection);

        Assert.True(parsed.Success);
        Assert.Equal(2, parsed.Transactions.Count);

        foreach (var tx in parsed.Transactions)
        {
            Assert.DoesNotContain("Page No", tx.Description);
            Assert.DoesNotContain("Account Branch", tx.Description);
            Assert.DoesNotContain("RTGS/NEFT IFSC", tx.Description);
            Assert.DoesNotContain("Statement of account", tx.Description);
        }
    }

    [Fact]
    public void ScenarioG_FooterAndLegalContent_ExcludedFromNarration()
    {
        var extraction = BuildSyntheticHdfcExtraction(pages =>
        {
            var rows = StandardHdfcHeaderRows(1);
            rows.Add(CreateRow(1, 230.0, [
                ("25/08/26", 35.0),
                ("FINAL TRANSACTION OF STATEMENT", 75.0),
                ("REF999", 295.0),
                ("25/08/26", 365.0),
                ("500.00", 515.0),
                ("10,500.00", 585.0)
            ]));
            rows.AddRange(StandardHdfcFooterRows(1));
            rows.Add(CreateRow(1, 840.0, [("GSTIN", 35.0), ("27AAACH2702H1Z1", 80.0), ("State", 180.0), ("account", 215.0), ("branch", 255.0), ("GSTN", 295.0)]));
            rows.Add(CreateRow(1, 850.0, [("Senapati", 35.0), ("Bapat", 85.0), ("Marg", 120.0), ("Lower", 155.0), ("Parel", 190.0)]));
            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        var parsed = _parser.Parse(extraction, detection);

        Assert.True(parsed.Success);
        Assert.Single(parsed.Transactions);

        var tx = parsed.Transactions[0];
        Assert.Equal("FINAL TRANSACTION OF STATEMENT", tx.Description);
        Assert.DoesNotContain("HDFC BANK LIMITED", tx.Description);
        Assert.DoesNotContain("Closing balance includes", tx.Description);
        Assert.DoesNotContain("Senapati Bapat", tx.Description);
        Assert.DoesNotContain("GSTIN", tx.Description);
    }

    [Fact]
    public void ScenarioH_TwoTransactionsWithSameDate_RemainTwoTransactions()
    {
        var extraction = BuildSyntheticHdfcExtraction(pages =>
        {
            var rows = StandardHdfcHeaderRows(1);
            rows.Add(CreateRow(1, 230.0, [
                ("10/09/26", 35.0),
                ("MORNING COFFEE SHOP", 75.0),
                ("UPI0001", 295.0),
                ("10/09/26", 365.0),
                ("150.00", 435.0),
                ("24,850.00", 585.0)
            ]));
            rows.Add(CreateRow(1, 260.0, [
                ("10/09/26", 35.0),
                ("AFTERNOON BOOKSTORE", 75.0),
                ("UPI0002", 295.0),
                ("10/09/26", 365.0),
                ("850.00", 435.0),
                ("24,000.00", 585.0)
            ]));
            rows.AddRange(StandardHdfcFooterRows(1));
            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        var parsed = _parser.Parse(extraction, detection);

        Assert.True(parsed.Success);
        Assert.Equal(2, parsed.Transactions.Count);

        Assert.Equal(new DateTime(2026, 9, 10), parsed.Transactions[0].TransactionDate);
        Assert.Equal("MORNING COFFEE SHOP", parsed.Transactions[0].Description);
        Assert.Equal(150.00m, parsed.Transactions[0].Debit);

        Assert.Equal(new DateTime(2026, 9, 10), parsed.Transactions[1].TransactionDate);
        Assert.Equal("AFTERNOON BOOKSTORE", parsed.Transactions[1].Description);
        Assert.Equal(850.00m, parsed.Transactions[1].Debit);
    }

    [Fact]
    public void ScenarioI_AmountSeparation_DebitCreditAndBalanceColumnsCorrectlySeparated()
    {
        var extraction = BuildSyntheticHdfcExtraction(pages =>
        {
            var rows = StandardHdfcHeaderRows(1);
            // Debit row
            rows.Add(CreateRow(1, 230.0, [
                ("12/09/26", 35.0),
                ("ONLINE BILL PAYMENT", 75.0),
                ("REF_DEBIT", 295.0),
                ("12/09/26", 365.0),
                ("2,500.00", 435.0),
                ("47,500.00", 585.0)
            ]));
            // Credit row
            rows.Add(CreateRow(1, 260.0, [
                ("13/09/26", 35.0),
                ("INTEREST CREDIT", 75.0),
                ("REF_CREDIT", 295.0),
                ("13/09/26", 365.0),
                ("1,200.00", 515.0),
                ("48,700.00", 585.0)
            ]));
            rows.AddRange(StandardHdfcFooterRows(1));
            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        var parsed = _parser.Parse(extraction, detection);

        Assert.True(parsed.Success);
        Assert.Equal(2, parsed.Transactions.Count);

        var drTx = parsed.Transactions[0];
        Assert.Equal(2500.00m, drTx.Debit);
        Assert.Null(drTx.Credit);
        Assert.Equal(2500.00m, drTx.Amount);
        Assert.Equal("Debit", drTx.TransactionType);
        Assert.Equal(47500.00m, drTx.Balance);

        var crTx = parsed.Transactions[1];
        Assert.Null(crTx.Debit);
        Assert.Equal(1200.00m, crTx.Credit);
        Assert.Equal(1200.00m, crTx.Amount);
        Assert.Equal("Credit", crTx.TransactionType);
        Assert.Equal(48700.00m, crTx.Balance);
    }

    [Fact]
    public void ScenarioJ_UnknownOrNonHdfcStatement_NotClassifiedAsHdfc()
    {
        var extraction = new PdfExtractionResult
        {
            FileId = Guid.NewGuid(),
            OriginalFileName = "UnknownBank_Statement.pdf",
            PageCount = 1,
            HasUsableText = true,
            PdfType = "DigitalWithText",
            Pages =
            [
                CreatePage(1, [
                    CreateRow(1, 50.0, [("STATE", 35.0), ("BANK", 80.0), ("OF", 120.0), ("INDIA", 145.0)]),
                    CreateRow(1, 100.0, [("Account", 35.0), ("Statement", 85.0), ("Account", 145.0), ("Number:", 190.0), ("12345678901", 240.0)]),
                    CreateRow(1, 150.0, [("Txn", 35.0), ("Date", 70.0), ("Description", 110.0), ("Ref", 200.0), ("Debit", 250.0), ("Credit", 300.0), ("Balance", 350.0)]),
                    CreateRow(1, 180.0, [("01/01/26", 35.0), ("OPENING BALANCE", 110.0), ("10000.00", 350.0)])
                ])
            ]
        };

        var detection = _detector.DetectBank(extraction);
        Assert.False(detection.IsSupported);
        Assert.NotEqual(BankType.Hdfc, detection.DetectedBank);
        Assert.Equal("Unknown", detection.BankName);
    }

    [Fact]
    public void ScenarioK_HdfcStatementDifferentMetadataFromTestPdfs_DetectedAndParsed()
    {
        // Metadata contains labels and format variations entirely distinct from Statement 1 & 2
        var extraction = BuildSyntheticHdfcExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>
            {
                CreateRow(1, 20.0, [("Page", 40.0), ("No", 65.0), (".:", 80.0), ("1", 95.0), ("Statement", 130.0), ("of", 185.0), ("account", 200.0)]),
                CreateRow(1, 35.0, [("HDFC", 40.0), ("BANK", 75.0), ("LIMITED", 115.0)]),
                CreateRow(1, 50.0, [("Customer", 40.0), ("Name:", 90.0), ("ZENITH", 130.0), ("LOGISTICS", 175.0), ("LLP", 235.0)]),
                CreateRow(1, 65.0, [("Account", 40.0), ("Branch", 85.0), (":", 125.0), ("FORT", 135.0), ("MUMBAI", 170.0)]),
                CreateRow(1, 80.0, [("Cust", 40.0), ("ID", 65.0), (":", 80.0), ("77665544", 90.0), ("Account", 180.0), ("No", 220.0), (":", 240.0), ("50200088192831", 250.0)]),
                CreateRow(1, 95.0, [("RTGS/NEFT", 40.0), ("IFSC", 95.0), (":", 120.0), ("HDFC0000060", 130.0), ("Branch", 220.0), ("Code", 255.0), (":", 280.0), ("0060", 290.0)]),
                CreateRow(1, 110.0, [("Statement", 40.0), ("From", 90.0), (":", 120.0), ("01/10/2026", 130.0), ("To", 195.0), (":", 215.0), ("15/10/2026", 225.0)]),
                CreateRow(1, 140.0, [
                    ("Date", 40.0),
                    ("Narration", 150.0),
                    ("Chq./Ref.No.", 290.0),
                    ("Value", 360.0), ("Dt", 385.0),
                    ("Withdrawal", 410.0), ("Amt.", 450.0),
                    ("Deposit", 490.0), ("Amt.", 525.0),
                    ("Closing", 565.0), ("Balance", 595.0)
                ]),
                CreateRow(1, 170.0, [
                    ("02/10/26", 35.0),
                    ("WAREHOUSE LEASE OCT 2026", 75.0),
                    ("LEASE882910", 295.0),
                    ("02/10/26", 365.0),
                    ("85,000.00", 435.0),
                    ("315,000.00", 585.0)
                ]),
                CreateRow(1, 200.0, [
                    ("05/10/26", 35.0),
                    ("FREIGHT REIMBURSEMENT CLIENT", 75.0),
                    ("FRT112299", 295.0),
                    ("05/10/26", 365.0),
                    ("42,500.00", 515.0),
                    ("357,500.00", 585.0)
                ]),
                CreateRow(1, 790.0, [("HDFC", 35.0), ("BANK", 65.0), ("LIMITED", 100.0)]),
                CreateRow(1, 805.0, [("Registered", 35.0), ("Office:", 85.0), ("HDFC", 125.0), ("Bank", 155.0), ("House", 185.0)])
            };
            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        Assert.True(detection.IsSupported);
        Assert.Equal(BankType.Hdfc, detection.DetectedBank);
        Assert.Equal("50200088192831", detection.AccountNumber);

        var parsed = _parser.Parse(extraction, detection);
        Assert.True(parsed.Success);
        Assert.Equal(2, parsed.Transactions.Count);
        Assert.Equal(85000.00m, parsed.Transactions[0].Debit);
        Assert.Equal(42500.00m, parsed.Transactions[1].Credit);
        Assert.Equal(357500.00m, parsed.Transactions[1].Balance);
    }

    #endregion

    #region RegressionTests_Hdfc159Page_And_FooterHeuristic

    [Fact]
    public void RegressionA_CommonShortWord_RepeatedAcrossPages_NotClassifiedAsFooter()
    {
        // A. Common short word repeated on multiple pages is NOT a footer.
        // Even if words like "PHONE", "ONE", "M PHONE" appear at the bottom across 10 pages,
        // they must NOT become repeated footer signatures.
        var pages = new List<PdfPageResult>();
        for (int p = 1; p <= 10; p++)
        {
            var rows = new List<PdfCandidateRow>
            {
                CreateRow(p, 50.0, [("HDFC", 35.0), ("BANK", 80.0)]),
                CreateRow(p, 200.0, [("01/01/26", 35.0), ("TRANSFER TO STORE", 75.0), ("1000.00", 450.0), ("50000.00", 580.0)]),
                CreateRow(p, 790.0, [("PHONE", 35.0)]),
                CreateRow(p, 800.0, [("ONE", 35.0)]),
                CreateRow(p, 810.0, [("M", 35.0), ("PHONE", 55.0)])
            };
            pages.Add(CreatePage(p, rows));
        }

        var method = typeof(PdfExtractionService).GetMethod("DetectRepeatedHeadersAndFooters",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        method.Invoke(null, [pages]);

        foreach (var page in pages)
        {
            Assert.DoesNotContain("PHONE", page.RepeatedFooters);
            Assert.DoesNotContain("ONE", page.RepeatedFooters);
            Assert.DoesNotContain("M PHONE", page.RepeatedFooters);
            Assert.Empty(page.RepeatedFooters);
        }
    }

    [Fact]
    public void RegressionB_TransactionNarration_ContainingPhoneOrOne_DoesNotTerminateParsing()
    {
        // B. Transaction narration containing a common footer-like word is NOT terminated.
        var extraction = BuildSyntheticHdfcExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>
            {
                CreateRow(1, 20.0, [("Page", 40.0), ("No", 65.0), (".:", 80.0), ("1", 95.0)]),
                CreateRow(1, 35.0, [("HDFC", 40.0), ("BANK", 75.0), ("LIMITED", 115.0)]),
                CreateRow(1, 50.0, [("Customer", 40.0), ("Name:", 90.0), ("TEST", 130.0), ("USER", 175.0)]),
                CreateRow(1, 80.0, [("Account", 40.0), ("No", 85.0), (":", 105.0), ("50100999888777", 115.0)]),
                CreateRow(1, 140.0, [
                    ("Date", 40.0), ("Narration", 150.0), ("Chq./Ref.No.", 290.0),
                    ("Value", 360.0), ("Dt", 385.0), ("Withdrawal", 410.0), ("Amt.", 450.0),
                    ("Deposit", 490.0), ("Amt.", 525.0), ("Closing", 565.0), ("Balance", 595.0)
                ]),
                CreateRow(1, 200.0, [
                    ("01/01/26", 35.0), ("PAYMENT FROM PHONE TO GROCERY", 75.0), ("REF001", 295.0),
                    ("01/01/26", 365.0), ("500.00", 435.0), ("49,500.00", 585.0)
                ]),
                CreateRow(1, 300.0, [
                    ("02/01/26", 35.0), ("TRANSFER TO ONE VENDOR", 75.0), ("REF002", 295.0),
                    ("02/01/26", 365.0), ("1,000.00", 435.0), ("48,500.00", 585.0)
                ]),
                CreateRow(1, 400.0, [
                    ("03/01/26", 35.0), ("SUBSEQUENT TRANSACTION AFTER PHONE", 75.0), ("REF003", 295.0),
                    ("03/01/26", 365.0), ("200.00", 435.0), ("48,300.00", 585.0)
                ]),
                CreateRow(1, 810.0, [("HDFC", 35.0), ("BANK", 65.0), ("LIMITED", 100.0)])
            };
            var page = CreatePage(1, rows);
            // Simulate unsafe repeated footer list if it had contained "PHONE" or "ONE"
            page.RepeatedFooters.Add("PHONE");
            page.RepeatedFooters.Add("ONE");
            pages.Add(page);
        });

        var detection = _detector.DetectBank(extraction);
        Assert.True(detection.IsSupported);

        var parsed = _parser.Parse(extraction, detection);
        Assert.True(parsed.Success);
        Assert.Equal(3, parsed.Transactions.Count);
        Assert.Equal("PAYMENT FROM PHONE TO GROCERY", parsed.Transactions[0].Description);
        Assert.Equal("TRANSFER TO ONE VENDOR", parsed.Transactions[1].Description);
        Assert.Equal("SUBSEQUENT TRANSACTION AFTER PHONE", parsed.Transactions[2].Description);
    }

    [Fact]
    public void RegressionC_CustomerMetadata_ContainingPhone_NotTreatedAsFooter()
    {
        // C. Customer metadata containing PHONE is NOT treated as transaction footer.
        var extraction = BuildSyntheticHdfcExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>
            {
                CreateRow(1, 20.0, [("Page", 40.0), ("No", 65.0), (".:", 80.0), ("1", 95.0)]),
                CreateRow(1, 35.0, [("HDFC", 40.0), ("BANK", 75.0), ("LIMITED", 115.0)]),
                CreateRow(1, 50.0, [("Customer", 40.0), ("Name:", 90.0), ("VIPUL", 130.0), ("ENTERPRISES", 175.0)]),
                CreateRow(1, 65.0, [("Account", 40.0), ("No", 85.0), (":", 105.0), ("50200011223344", 115.0)]),
                CreateRow(1, 80.0, [("Phone", 40.0), ("no.", 75.0), (":", 95.0), ("9876543210", 105.0)]),
                CreateRow(1, 140.0, [
                    ("Date", 40.0), ("Narration", 150.0), ("Chq./Ref.No.", 290.0),
                    ("Value", 360.0), ("Dt", 385.0), ("Withdrawal", 410.0), ("Amt.", 450.0),
                    ("Deposit", 490.0), ("Amt.", 525.0), ("Closing", 565.0), ("Balance", 595.0)
                ]),
                CreateRow(1, 200.0, [
                    ("10/01/26", 35.0), ("CLIENT PAYMENT RECEIVED", 75.0), ("REF8811", 295.0),
                    ("10/01/26", 365.0), ("15,000.00", 515.0), ("65,000.00", 585.0)
                ]),
                CreateRow(1, 810.0, [("HDFC", 35.0), ("BANK", 65.0), ("LIMITED", 100.0)])
            };
            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        Assert.True(detection.IsSupported);

        var parsed = _parser.Parse(extraction, detection);
        Assert.True(parsed.Success);
        Assert.Single(parsed.Transactions);
        Assert.Equal(15000.00m, parsed.Transactions[0].Credit);
    }

    [Fact]
    public void RegressionD_ActualRepeatedLegalFooter_IsDetectedAndFiltered()
    {
        // D. Actual repeated legal footer is still detected and filtered.
        var pages = new List<PdfPageResult>();
        for (int p = 1; p <= 5; p++)
        {
            var rows = new List<PdfCandidateRow>
            {
                CreateRow(p, 50.0, [("HDFC", 35.0), ("BANK", 80.0)]),
                CreateRow(p, 200.0, [("01/01/26", 35.0), ($"TRANSACTION {p}", 75.0), ("100.00", 450.0), ("1000.00", 580.0)]),
                CreateRow(p, 800.0, [("HDFC BANK LIMITED *Closing balance includes funds earmarked for hold", 35.0)]),
                CreateRow(p, 815.0, [("Registered Office Address: Senapati Bapat Marg Lower Parel Mumbai", 35.0)])
            };
            pages.Add(CreatePage(p, rows));
        }

        var method = typeof(PdfExtractionService).GetMethod("DetectRepeatedHeadersAndFooters",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        method.Invoke(null, [pages]);

        foreach (var page in pages)
        {
            Assert.Contains(page.RepeatedFooters, f => f.Contains("HDFC BANK LIMITED"));
            Assert.Contains(page.RepeatedFooters, f => f.Contains("Registered Office Address"));
        }
    }

    [Fact]
    public void RegressionE_FooterDetection_OnlyTerminatesInLegitimateFooterRegion()
    {
        // E. Footer detection only terminates parsing in the legitimate footer region (line.Y >= page.Height * 0.75).
        // If a transaction line happens to have text matching a footer signature but is at Y = 250 (inside table),
        // it must NOT trigger page footer termination.
        var extraction = BuildSyntheticHdfcExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>
            {
                CreateRow(1, 20.0, [("Page", 40.0), ("No", 65.0), (".:", 80.0), ("1", 95.0)]),
                CreateRow(1, 35.0, [("HDFC", 40.0), ("BANK", 75.0), ("LIMITED", 115.0)]),
                CreateRow(1, 50.0, [("Customer", 40.0), ("Name:", 90.0), ("TEST", 130.0), ("ACCOUNT", 175.0)]),
                CreateRow(1, 80.0, [("Account", 40.0), ("No", 85.0), (":", 105.0), ("50100223344556", 115.0)]),
                CreateRow(1, 140.0, [
                    ("Date", 40.0), ("Narration", 150.0), ("Chq./Ref.No.", 290.0),
                    ("Value", 360.0), ("Dt", 385.0), ("Withdrawal", 410.0), ("Amt.", 450.0),
                    ("Deposit", 490.0), ("Amt.", 525.0), ("Closing", 565.0), ("Balance", 595.0)
                ]),
                CreateRow(1, 200.0, [
                    ("05/01/26", 35.0), ("REGULAR TXN BEFORE FOOTER-LIKE STRING", 75.0), ("REF01", 295.0),
                    ("05/01/26", 365.0), ("300.00", 435.0), ("20,000.00", 585.0)
                ]),
                // Line at Y = 250 with footer-like text
                CreateRow(1, 250.0, [
                    ("06/01/26", 35.0), ("CONSIDERED CORRECT GOODS AND SERVICE TAX INVOICE", 75.0), ("REF02", 295.0),
                    ("06/01/26", 365.0), ("500.00", 435.0), ("19,500.00", 585.0)
                ]),
                CreateRow(1, 320.0, [
                    ("07/01/26", 35.0), ("TXN AFTER FOOTER-LIKE STRING", 75.0), ("REF03", 295.0),
                    ("07/01/26", 365.0), ("1,000.00", 435.0), ("18,500.00", 585.0)
                ]),
                CreateRow(1, 810.0, [("HDFC", 35.0), ("BANK", 65.0), ("LIMITED", 100.0)])
            };
            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        var parsed = _parser.Parse(extraction, detection);

        Assert.True(parsed.Success);
        Assert.Equal(3, parsed.Transactions.Count);
        Assert.Equal("TXN AFTER FOOTER-LIKE STRING", parsed.Transactions[2].Description);
    }

    [Fact]
    public void RegressionF_CrossPageNarration_PreservedAcrossBoundary()
    {
        // F. Cross-page narration remains intact (Page 63 -> Page 64 example).
        var extraction = BuildSyntheticHdfcExtraction(pages =>
        {
            // Page 1: ends with a transaction that spans to page 2
            var p1Rows = new List<PdfCandidateRow>
            {
                CreateRow(1, 20.0, [("Page", 40.0), ("No", 65.0), (".:", 80.0), ("63", 95.0)]),
                CreateRow(1, 35.0, [("HDFC", 40.0), ("BANK", 75.0), ("LIMITED", 115.0)]),
                CreateRow(1, 50.0, [("Customer", 40.0), ("Name:", 90.0), ("TEST", 130.0), ("CLIENT", 175.0)]),
                CreateRow(1, 80.0, [("Account", 40.0), ("No", 85.0), (":", 105.0), ("50100445566778", 115.0)]),
                CreateRow(1, 140.0, [
                    ("Date", 40.0), ("Narration", 150.0), ("Chq./Ref.No.", 290.0),
                    ("Value", 360.0), ("Dt", 385.0), ("Withdrawal", 410.0), ("Amt.", 450.0),
                    ("Deposit", 490.0), ("Amt.", 525.0), ("Closing", 565.0), ("Balance", 595.0)
                ]),
                CreateRow(1, 720.0, [
                    ("26/03/26", 35.0), ("UPI-ABDUL MUJIB ABDUL", 75.0), ("REF992211", 295.0),
                    ("26/03/26", 365.0), ("2,000.00", 435.0), ("80,000.00", 585.0)
                ]),
                CreateRow(1, 735.0, [("SA-MUJIBAS@OKSBI-I", 75.0)]),
                CreateRow(1, 810.0, [("HDFC", 35.0), ("BANK", 65.0), ("LIMITED", 100.0)])
            };
            pages.Add(CreatePage(1, p1Rows));

            // Page 2: starts with continuation fragment, then next transaction date
            var p2Rows = new List<PdfCandidateRow>
            {
                CreateRow(2, 20.0, [("Page", 40.0), ("No", 65.0), (".:", 80.0), ("64", 95.0)]),
                CreateRow(2, 35.0, [("HDFC", 40.0), ("BANK", 75.0), ("LIMITED", 115.0)]),
                CreateRow(2, 50.0, [("Customer", 40.0), ("Name:", 90.0), ("TEST", 130.0), ("CLIENT", 175.0)]),
                CreateRow(2, 80.0, [("Account", 40.0), ("No", 85.0), (":", 105.0), ("50100445566778", 115.0)]),
                CreateRow(2, 140.0, [
                    ("Date", 40.0), ("Narration", 150.0), ("Chq./Ref.No.", 290.0),
                    ("Value", 360.0), ("Dt", 385.0), ("Withdrawal", 410.0), ("Amt.", 450.0),
                    ("Deposit", 490.0), ("Amt.", 525.0), ("Closing", 565.0), ("Balance", 595.0)
                ]),
                // Continuation fragment before next date
                CreateRow(2, 170.0, [("BKL0000041-608549247059-ST", 75.0)]),
                // Next transaction date
                CreateRow(2, 210.0, [
                    ("27/03/26", 35.0), ("NEXT TRANSACTION", 75.0), ("REF992212", 295.0),
                    ("27/03/26", 365.0), ("500.00", 435.0), ("79,500.00", 585.0)
                ]),
                CreateRow(2, 810.0, [("HDFC", 35.0), ("BANK", 65.0), ("LIMITED", 100.0)])
            };
            pages.Add(CreatePage(2, p2Rows));
        });

        var detection = _detector.DetectBank(extraction);
        var parsed = _parser.Parse(extraction, detection);

        Assert.True(parsed.Success);
        Assert.Equal(2, parsed.Transactions.Count);

        var firstTx = parsed.Transactions[0];
        Assert.Equal("UPI-ABDUL MUJIB ABDUL SA-MUJIBAS@OKSBI-I BKL0000041-608549247059-ST", firstTx.Description);
        Assert.Equal(new DateTime(2026, 3, 26), firstTx.TransactionDate);
        Assert.Equal(2000.00m, firstTx.Debit);

        var secondTx = parsed.Transactions[1];
        Assert.Equal("NEXT TRANSACTION", secondTx.Description);
        Assert.Equal(new DateTime(2026, 3, 27), secondTx.TransactionDate);
        Assert.Equal(500.00m, secondTx.Debit);
    }

    [Fact]
    public async Task RegressionG_RealPdf_Hdfc159Page_RecoversAll1801Transactions_FullPipelineVerification()
    {
        string path = @"E:\Bank Statements\New 07-08-2026\HDFC-0371  PASS-4750116.pdf";
        if (!File.Exists(path))
        {
            _output.WriteLine($"[SKIPPED] File not found: {path}");
            return;
        }

        using var doc = PdfDocument.Open(path, new ParsingOptions { Password = "4750116", ClipPaths = false });
        Assert.Equal(159, doc.NumberOfPages);

        var dummyFile = new FileRecord
        {
            Id = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            OriginalFileName = Path.GetFileName(path),
            StoredFileName = Path.GetFileName(path)
        };

        var method = typeof(PdfExtractionService).GetMethod("ProcessPdfDocument",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var stopwatch = new System.Diagnostics.Stopwatch();
        stopwatch.Start();
        var extraction = (PdfExtractionResult)method.Invoke(null, [doc, dummyFile, DateTime.UtcNow, stopwatch])!;
        Assert.NotNull(extraction);
        Assert.Equal(159, extraction.PageCount);

        // Count genuine source transaction starts in extraction
        var dateRegex = new System.Text.RegularExpressions.Regex(@"^\d{2}/\d{2}/\d{2,4}$", System.Text.RegularExpressions.RegexOptions.Compiled);
        int totalSourceStarts = 0;
        var sourceStartsByPage = new Dictionary<int, int>();
        foreach (var page in extraction.Pages)
        {
            int pageStarts = 0;
            foreach (var row in page.CandidateRows)
            {
                var ft = row.LineFragments.OrderBy(f => f.X).FirstOrDefault();
                if (ft != null && ft.X < 55.0 && dateRegex.IsMatch(ft.Text.Trim()))
                {
                    pageStarts++;
                }
            }
            sourceStartsByPage[page.PageNumber] = pageStarts;
            totalSourceStarts += pageStarts;
        }

        _output.WriteLine($"Total Source Transaction Starts in Document: {totalSourceStarts}");
        Assert.Equal(1801, totalSourceStarts);

        // 1. Bank Detection
        var detection = _detector.DetectBank(extraction);
        Assert.True(detection.IsSupported);
        Assert.Equal(BankType.Hdfc, detection.DetectedBank);
        Assert.Equal("01028130000371", detection.AccountNumber);
        _output.WriteLine($"Detected Bank: {detection.BankName}, Account: {detection.AccountNumber}, Confidence: {detection.Confidence:P0}");

        // 2. Parser Execution
        var result = _parser.Parse(extraction, detection);
        Assert.True(result.Success);
        _output.WriteLine($"Total Transactions Parsed: {result.Transactions.Count}");
        Assert.Equal(1801, result.Transactions.Count);

        // Group parsed transactions by source page
        var parsedByPage = result.Transactions
            .GroupBy(t => t.SourcePageNumber)
            .ToDictionary(g => g.Key, g => g.Count());

        int totalMissing = 0;
        _output.WriteLine("\n--- PAGE-BY-PAGE VALIDATION (159 Pages) ---");
        _output.WriteLine(string.Format("{0,-6} | {1,-14} | {2,-19} | {3,-8} | {4}", "Page", "Source Starts", "Parsed Transactions", "Missing", "Status"));
        _output.WriteLine(new string('-', 65));

        for (int p = 1; p <= 159; p++)
        {
            int src = sourceStartsByPage.GetValueOrDefault(p, 0);
            int prs = parsedByPage.GetValueOrDefault(p, 0);
            int diff = src - prs;
            if (diff > 0) totalMissing += diff;

            string status = diff == 0 ? "OK" : $"MISSING {diff}";
            _output.WriteLine(string.Format("{0,-6} | {1,-14} | {2,-19} | {3,-8} | {4}", p, src, prs, diff, status));
        }

        _output.WriteLine(new string('-', 65));
        _output.WriteLine($"Total Source Starts: {totalSourceStarts}, Total Parsed: {result.Transactions.Count}, Total Missing: {totalMissing}");
        Assert.Equal(0, totalMissing);

        // Detailed check of previously failing pages
        int[] previouslyFailedPages = [43, 49, 51, 57, 65, 68, 75, 80, 86, 95, 111, 118, 132, 137, 146, 148, 150];
        foreach (var p in previouslyFailedPages)
        {
            int src = sourceStartsByPage[p];
            int prs = parsedByPage[p];
            Assert.True(prs > 0, $"Page {p} should have parsed transactions");
            Assert.Equal(src, prs);
            _output.WriteLine($"Verified Recovered Page {p}: Source={src}, Parsed={prs}, Missing=0");
        }

        // 3. Verify Running Balance Reconciliation
        int balanceMatches = 0;
        for (int i = 1; i < result.Transactions.Count; i++)
        {
            var prev = result.Transactions[i - 1];
            var curr = result.Transactions[i];
            if (prev.Balance.HasValue && curr.Balance.HasValue)
            {
                decimal expected = prev.Balance.Value - (curr.Debit ?? 0m) + (curr.Credit ?? 0m);
                if (Math.Abs(expected - curr.Balance.Value) <= 0.01m)
                {
                    balanceMatches++;
                }
            }
        }
        _output.WriteLine($"Running Balance Matches: {balanceMatches}/{result.Transactions.Count - 1}");

        // 4. Test Phase 5 Validation Pipeline
        var validationService = new TransactionValidationService();
        var entityTxs = result.Transactions.Select(tx => new Transaction
        {
            Id = tx.Id,
            TransactionDate = tx.TransactionDate,
            Description = tx.Description,
            Debit = tx.Debit,
            Credit = tx.Credit,
            Amount = tx.Amount,
            Balance = tx.Balance,
            Reference = tx.Reference,
            Utr = tx.Utr,
            TransactionType = tx.TransactionType,
            BankCode = tx.BankCode,
            Account = detection.AccountNumber,
            SourceFileId = dummyFile.Id,
            ClientId = dummyFile.ClientId,
            FinancialYearId = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow
        }).ToList();

        var enrichedTxs = validationService.ValidateAndEnrichStatement(entityTxs, null);
        Assert.Equal(1801, enrichedTxs.Count);
        int invalidCount = enrichedTxs.Count(t => t.ValidationStatus == "INVALID");
        _output.WriteLine($"Validation Pipeline: Total={enrichedTxs.Count}, Invalid={invalidCount}, Valid/Review={enrichedTxs.Count - invalidCount}");
        Assert.Equal(0, invalidCount);

        // 5. Test Phase 6 Excel Export
        var exportService = new ExcelExportService(NullLogger<ExcelExportService>.Instance);
        var summary = validationService.ComputeSummary(dummyFile.Id, 1, "HDFC Bank", "HDFC-v1", enrichedTxs);
        var excelBytes = await exportService.GenerateStatementWorkbookAsync(dummyFile, summary, enrichedTxs);
        Assert.NotNull(excelBytes);
        Assert.True(excelBytes.Length > 10000, $"Excel size: {excelBytes.Length} bytes");

        using var excelStream = new MemoryStream(excelBytes);
        using var workbook = new XLWorkbook(excelStream);
        var txSheet = workbook.Worksheet("Transactions");
        Assert.NotNull(txSheet);
        int lastRow = txSheet.LastRowUsed()?.RowNumber() ?? 0;
        _output.WriteLine($"Excel Export: Last Row = {lastRow} (1 header + 1801 transactions = 1802 expected)");
        Assert.Equal(1802, lastRow);

        // Verify representative recovered rows in Excel
        var txP43 = result.Transactions.First(t => t.SourcePageNumber == 43);
        Assert.Contains(enrichedTxs, t => t.Id == txP43.Id);

        var txP111 = result.Transactions.First(t => t.SourcePageNumber == 111);
        Assert.Contains(enrichedTxs, t => t.Id == txP111.Id);

        var txP150 = result.Transactions.First(t => t.SourcePageNumber == 150);
        Assert.Contains(enrichedTxs, t => t.Id == txP150.Id);
    }

    #endregion
}
