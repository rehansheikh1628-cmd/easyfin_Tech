using System;
using System.Collections.Generic;

namespace EasyFin_Tech.Server.Validation.Models;

public class FieldChange
{
    public string FieldName { get; set; } = string.Empty;

    public string? OldValue { get; set; }

    public string? NewValue { get; set; }
}

public class CorrectionHistoryEntry
{
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    public Guid UserId { get; set; }

    public string Reason { get; set; } = string.Empty;

    public List<FieldChange> Changes { get; set; } = [];
}

public class TransactionAuditRecord
{
    public Guid TransactionId { get; set; }

    public bool IsCorrected { get; set; }

    public OriginalTransactionSnapshot OriginalValues { get; set; } = null!;

    public List<CorrectionHistoryEntry> History { get; set; } = [];
}

public class StatementAuditSidecar
{
    public Guid StatementFileId { get; set; }

    public int BankCode { get; set; }

    public string BankName { get; set; } = string.Empty;

    public string ParserVersion { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime LastModifiedAtUtc { get; set; } = DateTime.UtcNow;

    public Dictionary<Guid, TransactionAuditRecord> Records { get; set; } = [];
}
