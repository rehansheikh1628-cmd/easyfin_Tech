using System;
using System.Collections.Generic;

namespace EasyFin_Tech.Server.Models;

public partial class PdfProcessingResult
{
    public Guid Id { get; set; }

    public Guid FileRecordId { get; set; }

    public int PdfType { get; set; }

    public int ExtractionMethod { get; set; }

    public int PageCount { get; set; }

    public bool HasUsableText { get; set; }

    public string ExtractedTextStoragePath { get; set; } = null!;

    public DateTime ProcessedAt { get; set; }

    public virtual FileRecord FileRecord { get; set; } = null!;
}
