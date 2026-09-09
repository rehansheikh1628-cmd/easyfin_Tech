using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ClosedXML.Excel;
using EasyFin_Tech.Server.ExcelToTally.Interfaces;
using EasyFin_Tech.Server.ExcelToTally.Models;
using Microsoft.Extensions.Logging;

namespace EasyFin_Tech.Server.ExcelToTally.Services;

public class ClosedXmlExcelReaderService : IExcelReaderService
{
    private readonly ILogger<ClosedXmlExcelReaderService> _logger;

    public const string RequiredWorksheetName = "Transactions";

    public static readonly string[] CanonicalHeaders =
    {
        "Date",
        "Narration",
        "Cheque / Ref No.",
        "Value Date",
        "Dr Amount",
        "Cr Amount",
        "Closing Balance",
        "Ledger Name",
        "Bank Name"
    };

    public ClosedXmlExcelReaderService(ILogger<ClosedXmlExcelReaderService> logger)
    {
        _logger = logger;
    }

    public Task<ExcelReadResult> ReadExcelAsync(Stream fileStream, CancellationToken cancellationToken = default)
    {
        var result = new ExcelReadResult();

        if (fileStream == null || fileStream.Length == 0)
        {
            result.Success = false;
            result.ErrorMessage = "The uploaded file is empty.";
            return Task.FromResult(result);
        }

        try
        {
            // ClosedXML reads the workbook from memory stream without executing any VBA macros.
            // Macro code in .xlsm is safely treated as untrusted static XML/binary package data.
            using var workbook = new XLWorkbook(fileStream);

            // 1. Template Integrity: Check for required "Transactions" worksheet
            if (!workbook.Worksheets.Contains(RequiredWorksheetName))
            {
                result.Success = false;
                result.ErrorMessage = $"The uploaded workbook does not match the EasyFin template structure. Missing required worksheet '{RequiredWorksheetName}'. Please download and use the official EasyFin XLSM template.";
                return Task.FromResult(result);
            }

            var worksheet = workbook.Worksheet(RequiredWorksheetName);

            // Check for official template metadata signature
            foreach (var prop in workbook.CustomProperties)
            {
                if (string.Equals(prop.Name, "EasyFin_Template_Type", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(prop.Value?.ToString(), "Tally_Import_XLSM", StringComparison.OrdinalIgnoreCase))
                {
                    result.HasOfficialTemplateSignature = true;
                    break;
                }
            }

            var rangeUsed = worksheet.RangeUsed();
            if (rangeUsed == null)
            {
                result.Success = false;
                result.ErrorMessage = $"The '{RequiredWorksheetName}' worksheet is completely empty.";
                return Task.FromResult(result);
            }

            // 2. Locate Header Row
            var firstRow = worksheet.FirstRowUsed();
            if (firstRow == null)
            {
                result.Success = false;
                result.ErrorMessage = $"No data rows found in '{RequiredWorksheetName}' worksheet.";
                return Task.FromResult(result);
            }

            int headerRowNumber = firstRow.RowNumber();
            var headerMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var cell in firstRow.CellsUsed())
            {
                var headerText = cell.GetString().Trim();
                if (!string.IsNullOrWhiteSpace(headerText))
                {
                    result.Headers.Add(headerText);
                    var canonicalKey = MapToCanonicalHeader(headerText);
                    if (canonicalKey != null && !headerMap.ContainsKey(canonicalKey))
                    {
                        headerMap[canonicalKey] = cell.Address.ColumnNumber;
                    }
                }
            }

            // Verify all 9 canonical headers exist
            foreach (var canonical in CanonicalHeaders)
            {
                if (!headerMap.ContainsKey(canonical))
                {
                    result.MissingRequiredHeaders.Add(canonical);
                }
            }

            if (result.MissingRequiredHeaders.Count > 0)
            {
                result.Success = false;
                result.ErrorMessage = $"Missing required column headers: {string.Join(", ", result.MissingRequiredHeaders)}";
                return Task.FromResult(result);
            }

            // 3. Read Data Rows
            int lastRowNumber = worksheet.LastRowUsed()?.RowNumber() ?? headerRowNumber;
            result.TotalPhysicalRowsRead = lastRowNumber - headerRowNumber;

            for (int r = headerRowNumber + 1; r <= lastRowNumber; r++)
            {
                var row = worksheet.Row(r);

                var rowData = new ExcelTransactionRow
                {
                    RowNumber = r,
                    RawDate = GetCellText(row, headerMap["Date"]),
                    RawNarration = GetCellText(row, headerMap["Narration"]),
                    RawChequeRefNo = GetCellText(row, headerMap["Cheque / Ref No."]),
                    RawValueDate = GetCellText(row, headerMap["Value Date"]),
                    RawDrAmount = GetCellText(row, headerMap["Dr Amount"]),
                    RawCrAmount = GetCellText(row, headerMap["Cr Amount"]),
                    RawClosingBalance = GetCellText(row, headerMap["Closing Balance"]),
                    RawLedgerName = GetCellText(row, headerMap["Ledger Name"]),
                    RawBankName = GetCellText(row, headerMap["Bank Name"])
                };

                // Ignore completely blank rows
                if (rowData.IsCompletelyEmpty)
                {
                    result.BlankRowsSkipped++;
                    continue;
                }

                // Ignore instructional / sample rows automatically
                if (IsSampleRow(rowData))
                {
                    result.SampleRowsSkipped++;
                    continue;
                }

                result.Rows.Add(rowData);
            }

            result.Success = true;
            return Task.FromResult(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read Excel stream.");
            result.Success = false;
            result.ErrorMessage = $"Malformed or unreadable Excel file: {ex.Message}";
            return Task.FromResult(result);
        }
    }

    private static bool IsSampleRow(ExcelTransactionRow row)
    {
        if (string.IsNullOrWhiteSpace(row.RawNarration))
        {
            return false;
        }

        var narration = row.RawNarration.Trim();
        return narration.StartsWith("[SAMPLE]", StringComparison.OrdinalIgnoreCase)
            || narration.Equals("SAMPLE", StringComparison.OrdinalIgnoreCase)
            || narration.StartsWith("SAMPLE:", StringComparison.OrdinalIgnoreCase)
            || narration.StartsWith("[EXAMPLE]", StringComparison.OrdinalIgnoreCase);
    }

    private static string? MapToCanonicalHeader(string header)
    {
        var cleaned = Regex.Replace(header.Trim().ToLowerInvariant(), @"[^a-z0-9]", "");

        return cleaned switch
        {
            "date" or "txndate" or "transactiondate" => "Date",
            "narration" or "description" or "particulars" or "details" => "Narration",
            "chequerefno" or "chqrefno" or "chequenumber" or "refno" or "referenceno" or "utr" or "chequeno" => "Cheque / Ref No.",
            "valuedate" or "valuedt" => "Value Date",
            "dramount" or "debitamount" or "debit" or "dr" => "Dr Amount",
            "cramount" or "creditamount" or "credit" or "cr" => "Cr Amount",
            "closingbalance" or "balance" or "bal" or "closingbal" => "Closing Balance",
            "ledgername" or "ledger" or "accounthead" or "partyname" => "Ledger Name",
            "bankname" or "bank" or "bankaccount" => "Bank Name",
            _ => null
        };
    }

    private static string? GetCellText(IXLRow row, int columnNumber)
    {
        var cell = row.Cell(columnNumber);
        if (cell.IsEmpty())
        {
            return null;
        }

        // Handle DateTime cell formats
        if (cell.DataType == XLDataType.DateTime)
        {
            return cell.GetDateTime().ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        }

        if (cell.DataType == XLDataType.Number)
        {
            return cell.GetDouble().ToString("0.00", CultureInfo.InvariantCulture);
        }

        var text = cell.GetFormattedString()?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            text = cell.GetString()?.Trim();
        }

        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
