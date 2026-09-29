using System;
using System.Collections.Generic;

namespace Accufex.Server.DTOs;

public record PdfBoundingBox(double X, double Y, double Width, double Height);

public class PdfTextBlock
{
    public string Text { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public int PageNumber { get; set; }
    public int ReadingOrderIndex { get; set; }
}

public class PdfCandidateCell
{
    public int ColumnIndex { get; set; }
    public string Text { get; set; } = string.Empty;
    public PdfBoundingBox BoundingBox { get; set; } = null!;
}

public class PdfCandidateColumn
{
    public int ColumnIndex { get; set; }
    public double LeftX { get; set; }
    public double RightX { get; set; }
    public string? HeaderText { get; set; }
}

public class PdfCandidateRow
{
    public int RowIndex { get; set; }
    public int PageNumber { get; set; }
    public double Y { get; set; }
    public double Height { get; set; }
    public string RawLineText { get; set; } = string.Empty;
    public List<PdfCandidateCell> Cells { get; set; } = [];
    public bool IsHeader { get; set; }
    public bool IsFooter { get; set; }
    public List<PdfTextBlock> LineFragments { get; set; } = [];
}

public class PdfCandidateTable
{
    public int TableIndex { get; set; }
    public int PageNumber { get; set; }
    public PdfBoundingBox BoundingBox { get; set; } = null!;
    public int ColumnCount { get; set; }
    public int RowCount { get; set; }
    public List<PdfCandidateColumn> Columns { get; set; } = [];
    public List<PdfCandidateRow> Rows { get; set; } = [];
    public double Confidence { get; set; }
}

public class PdfPageResult
{
    public int PageNumber { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public string RawText { get; set; } = string.Empty;
    public int WordCount { get; set; }
    public int CharacterCount { get; set; }
    public bool HasUsableText { get; set; }
    public List<PdfTextBlock> TextBlocks { get; set; } = [];
    public List<PdfCandidateRow> CandidateRows { get; set; } = [];
    public List<PdfCandidateTable> CandidateTables { get; set; } = [];
    public List<string> RepeatedHeaders { get; set; } = [];
    public List<string> RepeatedFooters { get; set; } = [];
}

public class PdfExtractionResult
{
    public Guid FileId { get; set; }
    public string OriginalFileName { get; set; } = string.Empty;
    public int PageCount { get; set; }
    public string ExtractionStatus { get; set; } = string.Empty; // DigitalTextExtracted, NoDigitalTextDetected, PasswordProtected, Failed
    public DateTime StartedAt { get; set; }
    public DateTime CompletedAt { get; set; }
    public long DurationMs { get; set; }
    public string PdfType { get; set; } = string.Empty; // DigitalWithText, ScannedOrRasterizedNoText, Encrypted, Unknown
    public bool HasUsableText { get; set; }
    public int CharacterCount { get; set; }
    public int WordCount { get; set; }
    public int TextBlockCount { get; set; }
    public int CandidateRowCount { get; set; }
    public int CandidateTableCount { get; set; }
    public List<PdfPageResult> Pages { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public List<string> Errors { get; set; } = [];
    public string? StorageArtifactPath { get; set; }
}

public class ExtractStatementRequest
{
    public string? Password { get; set; }
}
