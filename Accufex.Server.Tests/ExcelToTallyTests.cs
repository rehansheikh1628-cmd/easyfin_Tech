using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using ClosedXML.Excel;
using Accufex.Server.Controllers;
using Accufex.Server.ExcelToTally.Interfaces;
using Accufex.Server.ExcelToTally.Models;
using Accufex.Server.ExcelToTally.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Accufex.Server.Tests;

public class ExcelToTallyTests
{
    private readonly IExcelReaderService _reader = new ClosedXmlExcelReaderService(NullLogger<ClosedXmlExcelReaderService>.Instance);
    private readonly IExcelToTallyValidationService _validator = new ExcelToTallyValidationService();
    private readonly IExcelTemplateService _templateService = new ExcelTemplateService();
    private readonly ITallyXmlGenerator _xmlGenerator = new TallyXmlGeneratorService();

    private static readonly string[] StandardHeaders =
    {
        "Date", "Narration", "Cheque / Ref No.", "Value Date", "Dr Amount", "Cr Amount", "Closing Balance", "Ledger Name", "Bank Name"
    };

    private static MemoryStream CreateWorkbookStream(string[] headers, Action<IXLWorksheet> populateRows)
    {
        var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Transactions");

        for (int i = 0; i < headers.Length; i++)
        {
            ws.Cell(1, i + 1).Value = headers[i];
        }

        populateRows(ws);

        var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;
        return ms;
    }

    #region 20 Minimum Required Validation Tests

    // 1. Valid Excel file
    [Fact]
    public async Task Validate_ValidExcelFile_SucceedsWithAllValidRows()
    {
        using var stream = CreateWorkbookStream(StandardHeaders, ws =>
        {
            ws.Cell(2, 1).Value = "01/04/2025";
            ws.Cell(2, 2).Value = "Office Stationery";
            ws.Cell(2, 3).Value = "UPI-102938";
            ws.Cell(2, 4).Value = "01/04/2025";
            ws.Cell(2, 5).Value = 1500.00;
            ws.Cell(2, 6).Value = "";
            ws.Cell(2, 7).Value = 48500.00;
            ws.Cell(2, 8).Value = "Stationery Expenses";
            ws.Cell(2, 9).Value = "HDFC Bank";
        });

        var readResult = await _reader.ReadExcelAsync(stream);
        Assert.True(readResult.Success);

        var validationResult = _validator.Validate(readResult);
        Assert.True(validationResult.Success);
        Assert.Equal(1, validationResult.TotalRows);
        Assert.Equal(1, validationResult.ValidRows);
        Assert.Equal(0, validationResult.InvalidRows);
        Assert.Equal(0, validationResult.WarningRows);
        Assert.True(validationResult.IsReadyForXmlGeneration);
        Assert.Empty(validationResult.ValidationMessages);
    }

    // 2. Multiple valid transactions
    [Fact]
    public async Task Validate_MultipleValidTransactions_CorrectlyParsedAndCounted()
    {
        using var stream = CreateWorkbookStream(StandardHeaders, ws =>
        {
            // Row 2: Debit
            ws.Cell(2, 1).Value = "05/04/2025";
            ws.Cell(2, 2).Value = "Vendor Payment A";
            ws.Cell(2, 3).Value = "NEFT-1122";
            ws.Cell(2, 5).Value = 5000.00;
            ws.Cell(2, 8).Value = "Sundry Creditors";
            ws.Cell(2, 9).Value = "Kotak Bank";

            // Row 3: Credit
            ws.Cell(3, 1).Value = "10/04/2025";
            ws.Cell(3, 2).Value = "Customer Receipt B";
            ws.Cell(3, 3).Value = "RTGS-9988";
            ws.Cell(3, 6).Value = 12000.00;
            ws.Cell(3, 8).Value = "Sundry Debtors";
            ws.Cell(3, 9).Value = "Kotak Bank";

            // Row 4: Debit
            ws.Cell(4, 1).Value = "15/04/2025";
            ws.Cell(4, 2).Value = "Electricity Bill";
            ws.Cell(4, 3).Value = "BILL-001";
            ws.Cell(4, 5).Value = 2400.00;
            ws.Cell(4, 8).Value = "Power & Fuel";
            ws.Cell(4, 9).Value = "Kotak Bank";
        });

        var readResult = await _reader.ReadExcelAsync(stream);
        var valResult = _validator.Validate(readResult);

        Assert.Equal(3, valResult.TotalRows);
        Assert.Equal(3, valResult.ValidRows);
        Assert.Equal(0, valResult.InvalidRows);
        Assert.True(valResult.IsReadyForXmlGeneration);
    }

    // 3. Missing required header
    [Fact]
    public async Task Validate_MissingRequiredHeader_ReportsFatalHeaderError()
    {
        // Omit "Bank Name"
        var partialHeaders = new[]
        {
            "Date", "Narration", "Cheque / Ref No.", "Value Date", "Dr Amount", "Cr Amount", "Closing Balance", "Ledger Name"
        };

        using var stream = CreateWorkbookStream(partialHeaders, ws =>
        {
            ws.Cell(2, 1).Value = "01/04/2025";
            ws.Cell(2, 5).Value = 1000.00;
            ws.Cell(2, 8).Value = "Rent";
        });

        var readResult = await _reader.ReadExcelAsync(stream);
        Assert.False(readResult.Success);
        Assert.Contains("Bank Name", readResult.MissingRequiredHeaders);

        var valResult = _validator.Validate(readResult);
        Assert.False(valResult.Success);
        Assert.False(valResult.IsReadyForXmlGeneration);
        Assert.Contains(valResult.ValidationMessages, m => m.Severity == ExcelValidationSeverity.Fatal && m.Message.Contains("Bank Name"));
    }

    // 4. Invalid Date
    [Fact]
    public async Task Validate_InvalidDate_ReportsRowSpecificFatalError()
    {
        using var stream = CreateWorkbookStream(StandardHeaders, ws =>
        {
            ws.Cell(2, 1).Value = "INVALID_DATE_TEXT";
            ws.Cell(2, 2).Value = "Payment";
            ws.Cell(2, 5).Value = 500.00;
            ws.Cell(2, 8).Value = "Expenses";
            ws.Cell(2, 9).Value = "Axis Bank";
        });

        var readResult = await _reader.ReadExcelAsync(stream);
        var valResult = _validator.Validate(readResult);

        Assert.Equal(1, valResult.InvalidRows);
        Assert.False(valResult.IsReadyForXmlGeneration);
        var issue = Assert.Single(valResult.ValidationMessages, m => m.RowNumber == 2 && m.Column == "Date");
        Assert.Equal(ExcelValidationSeverity.Fatal, issue.Severity);
        Assert.Equal("Row 2: Invalid Date format: 'INVALID_DATE_TEXT'. Expected format like dd/MM/yyyy or dd-MMM-yyyy.", issue.FormattedMessage);
    }

    // 5. Invalid Value Date
    [Fact]
    public async Task Validate_InvalidValueDate_ReportsRowSpecificFatalError()
    {
        using var stream = CreateWorkbookStream(StandardHeaders, ws =>
        {
            ws.Cell(2, 1).Value = "01/05/2025";
            ws.Cell(2, 2).Value = "Payment";
            ws.Cell(2, 4).Value = "NOT_A_DATE";
            ws.Cell(2, 5).Value = 750.00;
            ws.Cell(2, 8).Value = "Expenses";
            ws.Cell(2, 9).Value = "SBI";
        });

        var readResult = await _reader.ReadExcelAsync(stream);
        var valResult = _validator.Validate(readResult);

        Assert.Equal(1, valResult.InvalidRows);
        Assert.False(valResult.IsReadyForXmlGeneration);
        var issue = Assert.Single(valResult.ValidationMessages, m => m.RowNumber == 2 && m.Column == "Value Date");
        Assert.Equal(ExcelValidationSeverity.Fatal, issue.Severity);
        Assert.Contains("Row 2: Invalid Value Date format", issue.FormattedMessage);
    }

    // 6. Invalid Dr Amount
    [Fact]
    public async Task Validate_InvalidDrAmount_ReportsRowSpecificFatalError()
    {
        using var stream = CreateWorkbookStream(StandardHeaders, ws =>
        {
            ws.Cell(2, 1).Value = "01/05/2025";
            ws.Cell(2, 2).Value = "Payment";
            ws.Cell(2, 5).Value = "one thousand";
            ws.Cell(2, 8).Value = "Expenses";
            ws.Cell(2, 9).Value = "SBI";
        });

        var readResult = await _reader.ReadExcelAsync(stream);
        var valResult = _validator.Validate(readResult);

        Assert.Equal(1, valResult.InvalidRows);
        Assert.False(valResult.IsReadyForXmlGeneration);
        var issue = Assert.Single(valResult.ValidationMessages, m => m.RowNumber == 2 && m.Column == "Dr Amount");
        Assert.Equal("Row 2: Dr Amount is not numeric: 'one thousand'.", issue.FormattedMessage);
    }

    // 7. Invalid Cr Amount
    [Fact]
    public async Task Validate_InvalidCrAmount_ReportsRowSpecificFatalError()
    {
        using var stream = CreateWorkbookStream(StandardHeaders, ws =>
        {
            ws.Cell(2, 1).Value = "01/05/2025";
            ws.Cell(2, 2).Value = "Receipt";
            ws.Cell(2, 6).Value = "abc##";
            ws.Cell(2, 8).Value = "Income";
            ws.Cell(2, 9).Value = "SBI";
        });

        var readResult = await _reader.ReadExcelAsync(stream);
        var valResult = _validator.Validate(readResult);

        Assert.Equal(1, valResult.InvalidRows);
        Assert.False(valResult.IsReadyForXmlGeneration);
        var issue = Assert.Single(valResult.ValidationMessages, m => m.RowNumber == 2 && m.Column == "Cr Amount");
        Assert.Equal("Row 2: Cr Amount is not numeric: 'abc##'.", issue.FormattedMessage);
    }

    // 8. Negative amount
    [Fact]
    public async Task Validate_NegativeAmount_ReportsRowSpecificFatalError()
    {
        using var stream = CreateWorkbookStream(StandardHeaders, ws =>
        {
            ws.Cell(2, 1).Value = "01/05/2025";
            ws.Cell(2, 2).Value = "Payment";
            ws.Cell(2, 5).Value = -2500.00;
            ws.Cell(2, 8).Value = "Expenses";
            ws.Cell(2, 9).Value = "SBI";
        });

        var readResult = await _reader.ReadExcelAsync(stream);
        var valResult = _validator.Validate(readResult);

        Assert.Equal(1, valResult.InvalidRows);
        Assert.False(valResult.IsReadyForXmlGeneration);
        var issue = Assert.Single(valResult.ValidationMessages, m => m.RowNumber == 2 && m.Column == "Dr Amount");
        Assert.Equal("Row 2: Dr Amount cannot be negative.", issue.FormattedMessage);
    }

    // 9. Both Dr and Cr populated
    [Fact]
    public async Task Validate_BothDrAndCrPopulated_ReportsRowSpecificFatalError()
    {
        using var stream = CreateWorkbookStream(StandardHeaders, ws =>
        {
            ws.Cell(2, 1).Value = "01/05/2025";
            ws.Cell(2, 2).Value = "Ambiguous Transfer";
            ws.Cell(2, 5).Value = 1000.00;
            ws.Cell(2, 6).Value = 1000.00;
            ws.Cell(2, 8).Value = "Bank Transfer";
            ws.Cell(2, 9).Value = "HDFC Bank";
        });

        var readResult = await _reader.ReadExcelAsync(stream);
        var valResult = _validator.Validate(readResult);

        Assert.Equal(1, valResult.InvalidRows);
        Assert.False(valResult.IsReadyForXmlGeneration);
        var issue = Assert.Single(valResult.ValidationMessages, m => m.RowNumber == 2 && m.Column == "Amount");
        Assert.Equal("Row 2: Dr Amount and Cr Amount cannot both be populated.", issue.FormattedMessage);
    }

    // 10. Neither Dr nor Cr populated
    [Fact]
    public async Task Validate_NeitherDrNorCrPopulated_ReportsRowSpecificFatalError()
    {
        using var stream = CreateWorkbookStream(StandardHeaders, ws =>
        {
            ws.Cell(2, 1).Value = "01/05/2025";
            ws.Cell(2, 2).Value = "Zero Amount Entry";
            ws.Cell(2, 5).Value = "";
            ws.Cell(2, 6).Value = "";
            ws.Cell(2, 8).Value = "Suspense";
            ws.Cell(2, 9).Value = "HDFC Bank";
        });

        var readResult = await _reader.ReadExcelAsync(stream);
        var valResult = _validator.Validate(readResult);

        Assert.Equal(1, valResult.InvalidRows);
        Assert.False(valResult.IsReadyForXmlGeneration);
        var issue = Assert.Single(valResult.ValidationMessages, m => m.RowNumber == 2 && m.Column == "Amount");
        Assert.Equal("Row 2: Transaction must have either a Dr Amount or a Cr Amount populated.", issue.FormattedMessage);
    }

    // 11. Missing Ledger Name
    [Fact]
    public async Task Validate_MissingLedgerName_ReportsRowSpecificFatalError()
    {
        using var stream = CreateWorkbookStream(StandardHeaders, ws =>
        {
            ws.Cell(2, 1).Value = "01/05/2025";
            ws.Cell(2, 2).Value = "Valid Payment";
            ws.Cell(2, 5).Value = 400.00;
            ws.Cell(2, 8).Value = "   "; // Empty ledger
            ws.Cell(2, 9).Value = "HDFC Bank";
        });

        var readResult = await _reader.ReadExcelAsync(stream);
        var valResult = _validator.Validate(readResult);

        Assert.Equal(1, valResult.InvalidRows);
        Assert.False(valResult.IsReadyForXmlGeneration);
        var issue = Assert.Single(valResult.ValidationMessages, m => m.RowNumber == 2 && m.Column == "Ledger Name");
        Assert.Equal("Row 2: Ledger Name cannot be empty.", issue.FormattedMessage);
    }

    // 12. Missing Bank Name
    [Fact]
    public async Task Validate_MissingBankName_ReportsRowSpecificFatalError()
    {
        using var stream = CreateWorkbookStream(StandardHeaders, ws =>
        {
            ws.Cell(2, 1).Value = "01/05/2025";
            ws.Cell(2, 2).Value = "Valid Payment";
            ws.Cell(2, 5).Value = 400.00;
            ws.Cell(2, 8).Value = "Office Expenses";
            ws.Cell(2, 9).Value = ""; // Empty bank
        });

        var readResult = await _reader.ReadExcelAsync(stream);
        var valResult = _validator.Validate(readResult);

        Assert.Equal(1, valResult.InvalidRows);
        Assert.False(valResult.IsReadyForXmlGeneration);
        var issue = Assert.Single(valResult.ValidationMessages, m => m.RowNumber == 2 && m.Column == "Bank Name");
        Assert.Equal("Row 2: Bank Name cannot be empty.", issue.FormattedMessage);
    }

    // 13. Empty rows
    [Fact]
    public async Task Validate_CompletelyEmptyRows_SafelyIgnoredWithoutErrors()
    {
        using var stream = CreateWorkbookStream(StandardHeaders, ws =>
        {
            // Row 2: valid
            ws.Cell(2, 1).Value = "01/05/2025";
            ws.Cell(2, 2).Value = "Txn 1";
            ws.Cell(2, 5).Value = 100.00;
            ws.Cell(2, 8).Value = "Ledger A";
            ws.Cell(2, 9).Value = "Bank A";

            // Row 3: completely blank
            // (all cells empty)

            // Row 4: valid
            ws.Cell(4, 1).Value = "02/05/2025";
            ws.Cell(4, 2).Value = "Txn 2";
            ws.Cell(4, 6).Value = 200.00;
            ws.Cell(4, 8).Value = "Ledger B";
            ws.Cell(4, 9).Value = "Bank B";
        });

        var readResult = await _reader.ReadExcelAsync(stream);
        Assert.True(readResult.Success);
        Assert.Equal(1, readResult.BlankRowsSkipped);
        Assert.Equal(2, readResult.Rows.Count);

        var valResult = _validator.Validate(readResult);
        Assert.Equal(2, valResult.TotalRows);
        Assert.Equal(2, valResult.ValidRows);
        Assert.Equal(0, valResult.InvalidRows);
        Assert.True(valResult.IsReadyForXmlGeneration);
    }

    // 14. Duplicate transaction detection
    [Fact]
    public async Task Validate_DuplicateTransactions_FlaggedAsWarningsWithoutBlocking()
    {
        using var stream = CreateWorkbookStream(StandardHeaders, ws =>
        {
            // Row 2
            ws.Cell(2, 1).Value = "01/05/2025";
            ws.Cell(2, 2).Value = "Same Narration";
            ws.Cell(2, 3).Value = "REF-999";
            ws.Cell(2, 5).Value = 5000.00;
            ws.Cell(2, 8).Value = "Supplies";
            ws.Cell(2, 9).Value = "ICICI Bank";

            // Row 3: duplicate of Row 2
            ws.Cell(3, 1).Value = "01/05/2025";
            ws.Cell(3, 2).Value = "Same Narration";
            ws.Cell(3, 3).Value = "REF-999";
            ws.Cell(3, 5).Value = 5000.00;
            ws.Cell(3, 8).Value = "Supplies";
            ws.Cell(3, 9).Value = "ICICI Bank";
        });

        var readResult = await _reader.ReadExcelAsync(stream);
        var valResult = _validator.Validate(readResult);

        Assert.Equal(2, valResult.TotalRows);
        Assert.Equal(1, valResult.ValidRows);
        Assert.Equal(1, valResult.WarningRows);
        Assert.Equal(0, valResult.InvalidRows);

        // Crucial requirement: Warnings do NOT block XML generation!
        Assert.True(valResult.IsReadyForXmlGeneration);

        var warning = Assert.Single(valResult.ValidationMessages, m => m.RowNumber == 3);
        Assert.Equal(ExcelValidationSeverity.Warning, warning.Severity);
        Assert.Contains("Potential duplicate transaction matching Row 2", warning.Message);
    }

    // 15. Malformed Excel file
    [Fact]
    public async Task Validate_MalformedExcelFile_ReportsFatalFileErrorGracefully()
    {
        var garbageBytes = new byte[] { 0x00, 0x01, 0x02, 0x03, 0xFF, 0xFE, 0x12 };
        using var stream = new MemoryStream(garbageBytes);

        var readResult = await _reader.ReadExcelAsync(stream);
        Assert.False(readResult.Success);
        Assert.Contains("Malformed or unreadable Excel file", readResult.ErrorMessage);

        var valResult = _validator.Validate(readResult);
        Assert.False(valResult.Success);
        Assert.False(valResult.IsReadyForXmlGeneration);
        Assert.Contains(valResult.ValidationMessages, m => m.Severity == ExcelValidationSeverity.Fatal);
    }

    // 16. Correct transaction parsing
    [Fact]
    public async Task Validate_CorrectTransactionParsing_ExtractsAllFieldsAccurately()
    {
        using var stream = CreateWorkbookStream(StandardHeaders, ws =>
        {
            ws.Cell(2, 1).Value = "15/06/2025";
            ws.Cell(2, 2).Value = "Consulting Advance / Proj-001";
            ws.Cell(2, 3).Value = "CHQ-889900";
            ws.Cell(2, 4).Value = "16/06/2025";
            ws.Cell(2, 5).Value = "";
            ws.Cell(2, 6).Value = 45000.50;
            ws.Cell(2, 7).Value = 150000.75;
            ws.Cell(2, 8).Value = "Client Retainer Account";
            ws.Cell(2, 9).Value = "Central Bank of India";
        });

        var readResult = await _reader.ReadExcelAsync(stream);
        var valResult = _validator.Validate(readResult);

        Assert.Single(valResult.ParsedTransactions);
        var tx = valResult.ParsedTransactions[0];

        Assert.Equal(2, tx.RowNumber);
        Assert.Equal(new DateTime(2025, 6, 15), tx.Date);
        Assert.Equal("Consulting Advance / Proj-001", tx.Narration);
        Assert.Equal("CHQ-889900", tx.ChequeRefNo);
        Assert.Equal(new DateTime(2025, 6, 16), tx.ValueDate);
        Assert.Null(tx.DrAmount);
        Assert.Equal(45000.50m, tx.CrAmount);
        Assert.Equal(150000.75m, tx.ClosingBalance);
        Assert.Equal("Client Retainer Account", tx.LedgerName);
        Assert.Equal("Central Bank of India", tx.BankName);
        Assert.True(tx.IsCredit);
        Assert.False(tx.IsDebit);
        Assert.Equal("Valid", tx.Status);
    }

    // 17. Correct validation counts
    [Fact]
    public async Task Validate_SummaryCounts_MatchValidWarningAndInvalidRows()
    {
        using var stream = CreateWorkbookStream(StandardHeaders, ws =>
        {
            // Row 2: Valid
            ws.Cell(2, 1).Value = "01/01/2025";
            ws.Cell(2, 2).Value = "Valid Row";
            ws.Cell(2, 5).Value = 100.00;
            ws.Cell(2, 8).Value = "Ledger 1";
            ws.Cell(2, 9).Value = "Bank 1";

            // Row 3: Warning (Duplicate of Row 2)
            ws.Cell(3, 1).Value = "01/01/2025";
            ws.Cell(3, 2).Value = "Valid Row";
            ws.Cell(3, 5).Value = 100.00;
            ws.Cell(3, 8).Value = "Ledger 1";
            ws.Cell(3, 9).Value = "Bank 1";

            // Row 4: Invalid (Missing Ledger Name)
            ws.Cell(4, 1).Value = "02/01/2025";
            ws.Cell(4, 2).Value = "Invalid Row";
            ws.Cell(4, 6).Value = 200.00;
            ws.Cell(4, 8).Value = "";
            ws.Cell(4, 9).Value = "Bank 1";
        });

        var readResult = await _reader.ReadExcelAsync(stream);
        var valResult = _validator.Validate(readResult);

        Assert.Equal(3, valResult.TotalRows);
        Assert.Equal(1, valResult.ValidRows);
        Assert.Equal(1, valResult.WarningRows);
        Assert.Equal(1, valResult.InvalidRows);
    }

    // 18. Correct IsReadyForXmlGeneration result: True when no fatal errors
    [Fact]
    public async Task Validate_IsReadyForXmlGeneration_TrueWhenNoFatalErrors()
    {
        using var stream = CreateWorkbookStream(StandardHeaders, ws =>
        {
            ws.Cell(2, 1).Value = "01/07/2025";
            ws.Cell(2, 2).Value = "Clean Transaction";
            ws.Cell(2, 5).Value = 300.00;
            ws.Cell(2, 8).Value = "Fuel";
            ws.Cell(2, 9).Value = "Yes Bank";
        });

        var readResult = await _reader.ReadExcelAsync(stream);
        var valResult = _validator.Validate(readResult);

        Assert.True(valResult.IsReadyForXmlGeneration);
    }

    // 19. XML generation is blocked when fatal validation errors exist
    [Fact]
    public async Task Validate_IsReadyForXmlGeneration_FalseWhenFatalErrorsExist()
    {
        using var stream = CreateWorkbookStream(StandardHeaders, ws =>
        {
            ws.Cell(2, 1).Value = "01/07/2025";
            ws.Cell(2, 2).Value = "Bad Row";
            ws.Cell(2, 5).Value = -300.00; // Negative amount
            ws.Cell(2, 8).Value = "Fuel";
            ws.Cell(2, 9).Value = "Yes Bank";
        });

        var readResult = await _reader.ReadExcelAsync(stream);
        var valResult = _validator.Validate(readResult);

        Assert.False(valResult.IsReadyForXmlGeneration);
    }

    // 20. Official .XLSM Template Verification: 9 canonical headers, instructions sheet, custom properties
    [Fact]
    public async Task Template_GeneratesValidXlsmWorkbook_WithExact9HeadersAndIntegritySignature()
    {
        var templateBytes = _templateService.GenerateTemplate();
        Assert.NotNull(templateBytes);
        Assert.True(templateBytes.Length > 0);

        using var ms = new MemoryStream(templateBytes);
        using var wb = new XLWorkbook(ms);

        // Verify "Transactions" worksheet
        var ws = wb.Worksheet("Transactions");
        Assert.NotNull(ws);

        for (int i = 0; i < StandardHeaders.Length; i++)
        {
            Assert.Equal(StandardHeaders[i], ws.Cell(1, i + 1).GetString());
        }

        // Verify "Instructions" worksheet
        var instWs = wb.Worksheet("Instructions");
        Assert.NotNull(instWs);

        // Verify custom properties for template integrity
        Assert.Contains(wb.CustomProperties, p => p.Name == "Accufex_Template_Type" && p.Value?.ToString() == "Tally_Import_XLSM");
        Assert.Contains(wb.CustomProperties, p => p.Name == "Accufex_Template_Version" && (p.Value?.ToString() == "1.0" || p.Value?.ToString() == "1"));

        // Verify template reader recognizes signature and skips sample rows automatically
        ms.Position = 0;
        var readResult = await _reader.ReadExcelAsync(ms);
        Assert.True(readResult.Success);
        Assert.True(readResult.HasOfficialTemplateSignature);
        Assert.Equal(2, readResult.SampleRowsSkipped); // Both [SAMPLE] rows skipped
        Assert.Empty(readResult.Rows); // No actual business transactions yet
    }

    // 21. Reader automatically ignores sample rows while reading client entered transactions
    [Fact]
    public async Task Reader_SkipsSampleRows_AndPreservesClientTransactions()
    {
        using var stream = CreateWorkbookStream(StandardHeaders, ws =>
        {
            // Sample Row (should be ignored)
            ws.Cell(2, 1).Value = "01/04/2025";
            ws.Cell(2, 2).Value = "[SAMPLE] Example Transaction";
            ws.Cell(2, 5).Value = 100.00;
            ws.Cell(2, 8).Value = "Sample Ledger";
            ws.Cell(2, 9).Value = "Sample Bank";

            // Real Client Row 1
            ws.Cell(3, 1).Value = "05/04/2025";
            ws.Cell(3, 2).Value = "Real Vendor Payment";
            ws.Cell(3, 5).Value = 4500.00;
            ws.Cell(3, 8).Value = "Sundry Creditors";
            ws.Cell(3, 9).Value = "HDFC Bank";

            // Real Client Row 2
            ws.Cell(4, 1).Value = "06/04/2025";
            ws.Cell(4, 2).Value = "Real Customer Receipt";
            ws.Cell(4, 6).Value = 9000.00;
            ws.Cell(4, 8).Value = "Sundry Debtors";
            ws.Cell(4, 9).Value = "HDFC Bank";
        });

        var readResult = await _reader.ReadExcelAsync(stream);
        Assert.True(readResult.Success);
        Assert.Equal(1, readResult.SampleRowsSkipped);
        Assert.Equal(2, readResult.Rows.Count);
        Assert.Equal("Real Vendor Payment", readResult.Rows[0].RawNarration);
        Assert.Equal("Real Customer Receipt", readResult.Rows[1].RawNarration);

        var valResult = _validator.Validate(readResult);
        Assert.True(valResult.Success);
        Assert.Equal(2, valResult.ValidRows);
        Assert.Equal(1, valResult.SampleRowsSkipped);
        Assert.True(valResult.IsReadyForXmlGeneration);
    }

    // 22. Template Integrity: Missing "Transactions" worksheet is rejected
    [Fact]
    public async Task Reader_RejectsWorkbook_WhenTransactionsWorksheetMissing()
    {
        var wb = new XLWorkbook();
        wb.Worksheets.Add("Sheet1"); // Wrong worksheet name

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;

        var readResult = await _reader.ReadExcelAsync(ms);
        Assert.False(readResult.Success);
        Assert.Contains("Transactions", readResult.ErrorMessage);

        var valResult = _validator.Validate(readResult);
        Assert.False(valResult.Success);
        Assert.False(valResult.IsReadyForXmlGeneration);
    }

    #endregion

    #region Controller API Endpoint Tests

    [Fact]
    public void Controller_DownloadTemplate_ReturnsXlsmAttachment()
    {
        var controller = new ExcelToTallyController(
            _reader,
            _validator,
            _templateService,
            _xmlGenerator,
            NullLogger<ExcelToTallyController>.Instance);

        var result = controller.DownloadTemplate();
        var fileResult = Assert.IsType<FileContentResult>(result);

        Assert.Equal("application/vnd.ms-excel.sheet.macroEnabled.12", fileResult.ContentType);
        Assert.Equal("Accufex_Tally_Import_Template_v1.xlsm", fileResult.FileDownloadName);
        Assert.NotEmpty(fileResult.FileContents);
    }

    [Fact]
    public async Task Controller_Validate_ValidXlsmFile_ReturnsOkWithValidationResult()
    {
        var controller = new ExcelToTallyController(
            _reader,
            _validator,
            _templateService,
            _xmlGenerator,
            NullLogger<ExcelToTallyController>.Instance);

        using var stream = CreateWorkbookStream(StandardHeaders, ws =>
        {
            ws.Cell(2, 1).Value = "01/04/2025";
            ws.Cell(2, 2).Value = "Controller Test Txn";
            ws.Cell(2, 5).Value = 999.00;
            ws.Cell(2, 8).Value = "Test Ledger";
            ws.Cell(2, 9).Value = "Test Bank";
        });

        var formFile = new FormFile(stream, 0, stream.Length, "file", "Accufex_Tally_Import_Template_v1.xlsm")
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/vnd.ms-excel.sheet.macroEnabled.12"
        };

        var actionResult = await controller.ValidateExcel(formFile, default);
        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var valResult = Assert.IsType<ExcelValidationResult>(okResult.Value);

        Assert.True(valResult.Success);
        Assert.Equal(1, valResult.TotalRows);
        Assert.Equal(1, valResult.ValidRows);
        Assert.True(valResult.IsReadyForXmlGeneration);
    }

    [Fact]
    public async Task Controller_Validate_InvalidExtension_ReturnsBadRequest()
    {
        var controller = new ExcelToTallyController(
            _reader,
            _validator,
            _templateService,
            _xmlGenerator,
            NullLogger<ExcelToTallyController>.Instance);

        using var stream = new MemoryStream(new byte[] { 1, 2, 3 });
        var formFile = new FormFile(stream, 0, stream.Length, "file", "unsupported.csv");

        var actionResult = await controller.ValidateExcel(formFile, default);
        var badRequest = Assert.IsType<BadRequestObjectResult>(actionResult.Result);
        var valResult = Assert.IsType<ExcelValidationResult>(badRequest.Value);

        Assert.False(valResult.Success);
        Assert.Contains("Unsupported file format", valResult.ErrorMessage);
    }

    [Fact]
    public async Task Controller_Validate_EmptyFile_ReturnsBadRequest()
    {
        var controller = new ExcelToTallyController(
            _reader,
            _validator,
            _templateService,
            _xmlGenerator,
            NullLogger<ExcelToTallyController>.Instance);

        using var stream = new MemoryStream();
        var formFile = new FormFile(stream, 0, 0, "file", "empty.xlsx");

        var actionResult = await controller.ValidateExcel(formFile, default);
        var badRequest = Assert.IsType<BadRequestObjectResult>(actionResult.Result);
        var valResult = Assert.IsType<ExcelValidationResult>(badRequest.Value);

        Assert.False(valResult.Success);
        Assert.Contains("empty", valResult.ErrorMessage);
    }

    #endregion

    #region Tally XML Generation Tests (Step 2)

    [Fact]
    public async Task GenerateXml_ValidDebitTransaction_GeneratesPaymentVoucher()
    {
        var valResult = new ExcelValidationResult
        {
            Success = true,
            TotalRows = 1,
            ValidRows = 1,
            IsReadyForXmlGeneration = true,
            ParsedTransactions =
            {
                new ValidatedExcelTransaction
                {
                    RowNumber = 2,
                    Date = new DateTime(2025, 4, 1),
                    Narration = "Office Stationery Supplies",
                    ChequeRefNo = "CHQ-1001",
                    DrAmount = 1000.00m,
                    LedgerName = "Office Expenses",
                    BankName = "HDFC Bank",
                    Status = "Valid"
                }
            }
        };

        var xmlBytes = await _xmlGenerator.GenerateTallyXmlAsync(valResult);
        Assert.NotEmpty(xmlBytes);

        var xmlString = Encoding.UTF8.GetString(xmlBytes);
        var xDoc = XDocument.Parse(xmlString);

        var voucher = xDoc.Descendants("VOUCHER").FirstOrDefault();
        Assert.NotNull(voucher);
        Assert.Equal("Payment", voucher.Attribute("VCHTYPE")?.Value);
        Assert.Equal("Payment", voucher.Element("VOUCHERTYPENAME")?.Value);
        Assert.Equal("20250401", voucher.Element("DATE")?.Value);
        Assert.Equal("Office Stationery Supplies", voucher.Element("NARRATION")?.Value);
        Assert.Equal("CHQ-1001", voucher.Element("REFERENCE")?.Value);

        // Ledger entries
        var entries = voucher.Elements("ALLLEDGERENTRIES.LIST").ToList();
        Assert.Equal(2, entries.Count);

        var partyEntry = entries.First(e => e.Element("LEDGERNAME")?.Value == "Office Expenses");
        Assert.Equal("Yes", partyEntry.Element("ISDEEMEDPOSITIVE")?.Value);
        Assert.Equal("-1000.00", partyEntry.Element("AMOUNT")?.Value);

        var bankEntry = entries.First(e => e.Element("LEDGERNAME")?.Value == "HDFC Bank");
        Assert.Equal("No", bankEntry.Element("ISDEEMEDPOSITIVE")?.Value);
        Assert.Equal("1000.00", bankEntry.Element("AMOUNT")?.Value);
    }

    [Fact]
    public async Task GenerateXml_ValidCreditTransaction_GeneratesReceiptVoucher()
    {
        var valResult = new ExcelValidationResult
        {
            Success = true,
            TotalRows = 1,
            ValidRows = 1,
            IsReadyForXmlGeneration = true,
            ParsedTransactions =
            {
                new ValidatedExcelTransaction
                {
                    RowNumber = 3,
                    Date = new DateTime(2025, 4, 2),
                    Narration = "Customer Payment Received",
                    ChequeRefNo = "NEFT-9988",
                    CrAmount = 5000.00m,
                    LedgerName = "Customer A",
                    BankName = "HDFC Bank",
                    Status = "Valid"
                }
            }
        };

        var xmlBytes = await _xmlGenerator.GenerateTallyXmlAsync(valResult);
        var xmlString = Encoding.UTF8.GetString(xmlBytes);
        var xDoc = XDocument.Parse(xmlString);

        var voucher = xDoc.Descendants("VOUCHER").FirstOrDefault();
        Assert.NotNull(voucher);
        Assert.Equal("Receipt", voucher.Attribute("VCHTYPE")?.Value);
        Assert.Equal("Receipt", voucher.Element("VOUCHERTYPENAME")?.Value);

        var entries = voucher.Elements("ALLLEDGERENTRIES.LIST").ToList();
        Assert.Equal(2, entries.Count);

        // In Receipt: Bank is debited (-amount), Customer is credited (+amount)
        var bankEntry = entries.First(e => e.Element("LEDGERNAME")?.Value == "HDFC Bank");
        Assert.Equal("Yes", bankEntry.Element("ISDEEMEDPOSITIVE")?.Value);
        Assert.Equal("-5000.00", bankEntry.Element("AMOUNT")?.Value);

        var customerEntry = entries.First(e => e.Element("LEDGERNAME")?.Value == "Customer A");
        Assert.Equal("No", customerEntry.Element("ISDEEMEDPOSITIVE")?.Value);
        Assert.Equal("5000.00", customerEntry.Element("AMOUNT")?.Value);
    }

    [Fact]
    public async Task GenerateXml_DateAndValueDate_CorrectlyHandled()
    {
        var valResult = new ExcelValidationResult
        {
            Success = true,
            TotalRows = 1,
            ValidRows = 1,
            IsReadyForXmlGeneration = true,
            ParsedTransactions =
            {
                new ValidatedExcelTransaction
                {
                    RowNumber = 2,
                    Date = new DateTime(2025, 4, 5),
                    ValueDate = new DateTime(2025, 4, 7),
                    DrAmount = 250.00m,
                    LedgerName = "Tea Expenses",
                    BankName = "Kotak Bank",
                    Status = "Valid"
                }
            }
        };

        var xmlBytes = await _xmlGenerator.GenerateTallyXmlAsync(valResult);
        var xDoc = XDocument.Parse(Encoding.UTF8.GetString(xmlBytes));
        var voucher = xDoc.Descendants("VOUCHER").First();

        Assert.Equal("20250405", voucher.Element("DATE")?.Value);
        Assert.Equal("20250407", voucher.Element("EFFECTIVEDATE")?.Value);
    }

    [Fact]
    public async Task GenerateXml_XmlSpecialCharacters_AreEscaped()
    {
        var valResult = new ExcelValidationResult
        {
            Success = true,
            TotalRows = 1,
            ValidRows = 1,
            IsReadyForXmlGeneration = true,
            ParsedTransactions =
            {
                new ValidatedExcelTransaction
                {
                    RowNumber = 2,
                    Date = new DateTime(2025, 4, 1),
                    Narration = "AT&T <Hardware> \"Store\" & 'Services'",
                    ChequeRefNo = "REF#101 & <202>",
                    DrAmount = 1200.00m,
                    LedgerName = "Hardware & Tools <Retail>",
                    BankName = "HDFC Bank & Co.",
                    Status = "Valid"
                }
            }
        };

        var xmlBytes = await _xmlGenerator.GenerateTallyXmlAsync(valResult);
        var xmlString = Encoding.UTF8.GetString(xmlBytes);

        // Verify well-formed XML and escaped characters (& and < >)
        Assert.Contains("AT&amp;T &lt;Hardware&gt;", xmlString);
        Assert.Contains("Hardware &amp; Tools &lt;Retail&gt;", xmlString);
        Assert.Contains("HDFC Bank &amp; Co.", xmlString);

        // Parsing must succeed cleanly and unescape all entities back to original text
        var xDoc = XDocument.Parse(xmlString);
        var voucher = xDoc.Descendants("VOUCHER").First();
        Assert.Equal("AT&T <Hardware> \"Store\" & 'Services'", voucher.Element("NARRATION")?.Value);
        Assert.Equal("Hardware & Tools <Retail>", voucher.Descendants("LEDGERNAME").First().Value);
    }

    [Fact]
    public async Task GenerateXml_MultipleTransactions_GeneratesMultipleVouchers()
    {
        var valResult = new ExcelValidationResult
        {
            Success = true,
            TotalRows = 3,
            ValidRows = 3,
            IsReadyForXmlGeneration = true,
            ParsedTransactions =
            {
                new ValidatedExcelTransaction
                {
                    RowNumber = 2,
                    Date = new DateTime(2025, 4, 1),
                    DrAmount = 100.00m,
                    LedgerName = "Ledger 1",
                    BankName = "Bank A",
                    Status = "Valid"
                },
                new ValidatedExcelTransaction
                {
                    RowNumber = 3,
                    Date = new DateTime(2025, 4, 2),
                    CrAmount = 200.00m,
                    LedgerName = "Ledger 2",
                    BankName = "Bank A",
                    Status = "Valid"
                },
                new ValidatedExcelTransaction
                {
                    RowNumber = 4,
                    Date = new DateTime(2025, 4, 3),
                    DrAmount = 300.00m,
                    LedgerName = "Ledger 3",
                    BankName = "Bank A",
                    Status = "Valid"
                }
            }
        };

        var xmlBytes = await _xmlGenerator.GenerateTallyXmlAsync(valResult);
        var xDoc = XDocument.Parse(Encoding.UTF8.GetString(xmlBytes));
        var vouchers = xDoc.Descendants("VOUCHER").ToList();

        Assert.Equal(3, vouchers.Count);
        Assert.Equal("Payment", vouchers[0].Attribute("VCHTYPE")?.Value);
        Assert.Equal("Receipt", vouchers[1].Attribute("VCHTYPE")?.Value);
        Assert.Equal("Payment", vouchers[2].Attribute("VCHTYPE")?.Value);
    }

    [Fact]
    public async Task Controller_GenerateXml_ValidData_ReturnsFileResultWithXmlContentTypeAndFilename()
    {
        var controller = new ExcelToTallyController(
            _reader,
            _validator,
            _templateService,
            _xmlGenerator,
            NullLogger<ExcelToTallyController>.Instance);

        var valResult = new ExcelValidationResult
        {
            Success = true,
            TotalRows = 1,
            ValidRows = 1,
            IsReadyForXmlGeneration = true,
            ParsedTransactions =
            {
                new ValidatedExcelTransaction
                {
                    RowNumber = 2,
                    Date = new DateTime(2025, 4, 1),
                    DrAmount = 1500.00m,
                    LedgerName = "Office Expenses",
                    BankName = "HDFC Bank",
                    Status = "Valid"
                }
            }
        };

        var actionResult = await controller.GenerateXml(valResult, default);
        var fileResult = Assert.IsType<FileContentResult>(actionResult);

        Assert.Equal("application/xml", fileResult.ContentType);
        Assert.StartsWith("Accufex_Tally_Export_", fileResult.FileDownloadName);
        Assert.EndsWith(".xml", fileResult.FileDownloadName);
        Assert.NotEmpty(fileResult.FileContents);
    }

    [Fact]
    public async Task Controller_GenerateXml_NullOrEmptyPayload_ReturnsBadRequest()
    {
        var controller = new ExcelToTallyController(
            _reader,
            _validator,
            _templateService,
            _xmlGenerator,
            NullLogger<ExcelToTallyController>.Instance);

        var nullResult = await controller.GenerateXml(null, default);
        Assert.IsType<BadRequestObjectResult>(nullResult);

        var emptyResult = await controller.GenerateXml(new ExcelValidationResult(), default);
        Assert.IsType<BadRequestObjectResult>(emptyResult);
    }

    [Fact]
    public async Task Controller_GenerateXml_FatalValidationError_BlocksGenerationWithRowNumber()
    {
        var controller = new ExcelToTallyController(
            _reader,
            _validator,
            _templateService,
            _xmlGenerator,
            NullLogger<ExcelToTallyController>.Instance);

        var valResult = new ExcelValidationResult
        {
            Success = false,
            TotalRows = 1,
            InvalidRows = 1,
            IsReadyForXmlGeneration = false,
            ParsedTransactions =
            {
                new ValidatedExcelTransaction
                {
                    RowNumber = 27,
                    Date = new DateTime(2025, 4, 1),
                    DrAmount = 1000.00m,
                    CrAmount = 2000.00m, // Both populated (Fatal)
                    LedgerName = "Party A",
                    BankName = "HDFC Bank",
                    Status = "Invalid",
                    Issues =
                    {
                        new ExcelValidationIssue
                        {
                            RowNumber = 27,
                            Severity = ExcelValidationSeverity.Fatal,
                            Message = "Dr Amount and Cr Amount cannot both be populated."
                        }
                    }
                }
            }
        };

        var actionResult = await controller.GenerateXml(valResult, default);
        var badRequest = Assert.IsType<BadRequestObjectResult>(actionResult);

        var messageProp = badRequest.Value?.GetType().GetProperty("message")?.GetValue(badRequest.Value)?.ToString();
        Assert.NotNull(messageProp);
        Assert.Contains("Row 27", messageProp);
        Assert.Contains("Dr Amount and Cr Amount cannot both be populated", messageProp);
    }

    [Fact]
    public async Task Controller_GenerateXml_WarningOnlyTransaction_DoesNotBlockGeneration()
    {
        var controller = new ExcelToTallyController(
            _reader,
            _validator,
            _templateService,
            _xmlGenerator,
            NullLogger<ExcelToTallyController>.Instance);

        var valResult = new ExcelValidationResult
        {
            Success = true,
            TotalRows = 1,
            ValidRows = 0,
            WarningRows = 1,
            InvalidRows = 0,
            IsReadyForXmlGeneration = true,
            ParsedTransactions =
            {
                new ValidatedExcelTransaction
                {
                    RowNumber = 5,
                    Date = new DateTime(2025, 4, 1),
                    DrAmount = 500.00m,
                    LedgerName = "Office Expenses",
                    BankName = "HDFC Bank",
                    Status = "Warning",
                    Issues =
                    {
                        new ExcelValidationIssue
                        {
                            RowNumber = 5,
                            Severity = ExcelValidationSeverity.Warning,
                            Message = "Potential duplicate transaction."
                        }
                    }
                }
            }
        };

        var actionResult = await controller.GenerateXml(valResult, default);
        var fileResult = Assert.IsType<FileContentResult>(actionResult);

        Assert.Equal("application/xml", fileResult.ContentType);
        Assert.NotEmpty(fileResult.FileContents);
    }

    [Fact]
    public async Task GenerateXml_EndToEndWorkflow_20Transactions_ProducesValidTallyXml()
    {
        // 1. Create a workbook with 20 mixed accounting transactions
        using var stream = CreateWorkbookStream(StandardHeaders, ws =>
        {
            for (int i = 1; i <= 20; i++)
            {
                int row = i + 1;
                bool isDebit = (i % 2 != 0); // odd = debit (Payment), even = credit (Receipt)
                ws.Cell(row, 1).Value = new DateTime(2025, 4, (i % 28) + 1).ToString("dd/MM/yyyy");
                ws.Cell(row, 2).Value = $"Transaction {i} Narration & Special <{i}>";
                ws.Cell(row, 3).Value = $"REF-{1000 + i}";
                ws.Cell(row, 4).Value = new DateTime(2025, 4, (i % 28) + 1).ToString("dd/MM/yyyy");

                if (isDebit)
                {
                    ws.Cell(row, 5).Value = 500.00 + (i * 10);
                    ws.Cell(row, 6).Value = "";
                }
                else
                {
                    ws.Cell(row, 5).Value = "";
                    ws.Cell(row, 6).Value = 1200.00 + (i * 15);
                }

                ws.Cell(row, 7).Value = 50000.00;
                ws.Cell(row, 8).Value = isDebit ? $"Vendor #{i} Pvt Ltd" : $"Customer #{i} Inc";
                ws.Cell(row, 9).Value = "HDFC Bank Ltd";
            }
        });

        // 2. Read Excel
        var readResult = await _reader.ReadExcelAsync(stream);
        Assert.True(readResult.Success);
        Assert.Equal(20, readResult.Rows.Count);

        // 3. Validate Excel
        var validationResult = _validator.Validate(readResult);
        Assert.True(validationResult.Success);
        Assert.Equal(20, validationResult.TotalRows);
        Assert.Equal(20, validationResult.ValidRows);
        Assert.Equal(0, validationResult.InvalidRows);
        Assert.True(validationResult.IsReadyForXmlGeneration);

        // 4. Generate Tally XML via Controller endpoint
        var controller = new ExcelToTallyController(
            _reader,
            _validator,
            _templateService,
            _xmlGenerator,
            NullLogger<ExcelToTallyController>.Instance);

        var actionResult = await controller.GenerateXml(validationResult, default);
        var fileResult = Assert.IsType<FileContentResult>(actionResult);

        Assert.Equal("application/xml", fileResult.ContentType);
        Assert.StartsWith("Accufex_Tally_Export_", fileResult.FileDownloadName);
        Assert.EndsWith(".xml", fileResult.FileDownloadName);

        // 5. Parse and structurally verify the generated XML
        string xmlContent = Encoding.UTF8.GetString(fileResult.FileContents);
        var xDoc = XDocument.Parse(xmlContent);

        var envelope = xDoc.Root;
        Assert.NotNull(envelope);
        Assert.Equal("ENVELOPE", envelope.Name.LocalName);

        var header = envelope.Element("HEADER");
        Assert.NotNull(header);
        Assert.Equal("Import Data", header.Element("TALLYREQUEST")?.Value);

        var body = envelope.Element("BODY");
        Assert.NotNull(body);
        var importData = body.Element("IMPORTDATA");
        Assert.NotNull(importData);

        var vouchers = importData.Descendants("VOUCHER").ToList();
        Assert.Equal(20, vouchers.Count);

        for (int i = 0; i < 20; i++)
        {
            var voucher = vouchers[i];
            int txIndex = i + 1;
            bool isDebit = (txIndex % 2 != 0);

            string expectedVchType = isDebit ? "Payment" : "Receipt";
            Assert.Equal(expectedVchType, voucher.Attribute("VCHTYPE")?.Value);
            Assert.Equal(expectedVchType, voucher.Element("VOUCHERTYPENAME")?.Value);
            Assert.Equal($"REF-{1000 + txIndex}", voucher.Element("REFERENCE")?.Value);
            Assert.Equal($"Transaction {txIndex} Narration & Special <{txIndex}>", voucher.Element("NARRATION")?.Value);

            var ledgerEntries = voucher.Elements("ALLLEDGERENTRIES.LIST").ToList();
            Assert.Equal(2, ledgerEntries.Count);

            if (isDebit)
            {
                decimal expectedAmount = 500.00m + (txIndex * 10);
                var partyEntry = ledgerEntries.First(e => e.Element("LEDGERNAME")?.Value == $"Vendor #{txIndex} Pvt Ltd");
                Assert.Equal("Yes", partyEntry.Element("ISDEEMEDPOSITIVE")?.Value);
                Assert.Equal((-expectedAmount).ToString("F2", System.Globalization.CultureInfo.InvariantCulture), partyEntry.Element("AMOUNT")?.Value);

                var bankEntry = ledgerEntries.First(e => e.Element("LEDGERNAME")?.Value == "HDFC Bank Ltd");
                Assert.Equal("No", bankEntry.Element("ISDEEMEDPOSITIVE")?.Value);
                Assert.Equal(expectedAmount.ToString("F2", System.Globalization.CultureInfo.InvariantCulture), bankEntry.Element("AMOUNT")?.Value);
            }
            else
            {
                decimal expectedAmount = 1200.00m + (txIndex * 15);
                var bankEntry = ledgerEntries.First(e => e.Element("LEDGERNAME")?.Value == "HDFC Bank Ltd");
                Assert.Equal("Yes", bankEntry.Element("ISDEEMEDPOSITIVE")?.Value);
                Assert.Equal((-expectedAmount).ToString("F2", System.Globalization.CultureInfo.InvariantCulture), bankEntry.Element("AMOUNT")?.Value);

                var customerEntry = ledgerEntries.First(e => e.Element("LEDGERNAME")?.Value == $"Customer #{txIndex} Inc");
                Assert.Equal("No", customerEntry.Element("ISDEEMEDPOSITIVE")?.Value);
                Assert.Equal(expectedAmount.ToString("F2", System.Globalization.CultureInfo.InvariantCulture), customerEntry.Element("AMOUNT")?.Value);
            }
        }
    }

    #endregion
}
