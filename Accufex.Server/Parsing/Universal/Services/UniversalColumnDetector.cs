using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Accufex.Server.DTOs;
using Accufex.Server.Parsing.Universal.Interfaces;
using Accufex.Server.Parsing.Universal.Models;
using Microsoft.Extensions.Logging;

namespace Accufex.Server.Parsing.Universal.Services;

/// <summary>
/// Bank-agnostic transaction table column detector.
/// Identifies column boundaries and semantic roles from spatial text blocks and candidate header rows.
/// </summary>
public class UniversalColumnDetector : IUniversalColumnDetector
{
    private readonly ILogger<UniversalColumnDetector> _logger;

    public UniversalColumnDetector(ILogger<UniversalColumnDetector> logger)
    {
        _logger = logger;
    }

    public List<DetectedColumnLayout> DetectColumns(List<PdfCandidateRow> candidateRows, List<PdfTextBlock> textBlocks, double pageWidth)
    {
        var result = new List<DetectedColumnLayout>();

        // 1. Identify candidate header row
        // A valid financial statement table header contains at least 3 distinct column concepts (e.g. Date, Description/Narration, Amount/Debit/Credit, Balance).
        PdfCandidateRow? bestHeaderRow = null;
        int bestMatchCount = 0;

        foreach (var row in candidateRows)
        {
            var lineText = row.RawLineText;
            if (string.IsNullOrWhiteSpace(lineText)) continue;

            int matchCount = 0;
            if (HasDateKeyword(lineText)) matchCount++;
            if (HasDescriptionKeyword(lineText)) matchCount++;
            if (HasDebitKeyword(lineText)) matchCount++;
            if (HasCreditKeyword(lineText)) matchCount++;
            if (HasBalanceKeyword(lineText)) matchCount++;
            if (HasAmountKeyword(lineText)) matchCount++;

            if (matchCount > bestMatchCount && matchCount >= 3)
            {
                bestMatchCount = matchCount;
                bestHeaderRow = row;
            }
        }

        if (bestHeaderRow == null)
        {
            _logger.LogInformation("No unambiguous tabular header row detected in candidate rows.");
            return result;
        }

        // 2. Classify individual column tokens in the header row
        var headerFrags = bestHeaderRow.LineFragments != null && bestHeaderRow.LineFragments.Count > 0
            ? bestHeaderRow.LineFragments.OrderBy(f => f.X).ToList()
            : bestHeaderRow.Cells?.Select(c => new PdfTextBlock { Text = c.Text, X = c.BoundingBox.X, Width = c.BoundingBox.Width, Y = c.BoundingBox.Y }).OrderBy(f => f.X).ToList()
            ?? [];

        if (headerFrags.Count == 0) return result;

        // Group closely adjacent header tokens (e.g. "Value" + "Date" or "Chq." + "No.")
        var tokenClusters = new List<List<PdfTextBlock>>();
        foreach (var frag in headerFrags)
        {
            if (string.IsNullOrWhiteSpace(frag.Text)) continue;

            if (tokenClusters.Count == 0)
            {
                tokenClusters.Add([frag]);
                continue;
            }

            var lastCluster = tokenClusters[^1];
            var lastFrag = lastCluster[^1];
            double gap = frag.X - (lastFrag.X + lastFrag.Width);

            // If gap is small (less than 15 points), consider it part of the same column header phrase
            if (gap < 15.0)
            {
                lastCluster.Add(frag);
            }
            else
            {
                tokenClusters.Add([frag]);
            }
        }

        int columnIndex = 0;
        for (int i = 0; i < tokenClusters.Count; i++)
        {
            var cluster = tokenClusters[i];
            var phrase = string.Join(" ", cluster.Select(w => w.Text)).Trim();
            double leftX = cluster.Min(w => w.X);
            double rightX = i + 1 < tokenClusters.Count ? tokenClusters[i + 1].Min(w => w.X) : (pageWidth > 0 ? pageWidth - 20.0 : leftX + 100.0);

            var colType = ClassifyColumnPhrase(phrase);

            result.Add(new DetectedColumnLayout
            {
                ColumnIndex = columnIndex++,
                ColumnType = colType,
                HeaderText = phrase,
                LeftX = leftX,
                RightX = rightX,
                Confidence = colType != StatementColumnType.Unknown ? 0.90 : 0.40
            });
        }

        _logger.LogInformation("Detected {Count} columns from candidate header '{Header}'.",
            result.Count, bestHeaderRow.RawLineText);

        return result;
    }

    private static StatementColumnType ClassifyColumnPhrase(string phrase)
    {
        var lower = phrase.ToLowerInvariant();

        if (lower.Contains("value date") || lower.Contains("value dt") || lower.Contains("val date"))
            return StatementColumnType.ValueDate;

        if (lower.Contains("date") || lower.Contains("txn date") || lower.Contains("tran date") || lower.Contains("post date"))
            return StatementColumnType.Date;

        if (lower.Contains("s.no") || lower.Contains("sl.no") || lower.Contains("sr no") || lower.Contains("serial no"))
            return StatementColumnType.SerialNumber;

        if (lower.Contains("chq") || lower.Contains("cheque") || lower.Contains("ref") || lower.Contains("utr") || lower.Contains("instrument"))
            return StatementColumnType.Reference;

        if (lower.Contains("withdrawal") || lower.Contains("debit") || lower.Contains("dr.") || lower.Equals("dr") || lower.Contains("payment"))
            return StatementColumnType.Debit;

        if (lower.Contains("deposit") || lower.Contains("credit") || lower.Contains("cr.") || lower.Equals("cr") || lower.Contains("receipt"))
            return StatementColumnType.Credit;

        if (lower.Contains("balance") || lower.Contains("closing") || lower.Contains("running"))
            return StatementColumnType.Balance;

        if (lower.Contains("particular") || lower.Contains("narration") || lower.Contains("description") || lower.Contains("details") || lower.Contains("remarks"))
            return StatementColumnType.Description;

        if (lower.Contains("debit/credit") || lower.Contains("dr/cr") || lower.Contains("cr/dr") || lower.Contains("d/c") || lower.Equals("type"))
            return StatementColumnType.Indicator;

        if (lower.Contains("amount") || lower.Contains("txn amount") || lower.Contains("transaction amount"))
            return StatementColumnType.SignedAmount;

        if (lower.Contains("branch"))
            return StatementColumnType.Branch;

        return StatementColumnType.Unknown;
    }

    private static bool HasDateKeyword(string text) =>
        Regex.IsMatch(text, @"\b(?:Date|Txn\s*Date|Tran\s*Date|Post\s*Date|Booking\s*Date)\b", RegexOptions.IgnoreCase);

    private static bool HasDescriptionKeyword(string text) =>
        Regex.IsMatch(text, @"\b(?:Particulars|Narration|Description|Transaction\s*Details|Details|Remarks)\b", RegexOptions.IgnoreCase);

    private static bool HasDebitKeyword(string text) =>
        Regex.IsMatch(text, @"\b(?:Withdrawal|Debit|Dr\.?|Debit\s*Amount)\b", RegexOptions.IgnoreCase);

    private static bool HasCreditKeyword(string text) =>
        Regex.IsMatch(text, @"\b(?:Deposit|Credit|Cr\.?|Credit\s*Amount)\b", RegexOptions.IgnoreCase);

    private static bool HasBalanceKeyword(string text) =>
        Regex.IsMatch(text, @"\b(?:Balance|Closing\s*Balance|Running\s*Balance)\b", RegexOptions.IgnoreCase);

    private static bool HasAmountKeyword(string text) =>
        Regex.IsMatch(text, @"\b(?:Amount|Amount\(INR\)|Txn\s*Amount|Transaction\s*Amount)\b", RegexOptions.IgnoreCase);
}
