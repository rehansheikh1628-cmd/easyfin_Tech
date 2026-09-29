using System;
using System.Collections.Generic;

namespace Accufex.Server.Parsing.Models;

public class BankParsingResult
{
    public bool Success { get; set; }

    public string? ErrorMessage { get; set; }

    public int BankCode { get; set; }

    public string BankName { get; set; } = string.Empty;

    public string ParserVersion { get; set; } = string.Empty;

    public string? AccountNumber { get; set; }

    public string? CustomerName { get; set; }

    public DateTime? StatementPeriodStart { get; set; }

    public DateTime? StatementPeriodEnd { get; set; }

    public int TotalDetected { get; set; }

    public int ProcessedCount { get; set; }

    public int RejectedCount { get; set; }

    public int WarningsCount { get; set; }

    public double OverallConfidence { get; set; }

    public List<ParsedTransaction> Transactions { get; set; } = [];

    public List<string> Warnings { get; set; } = [];

    public List<string> Errors { get; set; } = [];
}
