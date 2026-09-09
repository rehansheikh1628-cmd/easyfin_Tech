using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EasyFin_Tech.Server.ExcelToTally.Interfaces;
using EasyFin_Tech.Server.ExcelToTally.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace EasyFin_Tech.Server.Controllers;

[ApiController]
[Route("api/excel-to-tally")]
public class ExcelToTallyController : ControllerBase
{
    private readonly IExcelReaderService _excelReader;
    private readonly IExcelToTallyValidationService _validationService;
    private readonly IExcelTemplateService _templateService;
    private readonly ITallyXmlGenerator _xmlGenerator;
    private readonly ILogger<ExcelToTallyController> _logger;

    public ExcelToTallyController(
        IExcelReaderService excelReader,
        IExcelToTallyValidationService validationService,
        IExcelTemplateService templateService,
        ITallyXmlGenerator xmlGenerator,
        ILogger<ExcelToTallyController> logger)
    {
        _excelReader = excelReader;
        _validationService = validationService;
        _templateService = templateService;
        _xmlGenerator = xmlGenerator;
        _logger = logger;
    }

    /// <summary>
    /// Downloads the official EasyFin Excel-to-Tally template (.XLSM) with 9 canonical columns and guidelines.
    /// </summary>
    [HttpGet("template")]
    public IActionResult DownloadTemplate()
    {
        try
        {
            var bytes = _templateService.GenerateTemplate();
            return File(
                bytes,
                "application/vnd.ms-excel.sheet.macroEnabled.12",
                "EasyFin_Tally_Import_Template_v1.xlsm");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate Excel template.");
            return StatusCode(500, new { message = "Error generating Excel template." });
        }
    }

    /// <summary>
    /// Reads and validates an uploaded Excel file, returning validation metrics, row-numbered error messages, and parsed transactions.
    /// </summary>
    [HttpPost("validate")]
    public async Task<ActionResult<ExcelValidationResult>> ValidateExcel(IFormFile? file, CancellationToken cancellationToken)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest(new ExcelValidationResult
            {
                Success = false,
                ErrorMessage = "No file was uploaded or file is empty.",
                IsReadyForXmlGeneration = false,
                ValidationMessages =
                {
                    new ExcelValidationIssue
                    {
                        Severity = ExcelValidationSeverity.Fatal,
                        Message = "Please select an official EasyFin Excel file (.xlsm) to upload."
                    }
                }
            });
        }

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext != ".xlsm" && ext != ".xlsx" && ext != ".xls")
        {
            return BadRequest(new ExcelValidationResult
            {
                Success = false,
                ErrorMessage = $"Unsupported file format '{ext}'. EasyFin expects the official .xlsm template (or .xlsx).",
                IsReadyForXmlGeneration = false,
                ValidationMessages =
                {
                    new ExcelValidationIssue
                    {
                        Severity = ExcelValidationSeverity.Fatal,
                        Message = $"Unsupported file extension '{ext}'. Please upload the official EasyFin template (.xlsm) or .xlsx."
                    }
                }
            });
        }

        const long maxSizeBytes = 50 * 1024 * 1024; // 50MB
        if (file.Length > maxSizeBytes)
        {
            return BadRequest(new ExcelValidationResult
            {
                Success = false,
                ErrorMessage = "File size exceeds the 50MB limit.",
                IsReadyForXmlGeneration = false,
                ValidationMessages =
                {
                    new ExcelValidationIssue
                    {
                        Severity = ExcelValidationSeverity.Fatal,
                        Message = $"File size ({file.Length / (1024 * 1024)}MB) exceeds maximum allowed size of 50MB."
                    }
                }
            });
        }

        try
        {
            using var stream = file.OpenReadStream();
            var readResult = await _excelReader.ReadExcelAsync(stream, cancellationToken);
            var validationResult = _validationService.Validate(readResult);

            return Ok(validationResult);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error processing Excel file {FileName}", file.FileName);
            return StatusCode(500, new ExcelValidationResult
            {
                Success = false,
                ErrorMessage = $"An error occurred processing the Excel file: {ex.Message}",
                IsReadyForXmlGeneration = false
            });
        }
    }

    /// <summary>
    /// Generates a Tally-compatible XML document from validated transactions.
    /// Performs strict server-side re-validation before generation.
    /// </summary>
    [HttpPost("generate-xml")]
    public async Task<IActionResult> GenerateXml([FromBody] ExcelValidationResult? request, CancellationToken cancellationToken)
    {
        if (request == null || request.ParsedTransactions == null || request.ParsedTransactions.Count == 0)
        {
            return BadRequest(new { message = "No transaction data provided for XML generation." });
        }

        // Server-side strict re-validation: Never trust isReadyForXmlGeneration from client alone
        foreach (var tx in request.ParsedTransactions)
        {
            if (tx.Date == null || tx.Date == default)
            {
                return BadRequest(new { message = $"XML generation blocked: Row {tx.RowNumber} — Date is required and must be valid." });
            }

            if (tx.DrAmount.HasValue && tx.DrAmount.Value < 0)
            {
                return BadRequest(new { message = $"XML generation blocked: Row {tx.RowNumber} — Dr Amount cannot be negative." });
            }

            if (tx.CrAmount.HasValue && tx.CrAmount.Value < 0)
            {
                return BadRequest(new { message = $"XML generation blocked: Row {tx.RowNumber} — Cr Amount cannot be negative." });
            }

            bool hasDr = tx.DrAmount.HasValue && tx.DrAmount.Value > 0;
            bool hasCr = tx.CrAmount.HasValue && tx.CrAmount.Value > 0;

            if (hasDr && hasCr)
            {
                return BadRequest(new { message = $"XML generation blocked: Row {tx.RowNumber} — Dr Amount and Cr Amount cannot both be populated." });
            }

            if (!hasDr && !hasCr)
            {
                return BadRequest(new { message = $"XML generation blocked: Row {tx.RowNumber} — Either Dr Amount or Cr Amount must be greater than zero." });
            }

            if (string.IsNullOrWhiteSpace(tx.LedgerName))
            {
                return BadRequest(new { message = $"XML generation blocked: Row {tx.RowNumber} — Ledger Name cannot be empty." });
            }

            if (string.IsNullOrWhiteSpace(tx.BankName))
            {
                return BadRequest(new { message = $"XML generation blocked: Row {tx.RowNumber} — Bank Name cannot be empty." });
            }

            if (tx.Issues != null && tx.Issues.Any(i => i.Severity == ExcelValidationSeverity.Fatal))
            {
                var fatal = tx.Issues.First(i => i.Severity == ExcelValidationSeverity.Fatal);
                return BadRequest(new { message = $"XML generation blocked: Row {tx.RowNumber} — {fatal.Message}" });
            }
        }

        try
        {
            var xmlBytes = await _xmlGenerator.GenerateTallyXmlAsync(request, cancellationToken);
            string fileName = $"EasyFin_Tally_Export_{DateTime.UtcNow:yyyyMMdd_HHmmss}.xml";

            return File(xmlBytes, "application/xml", fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate Tally XML.");
            return StatusCode(500, new { message = "XML generation failed due to a server error. No XML file was generated." });
        }
    }
}
