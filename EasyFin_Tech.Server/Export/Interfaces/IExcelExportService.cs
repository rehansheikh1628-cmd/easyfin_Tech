using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EasyFin_Tech.Server.DTOs;
using EasyFin_Tech.Server.Models;

namespace EasyFin_Tech.Server.Export.Interfaces;

/// <summary>
/// Service contract for generating professional, accounting-grade Excel workbooks
/// from validated and reviewed statement transactions.
/// </summary>
public interface IExcelExportService
{
    /// <summary>
    /// Generates a standardized multi-sheet OpenXML Excel workbook (.xlsx)
    /// containing final current transaction values, statement metadata, and accounting summary.
    /// </summary>
    Task<byte[]> GenerateStatementWorkbookAsync(
        FileRecord fileRecord,
        StatementValidationSummaryDto summary,
        List<TransactionReviewDto> transactions,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates a safe, sanitized filename for downloading the exported spreadsheet.
    /// </summary>
    string GenerateSafeExportFileName(
        string originalFileName,
        string bankName,
        DateTime? exportTimestamp = null);
}
