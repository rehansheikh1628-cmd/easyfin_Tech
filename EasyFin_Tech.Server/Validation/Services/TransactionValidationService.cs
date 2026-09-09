using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EasyFin_Tech.Server.DTOs;
using EasyFin_Tech.Server.Models;
using EasyFin_Tech.Server.Parsing;
using EasyFin_Tech.Server.Validation.Models;

namespace EasyFin_Tech.Server.Validation.Services;

public class TransactionValidationService : ITransactionValidationService
{
    public List<TransactionReviewDto> ValidateAndEnrichStatement(
        List<Transaction> transactions,
        StatementAuditSidecar? sidecar)
    {
        if (transactions == null || transactions.Count == 0)
        {
            return [];
        }

        // Sort by Date then CreatedAt for deterministic sequence
        var sorted = transactions
            .OrderBy(t => t.TransactionDate)
            .ThenBy(t => t.CreatedAt)
            .ToList();

        // 1. Compute duplicate fingerprints across statement
        var duplicateFingerprints = IdentifyDuplicates(sorted);

        // 2. Validate and enrich each transaction sequentially
        var result = new List<TransactionReviewDto>(sorted.Count);
        Transaction? previous = null;

        foreach (var tx in sorted)
        {
            TransactionAuditRecord? auditRecord = null;
            sidecar?.Records.TryGetValue(tx.Id, out auditRecord);

            var fingerprint = CreateFingerprint(tx);
            var isDuplicate = duplicateFingerprints.Contains(fingerprint);

            var enriched = ValidateAndEnrichSingle(tx, auditRecord, previous, isDuplicate);
            result.Add(enriched);

            previous = tx;
        }

        return result;
    }

    public TransactionReviewDto ValidateAndEnrichSingle(
        Transaction transaction,
        TransactionAuditRecord? auditRecord,
        Transaction? previousTransaction = null,
        bool isDuplicate = false)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        // -------------------------------------------------------------
        // Rule A: Required Fields
        // -------------------------------------------------------------
        if (transaction.TransactionDate == default || transaction.TransactionDate == DateTime.MinValue)
        {
            errors.Add("Missing or invalid transaction date.");
        }
        else if (transaction.TransactionDate > DateTime.UtcNow.AddDays(1))
        {
            errors.Add("Transaction date cannot be in the future.");
        }

        if (string.IsNullOrWhiteSpace(transaction.Description))
        {
            errors.Add("Description/narration is required.");
        }
        else if (transaction.Description.Trim().Length < 2)
        {
            warnings.Add("Description is unusually short.");
        }

        if (transaction.Amount < 0.00m)
        {
            errors.Add("Transaction amount cannot be negative.");
        }
        else if (transaction.Amount == 0.00m)
        {
            if (transaction.Balance.HasValue && (transaction.BankCode == (int)BankType.CentralBank || transaction.BankCode == (int)BankType.ICICI || transaction.BankCode == (int)BankType.Kotak))
            {
                warnings.Add("Zero amount transaction recorded in bank statement (e.g. cheque return or memo entry).");
            }
            else
            {
                errors.Add("Transaction amount must be greater than zero.");
            }
        }

        var normalizedType = (transaction.TransactionType ?? string.Empty).Trim().ToUpperInvariant();
        if (normalizedType != "DEBIT" && normalizedType != "CREDIT")
        {
            errors.Add("Transaction type must be Debit or Credit.");
        }

        // -------------------------------------------------------------
        // Rule B: Optional & Format-Sensitive Fields
        // -------------------------------------------------------------
        DateTime? valueDate = auditRecord?.OriginalValues?.ValueDate;
        if (valueDate.HasValue)
        {
            var dayDifference = (valueDate.Value - transaction.TransactionDate).TotalDays;
            if (dayDifference < -30)
            {
                warnings.Add("Value date precedes transaction date by more than 30 days.");
            }
            else if (dayDifference > 30)
            {
                warnings.Add("Value date exceeds transaction date by more than 30 days.");
            }
        }

        // -------------------------------------------------------------
        // Rule C: Monetary Integrity
        // -------------------------------------------------------------
        var debit = transaction.Debit;
        var credit = transaction.Credit;

        var isZeroAmountAllowed = transaction.Amount == 0.00m &&
            transaction.Balance.HasValue &&
            (transaction.BankCode == (int)BankType.CentralBank || transaction.BankCode == (int)BankType.ICICI || transaction.BankCode == (int)BankType.Kotak);

        if (debit.HasValue && debit.Value > 0 && credit.HasValue && credit.Value > 0)
        {
            errors.Add("Both Debit and Credit amounts are populated.");
        }
        else if (isZeroAmountAllowed)
        {
            warnings.Add("Zero monetary value recorded in statement.");
        }
        else if ((!debit.HasValue || debit.Value < 0) && (!credit.HasValue || credit.Value < 0))
        {
            errors.Add("Either Debit or Credit must be populated.");
        }
        else if ((!debit.HasValue || debit.Value <= 0) && (!credit.HasValue || credit.Value <= 0))
        {
            errors.Add("Either Debit or Credit must be populated with a positive amount.");
        }

        if ((debit.HasValue && debit.Value < 0) || (credit.HasValue && credit.Value < 0))
        {
            errors.Add("Monetary values cannot be negative.");
        }

        if (debit.HasValue && debit.Value > 0 && Math.Abs(debit.Value - transaction.Amount) > 0.01m)
        {
            errors.Add($"Amount ({transaction.Amount:F2}) does not match Debit value ({debit.Value:F2}).");
        }

        if (credit.HasValue && credit.Value > 0 && Math.Abs(credit.Value - transaction.Amount) > 0.01m)
        {
            errors.Add($"Amount ({transaction.Amount:F2}) does not match Credit value ({credit.Value:F2}).");
        }

        // -------------------------------------------------------------
        // Rule D: Running Balance Validation
        // -------------------------------------------------------------
        var balanceStatus = BalanceStatus.NotCheckable;
        if (previousTransaction?.Balance.HasValue == true && transaction.Balance.HasValue)
        {
            var prevBalance = previousTransaction.Balance.Value;
            var currentBalance = transaction.Balance.Value;

            var dVal = (debit.HasValue && debit.Value > 0) ? debit.Value : 0.00m;
            var cVal = (credit.HasValue && credit.Value > 0) ? credit.Value : 0.00m;

            var expectedBalance = prevBalance - dVal + cVal;
            var diff = Math.Abs(expectedBalance - currentBalance);

            if (diff <= 0.01m)
            {
                balanceStatus = BalanceStatus.Balanced;
            }
            else
            {
                balanceStatus = BalanceStatus.Mismatch;
                warnings.Add($"Running balance mismatch: expected {expectedBalance:N2}, actual {currentBalance:N2} (diff: {diff:N2}).");
            }
        }

        // -------------------------------------------------------------
        // Rule E: Duplicate Check
        // -------------------------------------------------------------
        if (isDuplicate)
        {
            warnings.Add("Potential duplicate transaction detected with identical business values.");
        }

        // -------------------------------------------------------------
        // Rule F: Status & Severity Determination
        // -------------------------------------------------------------
        string status;
        string severity;
        bool isReadyForExport;

        bool isCorrected = auditRecord?.IsCorrected == true;

        if (errors.Count > 0)
        {
            status = ValidationStatus.Invalid;
            severity = ValidationSeverity.Error;
            isReadyForExport = false;
        }
        else if (warnings.Count > 0)
        {
            status = isCorrected ? ValidationStatus.Corrected : ValidationStatus.Review;
            severity = ValidationSeverity.Warning;
            isReadyForExport = true;
        }
        else
        {
            status = isCorrected ? ValidationStatus.Corrected : ValidationStatus.Valid;
            severity = ValidationSeverity.Info;
            isReadyForExport = true;
        }

        // -------------------------------------------------------------
        // Map DTO
        // -------------------------------------------------------------
        var bankName = transaction.BankCode switch
        {
            1 => "HDFC Bank",
            2 => "YES BANK",
            3 => "Axis Bank",
            4 => "Central Bank of India",
            5 => "ICICI Bank",
            6 => "State Bank of India",
            7 => "Bank of India",
            8 => "Kotak Mahindra Bank",
            _ => "Unknown Bank"
        };

        var parserVersion = auditRecord?.OriginalValues?.ParserVersion
            ?? (transaction.BankCode switch
            {
                1 => "HDFC-v1",
                2 => "YES-v1",
                3 => "AXIS-v1",
                4 => "CENTRAL-v1",
                5 => "ICICI-v1",
                6 => "SBI-v1",
                7 => "BOI-v1",
                8 => "KOTAK-v1",
                _ => "v1"
            });

        return new TransactionReviewDto
        {
            Id = transaction.Id,
            TransactionDate = transaction.TransactionDate,
            ValueDate = valueDate,
            Description = transaction.Description,
            Debit = transaction.Debit,
            Credit = transaction.Credit,
            Amount = transaction.Amount,
            Balance = transaction.Balance,
            Reference = transaction.Reference,
            Utr = transaction.Utr,
            TransactionType = transaction.TransactionType ?? (transaction.Debit.HasValue ? "Debit" : "Credit"),
            BankCode = transaction.BankCode,
            BankName = bankName,
            Account = transaction.Account,
            SourceFileId = transaction.SourceFileId,
            SourcePageNumber = auditRecord?.OriginalValues?.SourcePageNumber ?? 1,
            SourceLineIndex = auditRecord?.OriginalValues?.SourceLineIndex ?? 0,
            ParserVersion = parserVersion,
            ParserWarning = transaction.ProcessingWarning, // Strictly original parser warning!

            ValidationStatus = status,
            ValidationSeverity = severity,
            ValidationErrors = errors,
            ValidationWarnings = warnings,
            BalanceStatus = balanceStatus,
            IsDuplicate = isDuplicate,
            IsReadyForExport = isReadyForExport,

            IsCorrected = isCorrected,
            CorrectionCount = auditRecord?.History.Count ?? 0,
            OriginalValues = auditRecord?.OriginalValues != null ? MapSnapshotDto(auditRecord.OriginalValues) : null,
            History = auditRecord?.History.Select(MapHistoryDto).ToList() ?? []
        };
    }

    public StatementValidationSummaryDto ComputeSummary(
        Guid statementFileId,
        int bankCode,
        string bankName,
        string parserVersion,
        List<TransactionReviewDto> enrichedTransactions)
    {
        if (enrichedTransactions == null || enrichedTransactions.Count == 0)
        {
            return new StatementValidationSummaryDto
            {
                StatementFileId = statementFileId,
                DetectedBank = bankCode,
                BankName = bankName,
                ParserVersion = parserVersion
            };
        }

        int valid = 0;
        int review = 0;
        int invalid = 0;
        int corrected = 0;
        int debits = 0;
        int credits = 0;
        decimal totalDebits = 0;
        decimal totalCredits = 0;
        int checkable = 0;
        int matched = 0;
        int mismatched = 0;
        int duplicates = 0;

        foreach (var t in enrichedTransactions)
        {
            if (t.ValidationStatus == ValidationStatus.Valid) valid++;
            else if (t.ValidationStatus == ValidationStatus.Review) review++;
            else if (t.ValidationStatus == ValidationStatus.Invalid) invalid++;
            else if (t.ValidationStatus == ValidationStatus.Corrected) corrected++;

            if (t.Debit.HasValue && t.Debit.Value > 0)
            {
                debits++;
                totalDebits += t.Debit.Value;
            }
            else if (t.Credit.HasValue && t.Credit.Value > 0)
            {
                credits++;
                totalCredits += t.Credit.Value;
            }

            if (t.BalanceStatus == BalanceStatus.Balanced)
            {
                checkable++;
                matched++;
            }
            else if (t.BalanceStatus == BalanceStatus.Mismatch)
            {
                checkable++;
                mismatched++;
            }

            if (t.IsDuplicate)
            {
                duplicates++;
            }
        }

        return new StatementValidationSummaryDto
        {
            StatementFileId = statementFileId,
            DetectedBank = bankCode,
            BankName = bankName,
            ParserVersion = parserVersion,
            TotalTransactions = enrichedTransactions.Count,
            ValidCount = valid,
            ReviewCount = review,
            InvalidCount = invalid,
            CorrectedCount = corrected,
            DebitCount = debits,
            CreditCount = credits,
            TotalDebits = totalDebits,
            TotalCredits = totalCredits,
            NetMovement = totalCredits - totalDebits,
            BalanceCheckableCount = checkable,
            BalanceMatchedCount = matched,
            BalanceMismatchedCount = mismatched,
            DuplicateCount = duplicates,
            IsReadyForExport = invalid == 0
        };
    }

    private static HashSet<string> IdentifyDuplicates(List<Transaction> transactions)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in transactions)
        {
            var fp = CreateFingerprint(t);
            counts[fp] = counts.TryGetValue(fp, out var c) ? c + 1 : 1;
        }

        var dupes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (fp, count) in counts)
        {
            if (count > 1)
            {
                dupes.Add(fp);
            }
        }

        return dupes;
    }

    private static string CreateFingerprint(Transaction t)
    {
        var date = t.TransactionDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var desc = (t.Description ?? string.Empty).Trim().ToUpperInvariant();
        var amt = t.Amount.ToString("F2", CultureInfo.InvariantCulture);
        var type = (t.TransactionType ?? (t.Debit.HasValue ? "DEBIT" : "CREDIT")).Trim().ToUpperInvariant();
        var reference = (t.Reference ?? string.Empty).Trim().ToUpperInvariant();
        var bal = t.Balance?.ToString("F2", CultureInfo.InvariantCulture) ?? string.Empty;

        return $"{date}|{desc}|{amt}|{type}|{reference}|{bal}";
    }

    private static OriginalTransactionSnapshotDto MapSnapshotDto(OriginalTransactionSnapshot s)
    {
        return new OriginalTransactionSnapshotDto
        {
            TransactionId = s.TransactionId,
            TransactionDate = s.TransactionDate,
            ValueDate = s.ValueDate,
            Description = s.Description,
            Debit = s.Debit,
            Credit = s.Credit,
            Amount = s.Amount,
            Balance = s.Balance,
            Reference = s.Reference,
            Utr = s.Utr,
            TransactionType = s.TransactionType,
            BankCode = s.BankCode,
            Account = s.Account,
            SourcePageNumber = s.SourcePageNumber,
            SourceLineIndex = s.SourceLineIndex,
            ParserVersion = s.ParserVersion,
            ProcessingWarning = s.ProcessingWarning
        };
    }

    private static CorrectionHistoryEntryDto MapHistoryDto(CorrectionHistoryEntry h)
    {
        return new CorrectionHistoryEntryDto
        {
            TimestampUtc = h.TimestampUtc,
            UserId = h.UserId,
            Reason = h.Reason,
            Changes = h.Changes.Select(c => new FieldChangeDto
            {
                FieldName = c.FieldName,
                OldValue = c.OldValue,
                NewValue = c.NewValue
            }).ToList()
        };
    }
}
