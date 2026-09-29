using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ClosedXML.Excel;
using Accufex.Server.DTOs;
using Accufex.Server.Export.Interfaces;
using Accufex.Server.Models;
using Microsoft.Extensions.Logging;

namespace Accufex.Server.Export.Services;

public class ExcelExportService : IExcelExportService
{
    private readonly ILogger<ExcelExportService> _logger;

    public ExcelExportService(ILogger<ExcelExportService> logger)
    {
        _logger = logger;
    }

    public Task<byte[]> GenerateStatementWorkbookAsync(
        FileRecord fileRecord,
        StatementValidationSummaryDto summary,
        List<TransactionReviewDto> transactions,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Generating Excel workbook for statement {FileId} ({Count} transactions).", fileRecord.Id, transactions.Count);

        using var workbook = new XLWorkbook();

        // 1. Sheet 1: Transactions
        BuildTransactionsSheet(workbook, summary, transactions);

        // 2. Sheet 2: Statement Info
        BuildStatementInfoSheet(workbook, fileRecord, summary, transactions);

        // 3. Sheet 3: Financial Summary
        BuildSummarySheet(workbook, summary, transactions);

        // Render to in-memory byte array
        using var memoryStream = new MemoryStream();
        workbook.SaveAs(memoryStream);
        return Task.FromResult(memoryStream.ToArray());
    }

    private static void BuildTransactionsSheet(
        XLWorkbook workbook,
        StatementValidationSummaryDto summary,
        List<TransactionReviewDto> transactions)
    {
        var ws = workbook.Worksheets.Add("Transactions");

        // Headers
        var headers = new[]
        {
            "Sr No",
            "Transaction Date",
            "Value Date",
            "Particulars / Narration",
            "Cheque / Reference",
            "UTR",
            "Debit (-)",
            "Credit (+)",
            "Amount",
            "Balance",
            "Transaction Type",
            "Bank",
            "Account",
            "Validation Status",
            "Review Notes"
        };

        var headerRow = ws.Row(1);
        headerRow.Height = 26;

        for (int i = 0; i < headers.Length; i++)
        {
            var cell = ws.Cell(1, i + 1);
            cell.SetValue(headers[i]);
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Fill.BackgroundColor = XLColor.FromArgb(30, 41, 59); // Slate 800
            cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            cell.Style.Alignment.Horizontal = (i == 0 || i == 1 || i == 2 || i == 10 || i == 13)
                ? XLAlignmentHorizontalValues.Center
                : (i >= 6 && i <= 9 ? XLAlignmentHorizontalValues.Right : XLAlignmentHorizontalValues.Left);
        }

        // Data Rows
        int rowIndex = 2;
        int srNo = 1;
        foreach (var tx in transactions)
        {
            var row = ws.Row(rowIndex);
            row.Height = 20;

            // 1. Sr No (Integer)
            var srCell = ws.Cell(rowIndex, 1);
            srCell.SetValue(srNo++);
            srCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            // 2. Transaction Date (Real Excel Date)
            var dateCell = ws.Cell(rowIndex, 2);
            dateCell.SetValue(tx.TransactionDate.Date);
            dateCell.Style.DateFormat.Format = "dd/mm/yyyy";
            dateCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            // 3. Value Date (Real Excel Date or Blank)
            var valDateCell = ws.Cell(rowIndex, 3);
            if (tx.ValueDate.HasValue)
            {
                valDateCell.SetValue(tx.ValueDate.Value.Date);
                valDateCell.Style.DateFormat.Format = "dd/mm/yyyy";
                valDateCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            }

            // 4. Particulars / Narration (Complete text, wrapped)
            var descCell = ws.Cell(rowIndex, 4);
            descCell.SetValue(tx.Description ?? string.Empty);
            descCell.Style.Alignment.WrapText = true;
            descCell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

            // 5. Cheque / Reference (Explicit Text to prevent scientific notation)
            var refCell = ws.Cell(rowIndex, 5);
            refCell.SetValue(tx.Reference ?? string.Empty);
            refCell.Style.NumberFormat.Format = "@";

            // 6. UTR (Explicit Text to prevent scientific notation)
            var utrCell = ws.Cell(rowIndex, 6);
            utrCell.SetValue(tx.Utr ?? string.Empty);
            utrCell.Style.NumberFormat.Format = "@";

            // 7. Debit (Numeric #,##0.00 or blank)
            var debitCell = ws.Cell(rowIndex, 7);
            if (tx.Debit.HasValue)
            {
                debitCell.SetValue(tx.Debit.Value);
                debitCell.Style.NumberFormat.Format = "#,##0.00";
                debitCell.Style.Font.FontColor = XLColor.FromArgb(185, 28, 28); // Red-700
            }

            // 8. Credit (Numeric #,##0.00 or blank)
            var creditCell = ws.Cell(rowIndex, 8);
            if (tx.Credit.HasValue)
            {
                creditCell.SetValue(tx.Credit.Value);
                creditCell.Style.NumberFormat.Format = "#,##0.00";
                creditCell.Style.Font.FontColor = XLColor.FromArgb(21, 128, 61); // Green-700
            }

            // 9. Amount (Numeric #,##0.00)
            var amtCell = ws.Cell(rowIndex, 9);
            amtCell.SetValue(tx.Amount);
            amtCell.Style.NumberFormat.Format = "#,##0.00";

            // 10. Balance (Numeric #,##0.00, negative preserved)
            var balCell = ws.Cell(rowIndex, 10);
            if (tx.Balance.HasValue)
            {
                balCell.SetValue(tx.Balance.Value);
                balCell.Style.NumberFormat.Format = "#,##0.00";
                balCell.Style.Font.Bold = true;
                if (tx.Balance.Value < 0)
                {
                    balCell.Style.Font.FontColor = XLColor.FromArgb(185, 28, 28);
                }
            }

            // 11. Transaction Type
            var typeCell = ws.Cell(rowIndex, 11);
            typeCell.SetValue(tx.TransactionType ?? string.Empty);
            typeCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            // 12. Bank Name
            var bankCell = ws.Cell(rowIndex, 12);
            bankCell.SetValue(string.IsNullOrWhiteSpace(tx.BankName) ? summary.BankName : tx.BankName);

            // 13. Account (Explicit Text)
            var accCell = ws.Cell(rowIndex, 13);
            accCell.SetValue(tx.Account ?? string.Empty);
            accCell.Style.NumberFormat.Format = "@";

            // 14. Validation Status
            var statusCell = ws.Cell(rowIndex, 14);
            var status = tx.ValidationStatus ?? "VALID";
            statusCell.SetValue(status);
            statusCell.Style.Font.Bold = true;
            statusCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            switch (status)
            {
                case "CORRECTED":
                    statusCell.Style.Font.FontColor = XLColor.FromArgb(15, 118, 110); // Teal 700
                    break;
                case "REVIEW":
                    statusCell.Style.Font.FontColor = XLColor.FromArgb(180, 83, 9); // Amber 700
                    break;
                case "INVALID":
                    statusCell.Style.Font.FontColor = XLColor.FromArgb(185, 28, 28); // Red 700
                    break;
                default:
                    statusCell.Style.Font.FontColor = XLColor.FromArgb(21, 128, 61); // Green 700
                    break;
            }

            // 15. Review Notes
            var notesCell = ws.Cell(rowIndex, 15);
            var notes = new List<string>();
            if (tx.ValidationErrors != null && tx.ValidationErrors.Count > 0)
            {
                notes.AddRange(tx.ValidationErrors);
            }
            if (tx.ValidationWarnings != null && tx.ValidationWarnings.Count > 0)
            {
                notes.AddRange(tx.ValidationWarnings);
            }
            notesCell.SetValue(string.Join("; ", notes));
            notesCell.Style.Font.FontSize = 9.5;
            notesCell.Style.Font.FontColor = XLColor.FromArgb(100, 116, 139); // Slate 500

            rowIndex++;
        }

        // Frozen Header Row
        ws.SheetView.FreezeRows(1);

        // AutoFilter
        var rowCount = Math.Max(1, rowIndex - 1);
        ws.Range(1, 1, rowCount, headers.Length).SetAutoFilter();

        // Standard column widths
        ws.Column(1).Width = 8;   // Sr No
        ws.Column(2).Width = 16;  // Tx Date
        ws.Column(3).Width = 16;  // Value Date
        ws.Column(4).Width = 48;  // Narration
        ws.Column(5).Width = 20;  // Cheque/Ref
        ws.Column(6).Width = 22;  // UTR
        ws.Column(7).Width = 18;  // Debit
        ws.Column(8).Width = 18;  // Credit
        ws.Column(9).Width = 18;  // Amount
        ws.Column(10).Width = 20; // Balance
        ws.Column(11).Width = 16; // Type
        ws.Column(12).Width = 18; // Bank
        ws.Column(13).Width = 20; // Account
        ws.Column(14).Width = 18; // Status
        ws.Column(15).Width = 36; // Review Notes
    }

    private static void BuildStatementInfoSheet(
        XLWorkbook workbook,
        FileRecord fileRecord,
        StatementValidationSummaryDto summary,
        List<TransactionReviewDto> transactions)
    {
        var ws = workbook.Worksheets.Add("Statement Info");

        // Title Header
        ws.Cell(1, 1).SetValue("Statement Metadata & Extraction Details");
        ws.Range(1, 1, 1, 2).Merge();
        ws.Row(1).Height = 28;
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 13;
        ws.Cell(1, 1).Style.Font.FontColor = XLColor.White;
        ws.Cell(1, 1).Style.Fill.BackgroundColor = XLColor.FromArgb(30, 41, 59);
        ws.Cell(1, 1).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

        var metadata = new List<(string Label, object Value, string? Format)>
        {
            ("Bank Name", summary.BankName, null),
            ("Parser Version", summary.ParserVersion, null),
            ("Account Number", transactions.FirstOrDefault(t => !string.IsNullOrEmpty(t.Account))?.Account ?? "N/A", "@"),
            ("Source Statement File", fileRecord.OriginalFileName, null),
            ("Statement File ID", fileRecord.Id.ToString(), "@"),
            ("Ingestion Date (UTC)", fileRecord.UploadedAt.ToString("yyyy-MM-dd HH:mm:ss UTC"), null),
            ("Export Timestamp (UTC)", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC"), null),
            ("Total Transactions", transactions.Count, "#,##0"),
            ("Valid Transactions", summary.ValidCount, "#,##0"),
            ("Needs Review Transactions", summary.ReviewCount, "#,##0"),
            ("Invalid Transactions", summary.InvalidCount, "#,##0"),
            ("Corrected Transactions", summary.CorrectedCount, "#,##0"),
            ("Total Outflow (Debits)", summary.TotalDebits, "#,##0.00"),
            ("Total Inflow (Credits)", summary.TotalCredits, "#,##0.00"),
            ("Net Balance Movement", summary.NetMovement, "#,##0.00"),
            ("Balance Continuity Status", summary.BalanceMismatchedCount == 0 ? "BALANCED" : "MISMATCH", null)
        };

        int r = 2;
        foreach (var item in metadata)
        {
            ws.Row(r).Height = 20;

            var labelCell = ws.Cell(r, 1);
            labelCell.SetValue(item.Label);
            labelCell.Style.Font.Bold = true;
            labelCell.Style.Fill.BackgroundColor = XLColor.FromArgb(241, 245, 249); // Slate 100
            labelCell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

            var valCell = ws.Cell(r, 2);
            if (item.Value is int intVal)
            {
                valCell.SetValue(intVal);
                if (item.Format != null) valCell.Style.NumberFormat.Format = item.Format;
            }
            else if (item.Value is decimal decVal)
            {
                valCell.SetValue(decVal);
                if (item.Format != null) valCell.Style.NumberFormat.Format = item.Format;
            }
            else
            {
                valCell.SetValue(item.Value?.ToString() ?? string.Empty);
                if (item.Format == "@") valCell.Style.NumberFormat.Format = "@";
            }

            valCell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            r++;
        }

        // Borders
        ws.Range(1, 1, r - 1, 2).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        ws.Range(1, 1, r - 1, 2).Style.Border.InsideBorder = XLBorderStyleValues.Thin;

        ws.Column(1).Width = 30;
        ws.Column(2).Width = 50;
    }

    private static void BuildSummarySheet(
        XLWorkbook workbook,
        StatementValidationSummaryDto summary,
        List<TransactionReviewDto> transactions)
    {
        var ws = workbook.Worksheets.Add("Summary");

        // Title Header
        ws.Cell(1, 1).SetValue("Accounting Summary & Reconciliation");
        ws.Range(1, 1, 1, 3).Merge();
        ws.Row(1).Height = 28;
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 13;
        ws.Cell(1, 1).Style.Font.FontColor = XLColor.White;
        ws.Cell(1, 1).Style.Fill.BackgroundColor = XLColor.FromArgb(22, 101, 52); // Green 800
        ws.Cell(1, 1).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

        // Subheaders
        ws.Row(2).Height = 22;
        ws.Cell(2, 1).SetValue("Financial Metric").Style.Font.Bold = true;
        ws.Cell(2, 2).SetValue("Amount / Value").Style.Font.Bold = true;
        ws.Cell(2, 3).SetValue("Details / Status").Style.Font.Bold = true;
        ws.Range(2, 1, 2, 3).Style.Fill.BackgroundColor = XLColor.FromArgb(240, 253, 244); // Green 50
        ws.Range(2, 1, 2, 3).Style.Font.FontColor = XLColor.FromArgb(22, 101, 52);

        // Derive opening and closing balance
        var firstWithBal = transactions.FirstOrDefault(t => t.Balance.HasValue);
        decimal? openingBalance = null;
        if (firstWithBal != null && firstWithBal.Balance.HasValue)
        {
            // Opening balance before first transaction
            if (firstWithBal.Debit.HasValue && firstWithBal.Debit > 0)
            {
                openingBalance = firstWithBal.Balance.Value + firstWithBal.Debit.Value;
            }
            else if (firstWithBal.Credit.HasValue && firstWithBal.Credit > 0)
            {
                openingBalance = firstWithBal.Balance.Value - firstWithBal.Credit.Value;
            }
            else
            {
                openingBalance = firstWithBal.Balance.Value;
            }
        }

        var lastWithBal = transactions.LastOrDefault(t => t.Balance.HasValue);
        decimal? closingBalance = lastWithBal?.Balance;

        var items = new List<(string Metric, object? Value, string? Format, string Notes)>
        {
            ("Opening Balance", openingBalance, "#,##0.00", "Derived balance before first transaction"),
            ("Total Inflow (Credits +)", summary.TotalCredits, "#,##0.00", $"{summary.CreditCount} transaction(s)"),
            ("Total Outflow (Debits -)", summary.TotalDebits, "#,##0.00", $"{summary.DebitCount} transaction(s)"),
            ("Net Financial Movement", summary.NetMovement, "#,##0.00", summary.NetMovement >= 0 ? "Net Positive Inflow" : "Net Outflow"),
            ("Closing Balance", closingBalance, "#,##0.00", "Final balance recorded in statement period"),
            ("Running Balance Continuity", summary.BalanceMismatchedCount == 0 ? "BALANCED" : "MISMATCH DETECTED", null,
                summary.BalanceMismatchedCount == 0 ? "Zero discrepancies across all sequential rows" : $"{summary.BalanceMismatchedCount} continuity mismatch(es) flagged"),
            ("Valid Records Ready", summary.ValidCount, "#,##0", "No review flags or errors"),
            ("Reviewed / Corrected", summary.CorrectedCount, "#,##0", "User-corrected records verified"),
            ("Pending Review", summary.ReviewCount, "#,##0", summary.ReviewCount == 0 ? "Clean" : "Needs manual confirmation"),
            ("Invalid Records", summary.InvalidCount, "#,##0", summary.InvalidCount == 0 ? "Zero errors" : "Critical discrepancies present")
        };

        int r = 3;
        foreach (var item in items)
        {
            ws.Row(r).Height = 20;

            var metricCell = ws.Cell(r, 1);
            metricCell.SetValue(item.Metric);
            metricCell.Style.Font.Bold = true;
            metricCell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

            var valCell = ws.Cell(r, 2);
            if (item.Value is decimal decVal)
            {
                valCell.SetValue(decVal);
                if (item.Format != null) valCell.Style.NumberFormat.Format = item.Format;
                valCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
                valCell.Style.Font.Bold = true;
            }
            else if (item.Value is int intVal)
            {
                valCell.SetValue(intVal);
                if (item.Format != null) valCell.Style.NumberFormat.Format = item.Format;
                valCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            }
            else
            {
                valCell.SetValue(item.Value?.ToString() ?? "-");
                valCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                valCell.Style.Font.Bold = true;
            }
            valCell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

            var noteCell = ws.Cell(r, 3);
            noteCell.SetValue(item.Notes);
            noteCell.Style.Font.FontColor = XLColor.FromArgb(100, 116, 139);
            noteCell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

            r++;
        }

        // Borders
        ws.Range(1, 1, r - 1, 3).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        ws.Range(1, 1, r - 1, 3).Style.Border.InsideBorder = XLBorderStyleValues.Thin;

        ws.Column(1).Width = 32;
        ws.Column(2).Width = 24;
        ws.Column(3).Width = 44;
    }

    public string GenerateSafeExportFileName(
        string originalFileName,
        string bankName,
        DateTime? exportTimestamp = null)
    {
        var timestamp = (exportTimestamp ?? DateTime.UtcNow).ToString("yyyyMMdd");
        var baseName = Path.GetFileNameWithoutExtension(originalFileName);

        // Sanitize bank name and base file name
        var cleanBank = Regex.Replace(bankName ?? "Statement", @"[^a-zA-Z0-9_-]", "_").Trim('_');
        var cleanBase = Regex.Replace(baseName ?? "Export", @"[^a-zA-Z0-9_-]", "_").Trim('_');

        // Clamp lengths
        if (cleanBank.Length > 20) cleanBank = cleanBank[..20];
        if (cleanBase.Length > 40) cleanBase = cleanBase[..40];

        return $"Accufex_{cleanBank}_{cleanBase}_{timestamp}.xlsx";
    }
}
