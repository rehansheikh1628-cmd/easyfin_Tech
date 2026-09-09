using System;

namespace EasyFin_Tech.Server.DTOs;

public class StatementUploadResponse
{
    public bool Success { get; set; }

    public string Message { get; set; } = string.Empty;

    public Guid FileId { get; set; }

    public string OriginalFileName { get; set; } = string.Empty;

    public string StoredFileName { get; set; } = string.Empty;

    public long FileSizeBytes { get; set; }

    public string FileSizeFormatted { get; set; } = string.Empty;

    public string ContentType { get; set; } = string.Empty;

    public DateTime UploadedAt { get; set; }

    public string Status { get; set; } = "ReadyForProcessing";

    public int ProcessingStatus { get; set; }

    public bool IsDuplicate { get; set; }

    public string? DuplicateNotice { get; set; }

    public string FileHash { get; set; } = string.Empty;

    public Guid ClientId { get; set; }

    public Guid FinancialYearId { get; set; }
}
