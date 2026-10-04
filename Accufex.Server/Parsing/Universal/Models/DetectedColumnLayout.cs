namespace Accufex.Server.Parsing.Universal.Models;

/// <summary>
/// Geometric and semantic representation of a single detected column in an unknown statement.
/// </summary>
public class DetectedColumnLayout
{
    public int ColumnIndex { get; set; }

    public StatementColumnType ColumnType { get; set; } = StatementColumnType.Unknown;

    public string HeaderText { get; set; } = string.Empty;

    public double LeftX { get; set; }

    public double RightX { get; set; }

    public double Confidence { get; set; }

    public string? SampleDataPattern { get; set; }

    public override string ToString() => $"Col #{ColumnIndex} [{ColumnType}] '{HeaderText}' ({LeftX:F1} to {RightX:F1})";
}
