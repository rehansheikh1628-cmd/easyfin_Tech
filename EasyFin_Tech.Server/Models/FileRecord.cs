using System;
using System.Collections.Generic;

namespace EasyFin_Tech.Server.Models;

public partial class FileRecord
{
    public Guid Id { get; set; }

    public Guid ClientId { get; set; }

    public Guid FinancialYearId { get; set; }

    public string OriginalFileName { get; set; } = null!;

    public string StoredFileName { get; set; } = null!;

    public string Extension { get; set; } = null!;

    public string ContentType { get; set; } = null!;

    public long SizeBytes { get; set; }

    public DateTime UploadedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public int ProcessingStatus { get; set; }

    public string? ProcessingError { get; set; }

    public virtual Client Client { get; set; } = null!;

    public virtual FinancialYear FinancialYear { get; set; } = null!;

    public virtual PdfProcessingResult? PdfProcessingResult { get; set; }

    public virtual TransactionImportResult? TransactionImportResult { get; set; }

    public virtual ICollection<Transaction> Transactions { get; set; } = new List<Transaction>();
}
