using System;
using System.Collections.Generic;

namespace Accufex.Server.Models;

public partial class Transaction
{
    public Guid Id { get; set; }

    public DateTime TransactionDate { get; set; }

    public string Description { get; set; } = null!;

    public decimal? Debit { get; set; }

    public decimal? Credit { get; set; }

    public decimal Amount { get; set; }

    public decimal? Balance { get; set; }

    public string? Reference { get; set; }

    public string? Utr { get; set; }

    public string? TransactionType { get; set; }

    public int BankCode { get; set; }

    public string? Account { get; set; }

    public Guid SourceFileId { get; set; }

    public Guid ClientId { get; set; }

    public Guid FinancialYearId { get; set; }

    public DateTime CreatedAt { get; set; }

    public string? ProcessingWarning { get; set; }

    public virtual Client Client { get; set; } = null!;

    public virtual FinancialYear FinancialYear { get; set; } = null!;

    public virtual FileRecord SourceFile { get; set; } = null!;
}
