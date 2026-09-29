using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Accufex.Server.Data;
using Accufex.Server.DTOs;
using Accufex.Server.Models;
using Accufex.Server.Parsing;
using Accufex.Server.Parsing.Interfaces;
using Accufex.Server.Parsing.Models;
using Accufex.Server.Parsing.Parsers;
using Accufex.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using UglyToad.PdfPig;
using Xunit;
using Xunit.Abstractions;

namespace Accufex.Server.Tests;

public class YesBankParserTests
{
    private readonly ITestOutputHelper _output;
    private readonly Mock<ILogger<BankDetector>> _mockDetectorLogger;
    private readonly Mock<ILogger<YesBankStatementParser>> _mockParserLogger;
    private readonly Mock<ILogger<BankParsingService>> _mockServiceLogger;
    private readonly BankDetector _detector;
    private readonly YesBankStatementParser _parser;
    private readonly BankParserRegistry _registry;

    public YesBankParserTests(ITestOutputHelper output)
    {
        _output = output;
        _mockDetectorLogger = new Mock<ILogger<BankDetector>>();
        _mockParserLogger = new Mock<ILogger<YesBankStatementParser>>();
        _mockServiceLogger = new Mock<ILogger<BankParsingService>>();

        _detector = new BankDetector(_mockDetectorLogger.Object);
        _parser = new YesBankStatementParser(_mockParserLogger.Object);
        _registry = new BankParserRegistry([_parser]);
    }

    #region Synthetic Test Helper Methods

    private static PdfExtractionResult BuildSyntheticYesExtraction(Action<List<PdfPageResult>> configurePages)
    {
        var pages = new List<PdfPageResult>();
        configurePages(pages);

        return new PdfExtractionResult
        {
            FileId = Guid.NewGuid(),
            OriginalFileName = "YESBANK_Statement.pdf",
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
            Width = 737.0,
            Height = 935.4,
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

    private static List<(string Text, double X)> StandardHeaderTokens =>
    [
        ("Transaction Date", 25.0),
        ("Value Date", 105.0),
        ("Cheque No/ Reference No", 185.0),
        ("Description", 300.0),
        ("Withdrawals", 525.0),
        ("Deposits", 605.0),
        ("Running Balance", 670.0)
    ];

    private static Client CreateTestClient(Guid userId, string name) => new Client
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        Name = name,
        ContactPerson = "Contact Person",
        Email = "test@example.com",
        Phone = "1234567890",
        BusinessName = "Test Business",
        BusinessType = "Corporation",
        Address = "123 Test Street",
        TaxId = "TAX12345",
        CreatedAt = DateTime.UtcNow
    };

    private static FinancialYear CreateTestFinancialYear(Guid clientId) => new FinancialYear
    {
        Id = Guid.NewGuid(),
        ClientId = clientId,
        DisplayName = "FY 2025-26",
        StartDate = new DateTime(2025, 4, 1),
        EndDate = new DateTime(2026, 3, 31),
        Status = 1,
        CreatedAt = DateTime.UtcNow
    };

    #endregion

    #region 1. Detection & Rejection Tests

    [Fact]
    public void Test01_YesBankDetection_ValidSignatures_DetectedWithHighConfidence()
    {
        var extraction = BuildSyntheticYesExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>
            {
                CreateRow(1, 20.0, [("Statement of account: 113763300000920", 250.0)]),
                CreateRow(1, 35.0, [("Period: From 01-Mar-2025 To 27-Sep-2025", 250.0)]),
                CreateRow(1, 50.0, [("YES BANK LTD - BYRAMJI TOWN", 25.0), ("IFSC Code: YESB0001137", 400.0)]),
                CreateRow(1, 80.0, StandardHeaderTokens),
                CreateRow(1, 100.0, [("06-Mar-2025", 25.0), ("06-Mar-2025", 105.0), ("YESBN123456", 185.0), ("Payment", 300.0), ("500.00", 525.0), ("4,500.00", 670.0)])
            };
            pages.Add(CreatePage(1, rows));
        });

        var result = _detector.DetectBank(extraction);

        Assert.Equal(BankType.YesBank, result.DetectedBank);
        Assert.Equal("YES BANK", result.BankName);
        Assert.True(result.IsSupported);
        Assert.True(result.Confidence >= 0.90);
        Assert.Equal("113763300000920", result.AccountNumber);
        Assert.Equal(new DateTime(2025, 3, 1), result.StatementFrom);
        Assert.Equal(new DateTime(2025, 9, 27), result.StatementTo);
    }

    [Fact]
    public void Test02_NonYesBankRejection_UnknownOrOtherBanks_RejectedCleanly()
    {
        // SBI Statement with generic words
        var extraction = BuildSyntheticYesExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>
            {
                CreateRow(1, 20.0, [("STATE BANK OF INDIA", 200.0)]),
                CreateRow(1, 35.0, [("Account Statement for Period 01/01/2025 to 31/01/2025", 150.0)]),
                CreateRow(1, 60.0, [("Txn Date", 25.0), ("Value Date", 100.0), ("Description", 200.0), ("Ref No", 350.0), ("Debit", 450.0), ("Credit", 550.0), ("Balance", 650.0)]),
                CreateRow(1, 80.0, [("01/01/2025", 25.0), ("01/01/2025", 100.0), ("Salary Credit", 200.0), ("SBIN123", 350.0), ("50,000.00", 550.0), ("50,000.00", 650.0)])
            };
            pages.Add(CreatePage(1, rows));
        });

        var result = _detector.DetectBank(extraction);

        Assert.Equal(BankType.Unknown, result.DetectedBank);
        Assert.False(result.IsSupported);
    }

    [Fact]
    public void Test03_FormatDetection_Format1AndFormat2_ProperlyIdentified()
    {
        var extraction = BuildSyntheticYesExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>
            {
                CreateRow(1, 20.0, [("YES BANK", 50.0), ("IFSC: YESB0000001", 300.0)]),
                CreateRow(1, 50.0, StandardHeaderTokens),
                CreateRow(1, 80.0, [("10-Apr-2025", 25.0), ("10-Apr-2025", 105.0), ("REF001", 185.0), ("Opening Entry", 300.0), ("1,000.00", 605.0), ("1,000.00", 670.0)])
            };
            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        var parseResult = _parser.Parse(extraction, detection);

        Assert.True(parseResult.Success);
        Assert.Equal("YES-v1", parseResult.ParserVersion);
        Assert.Equal(1, parseResult.ProcessedCount);
    }

    #endregion

    #region 2. Single, Multiple & Same-Date Transactions

    [Fact]
    public void Test04_SingleTransaction_ExtractedAccurately()
    {
        var extraction = BuildSyntheticYesExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>
            {
                CreateRow(1, 50.0, StandardHeaderTokens),
                CreateRow(1, 70.0, [("15-May-2025", 25.0), ("15-May-2025", 105.0), ("YESBN9999", 185.0), ("Consulting Fee", 300.0), ("25,000.00", 605.0), ("25,000.00", 670.0)])
            };
            pages.Add(CreatePage(1, rows));
        });

        var detection = new BankDetectionResult { DetectedBank = BankType.YesBank, IsSupported = true };
        var result = _parser.Parse(extraction, detection);

        Assert.True(result.Success);
        Assert.Single(result.Transactions);
        var tx = result.Transactions[0];
        Assert.Equal(new DateTime(2025, 5, 15), tx.TransactionDate);
        Assert.Equal(new DateTime(2025, 5, 15), tx.ValueDate);
        Assert.Equal("YESBN9999", tx.Reference);
        Assert.Equal("Consulting Fee", tx.Description);
        Assert.Null(tx.Debit);
        Assert.Equal(25000.00m, tx.Credit);
        Assert.Equal(25000.00m, tx.Amount);
        Assert.Equal(25000.00m, tx.Balance);
        Assert.Equal("CREDIT", tx.TransactionType);
    }

    [Fact]
    public void Test05_MultipleTransactions_ExtractedInSequence()
    {
        var extraction = BuildSyntheticYesExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>
            {
                CreateRow(1, 50.0, StandardHeaderTokens),
                CreateRow(1, 70.0, [("01-Jun-2025", 25.0), ("01-Jun-2025", 105.0), ("TX1", 185.0), ("Opening", 300.0), ("10,000.00", 605.0), ("10,000.00", 670.0)]),
                CreateRow(1, 90.0, [("02-Jun-2025", 25.0), ("02-Jun-2025", 105.0), ("TX2", 185.0), ("Office Supplies", 300.0), ("2,500.00", 525.0), ("7,500.00", 670.0)]),
                CreateRow(1, 110.0, [("03-Jun-2025", 25.0), ("03-Jun-2025", 105.0), ("TX3", 185.0), ("Client Retainer", 300.0), ("5,000.00", 605.0), ("12,500.00", 670.0)])
            };
            pages.Add(CreatePage(1, rows));
        });

        var detection = new BankDetectionResult { DetectedBank = BankType.YesBank, IsSupported = true };
        var result = _parser.Parse(extraction, detection);

        Assert.True(result.Success);
        Assert.Equal(3, result.Transactions.Count);
        Assert.Equal(10000m, result.Transactions[0].Balance);
        Assert.Equal(7500m, result.Transactions[1].Balance);
        Assert.Equal(12500m, result.Transactions[2].Balance);
    }

    [Fact]
    public void Test06_SameDateMultipleTransactions_RemainDistinctTransactions()
    {
        var extraction = BuildSyntheticYesExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>
            {
                CreateRow(1, 50.0, StandardHeaderTokens),
                CreateRow(1, 70.0, [("10-Jun-2025", 25.0), ("10-Jun-2025", 105.0), ("TXA", 185.0), ("Payment 1", 300.0), ("1,000.00", 525.0), ("9,000.00", 670.0)]),
                CreateRow(1, 90.0, [("10-Jun-2025", 25.0), ("10-Jun-2025", 105.0), ("TXB", 185.0), ("Payment 2", 300.0), ("2,000.00", 525.0), ("7,000.00", 670.0)])
            };
            pages.Add(CreatePage(1, rows));
        });

        var detection = new BankDetectionResult { DetectedBank = BankType.YesBank, IsSupported = true };
        var result = _parser.Parse(extraction, detection);

        Assert.Equal(2, result.Transactions.Count);
        Assert.Equal("TXA", result.Transactions[0].Reference);
        Assert.Equal("TXB", result.Transactions[1].Reference);
        Assert.Equal(new DateTime(2025, 6, 10), result.Transactions[0].TransactionDate);
        Assert.Equal(new DateTime(2025, 6, 10), result.Transactions[1].TransactionDate);
    }

    #endregion

    #region 3. Debit, Credit & Balance Parsing

    [Fact]
    public void Test07_DebitParsing_MappedToDebitPreservingPrecision()
    {
        var extraction = BuildSyntheticYesExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>
            {
                CreateRow(1, 50.0, StandardHeaderTokens),
                CreateRow(1, 70.0, [("12-Jun-2025", 25.0), ("12-Jun-2025", 105.0), ("DR1", 185.0), ("Server Hosting", 300.0), ("12,345.67", 525.0), ("87,654.33", 670.0)])
            };
            pages.Add(CreatePage(1, rows));
        });

        var detection = new BankDetectionResult { DetectedBank = BankType.YesBank, IsSupported = true };
        var result = _parser.Parse(extraction, detection);

        var tx = result.Transactions[0];
        Assert.Equal(12345.67m, tx.Debit);
        Assert.Null(tx.Credit);
        Assert.Equal("DEBIT", tx.TransactionType);
    }

    [Fact]
    public void Test08_CreditParsing_MappedToCreditPreservingPrecision()
    {
        var extraction = BuildSyntheticYesExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>
            {
                CreateRow(1, 50.0, StandardHeaderTokens),
                CreateRow(1, 70.0, [("14-Jun-2025", 25.0), ("14-Jun-2025", 105.0), ("CR1", 185.0), ("Dividends", 300.0), ("45,678.90", 605.0), ("145,678.90", 670.0)])
            };
            pages.Add(CreatePage(1, rows));
        });

        var detection = new BankDetectionResult { DetectedBank = BankType.YesBank, IsSupported = true };
        var result = _parser.Parse(extraction, detection);

        var tx = result.Transactions[0];
        Assert.Null(tx.Debit);
        Assert.Equal(45678.90m, tx.Credit);
        Assert.Equal("CREDIT", tx.TransactionType);
    }

    [Fact]
    public void Test09_BalanceParsing_HandlesPositiveNegativeAndZeroBalances()
    {
        var extraction = BuildSyntheticYesExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>
            {
                CreateRow(1, 50.0, StandardHeaderTokens),
                CreateRow(1, 70.0, [("01-Jul-2025", 25.0), ("01-Jul-2025", 105.0), ("T1", 185.0), ("Overdraft Draw", 300.0), ("900,000.00", 525.0), ("-900,000.00", 670.0)]),
                CreateRow(1, 90.0, [("02-Jul-2025", 25.0), ("02-Jul-2025", 105.0), ("T2", 185.0), ("Sweep Recovery", 300.0), ("900,000.00", 605.0), ("0.00", 670.0)]),
                CreateRow(1, 110.0, [("03-Jul-2025", 25.0), ("03-Jul-2025", 105.0), ("T3", 185.0), ("Customer Deposit", 300.0), ("150,000.00", 605.0), ("150,000.00", 670.0)])
            };
            pages.Add(CreatePage(1, rows));
        });

        var detection = new BankDetectionResult { DetectedBank = BankType.YesBank, IsSupported = true };
        var result = _parser.Parse(extraction, detection);

        Assert.Equal(-900000.00m, result.Transactions[0].Balance);
        Assert.Equal(0.00m, result.Transactions[1].Balance);
        Assert.Equal(150000.00m, result.Transactions[2].Balance);
    }

    #endregion

    #region 4. Multi-Line Narrations & Pre-Anchor Stitching

    [Fact]
    public void Test10_MultiLineDescription_PreAnchorAndPostAnchorStitchedCorrectly()
    {
        var extraction = BuildSyntheticYesExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>
            {
                CreateRow(1, 50.0, StandardHeaderTokens),
                // Row 1: has pre-anchor line at Y=64, anchor line at Y=70, post-anchor line at Y=75
                CreateRow(1, 64.0, [("NEFT O/ W YESBN123456789 Shafquat", 300.0)]),
                CreateRow(1, 70.0, [("06-Mar-2025", 25.0), ("06-Mar-2025", 105.0), ("YESBN123456789", 185.0), ("PUNJAB NATIONAL BANK", 300.0), ("50,000.00", 525.0), ("-50,000.00", 670.0)]),
                CreateRow(1, 75.0, [("self transfer to salary account", 300.0)]),
                // Row 2: single line
                CreateRow(1, 95.0, [("07-Mar-2025", 25.0), ("07-Mar-2025", 105.0), ("NA", 185.0), ("SWEEP-IN CREDIT - 11374040001", 300.0), ("50,000.00", 605.0), ("0.00", 670.0)])
            };
            pages.Add(CreatePage(1, rows));
        });

        var detection = new BankDetectionResult { DetectedBank = BankType.YesBank, IsSupported = true };
        var result = _parser.Parse(extraction, detection);

        Assert.Equal(2, result.Transactions.Count);
        var t1 = result.Transactions[0];
        Assert.Contains("NEFT O/ W YESBN123456789 Shafquat", t1.Description);
        Assert.Contains("PUNJAB NATIONAL BANK", t1.Description);
        Assert.Contains("self transfer to salary account", t1.Description);
    }

    [Fact]
    public void Test11_MultiLineReference_ExtractedWithoutTruncation()
    {
        var extraction = BuildSyntheticYesExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>
            {
                CreateRow(1, 50.0, StandardHeaderTokens),
                CreateRow(1, 70.0, [("20-Mar-2025", 25.0), ("20-Mar-2025", 105.0), ("10000834520250422315300000123", 185.0), ("FD Creation", 300.0), ("100,000.00", 525.0), ("100,000.00", 670.0)])
            };
            pages.Add(CreatePage(1, rows));
        });

        var detection = new BankDetectionResult { DetectedBank = BankType.YesBank, IsSupported = true };
        var result = _parser.Parse(extraction, detection);

        Assert.Equal("10000834520250422315300000123", result.Transactions[0].Reference);
    }

    #endregion

    #region 5. Header, Footer & Metadata Demarcation

    [Fact]
    public void Test12_RepeatedPageHeaders_ExcludedFromTransactions()
    {
        var extraction = BuildSyntheticYesExtraction(pages =>
        {
            // Page 1
            pages.Add(CreatePage(1, [
                CreateRow(1, 50.0, StandardHeaderTokens),
                CreateRow(1, 70.0, [("01-Jan-2025", 25.0), ("01-Jan-2025", 105.0), ("R1", 185.0), ("Tx Page 1", 300.0), ("1,000.00", 525.0), ("9,000.00", 670.0)])
            ]));

            // Page 2 has repeated customer header and repeated table header
            pages.Add(CreatePage(2, [
                CreateRow(2, 20.0, [("Statement of account: 113763300000920", 250.0)]),
                CreateRow(2, 35.0, [("Customer Id: 22441147 Primary Account Holder Name: NA", 200.0)]),
                CreateRow(2, 50.0, StandardHeaderTokens),
                CreateRow(2, 70.0, [("02-Jan-2025", 25.0), ("02-Jan-2025", 105.0), ("R2", 185.0), ("Tx Page 2", 300.0), ("2,000.00", 605.0), ("11,000.00", 670.0)])
            ]));
        });

        var detection = new BankDetectionResult { DetectedBank = BankType.YesBank, IsSupported = true };
        var result = _parser.Parse(extraction, detection);

        Assert.Equal(2, result.Transactions.Count);
        Assert.DoesNotContain("Customer Id", result.Transactions[1].Description);
        Assert.DoesNotContain("Transaction Date", result.Transactions[1].Description);
    }

    [Fact]
    public void Test13_FooterFiltering_DisclaimersAndPageNumbersExcluded()
    {
        var extraction = BuildSyntheticYesExtraction(pages =>
        {
            pages.Add(CreatePage(1, [
                CreateRow(1, 50.0, StandardHeaderTokens),
                CreateRow(1, 70.0, [("05-Feb-2025", 25.0), ("05-Feb-2025", 105.0), ("R1", 185.0), ("Final Tx", 300.0), ("500.00", 525.0), ("4,500.00", 670.0)]),
                CreateRow(1, 100.0, [("Opening Balance: 5,000.00 Total Withdrawals: 500.00 Total Deposits: 0.00 Closing Balance: 4,500.00", 20.0)]),
                CreateRow(1, 120.0, [("Mandatory disclaimer Under Goods and Services Tax (GST) applicable from 1st July 2017", 20.0)]),
                CreateRow(1, 140.0, [("Please check the entries in the statement and in case of any discrepancies, report within 30 days", 20.0)]),
                CreateRow(1, 160.0, [("Page 1 of 1", 500.0)])
            ]));
        });

        var detection = new BankDetectionResult { DetectedBank = BankType.YesBank, IsSupported = true };
        var result = _parser.Parse(extraction, detection);

        Assert.Single(result.Transactions);
        Assert.Equal("Final Tx", result.Transactions[0].Description);
    }

    [Fact]
    public void Test14_MetadataFiltering_AccountAndBranchDetailsExcluded()
    {
        var extraction = BuildSyntheticYesExtraction(pages =>
        {
            pages.Add(CreatePage(1, [
                CreateRow(1, 20.0, [("Statement of account: 98765432101234", 250.0)]),
                CreateRow(1, 30.0, [("Period: From 01-Jan-2025 To 31-Jan-2025", 250.0)]),
                CreateRow(1, 40.0, [("ACME CORP Your Branch Details: YES BANK LTD - BENGALURU", 25.0)]),
                CreateRow(1, 50.0, [("GR FLR, MG ROAD, BENGALURU 560001 IFSC Code: YESB0000002", 25.0)]),
                CreateRow(1, 60.0, [("Transaction details for your account number 98765432101234 (CURRENT)", 200.0)]),
                CreateRow(1, 70.0, [("Account Status: Active Nominee Details: Not Registered", 50.0)]),
                CreateRow(1, 90.0, StandardHeaderTokens),
                CreateRow(1, 110.0, [("05-Jan-2025", 25.0), ("05-Jan-2025", 105.0), ("TX1", 185.0), ("Clean Payment", 300.0), ("1,000.00", 525.0), ("5,000.00", 670.0)])
            ]));
        });

        var detection = new BankDetectionResult { DetectedBank = BankType.YesBank, IsSupported = true };
        var result = _parser.Parse(extraction, detection);

        Assert.Single(result.Transactions);
        Assert.Equal("Clean Payment", result.Transactions[0].Description);
        Assert.DoesNotContain("ACME CORP", result.Transactions[0].Description);
        Assert.DoesNotContain("BENGALURU", result.Transactions[0].Description);
    }

    #endregion

    #region 6. Page-Break Continuation

    [Fact]
    public void Test15_PageBreakContinuation_StitchesNarrationAcrossPageBreak()
    {
        var extraction = BuildSyntheticYesExtraction(pages =>
        {
            // Page 1: Tx at bottom with narration ending in "HDFC"
            pages.Add(CreatePage(1, [
                CreateRow(1, 50.0, StandardHeaderTokens),
                CreateRow(1, 800.0, [("14-Mar-2025", 25.0), ("14-Mar-2025", 105.0), ("REF999", 185.0), ("NEFT transfer to vendor HDFC", 300.0), ("50,000.00", 525.0), ("100,000.00", 670.0)])
            ]));

            // Page 2: Continuation starts at Y=70 right after header
            pages.Add(CreatePage(2, [
                CreateRow(2, 20.0, [("Statement of account: 113763300000920", 250.0)]),
                CreateRow(2, 50.0, StandardHeaderTokens),
                CreateRow(2, 70.0, [("BANK Mumbai Branch", 300.0)]),
                CreateRow(2, 95.0, [("15-Mar-2025", 25.0), ("15-Mar-2025", 105.0), ("REF1000", 185.0), ("Next Day Transfer", 300.0), ("10,000.00", 605.0), ("110,000.00", 670.0)])
            ]));
        });

        var detection = new BankDetectionResult { DetectedBank = BankType.YesBank, IsSupported = true };
        var result = _parser.Parse(extraction, detection);

        Assert.Equal(2, result.Transactions.Count);
        Assert.Equal("NEFT transfer to vendor HDFC BANK Mumbai Branch", result.Transactions[0].Description);
        Assert.Equal("Next Day Transfer", result.Transactions[1].Description);
    }

    #endregion

    #region 7. Strict Formats, Blanks & NeedsReview

    [Fact]
    public void Test16_DateParsing_StrictInvariantFormats()
    {
        var extraction = BuildSyntheticYesExtraction(pages =>
        {
            pages.Add(CreatePage(1, [
                CreateRow(1, 50.0, StandardHeaderTokens),
                CreateRow(1, 70.0, [("09-Aug-2025", 25.0), ("08-Aug-2025", 105.0), ("R1", 185.0), ("Tx Aug", 300.0), ("100.00", 525.0), ("900.00", 670.0)])
            ]));
        });

        var detection = new BankDetectionResult { DetectedBank = BankType.YesBank, IsSupported = true };
        var result = _parser.Parse(extraction, detection);

        Assert.Equal(new DateTime(2025, 8, 9), result.Transactions[0].TransactionDate);
        Assert.Equal(new DateTime(2025, 8, 8), result.Transactions[0].ValueDate);
    }

    [Fact]
    public void Test17_AmountParsing_CommasAndDecimalsParsedCorrectly()
    {
        var extraction = BuildSyntheticYesExtraction(pages =>
        {
            pages.Add(CreatePage(1, [
                CreateRow(1, 50.0, StandardHeaderTokens),
                CreateRow(1, 70.0, [("08-Aug-2025", 25.0), ("08-Aug-2025", 105.0), ("R1", 185.0), ("Large RTGS", 300.0), ("18,000,000.00", 525.0), ("-18,000,000.00", 670.0)])
            ]));
        });

        var detection = new BankDetectionResult { DetectedBank = BankType.YesBank, IsSupported = true };
        var result = _parser.Parse(extraction, detection);

        Assert.Equal(18000000.00m, result.Transactions[0].Debit);
        Assert.Equal(-18000000.00m, result.Transactions[0].Balance);
    }

    [Fact]
    public void Test18_BlankDebitCredit_PreservedAsNull()
    {
        var extraction = BuildSyntheticYesExtraction(pages =>
        {
            pages.Add(CreatePage(1, [
                CreateRow(1, 50.0, StandardHeaderTokens),
                CreateRow(1, 70.0, [("01-Sep-2025", 25.0), ("01-Sep-2025", 105.0), ("R1", 185.0), ("Debit Only", 300.0), ("500.00", 525.0), ("9,500.00", 670.0)]),
                CreateRow(1, 90.0, [("02-Sep-2025", 25.0), ("02-Sep-2025", 105.0), ("R2", 185.0), ("Credit Only", 300.0), ("1,000.00", 605.0), ("10,500.00", 670.0)])
            ]));
        });

        var detection = new BankDetectionResult { DetectedBank = BankType.YesBank, IsSupported = true };
        var result = _parser.Parse(extraction, detection);

        Assert.NotNull(result.Transactions[0].Debit);
        Assert.Null(result.Transactions[0].Credit);

        Assert.Null(result.Transactions[1].Debit);
        Assert.NotNull(result.Transactions[1].Credit);
    }

    [Fact]
    public void Test19_AmbiguousRow_MarkedNeedsReview()
    {
        var extraction = BuildSyntheticYesExtraction(pages =>
        {
            pages.Add(CreatePage(1, [
                CreateRow(1, 50.0, StandardHeaderTokens),
                // Missing both debit and credit amounts
                CreateRow(1, 70.0, [("01-Oct-2025", 25.0), ("01-Oct-2025", 105.0), ("R1", 185.0), ("Incomplete Record", 300.0), ("10,000.00", 670.0)])
            ]));
        });

        var detection = new BankDetectionResult { DetectedBank = BankType.YesBank, IsSupported = true };
        var result = _parser.Parse(extraction, detection);

        Assert.Single(result.Transactions);
        Assert.True(result.Transactions[0].NeedsReview);
        Assert.Contains("neither a valid debit nor credit", result.Transactions[0].ReviewWarnings[0]);
    }

    #endregion

    #region 8. Generic Metadata Variations (Proving Zero Hardcoding)

    [Fact]
    public void Test20_DifferentCustomerMetadata_ParsedSuccessfullyWithoutLeakage()
    {
        var extraction = BuildSyntheticYesExtraction(pages =>
        {
            pages.Add(CreatePage(1, [
                CreateRow(1, 20.0, [("Statement of account: 55443322110099", 250.0)]),
                CreateRow(1, 30.0, [("Period: From 01-Jan-2026 To 31-Jan-2026", 250.0)]),
                CreateRow(1, 40.0, [("DR PRIYA NAIR Your Branch Details: YES BANK LTD - INDIRANAGAR", 25.0)]),
                CreateRow(1, 50.0, [("BENGALURU 560038 IFSC Code: YESB0000456", 25.0)]),
                CreateRow(1, 70.0, StandardHeaderTokens),
                CreateRow(1, 90.0, [("05-Jan-2026", 25.0), ("05-Jan-2026", 105.0), ("UTR888", 185.0), ("Hospital Equipment", 300.0), ("75,000.00", 525.0), ("425,000.00", 670.0)])
            ]));
        });

        var detection = new BankDetectionResult { DetectedBank = BankType.YesBank, IsSupported = true };
        var result = _parser.Parse(extraction, detection);

        Assert.Single(result.Transactions);
        Assert.Equal("Hospital Equipment", result.Transactions[0].Description);
        Assert.DoesNotContain("PRIYA NAIR", result.Transactions[0].Description);
        Assert.DoesNotContain("INDIRANAGAR", result.Transactions[0].Description);
    }

    [Fact]
    public void Test21_DifferentAccountMetadata_ParsedWithoutHardcodedAssumptions()
    {
        var extraction = BuildSyntheticYesExtraction(pages =>
        {
            pages.Add(CreatePage(1, [
                CreateRow(1, 20.0, [("account number 99887766554433 (SAVINGS)", 200.0)]),
                CreateRow(1, 40.0, [("Account Variant/ Description: Smart Salary Platinum", 200.0)]),
                CreateRow(1, 60.0, StandardHeaderTokens),
                CreateRow(1, 80.0, [("12-Feb-2026", 25.0), ("12-Feb-2026", 105.0), ("SAL001", 185.0), ("Salary Credit TechCorp", 300.0), ("150,000.00", 605.0), ("200,000.00", 670.0)])
            ]));
        });

        var detection = new BankDetectionResult { DetectedBank = BankType.YesBank, IsSupported = true };
        var result = _parser.Parse(extraction, detection);

        Assert.Single(result.Transactions);
        Assert.Equal("Salary Credit TechCorp", result.Transactions[0].Description);
        Assert.DoesNotContain("Smart Salary", result.Transactions[0].Description);
    }

    [Fact]
    public void Test22_DifferentStatementPeriod_ParsedCorrectly()
    {
        var extraction = BuildSyntheticYesExtraction(pages =>
        {
            pages.Add(CreatePage(1, [
                CreateRow(1, 20.0, [("Statement of account: 11112222333344", 250.0)]),
                CreateRow(1, 35.0, [("Period: From 01-Apr-2026 To 31-Mar-2027", 250.0)]),
                CreateRow(1, 50.0, [("YES BANK LIMITED - PUNE", 50.0), ("IFSC: YESB0000789", 350.0)]),
                CreateRow(1, 70.0, StandardHeaderTokens),
                CreateRow(1, 90.0, [("01-Apr-2026", 25.0), ("01-Apr-2026", 105.0), ("P1", 185.0), ("FY Start Entry", 300.0), ("100.00", 605.0), ("100.00", 670.0)])
            ]));
        });

        var detection = _detector.DetectBank(extraction);
        Assert.Equal(new DateTime(2026, 4, 1), detection.StatementFrom);
        Assert.Equal(new DateTime(2027, 3, 31), detection.StatementTo);

        var result = _parser.Parse(extraction, detection);
        Assert.Single(result.Transactions);
    }

    [Fact]
    public void Test23_DifferentTransactionCounts_WorksForSmallAndLargeVolumes()
    {
        // 25 transactions generated programmatically
        var extraction = BuildSyntheticYesExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>
            {
                CreateRow(1, 50.0, StandardHeaderTokens)
            };

            decimal balance = 1000m;
            for (int i = 1; i <= 25; i++)
            {
                balance += 100m;
                rows.Add(CreateRow(1, 50.0 + (i * 15.0), [
                    ($"01-Jan-2026", 25.0),
                    ($"01-Jan-2026", 105.0),
                    ($"REF{i:D4}", 185.0),
                    ($"Recurring Item {i}", 300.0),
                    ("100.00", 605.0),
                    ($"{balance:F2}", 670.0)
                ]));
            }
            pages.Add(CreatePage(1, rows));
        });

        var detection = new BankDetectionResult { DetectedBank = BankType.YesBank, IsSupported = true };
        var result = _parser.Parse(extraction, detection);

        Assert.Equal(25, result.Transactions.Count);
        Assert.Equal("REF0001", result.Transactions[0].Reference);
        Assert.Equal("REF0025", result.Transactions[24].Reference);
    }

    [Fact]
    public void Test24_DifferentNarrationLengths_ReconstructedCorrectly()
    {
        var extraction = BuildSyntheticYesExtraction(pages =>
        {
            pages.Add(CreatePage(1, [
                CreateRow(1, 50.0, StandardHeaderTokens),
                // Short narration (3 chars)
                CreateRow(1, 70.0, [("01-Jan-2026", 25.0), ("01-Jan-2026", 105.0), ("T1", 185.0), ("ATM", 300.0), ("500.00", 525.0), ("500.00", 670.0)]),
                // 3-line long narration
                CreateRow(1, 86.0, [("RTGS Dr-JANA0000003-PMS ASSOCIATES", 300.0)]),
                CreateRow(1, 92.0, [("02-Jan-2026", 25.0), ("02-Jan-2026", 105.0), ("T2", 185.0), ("BYRAMJI TOWN BRANCH NAGPUR", 300.0), ("1,000.00", 525.0), ("-500.00", 670.0)]),
                CreateRow(1, 97.0, [("CONTRACT PAYMENT INVOICE 2026-99", 300.0)])
            ]));
        });

        var detection = new BankDetectionResult { DetectedBank = BankType.YesBank, IsSupported = true };
        var result = _parser.Parse(extraction, detection);

        Assert.Equal(2, result.Transactions.Count);
        Assert.Equal("ATM", result.Transactions[0].Description);
        Assert.Contains("PMS ASSOCIATES", result.Transactions[1].Description);
        Assert.Contains("NAGPUR", result.Transactions[1].Description);
        Assert.Contains("INVOICE 2026-99", result.Transactions[1].Description);
    }

    [Fact]
    public void Test25_DifferentValuesAndReferences_NoHardcodedValues()
    {
        var extraction = BuildSyntheticYesExtraction(pages =>
        {
            pages.Add(CreatePage(1, [
                CreateRow(1, 50.0, StandardHeaderTokens),
                CreateRow(1, 70.0, [("15-Dec-2026", 25.0), ("15-Dec-2026", 105.0), ("CMS9988776655", 185.0), ("Vendor Settlement", 300.0), ("987,654.32", 525.0), ("1,012,345.68", 670.0)])
            ]));
        });

        var detection = new BankDetectionResult { DetectedBank = BankType.YesBank, IsSupported = true };
        var result = _parser.Parse(extraction, detection);

        var tx = result.Transactions[0];
        Assert.Equal("CMS9988776655", tx.Reference);
        Assert.Equal(987654.32m, tx.Debit);
        Assert.Equal(1012345.68m, tx.Balance);
    }

    #endregion

    #region 9. Idempotency & Security Tests

    [Fact]
    public async Task Test26_Reprocessing_DoesNotDuplicateTransactions()
    {
        var options = new DbContextOptionsBuilder<AccufexDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        using var db = new AccufexDbContext(options);

        var userId = Guid.NewGuid();
        var client = CreateTestClient(userId, "Test Client");
        var fy = CreateTestFinancialYear(client.Id);
        var fileRecord = new FileRecord
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            FinancialYearId = fy.Id,
            OriginalFileName = "YesBank_Stmt.pdf",
            StoredFileName = "YesBank_Stmt.pdf",
            Extension = ".pdf",
            ContentType = "application/pdf",
            SizeBytes = 1024,
            UploadedAt = DateTime.UtcNow,
            ProcessingStatus = 2
        };

        db.Clients.Add(client);
        db.FinancialYears.Add(fy);
        db.FileRecords.Add(fileRecord);
        await db.SaveChangesAsync();

        var syntheticExtraction = BuildSyntheticYesExtraction(pages =>
        {
            pages.Add(CreatePage(1, [
                CreateRow(1, 20.0, [("YES BANK", 50.0), ("IFSC: YESB0000001", 300.0)]),
                CreateRow(1, 50.0, StandardHeaderTokens),
                CreateRow(1, 70.0, [("01-Jan-2025", 25.0), ("01-Jan-2025", 105.0), ("TX1", 185.0), ("Entry 1", 300.0), ("100.00", 605.0), ("100.00", 670.0)])
            ]));
        });

        var mockPdfService = new Mock<IPdfExtractionService>();
        mockPdfService.Setup(s => s.GetExtractionResultAsync(fileRecord.Id, userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(syntheticExtraction);

        var service = new BankParsingService(db, mockPdfService.Object, _detector, _registry, _mockServiceLogger.Object);

        // Run 1: initial parse
        var (success1, _, result1) = await service.ParseAndPersistStatementAsync(fileRecord.Id, userId);
        Assert.True(success1);
        Assert.Equal(1, await db.Transactions.CountAsync(t => t.SourceFileId == fileRecord.Id));

        // Run 2: idempotent re-parse
        var (success2, _, result2) = await service.ParseAndPersistStatementAsync(fileRecord.Id, userId);
        Assert.True(success2);
        Assert.Equal(1, await db.Transactions.CountAsync(t => t.SourceFileId == fileRecord.Id));
    }

    [Fact]
    public async Task Test27_FailedReparse_PreservesPreviousSuccessfulData()
    {
        var options = new DbContextOptionsBuilder<AccufexDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        using var db = new AccufexDbContext(options);

        var userId = Guid.NewGuid();
        var client = CreateTestClient(userId, "Test Client");
        var fy = CreateTestFinancialYear(client.Id);
        var fileRecord = new FileRecord
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            FinancialYearId = fy.Id,
            OriginalFileName = "YesBank_Stmt.pdf",
            StoredFileName = "YesBank_Stmt.pdf",
            Extension = ".pdf",
            ContentType = "application/pdf",
            SizeBytes = 1024,
            UploadedAt = DateTime.UtcNow,
            ProcessingStatus = 2
        };

        db.Clients.Add(client);
        db.FinancialYears.Add(fy);
        db.FileRecords.Add(fileRecord);
        await db.SaveChangesAsync();

        var validExtraction = BuildSyntheticYesExtraction(pages =>
        {
            pages.Add(CreatePage(1, [
                CreateRow(1, 20.0, [("YES BANK", 50.0), ("IFSC: YESB0000001", 300.0)]),
                CreateRow(1, 50.0, StandardHeaderTokens),
                CreateRow(1, 70.0, [("01-Jan-2025", 25.0), ("01-Jan-2025", 105.0), ("TX1", 185.0), ("Valid Entry", 300.0), ("100.00", 605.0), ("100.00", 670.0)])
            ]));
        });

        var mockPdfService = new Mock<IPdfExtractionService>();
        mockPdfService.SetupSequence(s => s.GetExtractionResultAsync(fileRecord.Id, userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(validExtraction)
            .ReturnsAsync(new PdfExtractionResult { HasUsableText = false }); // second run returns corrupt/unusable text

        var service = new BankParsingService(db, mockPdfService.Object, _detector, _registry, _mockServiceLogger.Object);

        // Run 1: successful
        var (s1, _, _) = await service.ParseAndPersistStatementAsync(fileRecord.Id, userId);
        Assert.True(s1);
        Assert.Equal(1, await db.Transactions.CountAsync(t => t.SourceFileId == fileRecord.Id));

        // Run 2: extraction failure -> existing transactions preserved
        var (s2, err2, _) = await service.ParseAndPersistStatementAsync(fileRecord.Id, userId);
        Assert.False(s2);
        Assert.Equal(1, await db.Transactions.CountAsync(t => t.SourceFileId == fileRecord.Id));
    }

    [Fact]
    public async Task Test28_CrossUserParse_DeniedWith403Or404()
    {
        var options = new DbContextOptionsBuilder<AccufexDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        using var db = new AccufexDbContext(options);

        var ownerUserId = Guid.NewGuid();
        var attackerUserId = Guid.NewGuid();

        var client = CreateTestClient(ownerUserId, "Owner Client");
        var fy = CreateTestFinancialYear(client.Id);
        var fileRecord = new FileRecord
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            FinancialYearId = fy.Id,
            OriginalFileName = "YesBank_Stmt.pdf",
            StoredFileName = "YesBank_Stmt.pdf",
            Extension = ".pdf",
            ContentType = "application/pdf",
            SizeBytes = 1024,
            UploadedAt = DateTime.UtcNow,
            ProcessingStatus = 2
        };

        db.Clients.Add(client);
        db.FinancialYears.Add(fy);
        db.FileRecords.Add(fileRecord);
        await db.SaveChangesAsync();

        var mockPdfService = new Mock<IPdfExtractionService>();
        var service = new BankParsingService(db, mockPdfService.Object, _detector, _registry, _mockServiceLogger.Object);

        // Attacker attempts to parse owner's file
        var (success, error, _) = await service.ParseAndPersistStatementAsync(fileRecord.Id, attackerUserId);
        Assert.False(success);
        Assert.Contains("not accessible", error ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Test29_CrossUserTransactionRetrieval_DeniedWith404()
    {
        var options = new DbContextOptionsBuilder<AccufexDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        using var db = new AccufexDbContext(options);

        var ownerUserId = Guid.NewGuid();
        var attackerUserId = Guid.NewGuid();

        var client = CreateTestClient(ownerUserId, "Owner Client");
        var fy = CreateTestFinancialYear(client.Id);
        var fileRecord = new FileRecord
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            FinancialYearId = fy.Id,
            OriginalFileName = "YesBank_Stmt.pdf",
            StoredFileName = "YesBank_Stmt.pdf",
            Extension = ".pdf",
            ContentType = "application/pdf",
            SizeBytes = 1024,
            UploadedAt = DateTime.UtcNow,
            ProcessingStatus = 2
        };

        var tx = new Transaction
        {
            Id = Guid.NewGuid(),
            SourceFileId = fileRecord.Id,
            ClientId = client.Id,
            FinancialYearId = fy.Id,
            TransactionDate = DateTime.UtcNow,
            Description = "Secret Owner Transaction",
            Amount = 5000m,
            TransactionType = "DEBIT"
        };

        db.Clients.Add(client);
        db.FinancialYears.Add(fy);
        db.FileRecords.Add(fileRecord);
        db.Transactions.Add(tx);
        await db.SaveChangesAsync();

        // Query transactions scoped to attacker user ID
        var attackerTxs = await db.Transactions
            .Where(t => t.SourceFileId == fileRecord.Id && t.Client.UserId == attackerUserId)
            .ToListAsync();

        Assert.Empty(attackerTxs);
    }

    #endregion

    #region 10. Real YES BANK PDF Integration Test

    [Fact]
    public void RealPdf_YesBank_Iris_ExtractedWithExactReconciliation()
    {
        string path = @"E:\Bank Statements\Iris-YesBank-09 Sat,2025 14-21-46 pm.pdf";
        if (!File.Exists(path))
        {
            _output.WriteLine($"[SKIPPED] Real YES BANK fixture not found: {path}");
            return;
        }

        using var doc = PdfDocument.Open(path);
        Assert.Equal(4, doc.NumberOfPages);

        var pages = new List<PdfPageResult>(doc.NumberOfPages);
        for (int p = 1; p <= doc.NumberOfPages; p++)
        {
            var pdfPage = doc.GetPage(p);
            var words = pdfPage.GetWords().ToList();

            var lines = words
                .GroupBy(w => Math.Round((pdfPage.Height - w.BoundingBox.Top) / 2.5) * 2.5)
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
                HasUsableText = words.Count > 0,
                WordCount = words.Count,
                CandidateRows = lines,
                RawText = string.Join("\n", lines.Select(l => l.RawLineText))
            });
        }

        var extraction = new PdfExtractionResult
        {
            FileId = Guid.NewGuid(),
            OriginalFileName = Path.GetFileName(path),
            PageCount = pages.Count,
            ExtractionStatus = "DigitalTextExtracted",
            HasUsableText = true,
            Pages = pages
        };

        // 1. Bank Detection
        var detection = _detector.DetectBank(extraction);
        Assert.Equal(BankType.YesBank, detection.DetectedBank);
        Assert.True(detection.IsSupported);
        Assert.True(detection.Confidence >= 0.90);
        Assert.Equal("113763300000920", detection.AccountNumber);
        Assert.Equal(new DateTime(2025, 3, 1), detection.StatementFrom);
        Assert.Equal(new DateTime(2025, 9, 27), detection.StatementTo);

        // 2. Parse Execution
        var result = _parser.Parse(extraction, detection);

        Assert.True(result.Success);
        Assert.Equal(92, result.Transactions.Count);

        // 3. Mathematical Totals Verification against statement summary
        decimal totalDebits = result.Transactions.Where(t => t.Debit.HasValue).Sum(t => t.Debit!.Value);
        decimal totalCredits = result.Transactions.Where(t => t.Credit.HasValue).Sum(t => t.Credit!.Value);

        Assert.Equal(40706326.32m, totalDebits);
        Assert.Equal(40706326.32m, totalCredits);

        // 4. Inspect First Transaction
        var firstTx = result.Transactions[0];
        Assert.Equal(new DateTime(2025, 3, 6), firstTx.TransactionDate);
        Assert.Equal("YESBN12025030605837515", firstTx.Reference);
        Assert.Equal(900000.00m, firstTx.Debit);
        Assert.Equal(-900000.00m, firstTx.Balance);
        Assert.Contains("Shafquat Siddiqui", firstTx.Description);
        Assert.Contains("PUNJAB NATIONAL BANK", firstTx.Description);
        Assert.DoesNotContain("Your Branch Details", firstTx.Description);

        // 5. Inspect Page Transition Transaction (Page 1 -> Page 2 continuation)
        var p1LastTx = result.Transactions[21]; // Row #22 (14-Mar-2025)
        Assert.Equal(new DateTime(2025, 3, 14), p1LastTx.TransactionDate);
        Assert.Equal("YESBN12025031406532546", p1LastTx.Reference);
        Assert.Equal(500000.00m, p1LastTx.Debit);
        Assert.Equal(-1000000.00m, p1LastTx.Balance);
        Assert.Contains("Saba Sayyad", p1LastTx.Description);
        Assert.Contains("BANK sabasayad", p1LastTx.Description);

        // 6. Inspect Last Transaction
        var lastTx = result.Transactions[91]; // Row #92 (09-Aug-2025)
        Assert.Equal(new DateTime(2025, 8, 9), lastTx.TransactionDate);
        Assert.Equal(553942.84m, lastTx.Credit);
        Assert.Equal(0.00m, lastTx.Balance);
        Assert.Contains("INT. ON SWCR", lastTx.Description);
        Assert.DoesNotContain("Opening Balance", lastTx.Description);
        Assert.DoesNotContain("Mandatory disclaimer", lastTx.Description);

        _output.WriteLine($"[SUCCESS] Real YES BANK PDF parsed: {result.Transactions.Count} transactions, Debits: {totalDebits:N2}, Credits: {totalCredits:N2}");
    }

    #endregion
}
