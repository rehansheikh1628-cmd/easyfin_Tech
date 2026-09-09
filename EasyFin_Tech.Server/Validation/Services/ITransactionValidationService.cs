using System;
using System.Collections.Generic;
using EasyFin_Tech.Server.DTOs;
using EasyFin_Tech.Server.Models;
using EasyFin_Tech.Server.Validation.Models;

namespace EasyFin_Tech.Server.Validation.Services;

public interface ITransactionValidationService
{
    List<TransactionReviewDto> ValidateAndEnrichStatement(
        List<Transaction> transactions,
        StatementAuditSidecar? sidecar);

    TransactionReviewDto ValidateAndEnrichSingle(
        Transaction transaction,
        TransactionAuditRecord? auditRecord,
        Transaction? previousTransaction = null,
        bool isDuplicate = false);

    StatementValidationSummaryDto ComputeSummary(
        Guid statementFileId,
        int bankCode,
        string bankName,
        string parserVersion,
        List<TransactionReviewDto> enrichedTransactions);
}
