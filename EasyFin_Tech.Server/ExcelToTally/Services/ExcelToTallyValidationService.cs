using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EasyFin_Tech.Server.ExcelToTally.Interfaces;
using EasyFin_Tech.Server.ExcelToTally.Models;

namespace EasyFin_Tech.Server.ExcelToTally.Services;

public class ExcelToTallyValidationService : IExcelToTallyValidationService
{
    private static readonly string[] DateFormats =
    {
        "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy",
        "dd-MMM-yyyy", "d-MMM-yyyy", "dd MMM yyyy", "d MMM yyyy",
        "yyyy-MM-dd", "yyyy/MM/dd", "MM/dd/yyyy", "M/d/yyyy"
    };

    public ExcelValidationResult Validate(ExcelReadResult readResult)
    {
        var result = new ExcelValidationResult();

        if (readResult == null)
        {
            result.Success = false;
            result.ErrorMessage = "No Excel read result provided.";
            result.IsReadyForXmlGeneration = false;
            return result;
        }

        result.SampleRowsSkipped = readResult.SampleRowsSkipped;
        result.HasOfficialTemplateSignature = readResult.HasOfficialTemplateSignature;

        // 1. Handle Reader Failures (Malformed file, missing headers, etc.)
        if (!readResult.Success)
        {
            result.Success = false;
            result.ErrorMessage = readResult.ErrorMessage ?? "Failed to read Excel file.";
            result.IsReadyForXmlGeneration = false;

            if (readResult.MissingRequiredHeaders != null && readResult.MissingRequiredHeaders.Count > 0)
            {
                result.ValidationMessages.Add(new ExcelValidationIssue
                {
                    RowNumber = null,
                    Column = "Headers",
                    Severity = ExcelValidationSeverity.Fatal,
                    Message = $"Missing required columns: {string.Join(", ", readResult.MissingRequiredHeaders)}"
                });
            }
            else
            {
                result.ValidationMessages.Add(new ExcelValidationIssue
                {
                    RowNumber = null,
                    Column = null,
                    Severity = ExcelValidationSeverity.Fatal,
                    Message = result.ErrorMessage
                });
            }

            return result;
        }

        // 2. Validate Row Data
        var seenSignatures = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawRow in readResult.Rows)
        {
            if (rawRow.IsCompletelyEmpty)
            {
                continue; // Rule 12: Completely empty rows are safely ignored
            }

            result.TotalRows++;
            var tx = new ValidatedExcelTransaction
            {
                RowNumber = rawRow.RowNumber,
                Narration = rawRow.RawNarration?.Trim(),
                ChequeRefNo = rawRow.RawChequeRefNo?.Trim(),
                LedgerName = rawRow.RawLedgerName?.Trim(),
                BankName = rawRow.RawBankName?.Trim(),
                RawDate = rawRow.RawDate,
                RawDrAmount = rawRow.RawDrAmount,
                RawCrAmount = rawRow.RawCrAmount,
                RawClosingBalance = rawRow.RawClosingBalance
            };

            var rowIssues = new List<ExcelValidationIssue>();

            // Rule 2: Date is valid
            if (string.IsNullOrWhiteSpace(rawRow.RawDate))
            {
                rowIssues.Add(new ExcelValidationIssue
                {
                    RowNumber = rawRow.RowNumber,
                    Column = "Date",
                    Severity = ExcelValidationSeverity.Fatal,
                    Message = "Date is required and cannot be empty."
                });
            }
            else if (TryParseDate(rawRow.RawDate, out var parsedDate))
            {
                tx.Date = parsedDate;
            }
            else
            {
                rowIssues.Add(new ExcelValidationIssue
                {
                    RowNumber = rawRow.RowNumber,
                    Column = "Date",
                    Severity = ExcelValidationSeverity.Fatal,
                    Message = $"Invalid Date format: '{rawRow.RawDate}'. Expected format like dd/MM/yyyy or dd-MMM-yyyy."
                });
            }

            // Rule 3: Value Date is valid when supplied
            if (!string.IsNullOrWhiteSpace(rawRow.RawValueDate))
            {
                if (TryParseDate(rawRow.RawValueDate, out var parsedValDate))
                {
                    tx.ValueDate = parsedValDate;
                }
                else
                {
                    rowIssues.Add(new ExcelValidationIssue
                    {
                        RowNumber = rawRow.RowNumber,
                        Column = "Value Date",
                        Severity = ExcelValidationSeverity.Fatal,
                        Message = $"Invalid Value Date format: '{rawRow.RawValueDate}'."
                    });
                }
            }

            // Rule 4, 5, 6, 7, 8: Dr Amount and Cr Amount validation
            bool drHasValue = !string.IsNullOrWhiteSpace(rawRow.RawDrAmount);
            bool crHasValue = !string.IsNullOrWhiteSpace(rawRow.RawCrAmount);

            decimal parsedDr = 0m;
            decimal parsedCr = 0m;
            bool drValid = true;
            bool crValid = true;

            if (drHasValue)
            {
                if (TryParseAmount(rawRow.RawDrAmount!, out parsedDr))
                {
                    if (parsedDr < 0m)
                    {
                        rowIssues.Add(new ExcelValidationIssue
                        {
                            RowNumber = rawRow.RowNumber,
                            Column = "Dr Amount",
                            Severity = ExcelValidationSeverity.Fatal,
                            Message = "Dr Amount cannot be negative."
                        });
                        drValid = false;
                    }
                    else
                    {
                        tx.DrAmount = parsedDr;
                    }
                }
                else
                {
                    rowIssues.Add(new ExcelValidationIssue
                    {
                        RowNumber = rawRow.RowNumber,
                        Column = "Dr Amount",
                        Severity = ExcelValidationSeverity.Fatal,
                        Message = $"Dr Amount is not numeric: '{rawRow.RawDrAmount}'."
                    });
                    drValid = false;
                }
            }

            if (crHasValue)
            {
                if (TryParseAmount(rawRow.RawCrAmount!, out parsedCr))
                {
                    if (parsedCr < 0m)
                    {
                        rowIssues.Add(new ExcelValidationIssue
                        {
                            RowNumber = rawRow.RowNumber,
                            Column = "Cr Amount",
                            Severity = ExcelValidationSeverity.Fatal,
                            Message = "Cr Amount cannot be negative."
                        });
                        crValid = false;
                    }
                    else
                    {
                        tx.CrAmount = parsedCr;
                    }
                }
                else
                {
                    rowIssues.Add(new ExcelValidationIssue
                    {
                        RowNumber = rawRow.RowNumber,
                        Column = "Cr Amount",
                        Severity = ExcelValidationSeverity.Fatal,
                        Message = $"Cr Amount is not numeric: '{rawRow.RawCrAmount}'."
                    });
                    crValid = false;
                }
            }

            // Rule 7: Cannot have both Dr Amount and Cr Amount populated
            if (drValid && crValid && tx.DrAmount.HasValue && tx.DrAmount.Value > 0m && tx.CrAmount.HasValue && tx.CrAmount.Value > 0m)
            {
                rowIssues.Add(new ExcelValidationIssue
                {
                    RowNumber = rawRow.RowNumber,
                    Column = "Amount",
                    Severity = ExcelValidationSeverity.Fatal,
                    Message = "Dr Amount and Cr Amount cannot both be populated."
                });
            }

            // Rule 8: Must normally have either Dr Amount or Cr Amount populated
            bool hasPositiveDr = tx.DrAmount.HasValue && tx.DrAmount.Value > 0m;
            bool hasPositiveCr = tx.CrAmount.HasValue && tx.CrAmount.Value > 0m;

            if (drValid && crValid && !hasPositiveDr && !hasPositiveCr)
            {
                rowIssues.Add(new ExcelValidationIssue
                {
                    RowNumber = rawRow.RowNumber,
                    Column = "Amount",
                    Severity = ExcelValidationSeverity.Fatal,
                    Message = "Transaction must have either a Dr Amount or a Cr Amount populated."
                });
            }

            // Rule 9: Closing Balance must be numeric when supplied
            if (!string.IsNullOrWhiteSpace(rawRow.RawClosingBalance))
            {
                if (TryParseAmount(rawRow.RawClosingBalance, out var parsedBalance))
                {
                    tx.ClosingBalance = parsedBalance;
                }
                else
                {
                    rowIssues.Add(new ExcelValidationIssue
                    {
                        RowNumber = rawRow.RowNumber,
                        Column = "Closing Balance",
                        Severity = ExcelValidationSeverity.Fatal,
                        Message = $"Closing Balance must be a valid numeric amount: '{rawRow.RawClosingBalance}'."
                    });
                }
            }

            // Rule 10: Ledger Name cannot be empty
            if (string.IsNullOrWhiteSpace(tx.LedgerName))
            {
                rowIssues.Add(new ExcelValidationIssue
                {
                    RowNumber = rawRow.RowNumber,
                    Column = "Ledger Name",
                    Severity = ExcelValidationSeverity.Fatal,
                    Message = "Ledger Name cannot be empty."
                });
            }

            // Rule 11: Bank Name cannot be empty
            if (string.IsNullOrWhiteSpace(tx.BankName))
            {
                rowIssues.Add(new ExcelValidationIssue
                {
                    RowNumber = rawRow.RowNumber,
                    Column = "Bank Name",
                    Severity = ExcelValidationSeverity.Fatal,
                    Message = "Bank Name cannot be empty."
                });
            }

            // Rule 16: Duplicate transaction detection (Reported as Warning)
            if (tx.Date.HasValue && (hasPositiveDr || hasPositiveCr))
            {
                decimal effectiveAmt = hasPositiveDr ? tx.DrAmount!.Value : tx.CrAmount!.Value;
                string side = hasPositiveDr ? "DR" : "CR";
                string sig = $"{tx.Date.Value:yyyyMMdd}_{side}_{effectiveAmt:F2}_{tx.ChequeRefNo ?? ""}_{tx.Narration ?? ""}";

                if (seenSignatures.TryGetValue(sig, out int originalRow))
                {
                    rowIssues.Add(new ExcelValidationIssue
                    {
                        RowNumber = rawRow.RowNumber,
                        Column = "Duplicate",
                        Severity = ExcelValidationSeverity.Warning,
                        Message = $"Potential duplicate transaction matching Row {originalRow} (Date: {tx.Date.Value:dd/MM/yyyy}, Amount: {effectiveAmt:N2}, Ref: '{tx.ChequeRefNo}')."
                    });
                }
                else
                {
                    seenSignatures[sig] = rawRow.RowNumber;
                }
            }

            // Assign Status based on issues
            bool hasFatal = rowIssues.Any(i => i.Severity == ExcelValidationSeverity.Fatal);
            bool hasWarning = rowIssues.Any(i => i.Severity == ExcelValidationSeverity.Warning);

            if (hasFatal)
            {
                tx.Status = "Invalid";
                result.InvalidRows++;
            }
            else if (hasWarning)
            {
                tx.Status = "Warning";
                result.WarningRows++;
            }
            else
            {
                tx.Status = "Valid";
                result.ValidRows++;
            }

            tx.Issues = rowIssues;
            result.ValidationMessages.AddRange(rowIssues);
            result.ParsedTransactions.Add(tx);
        }

        // Accounting Safety Gate: XML generation blocked if any fatal error exists or zero transactions
        bool hasFatalIssues = result.ValidationMessages.Any(m => m.Severity == ExcelValidationSeverity.Fatal);
        result.IsReadyForXmlGeneration = result.TotalRows > 0 && result.InvalidRows == 0 && !hasFatalIssues;

        return result;
    }

    private static bool TryParseDate(string text, out DateTime date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        text = text.Trim();

        // 1. Exact match with standard patterns
        if (DateTime.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            return true;
        }

        // 2. Fallback general parse
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            return true;
        }

        return false;
    }

    private static bool TryParseAmount(string text, out decimal amount)
    {
        amount = 0m;
        if (string.IsNullOrWhiteSpace(text)) return false;

        text = text.Trim().Replace(",", "").Replace("₹", "").Replace("$", "");

        // Remove Dr/Cr trailing tags if any
        if (text.EndsWith("Dr", StringComparison.OrdinalIgnoreCase) || text.EndsWith("Cr", StringComparison.OrdinalIgnoreCase))
        {
            text = text.Substring(0, text.Length - 2).Trim();
        }

        return decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out amount);
    }
}
