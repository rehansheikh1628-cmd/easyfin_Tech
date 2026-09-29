using System;
using System.Collections.Generic;
using Accufex.Server.DTOs;
using Accufex.Server.Models;
using Accufex.Server.Validation.Models;

namespace Accufex.Server.Validation.Services;

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
