using System;
using System.Collections.Generic;

namespace Accufex.Server.Parsing.Models;

public class BankDetectionResult
{
    public BankType DetectedBank { get; set; } = BankType.Unknown;
    public string BankName { get; set; } = "Unknown";
    public double Confidence { get; set; }
    public bool IsSupported { get; set; }
    public string? AccountNumber { get; set; }
    public string? CustomerName { get; set; }
    public DateTime? StatementFrom { get; set; }
    public DateTime? StatementTo { get; set; }
    public string? DetectedFormat { get; set; }
    public List<string> DetectionSignals { get; set; } = [];
}
