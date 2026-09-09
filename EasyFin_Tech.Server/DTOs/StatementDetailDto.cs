using System;

namespace EasyFin_Tech.Server.DTOs;

public class StatementDetailDto
{
    public Guid Id { get; set; }

    public string OriginalFileName { get; set; } = string.Empty;

    public string StoredFileName { get; set; } = string.Empty;

    public string Extension { get; set; } = string.Empty;

    public string ContentType { get; set; } = string.Empty;

    public long FileSizeBytes { get; set; }

    public string FileSizeFormatted { get; set; } = string.Empty;

    public DateTime UploadedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public string Status { get; set; } = "ReadyForProcessing";

    public int ProcessingStatus { get; set; }

    public string? ProcessingError { get; set; }

    public Guid ClientId { get; set; }

    public string? ClientName { get; set; }

    public Guid FinancialYearId { get; set; }

    public string? FinancialYearName { get; set; }
}
