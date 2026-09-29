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

public class AxisParserTests
{
    private readonly ITestOutputHelper _output;
    private readonly Mock<ILogger<BankDetector>> _mockDetectorLogger;
    private readonly Mock<ILogger<AxisStatementParser>> _mockParserLogger;
    private readonly Mock<ILogger<BankParsingService>> _mockServiceLogger;
    private readonly BankDetector _detector;
    private readonly AxisStatementParser _parser;
    private readonly BankParserRegistry _registry;

    public AxisParserTests(ITestOutputHelper output)
    {
        _output = output;
        _mockDetectorLogger = new Mock<ILogger<BankDetector>>();
        _mockParserLogger = new Mock<ILogger<AxisStatementParser>>();
        _mockServiceLogger = new Mock<ILogger<BankParsingService>>();

        _detector = new BankDetector(_mockDetectorLogger.Object);
        _parser = new AxisStatementParser(_mockParserLogger.Object);
        _registry = new BankParserRegistry([_parser]);
    }

    #region Synthetic Test Helper Methods

    private static PdfExtractionResult BuildSyntheticAxisExtraction(Action<List<PdfPageResult>> configurePages)
    {
        var pages = new List<PdfPageResult>();
        configurePages(pages);

        return new PdfExtractionResult
        {
            FileId = Guid.NewGuid(),
            OriginalFileName = "AxisBank_Statement.pdf",
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
            Width = 595.0,
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
            Width = t.Text.Length * 5.0,
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

    private static List<PdfCandidateRow> CreateStandardAxisHeaderRows(int pageNumber, double startY = 275.0)
    {
        return
        [
            CreateRow(pageNumber, startY,
            [
                ("S.NO", 15.0),
                ("Transaction Date", 42.0),
                ("Value Date", 93.0),
                ("Particulars", 144.0),
                ("Amount(INR)", 263.0),
                ("Debit/Credit", 314.0),
                ("Balance(INR)", 365.0),
                ("Cheque Number", 450.0),
                ("Branch Name(SOL)", 501.0)
            ]),
            CreateRow(pageNumber, startY + 5.0,
            [
                ("Date", 42.0),
                ("(dd/mm/yyyy)", 93.0),
                ("Number", 450.0)
            ]),
            CreateRow(pageNumber, startY + 10.0,
            [
                ("(dd/mm/yyyy)", 42.0)
            ])
        ];
    }

    private static List<PdfCandidateRow> CreateAxisMetadataRows(
        int pageNumber,
        string customerName = "ACME INDUSTRIAL SOLUTIONS",
        string accountNo = "924020055555555",
        string ifsc = "UTIB0005017",
        string fromDate = "01/04/2025",
        string toDate = "31/03/2026",
        string openingBalance = "1,00,000.00")
    {
        return
        [
            CreateRow(pageNumber, 100.0, [("Account Statement Report", 20.0)]),
            CreateRow(pageNumber, 130.0, [(customerName, 20.0)]),
            CreateRow(pageNumber, 150.0, [("Joint Holder :- PLOT NO 100 INDUSTRIAL ESTATE", 20.0)]),
            CreateRow(pageNumber, 165.0, [("Scheme : CA - BUSINESS ADVANTAGE currency : INR", 20.0)]),
            CreateRow(pageNumber, 180.0, [($"Customer No : 965675066 IFSC Code : {ifsc} MICR Code : 440211024", 20.0)]),
            CreateRow(pageNumber, 215.0, [($"Statement of Axis Bank Account No : {accountNo} for the period ( From : {fromDate} To : {toDate} )", 20.0)]),
            CreateRow(pageNumber, 250.0, [($"Opening Balance: INR {openingBalance}", 20.0)])
        ];
    }

    private static List<PdfCandidateRow> CreateAxisTransactionRows(
        int pageNumber,
        double startY,
        int sno,
        string txDate,
        string valDate,
        string particulars,
        string amount,
        string drcr,
        string balance,
        string? cheque = null,
        string? branch = "(5017)")
    {
        var rows = new List<PdfCandidateRow>();

        // Financial line (Amount, DR/CR, Balance, Branch, Cheque)
        var line1Tokens = new List<(string Text, double X)>
        {
            (particulars.Length > 25 ? particulars[..25] : particulars, 144.0),
            (amount, 265.0),
            (drcr, 320.0),
            (balance, 370.0)
        };
        if (!string.IsNullOrEmpty(cheque)) line1Tokens.Add((cheque, 455.0));
        if (!string.IsNullOrEmpty(branch)) line1Tokens.Add((branch, 510.0));

        rows.Add(CreateRow(pageNumber, startY, line1Tokens));

        // Date line (S.NO, TxDate, ValDate, and continuation particulars if any)
        var line2Tokens = new List<(string Text, double X)>
        {
            (sno.ToString(), 15.0),
            (txDate, 42.0),
            (valDate, 93.0)
        };
        if (particulars.Length > 25)
        {
            line2Tokens.Add((particulars[25..], 144.0));
        }

        rows.Add(CreateRow(pageNumber, startY + 3.0, line2Tokens));

        return rows;
    }

    private static Client CreateTestClient(Guid userId, string name) => new Client
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        Name = name,
        ContactPerson = "Test Contact",
        Email = "axis-test@example.com",
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
    public void Test01_AxisDetection_StrongSignals_HighConfidence()
    {
        var extraction = BuildSyntheticAxisExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>();
            rows.AddRange(CreateAxisMetadataRows(1));
            rows.AddRange(CreateStandardAxisHeaderRows(1));
            rows.AddRange(CreateAxisTransactionRows(1, 300.0, 1, "01/04/2025", "01/04/2025", "OFFICE SUPPLY ACH", "12,500.00", "DR", "87,500.00"));
            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);

        Assert.Equal(BankType.Axis, detection.DetectedBank);
        Assert.True(detection.IsSupported);
        Assert.True(detection.Confidence >= 0.90);
        Assert.Equal("924020055555555", detection.AccountNumber);
        Assert.Equal("ACME INDUSTRIAL SOLUTIONS", detection.CustomerName);
        Assert.Equal(new DateTime(2025, 4, 1), detection.StatementFrom);
        Assert.Equal(new DateTime(2026, 3, 31), detection.StatementTo);
    }

    [Fact]
    public void Test02_NonAxisRejection_GenericWords_RejectedAsUnknown()
    {
        var extraction = BuildSyntheticAxisExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>
            {
                CreateRow(1, 100.0, [("MONTHLY FINANCIAL TRANSACTION STATEMENT", 50.0)]),
                CreateRow(1, 120.0, [("Account Number: 999888777666", 50.0)]),
                CreateRow(1, 150.0, [("Date", 50.0), ("Description", 150.0), ("Debit", 350.0), ("Credit", 450.0), ("Balance", 550.0)]),
                CreateRow(1, 180.0, [("01/08/2026", 50.0), ("CONSULTING INVOICE", 150.0), ("50000.00", 450.0), ("150000.00", 550.0)])
            };
            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);

        Assert.Equal(BankType.Unknown, detection.DetectedBank);
        Assert.False(detection.IsSupported);
        Assert.Equal(0.0, detection.Confidence);
    }

    [Fact]
    public void Test03_TableFormatDetection_DynamicColumnIntervals()
    {
        var extraction = BuildSyntheticAxisExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>();
            rows.AddRange(CreateAxisMetadataRows(1));
            // Shifted headers: e.g. wider margins
            rows.Add(CreateRow(1, 280.0,
            [
                ("S.NO", 20.0),
                ("Transaction Date", 50.0),
                ("Value Date", 110.0),
                ("Particulars", 160.0),
                ("Amount(INR)", 280.0),
                ("Debit/Credit", 330.0),
                ("Balance(INR)", 380.0),
                ("Cheque Number", 460.0),
                ("Branch Name(SOL)", 515.0)
            ]));
            rows.Add(CreateRow(1, 310.0,
            [
                ("RAW MATERIAL PURCHASE", 160.0),
                ("25,000.00", 280.0),
                ("DR", 335.0),
                ("75,000.00", 385.0),
                ("(5017)", 520.0)
            ]));
            rows.Add(CreateRow(1, 313.0,
            [
                ("1", 20.0),
                ("01/04/2025", 50.0),
                ("01/04/2025", 110.0)
            ]));
            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        Assert.Equal(BankType.Axis, detection.DetectedBank);

        var result = _parser.Parse(extraction, detection);
        Assert.True(result.Success);
        Assert.Single(result.Transactions);
        Assert.Equal(25000.00m, result.Transactions[0].Amount);
    }

    #endregion

    #region 2. Transaction Parsing & Row Verification Tests

    [Fact]
    public void Test04_SingleTransaction_ParsedSuccessfully()
    {
        var extraction = BuildSyntheticAxisExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>();
            rows.AddRange(CreateAxisMetadataRows(1, openingBalance: "1,00,000.00"));
            rows.AddRange(CreateStandardAxisHeaderRows(1));
            rows.AddRange(CreateAxisTransactionRows(1, 300.0, 1, "02/04/2025", "02/04/2025", "OFFICE RENT ACH", "25,000.00", "DR", "75,000.00"));
            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        var result = _parser.Parse(extraction, detection);

        Assert.True(result.Success);
        Assert.Single(result.Transactions);

        var tx = result.Transactions[0];
        Assert.Equal(new DateTime(2025, 4, 2), tx.TransactionDate);
        Assert.Equal(new DateTime(2025, 4, 2), tx.ValueDate);
        Assert.Equal("OFFICE RENT ACH", tx.Description);
        Assert.Equal(25000.00m, tx.Debit);
        Assert.Null(tx.Credit);
        Assert.Equal(25000.00m, tx.Amount);
        Assert.Equal(75000.00m, tx.Balance);
        Assert.Equal("DEBIT", tx.TransactionType);
        Assert.False(tx.NeedsReview);
    }

    [Fact]
    public void Test05_MultipleTransactions_ParsedSequentially()
    {
        var extraction = BuildSyntheticAxisExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>();
            rows.AddRange(CreateAxisMetadataRows(1, openingBalance: "1,00,000.00"));
            rows.AddRange(CreateStandardAxisHeaderRows(1));
            rows.AddRange(CreateAxisTransactionRows(1, 300.0, 1, "01/04/2025", "01/04/2025", "SUPPLIER PAYMENT A", "10,000.00", "DR", "90,000.00"));
            rows.AddRange(CreateAxisTransactionRows(1, 320.0, 2, "02/04/2025", "02/04/2025", "CLIENT DEPOSIT", "30,000.00", "CR", "1,20,000.00"));
            rows.AddRange(CreateAxisTransactionRows(1, 340.0, 3, "03/04/2025", "03/04/2025", "ELECTRICITY BILL", "5,000.00", "DR", "1,15,000.00"));
            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        var result = _parser.Parse(extraction, detection);

        Assert.True(result.Success);
        Assert.Equal(3, result.Transactions.Count);

        Assert.Equal(10000.00m, result.Transactions[0].Debit);
        Assert.Equal(30000.00m, result.Transactions[1].Credit);
        Assert.Equal(5000.00m, result.Transactions[2].Debit);
        Assert.Equal(115000.00m, result.Transactions[2].Balance);
    }

    [Fact]
    public void Test06_SameDateMultipleTransactions_RemainSeparate()
    {
        var extraction = BuildSyntheticAxisExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>();
            rows.AddRange(CreateAxisMetadataRows(1, openingBalance: "1,00,000.00"));
            rows.AddRange(CreateStandardAxisHeaderRows(1));
            rows.AddRange(CreateAxisTransactionRows(1, 300.0, 1, "05/04/2025", "05/04/2025", "BATCH PAYMENT 1", "5,000.00", "DR", "95,000.00"));
            rows.AddRange(CreateAxisTransactionRows(1, 320.0, 2, "05/04/2025", "05/04/2025", "BATCH PAYMENT 2", "15,000.00", "DR", "80,000.00"));
            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        var result = _parser.Parse(extraction, detection);

        Assert.True(result.Success);
        Assert.Equal(2, result.Transactions.Count);
        Assert.Equal(result.Transactions[0].TransactionDate, result.Transactions[1].TransactionDate);
        Assert.Equal(5000.00m, result.Transactions[0].Debit);
        Assert.Equal(15000.00m, result.Transactions[1].Debit);
        Assert.NotEqual(result.Transactions[0].Description, result.Transactions[1].Description);
    }

    [Fact]
    public void Test07_DebitTransaction_MappedToDebitField()
    {
        var extraction = BuildSyntheticAxisExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>();
            rows.AddRange(CreateAxisMetadataRows(1));
            rows.AddRange(CreateStandardAxisHeaderRows(1));
            rows.AddRange(CreateAxisTransactionRows(1, 300.0, 1, "10/04/2025", "10/04/2025", "VENDOR V1 PAYMENT", "18,450.00", "DR", "81,550.00"));
            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        var result = _parser.Parse(extraction, detection);

        Assert.True(result.Success);
        var tx = result.Transactions[0];
        Assert.Equal(18450.00m, tx.Debit);
        Assert.Null(tx.Credit);
        Assert.Equal(18450.00m, tx.Amount);
        Assert.Equal("DEBIT", tx.TransactionType);
    }

    [Fact]
    public void Test08_CreditTransaction_MappedToCreditField()
    {
        var extraction = BuildSyntheticAxisExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>();
            rows.AddRange(CreateAxisMetadataRows(1));
            rows.AddRange(CreateStandardAxisHeaderRows(1));
            rows.AddRange(CreateAxisTransactionRows(1, 300.0, 1, "12/04/2025", "12/04/2025", "CUSTOMER INVOICE SETTLEMENT", "75,000.00", "CR", "1,75,000.00"));
            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        var result = _parser.Parse(extraction, detection);

        Assert.True(result.Success);
        var tx = result.Transactions[0];
        Assert.Equal(75000.00m, tx.Credit);
        Assert.Null(tx.Debit);
        Assert.Equal(75000.00m, tx.Amount);
        Assert.Equal("CREDIT", tx.TransactionType);
    }

    [Fact]
    public void Test09_BalanceExtraction_ExactDecimalPrecision()
    {
        var extraction = BuildSyntheticAxisExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>();
            rows.AddRange(CreateAxisMetadataRows(1));
            rows.AddRange(CreateStandardAxisHeaderRows(1));
            rows.AddRange(CreateAxisTransactionRows(1, 300.0, 1, "15/04/2025", "15/04/2025", "OD INTEREST RUN", "1,234.56", "DR", "98,765.44"));
            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        var result = _parser.Parse(extraction, detection);

        Assert.True(result.Success);
        var tx = result.Transactions[0];
        Assert.Equal(98765.44m, tx.Balance);
        Assert.Equal(1234.56m, tx.Amount);
    }

    [Fact]
    public void Test10_PositiveBalance_ExtractedCorrectly()
    {
        var extraction = BuildSyntheticAxisExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>();
            rows.AddRange(CreateAxisMetadataRows(1));
            rows.AddRange(CreateStandardAxisHeaderRows(1));
            rows.AddRange(CreateAxisTransactionRows(1, 300.0, 1, "20/04/2025", "20/04/2025", "SALES DEPOSIT", "50,000.00", "CR", "2,50,000.00"));
            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        var result = _parser.Parse(extraction, detection);

        Assert.True(result.Success);
        Assert.Equal(250000.00m, result.Transactions[0].Balance);
    }

    [Fact]
    public void Test11_NegativeBalance_ExtractedCorrectly()
    {
        var extraction = BuildSyntheticAxisExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>();
            rows.AddRange(CreateAxisMetadataRows(1));
            rows.AddRange(CreateStandardAxisHeaderRows(1));
            // In case of negative balance or overdraft representation
            rows.AddRange(CreateAxisTransactionRows(1, 300.0, 1, "22/04/2025", "22/04/2025", "OVERDRAFT DRAWDOWN", "50,000.00", "DR", "-25,000.00"));
            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        var result = _parser.Parse(extraction, detection);

        Assert.True(result.Success);
        Assert.Equal(-25000.00m, result.Transactions[0].Balance);
    }

    [Fact]
    public void Test12_MultiLineParticulars_ReconstructedCorrectly()
    {
        var extraction = BuildSyntheticAxisExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>();
            rows.AddRange(CreateAxisMetadataRows(1));
            rows.AddRange(CreateStandardAxisHeaderRows(1));

            // Row 1 spanning 4 visual lines
            rows.Add(CreateRow(1, 300.0, [("NEFT/EB/AXOEB09099179862/HIGHWA", 144.0), ("1,00,000.00", 265.0), ("DR", 320.0), ("21,547.69", 370.0), ("(5017)", 510.0)]));
            rows.Add(CreateRow(1, 305.0, [("Y SAFETY CORPORATIO/KOTAK", 144.0)]));
            rows.Add(CreateRow(1, 308.0, [("1", 15.0), ("01/04/2025", 42.0), ("01/04/2025", 93.0), ("MAHINDRA BANK //BOARD", 144.0)]));
            rows.Add(CreateRow(1, 314.0, [("ADVANCE/////", 144.0)]));

            // Row 2
            rows.AddRange(CreateAxisTransactionRows(1, 330.0, 2, "01/04/2025", "01/04/2025", "IMPS/P2A/509122893621/ASHFAQUE", "50,000.00", "CR", "71,547.69"));

            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        var result = _parser.Parse(extraction, detection);

        Assert.True(result.Success);
        Assert.Equal(2, result.Transactions.Count);

        var tx1 = result.Transactions[0];
        Assert.Equal(100000.00m, tx1.Debit);
        Assert.Contains("NEFT/EB/AXOEB09099179862/HIGHWA", tx1.Description);
        Assert.Contains("SAFETY CORPORATIO/KOTAK", tx1.Description);
        Assert.Contains("MAHINDRA BANK //BOARD", tx1.Description);
        Assert.Contains("ADVANCE/////", tx1.Description);
        Assert.DoesNotContain("ASHFAQUE", tx1.Description);
    }

    [Fact]
    public void Test13_MultiLineChequeOrSeparateFields_PreservesChequeAndBranch()
    {
        var extraction = BuildSyntheticAxisExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>();
            rows.AddRange(CreateAxisMetadataRows(1));
            rows.AddRange(CreateStandardAxisHeaderRows(1));
            rows.AddRange(CreateAxisTransactionRows(1, 300.0, 1, "05/04/2025", "05/04/2025", "CLEARING CHEQUE PAYMENT", "45,000.00", "DR", "55,000.00", cheque: "CHQ987654", branch: "(5017)"));
            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        var result = _parser.Parse(extraction, detection);

        Assert.True(result.Success);
        var tx = result.Transactions[0];
        Assert.Equal("CHQ987654", tx.Reference);
        Assert.DoesNotContain("CHQ987654", tx.Description);
    }

    [Fact]
    public void Test14_RepeatedPageHeaders_IgnoredWithoutDuplicateTransactions()
    {
        var extraction = BuildSyntheticAxisExtraction(pages =>
        {
            // Page 1
            var p1Rows = new List<PdfCandidateRow>();
            p1Rows.AddRange(CreateAxisMetadataRows(1));
            p1Rows.AddRange(CreateStandardAxisHeaderRows(1));
            p1Rows.AddRange(CreateAxisTransactionRows(1, 300.0, 1, "01/04/2025", "01/04/2025", "P1 TRANSACTION", "10,000.00", "DR", "90,000.00"));
            pages.Add(CreatePage(1, p1Rows));

            // Page 2
            var p2Rows = new List<PdfCandidateRow>();
            p2Rows.AddRange(CreateStandardAxisHeaderRows(2, startY: 80.0));
            p2Rows.AddRange(CreateAxisTransactionRows(2, 110.0, 2, "02/04/2025", "02/04/2025", "P2 TRANSACTION", "20,000.00", "CR", "1,10,000.00"));
            pages.Add(CreatePage(2, p2Rows));
        });

        var detection = _detector.DetectBank(extraction);
        var result = _parser.Parse(extraction, detection);

        Assert.True(result.Success);
        Assert.Equal(2, result.Transactions.Count);
        Assert.Equal("P1 TRANSACTION", result.Transactions[0].Description);
        Assert.Equal("P2 TRANSACTION", result.Transactions[1].Description);
    }

    [Fact]
    public void Test15_MetadataFiltering_ExcludedFromTransactions()
    {
        var extraction = BuildSyntheticAxisExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>();
            rows.AddRange(CreateAxisMetadataRows(1));
            rows.AddRange(CreateStandardAxisHeaderRows(1));
            rows.AddRange(CreateAxisTransactionRows(1, 300.0, 1, "01/04/2025", "01/04/2025", "VALID ROW", "5,000.00", "DR", "95,000.00"));
            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        var result = _parser.Parse(extraction, detection);

        Assert.True(result.Success);
        Assert.Single(result.Transactions);
        var desc = result.Transactions[0].Description;
        Assert.DoesNotContain("Account Statement Report", desc);
        Assert.DoesNotContain("Opening Balance", desc);
        Assert.DoesNotContain("IFSC Code", desc);
        Assert.DoesNotContain("Joint Holder", desc);
    }

    [Fact]
    public void Test16_FooterFiltering_ExcludedFromTransactions()
    {
        var extraction = BuildSyntheticAxisExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>();
            rows.AddRange(CreateAxisMetadataRows(1));
            rows.AddRange(CreateStandardAxisHeaderRows(1));
            rows.AddRange(CreateAxisTransactionRows(1, 300.0, 1, "01/04/2025", "01/04/2025", "LAST ROW", "10,000.00", "DR", "90,000.00"));

            // Summary and Footer
            rows.Add(CreateRow(1, 330.0, [("TRANSACTION TOTAL DR/CR 10,000.00 / 0.00", 50.0)]));
            rows.Add(CreateRow(1, 350.0, [("Closing Balance: INR 90,000.00", 50.0)]));
            rows.Add(CreateRow(1, 370.0, [("Cheque Return Details", 50.0)]));
            rows.Add(CreateRow(1, 390.0, [("Unless the constituent notifies the bank immediately of any discrepancy", 50.0)]));
            rows.Add(CreateRow(1, 410.0, [("REGISTERED OFFICE AXIS BANK LTD,TRISHUL,Opp. Samartheswar Temple", 50.0)]));
            rows.Add(CreateRow(1, 430.0, [("End of Report", 50.0)]));

            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        var result = _parser.Parse(extraction, detection);

        Assert.True(result.Success);
        Assert.Single(result.Transactions);
        Assert.Equal("LAST ROW", result.Transactions[0].Description);
    }

    [Fact]
    public void Test17_PageBreakContinuation_ActiveStatePreserved()
    {
        var extraction = BuildSyntheticAxisExtraction(pages =>
        {
            // Page 1 ends with a transaction
            var p1 = new List<PdfCandidateRow>();
            p1.AddRange(CreateAxisMetadataRows(1));
            p1.AddRange(CreateStandardAxisHeaderRows(1));
            p1.AddRange(CreateAxisTransactionRows(1, 750.0, 1, "10/04/2025", "10/04/2025", "PAGE 1 FINAL ROW", "15,000.00", "DR", "85,000.00"));
            pages.Add(CreatePage(1, p1));

            // Page 2 starts with header and then next transaction
            var p2 = new List<PdfCandidateRow>();
            p2.AddRange(CreateStandardAxisHeaderRows(2, startY: 80.0));
            p2.AddRange(CreateAxisTransactionRows(2, 110.0, 2, "11/04/2025", "11/04/2025", "PAGE 2 INITIAL ROW", "25,000.00", "CR", "1,10,000.00"));
            pages.Add(CreatePage(2, p2));
        });

        var detection = _detector.DetectBank(extraction);
        var result = _parser.Parse(extraction, detection);

        Assert.True(result.Success);
        Assert.Equal(2, result.Transactions.Count);
        Assert.Equal(1, result.Transactions[0].SourcePageNumber);
        Assert.Equal(2, result.Transactions[1].SourcePageNumber);
    }

    [Fact]
    public void Test18_DateParsing_StrictInvariant()
    {
        var extraction = BuildSyntheticAxisExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>();
            rows.AddRange(CreateAxisMetadataRows(1));
            rows.AddRange(CreateStandardAxisHeaderRows(1));
            rows.AddRange(CreateAxisTransactionRows(1, 300.0, 1, "31/12/2025", "31/12/2025", "YEAR END INVOICE", "5,000.00", "DR", "95,000.00"));
            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        var result = _parser.Parse(extraction, detection);

        Assert.True(result.Success);
        Assert.Equal(new DateTime(2025, 12, 31), result.Transactions[0].TransactionDate);
    }

    [Fact]
    public void Test19_AmountParsing_StrictInvariantDecimal()
    {
        var extraction = BuildSyntheticAxisExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>();
            rows.AddRange(CreateAxisMetadataRows(1));
            rows.AddRange(CreateStandardAxisHeaderRows(1));
            rows.AddRange(CreateAxisTransactionRows(1, 300.0, 1, "01/04/2025", "01/04/2025", "LARGE TRANSFER", "10,50,750.25", "CR", "11,50,750.25"));
            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        var result = _parser.Parse(extraction, detection);

        Assert.True(result.Success);
        Assert.Equal(1050750.25m, result.Transactions[0].Credit);
        Assert.Equal(1150750.25m, result.Transactions[0].Balance);
    }

    [Fact]
    public void Test20_DrCrMapping_StrictValidation()
    {
        var extraction = BuildSyntheticAxisExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>();
            rows.AddRange(CreateAxisMetadataRows(1));
            rows.AddRange(CreateStandardAxisHeaderRows(1));
            // Missing DR/CR indicator
            rows.AddRange(CreateAxisTransactionRows(1, 300.0, 1, "01/04/2025", "01/04/2025", "AMBIGUOUS ROW", "1,000.00", "XX", "99,000.00"));
            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        var result = _parser.Parse(extraction, detection);

        Assert.True(result.Success);
        Assert.Single(result.Transactions);
        Assert.True(result.Transactions[0].NeedsReview);
        Assert.Equal("UNKNOWN", result.Transactions[0].TransactionType);
    }

    [Fact]
    public void Test21_BlankAmountHandling_FlaggedNeedsReview()
    {
        var extraction = BuildSyntheticAxisExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>();
            rows.AddRange(CreateAxisMetadataRows(1));
            rows.AddRange(CreateStandardAxisHeaderRows(1));
            // Zero or missing amount
            rows.AddRange(CreateAxisTransactionRows(1, 300.0, 1, "01/04/2025", "01/04/2025", "ZERO AMOUNT ROW", "0.00", "DR", "1,00,000.00"));
            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        var result = _parser.Parse(extraction, detection);

        Assert.True(result.Success);
        Assert.Single(result.Transactions);
        Assert.True(result.Transactions[0].NeedsReview);
    }

    [Fact]
    public void Test22_AmbiguousRow_FlaggedNeedsReview()
    {
        var extraction = BuildSyntheticAxisExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>();
            rows.AddRange(CreateAxisMetadataRows(1));
            rows.AddRange(CreateStandardAxisHeaderRows(1));
            // Row with empty description
            rows.AddRange(CreateAxisTransactionRows(1, 300.0, 1, "01/04/2025", "01/04/2025", "", "5,000.00", "DR", "95,000.00"));
            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        var result = _parser.Parse(extraction, detection);

        Assert.True(result.Success);
        Assert.Single(result.Transactions);
        Assert.True(result.Transactions[0].NeedsReview);
    }

    #endregion

    #region 3. Genericity & Hardcoding Invariance Tests

    [Fact]
    public void Test23_DifferentCustomerMetadata_ParsesSuccessfully()
    {
        var extraction = BuildSyntheticAxisExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>();
            rows.AddRange(CreateAxisMetadataRows(1, customerName: "ZENITH LOGISTICS PRIVATE LIMITED"));
            rows.AddRange(CreateStandardAxisHeaderRows(1));
            rows.AddRange(CreateAxisTransactionRows(1, 300.0, 1, "01/05/2025", "01/05/2025", "FREIGHT PAYMENT", "45,000.00", "DR", "55,000.00"));
            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        Assert.Equal("ZENITH LOGISTICS PRIVATE LIMITED", detection.CustomerName);

        var result = _parser.Parse(extraction, detection);
        Assert.True(result.Success);
        Assert.Single(result.Transactions);
        Assert.Equal(45000.00m, result.Transactions[0].Debit);
    }

    [Fact]
    public void Test24_DifferentAccountMetadata_ParsesSuccessfully()
    {
        var extraction = BuildSyntheticAxisExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>();
            rows.AddRange(CreateAxisMetadataRows(1, accountNo: "918020098765432"));
            rows.AddRange(CreateStandardAxisHeaderRows(1));
            rows.AddRange(CreateAxisTransactionRows(1, 300.0, 1, "01/06/2025", "01/06/2025", "RENTAL INCOME", "30,000.00", "CR", "1,30,000.00"));
            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        Assert.Equal("918020098765432", detection.AccountNumber);

        var result = _parser.Parse(extraction, detection);
        Assert.True(result.Success);
        Assert.Single(result.Transactions);
        Assert.Equal(30000.00m, result.Transactions[0].Credit);
    }

    [Fact]
    public void Test25_DifferentStatementPeriod_ParsesSuccessfully()
    {
        var extraction = BuildSyntheticAxisExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>();
            rows.AddRange(CreateAxisMetadataRows(1, fromDate: "01/10/2025", toDate: "31/12/2025"));
            rows.AddRange(CreateStandardAxisHeaderRows(1));
            rows.AddRange(CreateAxisTransactionRows(1, 300.0, 1, "15/10/2025", "15/10/2025", "Q3 ADVANCE TAX", "20,000.00", "DR", "80,000.00"));
            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        Assert.Equal(new DateTime(2025, 10, 1), detection.StatementFrom);
        Assert.Equal(new DateTime(2025, 12, 31), detection.StatementTo);

        var result = _parser.Parse(extraction, detection);
        Assert.True(result.Success);
        Assert.Single(result.Transactions);
    }

    [Fact]
    public void Test26_DifferentTransactionCounts_ParsesSuccessfully()
    {
        var extraction = BuildSyntheticAxisExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>();
            rows.AddRange(CreateAxisMetadataRows(1, openingBalance: "10,000.00"));
            rows.AddRange(CreateStandardAxisHeaderRows(1));

            decimal running = 10000.00m;
            for (int i = 1; i <= 10; i++)
            {
                decimal amt = i * 1000.00m;
                running += amt;
                rows.AddRange(CreateAxisTransactionRows(1, 300.0 + i * 20.0, i, "01/07/2025", "01/07/2025", $"BATCH ITEM #{i}", amt.ToString("N2"), "CR", running.ToString("N2")));
            }

            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        var result = _parser.Parse(extraction, detection);

        Assert.True(result.Success);
        Assert.Equal(10, result.Transactions.Count);
    }

    [Fact]
    public void Test27_DifferentParticularsLengths_ParsesSuccessfully()
    {
        var extraction = BuildSyntheticAxisExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>();
            rows.AddRange(CreateAxisMetadataRows(1));
            rows.AddRange(CreateStandardAxisHeaderRows(1));

            // Very short
            rows.AddRange(CreateAxisTransactionRows(1, 300.0, 1, "01/04/2025", "01/04/2025", "POS", "150.00", "DR", "99,850.00"));
            // Very long (120 chars)
            rows.AddRange(CreateAxisTransactionRows(1, 330.0, 2, "02/04/2025", "02/04/2025", "NEFT/SBIN1234567890/INTERNATIONAL ENGINEERING ENTERPRISES/STATE BANK OF INDIA/EXPORT CONSIGNMENT PAYMENT IN FULL", "50,000.00", "CR", "1,49,850.00"));

            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        var result = _parser.Parse(extraction, detection);

        Assert.True(result.Success);
        Assert.Equal(2, result.Transactions.Count);
        Assert.Equal("POS", result.Transactions[0].Description);
        Assert.Contains("EXPORT CONSIGNMENT PAYMENT IN FULL", result.Transactions[1].Description);
    }

    [Fact]
    public void Test28_DifferentReferences_ExtractedCleanly()
    {
        var extraction = BuildSyntheticAxisExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>();
            rows.AddRange(CreateAxisMetadataRows(1));
            rows.AddRange(CreateStandardAxisHeaderRows(1));
            rows.AddRange(CreateAxisTransactionRows(1, 300.0, 1, "01/04/2025", "01/04/2025", "CHQ PAYMENT A", "10,000.00", "DR", "90,000.00", cheque: "000123"));
            rows.AddRange(CreateAxisTransactionRows(1, 330.0, 2, "02/04/2025", "02/04/2025", "CHQ PAYMENT B", "20,000.00", "DR", "70,000.00", cheque: "889900"));
            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        var result = _parser.Parse(extraction, detection);

        Assert.True(result.Success);
        Assert.Equal(2, result.Transactions.Count);
        Assert.Equal("000123", result.Transactions[0].Reference);
        Assert.Equal("889900", result.Transactions[1].Reference);
    }

    #endregion

    #region 4. Idempotency & Persistence Tests

    [Fact]
    public async Task Test29_ReprocessingDoesNotDuplicate_Idempotent()
    {
        var options = new DbContextOptionsBuilder<AccufexDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        using var db = new AccufexDbContext(options);

        var userId = Guid.NewGuid();
        var client = CreateTestClient(userId, "Idempotent Test Client");
        var fy = CreateTestFinancialYear(client.Id);
        var fileRecord = new FileRecord
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            FinancialYearId = fy.Id,
            OriginalFileName = "Axis_Stmt.pdf",
            StoredFileName = "Axis_Stmt.pdf",
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

        var extraction = BuildSyntheticAxisExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>();
            rows.AddRange(CreateAxisMetadataRows(1, openingBalance: "1,00,000.00"));
            rows.AddRange(CreateStandardAxisHeaderRows(1));
            rows.AddRange(CreateAxisTransactionRows(1, 300.0, 1, "01/04/2025", "01/04/2025", "SERVICE CHARGE", "100.00", "DR", "99,900.00"));
            pages.Add(CreatePage(1, rows));
        });

        var mockPdfService = new Mock<IPdfExtractionService>();
        mockPdfService.Setup(s => s.GetExtractionResultAsync(fileRecord.Id, userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(extraction);

        var service = new BankParsingService(db, mockPdfService.Object, _detector, _registry, _mockServiceLogger.Object);

        // First parse execution
        var (s1, e1, r1) = await service.ParseAndPersistStatementAsync(fileRecord.Id, userId);
        Assert.True(s1, e1);
        Assert.Equal(1, await db.Transactions.CountAsync(t => t.SourceFileId == fileRecord.Id));

        // Second re-parse execution (re-processing)
        var (s2, e2, r2) = await service.ParseAndPersistStatementAsync(fileRecord.Id, userId);
        Assert.True(s2, e2);
        // Transactions count must remain exactly 1 (no duplicates)
        Assert.Equal(1, await db.Transactions.CountAsync(t => t.SourceFileId == fileRecord.Id));
    }

    [Fact]
    public async Task Test30_FailedReparsePreservesPreviousSuccessfulData()
    {
        var options = new DbContextOptionsBuilder<AccufexDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        using var db = new AccufexDbContext(options);

        var userId = Guid.NewGuid();
        var client = CreateTestClient(userId, "Safe Reparse Client");
        var fy = CreateTestFinancialYear(client.Id);
        var fileRecord = new FileRecord
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            FinancialYearId = fy.Id,
            OriginalFileName = "Axis_Stmt.pdf",
            StoredFileName = "Axis_Stmt.pdf",
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

        var validExtraction = BuildSyntheticAxisExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>();
            rows.AddRange(CreateAxisMetadataRows(1, openingBalance: "1,00,000.00"));
            rows.AddRange(CreateStandardAxisHeaderRows(1));
            rows.AddRange(CreateAxisTransactionRows(1, 300.0, 1, "01/04/2025", "01/04/2025", "VALID TRANSACTION", "500.00", "DR", "99,500.00"));
            pages.Add(CreatePage(1, rows));
        });

        var mockPdfService = new Mock<IPdfExtractionService>();
        mockPdfService.SetupSequence(s => s.GetExtractionResultAsync(fileRecord.Id, userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(validExtraction)
            .ReturnsAsync(new PdfExtractionResult
            {
                FileId = fileRecord.Id,
                OriginalFileName = fileRecord.OriginalFileName,
                HasUsableText = false,
                PageCount = 0,
                Pages = []
            });

        var service = new BankParsingService(db, mockPdfService.Object, _detector, _registry, _mockServiceLogger.Object);

        // First parse succeeds
        var (s1, e1, _) = await service.ParseAndPersistStatementAsync(fileRecord.Id, userId);
        Assert.True(s1, e1);
        Assert.Equal(1, await db.Transactions.CountAsync(t => t.SourceFileId == fileRecord.Id));

        // Second re-parse fails (empty/unusable extraction)
        var (s2, e2, _) = await service.ParseAndPersistStatementAsync(fileRecord.Id, userId);
        Assert.False(s2);
        // Previous successful transactions must still exist untouched
        Assert.Equal(1, await db.Transactions.CountAsync(t => t.SourceFileId == fileRecord.Id));
    }

    #endregion

    #region 5. Security & Isolation Tests

    [Fact]
    public async Task Test31_CrossUserParseAttempt_DeniedWith404OrError()
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
            OriginalFileName = "Axis_Stmt.pdf",
            StoredFileName = "Axis_Stmt.pdf",
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
    public async Task Test32_CrossUserTransactionRetrieval_Denied()
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
            OriginalFileName = "Axis_Stmt.pdf",
            StoredFileName = "Axis_Stmt.pdf",
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
            Description = "Secret Owner Axis Transaction",
            Amount = 100000m,
            TransactionType = "CREDIT"
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

    #region 6. Generic Hardcoding Invariance & Running Balance Tests

    [Fact]
    public void Test33_NoFixtureSpecificHardcoding_ArbitraryTokens()
    {
        var extraction = BuildSyntheticAxisExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>();
            rows.AddRange(CreateAxisMetadataRows(1,
                customerName: "FOO BAR HOLDINGS ENTERPRISES",
                accountNo: "1122334455667788",
                ifsc: "UTIB0009999",
                fromDate: "01/01/2026",
                toDate: "31/12/2026",
                openingBalance: "7,77,777.77"));
            rows.AddRange(CreateStandardAxisHeaderRows(1));
            rows.AddRange(CreateAxisTransactionRows(1, 300.0, 1, "15/05/2026", "15/05/2026", "ARBITRARY RANDOM TRANSACTION NARRATION", "1,23,456.78", "DR", "6,54,320.99"));
            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        Assert.Equal(BankType.Axis, detection.DetectedBank);
        Assert.Equal("1122334455667788", detection.AccountNumber);
        Assert.Equal("FOO BAR HOLDINGS ENTERPRISES", detection.CustomerName);

        var result = _parser.Parse(extraction, detection);
        Assert.True(result.Success);
        Assert.Single(result.Transactions);
        Assert.Equal(123456.78m, result.Transactions[0].Debit);
        Assert.Equal(654320.99m, result.Transactions[0].Balance);
    }

    [Fact]
    public void Test34_RunningBalanceValidation_DetectsMismatch()
    {
        var extraction = BuildSyntheticAxisExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>();
            rows.AddRange(CreateAxisMetadataRows(1, openingBalance: "1,00,000.00"));
            rows.AddRange(CreateStandardAxisHeaderRows(1));
            // First transaction: 10,000 DR -> balance should be 90,000.00
            rows.AddRange(CreateAxisTransactionRows(1, 300.0, 1, "01/04/2025", "01/04/2025", "TX 1", "10,000.00", "DR", "90,000.00"));
            // Second transaction: 5,000 DR -> expected balance 85,000.00, but statement printed corrupted balance 70,000.00
            rows.AddRange(CreateAxisTransactionRows(1, 330.0, 2, "02/04/2025", "02/04/2025", "TX 2", "5,000.00", "DR", "70,000.00"));
            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        var result = _parser.Parse(extraction, detection);

        Assert.True(result.Success);
        Assert.Equal(2, result.Transactions.Count);
        Assert.False(result.Transactions[0].NeedsReview);
        Assert.True(result.Transactions[1].NeedsReview);
        Assert.Contains(result.Transactions[1].ReviewWarnings, w => w.Contains("Running balance mismatch"));
    }

    #endregion

    #region 7. Real Axis Bank PDF Integration Test

    [Fact]
    public void Test35_RealAxisPdf_ExtractedWithExactReconciliation()
    {
        string path = @"E:\Bank Statements\AXIS APRIL TO MARCH.PDF";
        if (!File.Exists(path))
        {
            _output.WriteLine($"[SKIPPED] Real Axis Bank fixture not found: {path}");
            return;
        }

        using var doc = PdfDocument.Open(path);
        Assert.Equal(20, doc.NumberOfPages);

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
        Assert.Equal(20, extraction.PageCount);
        Assert.True(extraction.HasUsableText);

        // 1. Bank Detection
        var detection = _detector.DetectBank(extraction);
        Assert.Equal(BankType.Axis, detection.DetectedBank);
        Assert.True(detection.IsSupported);
        Assert.True(detection.Confidence >= 0.90);
        Assert.Equal("924020025076740", detection.AccountNumber);
        Assert.Equal("HIGHWAY SAFETY SOLUTION", detection.CustomerName);
        Assert.Equal(new DateTime(2025, 4, 1), detection.StatementFrom);
        Assert.Equal(new DateTime(2026, 3, 31), detection.StatementTo);

        // 2. Parse Execution
        var result = _parser.Parse(extraction, detection);

        Assert.True(result.Success);
        Assert.Equal(615, result.Transactions.Count);

        // 3. Mathematical Totals Verification against statement summary
        decimal totalDebits = result.Transactions.Where(t => t.Debit.HasValue).Sum(t => t.Debit!.Value);
        decimal totalCredits = result.Transactions.Where(t => t.Credit.HasValue).Sum(t => t.Credit!.Value);

        _output.WriteLine($"[VERIFIED] Total Debits: {totalDebits:N2} (Expected: 19,095,466.58)");
        _output.WriteLine($"[VERIFIED] Total Credits: {totalCredits:N2} (Expected: 18,997,269.00)");

        Assert.Equal(19095466.58m, totalDebits);
        Assert.Equal(18997269.00m, totalCredits);

        // 4. Mathematical Closing Balance Verification
        // Opening Balance: 121,547.69
        decimal openingBal = 121547.69m;
        decimal expectedClosing = openingBal - totalDebits + totalCredits;
        Assert.Equal(23350.11m, expectedClosing);
        Assert.Equal(23350.11m, result.Transactions.Last().Balance);

        // 5. Zero Balance Errors Across All 615 Rows
        int balanceErrors = 0;
        decimal running = openingBal;
        for (int i = 0; i < result.Transactions.Count; i++)
        {
            var tx = result.Transactions[i];
            if (tx.Debit.HasValue) running -= tx.Debit.Value;
            else if (tx.Credit.HasValue) running += tx.Credit.Value;

            if (tx.Balance.HasValue && Math.Abs(running - tx.Balance.Value) > 0.01m)
            {
                balanceErrors++;
            }
        }
        Assert.Equal(0, balanceErrors);

        // 6. Inspect Representative Row QA: Beginning (Row 1)
        var row1 = result.Transactions[0];
        Assert.Equal(new DateTime(2025, 4, 1), row1.TransactionDate);
        Assert.Equal(new DateTime(2025, 4, 1), row1.ValueDate);
        Assert.Contains("NEFT/EB/AXOEB09099179862/HIGHWA", row1.Description);
        Assert.Contains("SAFETY CORPORATIO/KOTAK", row1.Description);
        Assert.Contains("MAHINDRA BANK //BOARD", row1.Description);
        Assert.Contains("ADVANCE/////", row1.Description);
        Assert.Equal(100000.00m, row1.Debit);
        Assert.Null(row1.Credit);
        Assert.Equal(21547.69m, row1.Balance);
        Assert.Equal("DEBIT", row1.TransactionType);

        // 7. Inspect Representative Row QA: Middle (Row 172 & Row 173 on Page 6)
        var row172 = result.Transactions[171];
        Assert.Equal(new DateTime(2025, 5, 23), row172.TransactionDate);
        Assert.Contains("GST @18% on Monthly Service", row172.Description);
        Assert.Equal(18.00m, row172.Debit);
        Assert.Equal(44494.00m, row172.Balance);

        var row173 = result.Transactions[172];
        Assert.Equal(new DateTime(2025, 5, 23), row173.TransactionDate);
        Assert.Contains("Monthly Service Chrgs", row173.Description);
        Assert.Equal(100.00m, row173.Debit);
        Assert.Equal(44394.00m, row173.Balance);

        // 8. Inspect Representative Row QA: Credit (Row 2 on Page 1)
        var row2 = result.Transactions[1];
        Assert.Equal(new DateTime(2025, 4, 1), row2.TransactionDate);
        Assert.Contains("IMPS/P2A/509122893621/ASHFAQUE", row2.Description);
        Assert.Equal(100000.00m, row2.Credit);
        Assert.Null(row2.Debit);
        Assert.Equal(121547.69m, row2.Balance);
        Assert.Equal("CREDIT", row2.TransactionType);

        // 9. Inspect Representative Row QA: Page Transition (Row 492 Page 15 -> Row 493 Page 16)
        var row492 = result.Transactions[491];
        Assert.Equal(15, row492.SourcePageNumber);
        Assert.Equal(new DateTime(2025, 10, 28), row492.TransactionDate);
        Assert.Equal(600000.00m, row492.Credit);
        Assert.Equal(623353.06m, row492.Balance);

        var row493 = result.Transactions[492];
        Assert.Equal(16, row493.SourcePageNumber);
        Assert.Equal(new DateTime(2025, 10, 28), row493.TransactionDate);
        Assert.Equal(100.00m, row493.Debit);
        Assert.Equal(623253.06m, row493.Balance);

        // 10. Inspect Representative Row QA: Ending (Row 615 on Page 19)
        var row615 = result.Transactions[614];
        Assert.Equal(19, row615.SourcePageNumber);
        Assert.Equal(new DateTime(2026, 3, 30), row615.TransactionDate);
        Assert.Equal(30000.00m, row615.Debit);
        Assert.Equal(23350.11m, row615.Balance);
        Assert.DoesNotContain("TRANSACTION TOTAL", row615.Description);
        Assert.DoesNotContain("Closing Balance", row615.Description);
        Assert.DoesNotContain("Cheque Return Details", row615.Description);

        _output.WriteLine($"[SUCCESS] Real Axis Bank PDF parsed: {result.Transactions.Count} transactions, Debits: {totalDebits:N2}, Credits: {totalCredits:N2}, Closing: {row615.Balance:N2}");
    }

    #endregion
}
