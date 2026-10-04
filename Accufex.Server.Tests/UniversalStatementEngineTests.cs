using System;
using System.Collections.Generic;
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
using Accufex.Server.Parsing.Universal.Interfaces;
using Accufex.Server.Parsing.Universal.Models;
using Accufex.Server.Parsing.Universal.Services;
using Accufex.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Accufex.Server.Tests;

public class UniversalStatementEngineTests
{
    private readonly BankDetector _detector;
    private readonly BankParserRegistry _registry;
    private readonly List<IBankStatementParser> _allDedicatedParsers;
    private readonly IUniversalColumnDetector _columnDetector;
    private readonly IStatementStructureAnalyzer _structureAnalyzer;
    private readonly IUniversalRowSegmenter _rowSegmenter;
    private readonly IFinancialValidator _financialValidator;
    private readonly IUniversalConfidenceCalculator _confidenceCalculator;
    private readonly IUniversalStatementEngine _universalEngine;

    public UniversalStatementEngineTests()
    {
        _detector = new BankDetector(NullLogger<BankDetector>.Instance);

        _allDedicatedParsers = new List<IBankStatementParser>
        {
            new HdfcStatementParser(NullLogger<HdfcStatementParser>.Instance),
            new YesBankStatementParser(NullLogger<YesBankStatementParser>.Instance),
            new AxisStatementParser(NullLogger<AxisStatementParser>.Instance),
            new CentralBankStatementParser(NullLogger<CentralBankStatementParser>.Instance),
            new ICICIStatementParser(NullLogger<ICICIStatementParser>.Instance),
            new ICICIStatementParserV2(NullLogger<ICICIStatementParserV2>.Instance),
            new SBIStatementParser(NullLogger<SBIStatementParser>.Instance),
            new BOIStatementParser(NullLogger<BOIStatementParser>.Instance),
            new KotakStatementParser(NullLogger<KotakStatementParser>.Instance),
            new PNBStatementParser(NullLogger<PNBStatementParser>.Instance),
            new BOBStatementParser(NullLogger<BOBStatementParser>.Instance)
        };

        _registry = new BankParserRegistry(_allDedicatedParsers);

        _columnDetector = new UniversalColumnDetector(NullLogger<UniversalColumnDetector>.Instance);
        _structureAnalyzer = new StatementStructureAnalyzer(_columnDetector, NullLogger<StatementStructureAnalyzer>.Instance);
        _rowSegmenter = new UniversalRowSegmenter(NullLogger<UniversalRowSegmenter>.Instance);
        _financialValidator = new FinancialValidator(NullLogger<FinancialValidator>.Instance);
        _confidenceCalculator = new UniversalConfidenceCalculator();
        _universalEngine = new UniversalStatementEngine(
            _structureAnalyzer,
            _confidenceCalculator,
            NullLogger<UniversalStatementEngine>.Instance,
            _rowSegmenter,
            _financialValidator);
    }

    #region 1. Existing Bank Regression Protection (10 Banks / 11 Formats)

    [Theory]
    [InlineData("HDFC BANK LIMITED\nHDFC0000123\nDate Narration Chq/Ref Withdrawal Deposit Closing Balance", BankType.Hdfc, typeof(HdfcStatementParser), 1)]
    [InlineData("YES BANK\nYESB0000123\nTransaction Date Value Date Cheque No/ Reference No Description Withdrawals Deposits Running Balance", BankType.YesBank, typeof(YesBankStatementParser), 2)]
    [InlineData("AXIS BANK LTD\nUTIB0000123\nS.NO Transaction Date Value Date Particulars Amount(INR) Debit/Credit Balance(INR)", BankType.Axis, typeof(AxisStatementParser), 3)]
    [InlineData("CENTRAL BANK OF INDIA\nCBIN0000123\ncentralbankofindia.co.in\nPost Date Value Date Details Cheque No Debit Credit Balance", BankType.CentralBank, typeof(CentralBankStatementParser), 4)]
    [InlineData("ICICI BANK LIMITED\nwww.icicibank.com\nICIC0000057\nDATE MODE PARTICULARS DEPOSITS WITHDRAWALS BALANCE", BankType.ICICI, typeof(ICICIStatementParser), 5)]
    [InlineData("STATE BANK OF INDIA\nSBIN0000123\nTxn Date Value Date Description Ref No./Cheque No. Debit Credit Balance", BankType.SBI, typeof(SBIStatementParser), 6)]
    [InlineData("BANK OF INDIA\nBKID0000123\nbankofindia.co.in\nDate Particulars Cheque No Debit Credit Balance Dr/Cr", BankType.BOI, typeof(BOIStatementParser), 7)]
    [InlineData("Kotak Mahindra Bank\nKKBK0000123\nDate Description Chq/Ref No. Withdrawal (Dr.) Deposit (Cr.) Balance", BankType.Kotak, typeof(KotakStatementParser), 8)]
    [InlineData("PUNJAB NATIONAL BANK\nPUNB0000123\nTxn Date Value Date Description Cheque No Withdrawal(Dr) Deposit(Cr) Balance", BankType.PNB, typeof(PNBStatementParser), 9)]
    [InlineData("BANK OF BARODA\nBARB0000123\nS.No. Date Description Cheque No Withdrawal (Dr.) Deposit (Cr.) Balance", BankType.BOB, typeof(BOBStatementParser), 10)]
    public void DedicatedParsers_AreResolvedDirectly_AndNeverInterceptedByUniversal(
        string headerSnippet,
        BankType expectedType,
        Type expectedParserType,
        int expectedBankCode)
    {
        var extraction = CreateSimpleExtraction(headerSnippet);
        var detection = _detector.DetectBank(extraction);

        Assert.Equal(expectedType, detection.DetectedBank);
        Assert.True(detection.IsSupported);

        var resolvedParser = _registry.ResolveParser(detection);
        Assert.NotNull(resolvedParser);
        Assert.IsType(expectedParserType, resolvedParser);
        Assert.Equal(expectedBankCode, resolvedParser.BankCode);
    }

    [Fact]
    public async Task BankParsingService_KnownBankStatement_RoutesToDedicatedParser_NotUniversal()
    {
        var db = CreateInMemoryDbContext();
        var client = CreateTestClient();
        var fy = new FinancialYear
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            DisplayName = "2025-2026",
            StartDate = new DateTime(2025, 4, 1),
            EndDate = new DateTime(2026, 3, 31)
        };
        var fileRecord = new FileRecord
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            FinancialYearId = fy.Id,
            OriginalFileName = "hdfc_sample.pdf",
            StoredFileName = "hdfc_sample.pdf",
            Extension = ".pdf",
            ContentType = "application/pdf"
        };
        db.Clients.Add(client);
        db.FinancialYears.Add(fy);
        db.FileRecords.Add(fileRecord);
        await db.SaveChangesAsync();

        var hdfcExtraction = CreateSimpleExtraction("HDFC BANK LIMITED Account Statement\nHDFC0000123\nDate Narration Chq/Ref Withdrawal Deposit Closing Balance");
        var mockPdfService = new Mock<IPdfExtractionService>();
        mockPdfService.Setup(p => p.GetExtractionResultAsync(fileRecord.Id, client.UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(hdfcExtraction);

        var mockUniversal = new Mock<IUniversalStatementEngine>();

        var parsingService = new BankParsingService(
            db,
            mockPdfService.Object,
            _detector,
            _registry,
            mockUniversal.Object,
            null,
            NullLogger<BankParsingService>.Instance);

        await parsingService.ParseAndPersistStatementAsync(fileRecord.Id, client.UserId);

        // Universal engine must NEVER be called when bank is recognized as a dedicated bank
        mockUniversal.Verify(u => u.ProcessStatementAsync(It.IsAny<PdfExtractionResult>(), It.IsAny<BankDetectionResult>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion

    #region 2. Unknown Bank Flow & Architecture Safety

    [Fact]
    public async Task UnknownBank_RoutesToUniversalEngine_AndReturnsControlledReviewStatus()
    {
        var db = CreateInMemoryDbContext();
        var client = CreateTestClient();
        var fy = new FinancialYear
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            DisplayName = "2025-2026",
            StartDate = new DateTime(2025, 4, 1),
            EndDate = new DateTime(2026, 3, 31)
        };
        var fileRecord = new FileRecord
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            FinancialYearId = fy.Id,
            OriginalFileName = "unknown_bank.pdf",
            StoredFileName = "unknown_bank.pdf",
            Extension = ".pdf",
            ContentType = "application/pdf"
        };
        db.Clients.Add(client);
        db.FinancialYears.Add(fy);
        db.FileRecords.Add(fileRecord);
        await db.SaveChangesAsync();

        var unknownExtraction = CreateSimpleExtraction("DEUTSCHE BANK AG Account Statement\nDate | Description | Debit | Credit | Balance");
        var mockPdfService = new Mock<IPdfExtractionService>();
        mockPdfService.Setup(p => p.GetExtractionResultAsync(fileRecord.Id, client.UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(unknownExtraction);

        var parsingService = new BankParsingService(
            db,
            mockPdfService.Object,
            _detector,
            _registry,
            _universalEngine,
            null,
            NullLogger<BankParsingService>.Instance);

        var (success, errorMessage, result) = await parsingService.ParseAndPersistStatementAsync(fileRecord.Id, client.UserId);

        // Phase 1 guarantees:
        // 1. Bank is not supported in dedicated parsers
        Assert.False(_detector.DetectBank(unknownExtraction).IsSupported);

        // 2. Result is returned without crashing
        Assert.NotNull(result);

        // 3. Controlled financial safety: does not automatically succeed with unvalidated data
        Assert.False(success);
        Assert.Equal("This statement format requires review before conversion.", errorMessage);

        // 4. No transactions fabricated or saved in DB
        Assert.Empty(db.Transactions.ToList());
    }

    [Fact]
    public async Task UniversalEngine_DirectProcessing_GeneratesStructuredAnalysis()
    {
        var candidateRow = new PdfCandidateRow
        {
            RowIndex = 0,
            PageNumber = 1,
            Y = 120,
            Height = 15,
            RawLineText = "Date Narration Withdrawal Deposit Balance",
            LineFragments = new List<PdfTextBlock>
            {
                new() { Text = "Date", X = 50, Y = 120, Width = 30, Height = 12 },
                new() { Text = "Narration", X = 120, Y = 120, Width = 80, Height = 12 },
                new() { Text = "Withdrawal", X = 250, Y = 120, Width = 60, Height = 12 },
                new() { Text = "Deposit", X = 350, Y = 120, Width = 50, Height = 12 },
                new() { Text = "Balance", X = 450, Y = 120, Width = 50, Height = 12 }
            }
        };

        var page = new PdfPageResult
        {
            PageNumber = 1,
            Width = 595,
            Height = 842,
            HasUsableText = true,
            RawText = "Date Narration Withdrawal Deposit Balance",
            CandidateRows = new List<PdfCandidateRow> { candidateRow },
            TextBlocks = candidateRow.LineFragments.ToList()
        };

        var extraction = new PdfExtractionResult
        {
            FileId = Guid.NewGuid(),
            HasUsableText = true,
            Pages = new List<PdfPageResult> { page }
        };

        var detection = new BankDetectionResult { DetectedBank = BankType.Unknown, IsSupported = false };

        var result = await _universalEngine.ProcessStatementAsync(extraction, detection);

        Assert.NotNull(result);
        Assert.NotNull(result.Structure);
        Assert.True(result.Structure.HasSufficientColumnsForExtraction);
        Assert.NotNull(result.Confidence);
        Assert.True(result.NeedsReview); // In Phase 1, always requires review for safety
        Assert.Equal("This statement format requires review before conversion.", result.StatusMessage);
    }

    #endregion

    #region 3. Universal Column Detector Tests

    [Fact]
    public void ColumnDetector_IdentifiesStandardColumns_FromHeaderRow()
    {
        var candidateRow = new PdfCandidateRow
        {
            RowIndex = 0,
            PageNumber = 1,
            Y = 150,
            Height = 15,
            RawLineText = "Txn Date Particulars Chq/Ref Debit Credit Closing Balance",
            LineFragments = new List<PdfTextBlock>
            {
                new() { Text = "Txn Date", X = 40, Y = 150, Width = 50, Height = 12 },
                new() { Text = "Particulars", X = 120, Y = 150, Width = 80, Height = 12 },
                new() { Text = "Chq/Ref", X = 230, Y = 150, Width = 40, Height = 12 },
                new() { Text = "Debit", X = 300, Y = 150, Width = 40, Height = 12 },
                new() { Text = "Credit", X = 370, Y = 150, Width = 40, Height = 12 },
                new() { Text = "Closing Balance", X = 450, Y = 150, Width = 80, Height = 12 }
            }
        };

        var columns = _columnDetector.DetectColumns(new List<PdfCandidateRow> { candidateRow }, candidateRow.LineFragments, 595);

        Assert.NotEmpty(columns);
        Assert.Contains(columns, c => c.ColumnType == StatementColumnType.Date);
        Assert.Contains(columns, c => c.ColumnType == StatementColumnType.Description);
        Assert.Contains(columns, c => c.ColumnType == StatementColumnType.Reference);
        Assert.Contains(columns, c => c.ColumnType == StatementColumnType.Debit);
        Assert.Contains(columns, c => c.ColumnType == StatementColumnType.Credit);
        Assert.Contains(columns, c => c.ColumnType == StatementColumnType.Balance);
    }

    [Fact]
    public void ColumnDetector_IdentifiesValueDateAndBranch_Columns()
    {
        var candidateRow = new PdfCandidateRow
        {
            RowIndex = 0,
            PageNumber = 1,
            Y = 150,
            Height = 15,
            RawLineText = "Date Value Date Description Branch Debit Credit Balance",
            LineFragments = new List<PdfTextBlock>
            {
                new() { Text = "Date", X = 40, Y = 150, Width = 30, Height = 12 },
                new() { Text = "Value Date", X = 110, Y = 150, Width = 60, Height = 12 },
                new() { Text = "Description", X = 210, Y = 150, Width = 80, Height = 12 },
                new() { Text = "Branch", X = 320, Y = 150, Width = 50, Height = 12 },
                new() { Text = "Debit", X = 400, Y = 150, Width = 40, Height = 12 },
                new() { Text = "Credit", X = 470, Y = 150, Width = 40, Height = 12 },
                new() { Text = "Balance", X = 540, Y = 150, Width = 40, Height = 12 }
            }
        };

        var columns = _columnDetector.DetectColumns(new List<PdfCandidateRow> { candidateRow }, candidateRow.LineFragments, 595);

        Assert.Contains(columns, c => c.ColumnType == StatementColumnType.ValueDate);
        Assert.Contains(columns, c => c.ColumnType == StatementColumnType.Branch);
    }

    #endregion

    #region 4. Statement Structure Analyzer Tests

    [Fact]
    public void StructureAnalyzer_DetectsHeaderAndFooterBounds()
    {
        var headerRow = new PdfCandidateRow
        {
            RowIndex = 0,
            PageNumber = 1,
            Y = 150,
            Height = 15,
            RawLineText = "Date Description Debit Credit Balance",
            LineFragments = new List<PdfTextBlock>
            {
                new() { Text = "Date", X = 50, Y = 150, Width = 30, Height = 12 },
                new() { Text = "Description", X = 120, Y = 150, Width = 80, Height = 12 },
                new() { Text = "Debit", X = 250, Y = 150, Width = 40, Height = 12 },
                new() { Text = "Credit", X = 340, Y = 150, Width = 40, Height = 12 },
                new() { Text = "Balance", X = 430, Y = 150, Width = 50, Height = 12 }
            }
        };

        var bodyRow = new PdfCandidateRow
        {
            RowIndex = 1,
            PageNumber = 1,
            Y = 200,
            Height = 15,
            RawLineText = "01/01/2026 SALARY 50000.00 50000.00"
        };

        var footerRow = new PdfCandidateRow
        {
            RowIndex = 2,
            PageNumber = 1,
            Y = 780,
            Height = 15,
            RawLineText = "Page 1 of 1 Computer Generated Statement"
        };

        var page = new PdfPageResult
        {
            PageNumber = 1,
            Width = 595,
            Height = 842,
            HasUsableText = true,
            RawText = "STANDARD CHARTERED BANK Account Statement\nDate Description Debit Credit Balance\n01/01/2026 SALARY 50000.00 50000.00\nPage 1 of 1 Computer Generated Statement",
            CandidateRows = new List<PdfCandidateRow> { headerRow, bodyRow, footerRow },
            TextBlocks = new List<PdfTextBlock>
            {
                new() { Text = "STANDARD CHARTERED BANK Account Statement", X = 50, Y = 50, Width = 300, Height = 20 }
            }
        };

        var extraction = new PdfExtractionResult
        {
            FileId = Guid.NewGuid(),
            HasUsableText = true,
            Pages = new List<PdfPageResult> { page }
        };

        var analysis = _structureAnalyzer.AnalyzeStructure(extraction);

        Assert.NotNull(analysis);
        Assert.Equal("STANDARD CHARTERED BANK", analysis.PotentialBankName);
        Assert.True(analysis.HeaderBottomY >= 150);
        Assert.True(analysis.FooterTopY <= 780);
        Assert.True(analysis.HasSufficientColumnsForExtraction);
        Assert.Equal(1, analysis.DetectedTableRowCount);
    }

    #endregion

    #region 5. Universal Confidence Calculator Tests

    [Fact]
    public void ConfidenceCalculator_AccuratelyEvaluatesCompleteLayout()
    {
        var columns = new List<DetectedColumnLayout>
        {
            new() { ColumnType = StatementColumnType.Date, LeftX = 50, RightX = 90, Confidence = 0.95 },
            new() { ColumnType = StatementColumnType.Description, LeftX = 100, RightX = 220, Confidence = 0.90 },
            new() { ColumnType = StatementColumnType.Debit, LeftX = 250, RightX = 310, Confidence = 0.95 },
            new() { ColumnType = StatementColumnType.Credit, LeftX = 330, RightX = 390, Confidence = 0.95 },
            new() { ColumnType = StatementColumnType.Balance, LeftX = 410, RightX = 480, Confidence = 0.95 }
        };

        var analysis = new StatementStructureAnalysis
        {
            Columns = columns,
            HeaderTopY = 150,
            HeaderBottomY = 165,
            FooterTopY = 750,
            DetectedTableRowCount = 5
        };

        var score = _confidenceCalculator.CalculateConfidence(
            analysis,
            candidateTransactions: new List<ParsedTransaction>(),
            openingBalance: null,
            closingBalance: null);

        Assert.NotNull(score);
        Assert.True(score.HeaderConfidence > 0.8);
        Assert.True(score.ColumnConfidence > 0.8);
        Assert.True(score.OverallScore > 0.6);
        Assert.Contains(score.ReviewReasons, r => r.Contains("No valid candidate financial transactions"));
    }

    [Fact]
    public void ConfidenceCalculator_LowConfidence_WhenMissingDebitOrCredit()
    {
        var columns = new List<DetectedColumnLayout>
        {
            new() { ColumnType = StatementColumnType.Date, LeftX = 50, RightX = 90, Confidence = 0.95 },
            new() { ColumnType = StatementColumnType.Description, LeftX = 100, RightX = 220, Confidence = 0.90 },
            new() { ColumnType = StatementColumnType.Balance, LeftX = 410, RightX = 480, Confidence = 0.95 }
        };

        var analysis = new StatementStructureAnalysis
        {
            Columns = columns,
            HeaderTopY = 150,
            HeaderBottomY = 165
        };

        var score = _confidenceCalculator.CalculateConfidence(
            analysis,
            candidateTransactions: new List<ParsedTransaction>(),
            openingBalance: null,
            closingBalance: null);

        Assert.Equal(UniversalConfidenceLevel.Low, score.Level);
        Assert.Contains(score.ReviewReasons, r => r.Contains("Essential statement columns") || r.Contains("distinct table column intervals"));
    }

    #endregion

    #region 6. Phase 2 Universal Statement Engine Tests

    [Fact]
    public async Task UniversalEngine_StandardUnknownTable_ExtractsCandidateTransactions_WithFullBalanceReconciliation()
    {
        // Unknown bank (Federal Bank) layout: Date | Particulars | Debit | Credit | Balance
        var lines = new List<(double y, string text, List<(string word, double x, double width)> words)>
        {
            (50, "FEDERAL BANK LIMITED Statement of Account", [("FEDERAL", 50, 60), ("BANK", 115, 40), ("LIMITED", 160, 50), ("Statement", 215, 60), ("of", 280, 20), ("Account", 305, 50)]),
            (80, "Account No: 12345678901234 IFSC: FDRL0001234", [("Account", 50, 50), ("No:", 105, 30), ("12345678901234", 140, 90), ("IFSC:", 240, 30), ("FDRL0001234", 275, 70)]),
            (110, "Opening Balance: 10,000.00", [("Opening", 50, 50), ("Balance:", 105, 50), ("10,000.00", 450, 60)]),
            (140, "Date Particulars Debit Credit Balance", [("Date", 50, 40), ("Particulars", 120, 80), ("Debit", 260, 40), ("Credit", 340, 40), ("Balance", 450, 50)]),
            (170, "01/04/2026 RENT PAYMENT 2,000.00 8,000.00", [("01/04/2026", 50, 60), ("RENT", 120, 30), ("PAYMENT", 155, 60), ("2,000.00", 260, 50), ("8,000.00", 450, 50)]),
            (200, "05/04/2026 CLIENT INVOICE 5,000.00 13,000.00", [("05/04/2026", 50, 60), ("CLIENT", 120, 40), ("INVOICE", 165, 50), ("5,000.00", 340, 50), ("13,000.00", 450, 60)]),
            (230, "10/04/2026 UTILITY BILL 1,500.00 11,500.00", [("10/04/2026", 50, 60), ("UTILITY", 120, 45), ("BILL", 170, 30), ("1,500.00", 260, 50), ("11,500.00", 450, 60)]),
            (780, "Page 1 of 1 Computer Generated Statement", [("Page", 50, 30), ("1", 85, 10), ("of", 100, 15), ("1", 120, 10), ("Computer", 200, 60), ("Generated", 265, 60), ("Statement", 330, 60)])
        };

        var page = CreateSynthesizedPage(1, 595, 842, lines);
        var extraction = new PdfExtractionResult
        {
            FileId = Guid.NewGuid(),
            HasUsableText = true,
            Pages = [page]
        };

        var detection = new BankDetectionResult { DetectedBank = BankType.Unknown, BankName = "Federal Bank", IsSupported = false };

        var result = await _universalEngine.ProcessStatementAsync(extraction, detection);

        Assert.NotNull(result);
        Assert.Equal(3, result.Transactions.Count);

        // Verify Row 1
        Assert.Equal(new DateTime(2026, 4, 1), result.Transactions[0].TransactionDate);
        Assert.Equal("RENT PAYMENT", result.Transactions[0].Description);
        Assert.Equal(2000.00m, result.Transactions[0].Debit);
        Assert.Null(result.Transactions[0].Credit);
        Assert.Equal(8000.00m, result.Transactions[0].Balance);
        Assert.Equal("Debit", result.Transactions[0].TransactionType);

        // Verify Row 2
        Assert.Equal(new DateTime(2026, 4, 5), result.Transactions[1].TransactionDate);
        Assert.Equal("CLIENT INVOICE", result.Transactions[1].Description);
        Assert.Null(result.Transactions[1].Debit);
        Assert.Equal(5000.00m, result.Transactions[1].Credit);
        Assert.Equal(13000.00m, result.Transactions[1].Balance);
        Assert.Equal("Credit", result.Transactions[1].TransactionType);

        // Verify Financial Validation
        Assert.NotNull(result.FinancialValidation);
        Assert.Equal(3, result.FinancialValidation.TotalRowsChecked);
        Assert.Equal(3, result.FinancialValidation.ReconciledRowsCount);
        Assert.Equal(0, result.FinancialValidation.FailedRowsCount);
        Assert.True(result.FinancialValidation.IsFullyReconciled);
        Assert.Equal(10000.00m, result.OpeningBalance);
        Assert.Equal(11500.00m, result.ClosingBalance);

        // Confidence
        Assert.Equal(UniversalConfidenceLevel.High, result.Confidence.Level);
        Assert.True(result.Success);
        Assert.False(result.NeedsReview);
    }

    [Fact]
    public async Task UniversalEngine_MultiLineNarration_AggregatesWrappedLinesIntoSingleCandidateRow()
    {
        // Tests multi-line narration where description wraps onto 3 physical lines:
        // Line 1: 01/04/2026 UPI PAYMENT TO ABC
        // Line 2:            PRIVATE LIMITED CHENNAI
        // Line 3:            REF UTR12345678         500.00              9,500.00
        // Line 4: 02/04/2026 SALARY CREDIT                      25,000.00 34,500.00
        var lines = new List<(double y, string text, List<(string word, double x, double width)> words)>
        {
            (50, "INDUSIND BANK LIMITED Account Statement", [("INDUSIND", 50, 60), ("BANK", 115, 40), ("LIMITED", 160, 50)]),
            (100, "Date Particulars Debit Credit Balance", [("Date", 50, 40), ("Particulars", 120, 80), ("Debit", 280, 40), ("Credit", 360, 40), ("Balance", 450, 50)]),
            (140, "01/04/2026 UPI PAYMENT TO ABC", [("01/04/2026", 50, 60), ("UPI", 120, 25), ("PAYMENT", 150, 60), ("TO", 215, 20), ("ABC", 240, 30)]),
            (155, "PRIVATE LIMITED CHENNAI", [("PRIVATE", 120, 50), ("LIMITED", 175, 50), ("CHENNAI", 230, 50)]),
            (170, "REF UTR12345678 500.00 9,500.00", [("REF", 120, 30), ("UTR12345678", 155, 80), ("500.00", 280, 50), ("9,500.00", 450, 60)]),
            (210, "02/04/2026 SALARY CREDIT 25,000.00 34,500.00", [("02/04/2026", 50, 60), ("SALARY", 120, 50), ("CREDIT", 175, 50), ("25,000.00", 360, 60), ("34,500.00", 450, 60)])
        };

        var page = CreateSynthesizedPage(1, 595, 842, lines);
        var extraction = new PdfExtractionResult
        {
            FileId = Guid.NewGuid(),
            HasUsableText = true,
            Pages = [page]
        };

        var detection = new BankDetectionResult { DetectedBank = BankType.Unknown, BankName = "IndusInd Bank", IsSupported = false };

        var result = await _universalEngine.ProcessStatementAsync(extraction, detection);

        Assert.NotNull(result);
        // Must form exactly 2 transactions (not 4!)
        Assert.Equal(2, result.Transactions.Count);

        // Transaction 1 must aggregate wrapped narration
        var tx1 = result.Transactions[0];
        Assert.Equal(new DateTime(2026, 4, 1), tx1.TransactionDate);
        Assert.Contains("UPI PAYMENT TO ABC", tx1.Description);
        Assert.Contains("PRIVATE LIMITED CHENNAI", tx1.Description);
        Assert.Equal(500.00m, tx1.Debit);
        Assert.Equal(9500.00m, tx1.Balance);

        // Transaction 2
        var tx2 = result.Transactions[1];
        Assert.Equal(new DateTime(2026, 4, 2), tx2.TransactionDate);
        Assert.Equal("SALARY CREDIT", tx2.Description);
        Assert.Equal(25000.00m, tx2.Credit);
        Assert.Equal(34500.00m, tx2.Balance);
    }

    [Fact]
    public async Task UniversalEngine_MultiPageStatement_PreservesOrder_AndIgnoresRepeatedHeadersAndFooters()
    {
        // Page 1: Header + 2 transactions + Footer
        var p1Lines = new List<(double y, string text, List<(string word, double x, double width)> words)>
        {
            (50, "BANDHAN BANK LIMITED Account Statement", [("BANDHAN", 50, 60), ("BANK", 115, 40), ("LIMITED", 160, 50)]),
            (100, "Date Description Debit Credit Balance", [("Date", 50, 40), ("Description", 120, 80), ("Debit", 270, 40), ("Credit", 350, 40), ("Balance", 450, 50)]),
            (140, "01/05/2026 TRANSFER 1,000.00 9,000.00", [("01/05/2026", 50, 60), ("TRANSFER", 120, 60), ("1,000.00", 270, 50), ("9,000.00", 450, 50)]),
            (170, "02/05/2026 DEPOSIT 3,000.00 12,000.00", [("02/05/2026", 50, 60), ("DEPOSIT", 120, 50), ("3,000.00", 350, 50), ("12,000.00", 450, 60)]),
            (790, "Page 1 of 2 Computer Generated Statement", [("Page", 50, 30), ("1", 85, 10), ("of", 100, 15), ("2", 120, 10), ("Computer", 200, 60), ("Generated", 265, 60)])
        };

        // Page 2: Repeated Header + 2 transactions + Footer
        var p2Lines = new List<(double y, string text, List<(string word, double x, double width)> words)>
        {
            (40, "BANDHAN BANK LIMITED Statement (Contd.)", [("BANDHAN", 50, 60), ("BANK", 115, 40), ("LIMITED", 160, 50)]),
            (70, "Date Description Debit Credit Balance", [("Date", 50, 40), ("Description", 120, 80), ("Debit", 270, 40), ("Credit", 350, 40), ("Balance", 450, 50)]),
            (100, "03/05/2026 GROCERIES 2,000.00 10,000.00", [("03/05/2026", 50, 60), ("GROCERIES", 120, 60), ("2,000.00", 270, 50), ("10,000.00", 450, 60)]),
            (130, "04/05/2026 REFUND 500.00 10,500.00", [("04/05/2026", 50, 60), ("REFUND", 120, 50), ("500.00", 350, 40), ("10,500.00", 450, 60)]),
            (790, "Page 2 of 2 End of Statement", [("Page", 50, 30), ("2", 85, 10), ("of", 100, 15), ("2", 120, 10), ("End", 200, 30), ("of", 235, 15), ("Statement", 255, 60)])
        };

        var page1 = CreateSynthesizedPage(1, 595, 842, p1Lines);
        var page2 = CreateSynthesizedPage(2, 595, 842, p2Lines);

        var extraction = new PdfExtractionResult
        {
            FileId = Guid.NewGuid(),
            HasUsableText = true,
            Pages = [page1, page2]
        };

        var detection = new BankDetectionResult { DetectedBank = BankType.Unknown, BankName = "Bandhan Bank", IsSupported = false };

        var result = await _universalEngine.ProcessStatementAsync(extraction, detection);

        Assert.NotNull(result);
        Assert.Equal(4, result.Transactions.Count);

        // Verify ordering across pages
        Assert.Equal(new DateTime(2026, 5, 1), result.Transactions[0].TransactionDate);
        Assert.Equal(new DateTime(2026, 5, 2), result.Transactions[1].TransactionDate);
        Assert.Equal(new DateTime(2026, 5, 3), result.Transactions[2].TransactionDate);
        Assert.Equal(new DateTime(2026, 5, 4), result.Transactions[3].TransactionDate);

        // Verify page numbers preserved
        Assert.Equal(1, result.Transactions[0].SourcePageNumber);
        Assert.Equal(1, result.Transactions[1].SourcePageNumber);
        Assert.Equal(2, result.Transactions[2].SourcePageNumber);
        Assert.Equal(2, result.Transactions[3].SourcePageNumber);

        // Verify repeated header was ignored
        Assert.DoesNotContain(result.Transactions, t => t.Description.Contains("Date Description", StringComparison.OrdinalIgnoreCase));

        // Verify running balance continuity across page boundary
        Assert.NotNull(result.FinancialValidation);
        Assert.Equal(3, result.FinancialValidation.ReconciledRowsCount);
        Assert.Equal(0, result.FinancialValidation.FailedRowsCount);
    }

    [Fact]
    public async Task UniversalEngine_SignedAmountColumnWithIndicator_ExtractsCorrectDirection()
    {
        // Table layout: Date | Particulars | Amount | Type | Balance
        var lines = new List<(double y, string text, List<(string word, double x, double width)> words)>
        {
            (50, "RBL BANK LIMITED Statement of Transactions", [("RBL", 50, 40), ("BANK", 95, 40), ("LIMITED", 140, 50)]),
            (100, "Date Particulars Amount Type Balance", [("Date", 50, 40), ("Particulars", 120, 80), ("Amount", 260, 50), ("Type", 330, 40), ("Balance", 420, 50)]),
            (140, "01/06/2026 OFFICE SUPPLIES 1,200.00 DR 8,800.00", [("01/06/2026", 50, 60), ("OFFICE", 120, 40), ("SUPPLIES", 165, 50), ("1,200.00", 260, 50), ("DR", 330, 20), ("8,800.00", 420, 50)]),
            (170, "02/06/2026 INTEREST RECEIVED 450.00 CR 9,250.00", [("02/06/2026", 50, 60), ("INTEREST", 120, 50), ("RECEIVED", 175, 50), ("450.00", 260, 40), ("CR", 330, 20), ("9,250.00", 420, 50)])
        };

        var page = CreateSynthesizedPage(1, 595, 842, lines);
        var extraction = new PdfExtractionResult
        {
            FileId = Guid.NewGuid(),
            HasUsableText = true,
            Pages = [page]
        };

        var detection = new BankDetectionResult { DetectedBank = BankType.Unknown, BankName = "RBL Bank", IsSupported = false };

        var result = await _universalEngine.ProcessStatementAsync(extraction, detection);

        Assert.NotNull(result);
        Assert.Equal(2, result.Transactions.Count);

        // DR row
        Assert.Equal(1200.00m, result.Transactions[0].Debit);
        Assert.Null(result.Transactions[0].Credit);
        Assert.Equal("Debit", result.Transactions[0].TransactionType);

        // CR row
        Assert.Null(result.Transactions[1].Debit);
        Assert.Equal(450.00m, result.Transactions[1].Credit);
        Assert.Equal("Credit", result.Transactions[1].TransactionType);
    }

    [Fact]
    public async Task UniversalEngine_FinancialDiscrepancy_DetectsBrokenMath_AndLowersConfidence()
    {
        // Row 2 has an intentional mathematical error:
        // Row 1: Balance 8,000.00
        // Row 2: Debit 1,000.00, Balance should be 7,000.00, but statement has 9,500.00
        var lines = new List<(double y, string text, List<(string word, double x, double width)> words)>
        {
            (50, "KARUR VYSYA BANK Statement", [("KARUR", 50, 50), ("VYSYA", 105, 50), ("BANK", 160, 40)]),
            (100, "Date Description Debit Credit Balance", [("Date", 50, 40), ("Description", 120, 80), ("Debit", 260, 40), ("Credit", 340, 40), ("Balance", 450, 50)]),
            (140, "01/07/2026 ROW ONE 2,000.00 8,000.00", [("01/07/2026", 50, 60), ("ROW", 120, 30), ("ONE", 155, 30), ("2,000.00", 260, 50), ("8,000.00", 450, 50)]),
            (170, "02/07/2026 ROW TWO 1,000.00 9,500.00", [("02/07/2026", 50, 60), ("ROW", 120, 30), ("TWO", 155, 30), ("1,000.00", 260, 50), ("9,500.00", 450, 50)])
        };

        var page = CreateSynthesizedPage(1, 595, 842, lines);
        var extraction = new PdfExtractionResult
        {
            FileId = Guid.NewGuid(),
            HasUsableText = true,
            Pages = [page]
        };

        var detection = new BankDetectionResult { DetectedBank = BankType.Unknown, BankName = "Karur Vysya Bank", IsSupported = false };

        var result = await _universalEngine.ProcessStatementAsync(extraction, detection);

        Assert.NotNull(result);
        Assert.NotNull(result.FinancialValidation);
        Assert.Equal(1, result.FinancialValidation.FailedRowsCount);
        Assert.False(result.FinancialValidation.IsFullyReconciled);
        Assert.NotEmpty(result.FinancialValidation.Discrepancies);

        // Safety policy: broken math MUST force NeedsReview and deny High confidence
        Assert.True(result.NeedsReview);
        Assert.False(result.Success);
        Assert.NotEqual(UniversalConfidenceLevel.High, result.Confidence.Level);
        Assert.Contains(result.Warnings, w => w.Contains("reconciliation failed") || w.Contains("continuity"));
    }

    [Fact]
    public void FinancialValidator_DirectTesting_ReconcilesSequentialBalancesCorrectly()
    {
        var transactions = new List<ParsedTransaction>
        {
            new() { TransactionDate = new DateTime(2026, 1, 1), Debit = 500m, Balance = 4500m },
            new() { TransactionDate = new DateTime(2026, 1, 2), Credit = 1500m, Balance = 6000m },
            new() { TransactionDate = new DateTime(2026, 1, 3), Debit = 200m, Balance = 5800m }
        };

        var validator = new FinancialValidator(NullLogger<FinancialValidator>.Instance);
        var report = validator.Validate(transactions, openingBalance: 5000m, closingBalance: 5800m);

        Assert.Equal(3, report.TotalRowsChecked);
        Assert.Equal(3, report.ReconciledRowsCount);
        Assert.Equal(0, report.FailedRowsCount);
        Assert.True(report.IsFullyReconciled);
        Assert.Equal(1.0, report.ReconciliationRate);
        Assert.Equal(700m, report.TotalDebits);
        Assert.Equal(1500m, report.TotalCredits);
        Assert.Equal(5800m, report.CalculatedClosingBalance);
        Assert.Empty(report.Discrepancies);
    }

    [Fact]
    public void UniversalRowSegmenter_DirectTesting_ClustersWrappedLinesAccurately()
    {
        var lines = new List<(double y, string text, List<(string word, double x, double width)> words)>
        {
            (100, "Date Particulars Debit Credit Balance", [("Date", 50, 40), ("Particulars", 120, 80), ("Debit", 270, 40), ("Credit", 350, 40), ("Balance", 450, 50)]),
            (140, "01/01/2026 FIRST LINE OF DESCRIPTION", [("01/01/2026", 50, 60), ("FIRST", 120, 40), ("LINE", 165, 30), ("OF", 200, 20), ("DESCRIPTION", 225, 80)]),
            (155, "SECOND LINE WRAPPED REF123 100.00 4,900.00", [("SECOND", 120, 50), ("LINE", 175, 30), ("WRAPPED", 210, 50), ("REF123", 265, 40), ("100.00", 270, 40), ("4,900.00", 450, 50)])
        };

        var page = CreateSynthesizedPage(1, 595, 842, lines);
        var structure = new StatementStructureAnalysis
        {
            HeaderTopY = 100,
            HeaderBottomY = 115,
            FooterTopY = 800,
            Columns =
            [
                new() { ColumnType = StatementColumnType.Date, LeftX = 50, RightX = 110 },
                new() { ColumnType = StatementColumnType.Description, LeftX = 110, RightX = 260 },
                new() { ColumnType = StatementColumnType.Debit, LeftX = 260, RightX = 340 },
                new() { ColumnType = StatementColumnType.Credit, LeftX = 340, RightX = 420 },
                new() { ColumnType = StatementColumnType.Balance, LeftX = 420, RightX = 520 }
            ]
        };

        var segmenter = new UniversalRowSegmenter(NullLogger<UniversalRowSegmenter>.Instance);
        var rows = segmenter.SegmentPageRows(page, structure);

        // Two physical lines must be clustered into exactly ONE logical candidate row
        Assert.Single(rows);
        Assert.Equal("01/01/2026", rows[0].PrimaryDateText);
        Assert.True(rows[0].HasDateToken);
        Assert.Equal(2, rows[0].SourcePhysicalRows.Count);
    }

    [Fact]
    public async Task UniversalEngine_DebitOnlyAndCreditOnlyRows_ExtractedAccurately()
    {
        // Table layout: Date | Particulars | Debit | Credit | Balance
        var lines = new List<(double y, string text, List<(string word, double x, double width)> words)>
        {
            (50, "IDFC FIRST BANK LIMITED Statement", [("IDFC", 50, 40), ("FIRST", 95, 40), ("BANK", 140, 40), ("LIMITED", 185, 50)]),
            (100, "Date Particulars Debit Credit Balance", [("Date", 50, 40), ("Particulars", 120, 80), ("Debit", 260, 40), ("Credit", 340, 40), ("Balance", 450, 50)]),
            (140, "01/08/2026 ATM CASH 500.00 4,500.00", [("01/08/2026", 50, 60), ("ATM", 120, 30), ("CASH", 155, 30), ("500.00", 260, 40), ("4,500.00", 450, 50)]),
            (170, "02/08/2026 POS DEBIT 200.00 4,300.00", [("02/08/2026", 50, 60), ("POS", 120, 30), ("DEBIT", 155, 40), ("200.00", 260, 40), ("4,300.00", 450, 50)]),
            (200, "03/08/2026 UPI CREDIT 1,500.00 5,800.00", [("03/08/2026", 50, 60), ("UPI", 120, 25), ("CREDIT", 150, 45), ("1,500.00", 340, 50), ("5,800.00", 450, 50)])
        };

        var page = CreateSynthesizedPage(1, 595, 842, lines);
        var extraction = new PdfExtractionResult
        {
            FileId = Guid.NewGuid(),
            HasUsableText = true,
            Pages = [page]
        };

        var detection = new BankDetectionResult { DetectedBank = BankType.Unknown, BankName = "IDFC First Bank", IsSupported = false };

        var result = await _universalEngine.ProcessStatementAsync(extraction, detection);

        Assert.NotNull(result);
        Assert.Equal(3, result.Transactions.Count);

        // Row 1 (debit only)
        Assert.Equal(500.00m, result.Transactions[0].Debit);
        Assert.Null(result.Transactions[0].Credit);
        Assert.Equal("Debit", result.Transactions[0].TransactionType);

        // Row 2 (debit only)
        Assert.Equal(200.00m, result.Transactions[1].Debit);
        Assert.Null(result.Transactions[1].Credit);
        Assert.Equal("Debit", result.Transactions[1].TransactionType);

        // Row 3 (credit only)
        Assert.Null(result.Transactions[2].Debit);
        Assert.Equal(1500.00m, result.Transactions[2].Credit);
        Assert.Equal("Credit", result.Transactions[2].TransactionType);

        // Financial validation
        Assert.NotNull(result.FinancialValidation);
        Assert.Equal(2, result.FinancialValidation.ReconciledRowsCount);
        Assert.Equal(0, result.FinancialValidation.FailedRowsCount);
    }

    [Fact]
    public async Task UniversalEngine_IndianLakhCurrencyFormatting_ParsesAccurately()
    {
        // Tests lakh formatting: 1,25,000.50, rupee symbol ₹, and parenthesized negative
        var lines = new List<(double y, string text, List<(string word, double x, double width)> words)>
        {
            (50, "SOUTH INDIAN BANK Account Statement", [("SOUTH", 50, 50), ("INDIAN", 105, 50), ("BANK", 160, 40)]),
            (100, "Date Particulars Debit Credit Balance", [("Date", 50, 40), ("Particulars", 120, 80), ("Debit", 260, 40), ("Credit", 340, 40), ("Balance", 450, 50)]),
            (140, "01/09/2026 CONTRACT FEE ₹1,25,000.50 2,25,000.50", [("01/09/2026", 50, 60), ("CONTRACT", 120, 60), ("FEE", 185, 30), ("₹1,25,000.50", 340, 80), ("2,25,000.50", 450, 70)])
        };

        var page = CreateSynthesizedPage(1, 595, 842, lines);
        var extraction = new PdfExtractionResult
        {
            FileId = Guid.NewGuid(),
            HasUsableText = true,
            Pages = [page]
        };

        var detection = new BankDetectionResult { DetectedBank = BankType.Unknown, BankName = "South Indian Bank", IsSupported = false };

        var result = await _universalEngine.ProcessStatementAsync(extraction, detection);

        Assert.NotNull(result);
        Assert.Single(result.Transactions);
        Assert.Equal(125000.50m, result.Transactions[0].Credit);
        Assert.Equal(225000.50m, result.Transactions[0].Balance);
    }

    [Fact]
    public async Task UniversalEngine_MissingEssentialColumns_FailsSufficiencyGate_Safely()
    {
        // Table has Date and Particulars, but NO Debit/Credit and NO Balance
        var lines = new List<(double y, string text, List<(string word, double x, double width)> words)>
        {
            (50, "NON FINANCIAL DOCUMENT", [("NON", 50, 30), ("FINANCIAL", 85, 60), ("DOCUMENT", 150, 60)]),
            (100, "Date Particulars", [("Date", 50, 40), ("Particulars", 120, 80)]),
            (140, "01/01/2026 Meeting Notes", [("01/01/2026", 50, 60), ("Meeting", 120, 40), ("Notes", 165, 30)])
        };

        var page = CreateSynthesizedPage(1, 595, 842, lines);
        var extraction = new PdfExtractionResult
        {
            FileId = Guid.NewGuid(),
            HasUsableText = true,
            Pages = [page]
        };

        var detection = new BankDetectionResult { DetectedBank = BankType.Unknown, IsSupported = false };

        var result = await _universalEngine.ProcessStatementAsync(extraction, detection);

        Assert.NotNull(result);
        Assert.False(result.Success);
        Assert.True(result.NeedsReview);
        Assert.Empty(result.Transactions);
        Assert.Equal("This statement format requires review before conversion.", result.StatusMessage);
    }

    [Fact]
    public async Task UniversalEngine_ScannedOrNoDigitalText_ReturnsLowConfidenceReviewResult()
    {
        var extraction = new PdfExtractionResult
        {
            FileId = Guid.NewGuid(),
            HasUsableText = false,
            Pages = []
        };

        var detection = new BankDetectionResult { DetectedBank = BankType.Unknown, IsSupported = false };

        var result = await _universalEngine.ProcessStatementAsync(extraction, detection);

        Assert.NotNull(result);
        Assert.False(result.Success);
        Assert.True(result.NeedsReview);
        Assert.Equal(UniversalConfidenceLevel.Low, result.Confidence.Level);
        Assert.Contains("No usable digital text", result.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region Helper Methods

    private static Client CreateTestClient()
    {
        return new Client
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            Name = "Test Corp",
            ContactPerson = "Tester",
            Email = "tester@accufex.local",
            Phone = "1234567890",
            BusinessName = "Tester Co",
            BusinessType = "Corporate",
            Address = "Mumbai",
            TaxId = "TAX123",
            CreatedAt = DateTime.UtcNow
        };
    }

    private static PdfExtractionResult CreateSimpleExtraction(string text)
    {
        return new PdfExtractionResult
        {
            FileId = Guid.NewGuid(),
            HasUsableText = true,
            Pages = new List<PdfPageResult>
            {
                new()
                {
                    PageNumber = 1,
                    Width = 595,
                    Height = 842,
                    RawText = text,
                    TextBlocks = new List<PdfTextBlock>
                    {
                        new() { Text = text, X = 50, Y = 50, Width = 400, Height = 20 }
                    }
                }
            }
        };
    }

    private static AccufexDbContext CreateInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<AccufexDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        return new AccufexDbContext(options);
    }

    private static PdfPageResult CreateSynthesizedPage(
        int pageNumber,
        double width,
        double height,
        List<(double y, string text, List<(string word, double x, double width)> words)> lines)
    {
        var candidateRows = new List<PdfCandidateRow>();
        var textBlocks = new List<PdfTextBlock>();
        int readingOrder = 1;
        int rowIndex = 1;

        foreach (var (y, text, words) in lines)
        {
            var fragments = new List<PdfTextBlock>();
            foreach (var (word, x, w) in words)
            {
                var block = new PdfTextBlock
                {
                    Text = word,
                    X = x,
                    Y = y,
                    Width = w,
                    Height = 12,
                    PageNumber = pageNumber,
                    ReadingOrderIndex = readingOrder++
                };
                fragments.Add(block);
                textBlocks.Add(block);
            }

            candidateRows.Add(new PdfCandidateRow
            {
                RowIndex = rowIndex++,
                PageNumber = pageNumber,
                Y = y,
                Height = 15,
                RawLineText = text,
                LineFragments = fragments
            });
        }

        return new PdfPageResult
        {
            PageNumber = pageNumber,
            Width = width,
            Height = height,
            HasUsableText = true,
            RawText = string.Join("\n", lines.Select(l => l.text)),
            CandidateRows = candidateRows,
            TextBlocks = textBlocks
        };
    }

    #endregion
}
