using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Accufex.Server.DTOs;
using Accufex.Server.Parsing.Interfaces;
using Accufex.Server.Parsing.Models;
using Microsoft.Extensions.Logging;

namespace Accufex.Server.Parsing;

public class BankDetector : IBankDetector
{
    private readonly ILogger<BankDetector> _logger;

    public BankDetector(ILogger<BankDetector> logger)
    {
        _logger = logger;
    }

    public BankDetectionResult DetectBank(PdfExtractionResult extraction)
    {
        if (extraction == null || extraction.Pages == null || extraction.Pages.Count == 0)
        {
            return new BankDetectionResult
            {
                DetectedBank = BankType.Unknown,
                BankName = "Unknown",
                Confidence = 0.0,
                IsSupported = false,
                DetectionSignals = ["No extracted pages available."]
            };
        }

        var signals = new List<string>();

        var samplePages = extraction.Pages.Take(Math.Min(4, extraction.Pages.Count)).ToList();
        var combinedText = string.Join("\n", samplePages.Select(p =>
            !string.IsNullOrWhiteSpace(p.RawText)
                ? p.RawText
                : (p.TextBlocks != null && p.TextBlocks.Count > 0
                    ? string.Join(" ", p.TextBlocks.Select(b => b.Text))
                    : (p.CandidateRows != null ? string.Join("\n", p.CandidateRows.Select(r => r.RawLineText)) : string.Empty))));

        // 1. Check HDFC Brand & Structural Signals
        bool hasHdfcBrand = combinedText.Contains("HDFC BANK", StringComparison.OrdinalIgnoreCase);
        bool hasHdfcIfsc = Regex.IsMatch(combinedText, @"\bHDFC0[A-Z0-9]{6}\b", RegexOptions.IgnoreCase);
        bool hasHdfcHeader = combinedText.Contains("HDFC BANK LIMITED", StringComparison.OrdinalIgnoreCase);

        // HDFC Table Column Structure (Expected: Date, Narration, Chq./Ref.No., Value Dt, Withdrawal Amt., Deposit Amt., Closing Balance)
        bool hasDateCol = combinedText.Contains("Date", StringComparison.OrdinalIgnoreCase);
        bool hasNarrCol = combinedText.Contains("Narration", StringComparison.OrdinalIgnoreCase);
        bool hasRefCol = combinedText.Contains("Chq", StringComparison.OrdinalIgnoreCase) || combinedText.Contains("Ref.No", StringComparison.OrdinalIgnoreCase);
        bool hasValueDtCol = combinedText.Contains("Value Dt", StringComparison.OrdinalIgnoreCase) || combinedText.Contains("Value Date", StringComparison.OrdinalIgnoreCase);
        bool hasWithdrawalCol = combinedText.Contains("Withdrawal", StringComparison.OrdinalIgnoreCase);
        bool hasDepositCol = combinedText.Contains("Deposit", StringComparison.OrdinalIgnoreCase);
        bool hasClosingBalanceCol = combinedText.Contains("Closing Balance", StringComparison.OrdinalIgnoreCase);
        bool hasBalanceCol = hasClosingBalanceCol || combinedText.Contains("Balance", StringComparison.OrdinalIgnoreCase);

        int matchedHdfcColumns = 0;
        if (hasDateCol) matchedHdfcColumns++;
        if (hasNarrCol) matchedHdfcColumns++;
        if (hasRefCol) matchedHdfcColumns++;
        if (hasValueDtCol) matchedHdfcColumns++;
        if (hasWithdrawalCol) matchedHdfcColumns++;
        if (hasDepositCol) matchedHdfcColumns++;
        if (hasBalanceCol) matchedHdfcColumns++;

        if (hasHdfcBrand) signals.Add("Detected 'HDFC BANK' branding text.");
        if (hasHdfcIfsc) signals.Add("Detected HDFC RTGS/NEFT IFSC code pattern (HDFC0...).");
        if (hasHdfcHeader) signals.Add("Detected 'HDFC BANK LIMITED' corporate footer signature.");
        if (matchedHdfcColumns >= 5) signals.Add($"Detected {matchedHdfcColumns}/7 HDFC standard table column headers.");

        // HDFC Metadata Extraction (Account Number, Statement Period, Customer Name)
        string? accountNumber = null;
        var accMatch = Regex.Match(combinedText, @"Account\s*(?:No|Number)\s*[:.]?\s*(\d{10,18})", RegexOptions.IgnoreCase);
        if (accMatch.Success)
        {
            accountNumber = accMatch.Groups[1].Value.Trim();
            signals.Add($"Identified Account Number: {accountNumber}");
        }

        DateTime? periodStart = null;
        DateTime? periodEnd = null;
        var periodMatch = Regex.Match(combinedText, @"(?:Statement\s+)?From\s*[:.]?\s*(\d{2}/\d{2}/\d{2,4})\s*To\s*[:.]?\s*(\d{2}/\d{2}/\d{2,4})", RegexOptions.IgnoreCase);
        if (periodMatch.Success)
        {
            string s1 = periodMatch.Groups[1].Value;
            string s2 = periodMatch.Groups[2].Value;
            if (DateTime.TryParseExact(s1, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var pStart) ||
                DateTime.TryParseExact(s1, "dd/MM/yy", CultureInfo.InvariantCulture, DateTimeStyles.None, out pStart))
            {
                periodStart = pStart;
            }
            if (DateTime.TryParseExact(s2, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var pEnd) ||
                DateTime.TryParseExact(s2, "dd/MM/yy", CultureInfo.InvariantCulture, DateTimeStyles.None, out pEnd))
            {
                periodEnd = pEnd;
            }
            if (periodStart.HasValue && periodEnd.HasValue)
            {
                signals.Add($"Identified Statement Period: {periodStart:dd/MM/yyyy} to {periodEnd:dd/MM/yyyy}");
            }
        }

        string? customerName = null;
        var nameMatch = Regex.Match(combinedText, @"\b(?:MR|MRS|MS|M/S\.?)\s+[A-Z0-9\s.]{3,40}", RegexOptions.IgnoreCase);
        if (nameMatch.Success)
        {
            customerName = nameMatch.Value.Trim();
            signals.Add($"Identified Customer Name: {customerName}");
        }

        // 2. Check YES BANK Brand & Structural Signals
        bool hasYesBrand = combinedText.Contains("YES BANK", StringComparison.OrdinalIgnoreCase);
        bool hasYesIfsc = Regex.IsMatch(combinedText, @"\bYESB0[A-Z0-9]{6}\b", RegexOptions.IgnoreCase);
        bool hasYesHeader = combinedText.Contains("YES BANK LIMITED", StringComparison.OrdinalIgnoreCase) ||
                            combinedText.Contains("YES BANK LTD", StringComparison.OrdinalIgnoreCase);
        bool hasYesService = combinedText.Contains("YesRewardz", StringComparison.OrdinalIgnoreCase) ||
                             combinedText.Contains("YES TOUCH", StringComparison.OrdinalIgnoreCase);
        bool hasYesStatementLabel = combinedText.Contains("Statement of account:", StringComparison.OrdinalIgnoreCase) ||
                                    combinedText.Contains("Transaction details for your account number", StringComparison.OrdinalIgnoreCase);

        var yesSignals = new List<string>();
        if (hasYesBrand) yesSignals.Add("Detected 'YES BANK' branding text.");
        if (hasYesIfsc) yesSignals.Add("Detected YES BANK IFSC code pattern (YESB0...).");
        if (hasYesHeader) yesSignals.Add("Detected YES BANK corporate entity header/footer signature.");
        if (hasYesService) yesSignals.Add("Detected YES BANK customer touchpoint signature (YesRewardz/YES TOUCH).");
        if (hasYesStatementLabel) yesSignals.Add("Detected YES BANK specific statement layout title.");

        // YES BANK Table Column Structure (Format 1: Transaction Date, Value Date, Cheque No/ Reference No, Description, Withdrawals, Deposits, Running Balance)
        bool hasYesTxDateCol = combinedText.Contains("Transaction Date", StringComparison.OrdinalIgnoreCase);
        bool hasYesValDateCol = combinedText.Contains("Value Date", StringComparison.OrdinalIgnoreCase) || combinedText.Contains("ValueDate", StringComparison.OrdinalIgnoreCase);
        bool hasYesRefCol = combinedText.Contains("Cheque No", StringComparison.OrdinalIgnoreCase) ||
                            combinedText.Contains("Reference No", StringComparison.OrdinalIgnoreCase) ||
                            combinedText.Contains("Chequeno/RefNo", StringComparison.OrdinalIgnoreCase) ||
                            combinedText.Contains("TranID", StringComparison.OrdinalIgnoreCase);
        bool hasYesDescCol = combinedText.Contains("Description", StringComparison.OrdinalIgnoreCase) ||
                             combinedText.Contains("Transaction Remarks", StringComparison.OrdinalIgnoreCase);
        bool hasYesWithdrawalCol = combinedText.Contains("Withdrawals", StringComparison.OrdinalIgnoreCase) ||
                                   combinedText.Contains("Withdrawl(Dr)", StringComparison.OrdinalIgnoreCase);
        bool hasYesDepositCol = combinedText.Contains("Deposits", StringComparison.OrdinalIgnoreCase) ||
                                combinedText.Contains("Deposit (Cr)", StringComparison.OrdinalIgnoreCase);
        bool hasYesBalanceCol = combinedText.Contains("Running Balance", StringComparison.OrdinalIgnoreCase);

        int matchedYesColumns = 0;
        if (hasYesTxDateCol) matchedYesColumns++;
        if (hasYesValDateCol) matchedYesColumns++;
        if (hasYesRefCol) matchedYesColumns++;
        if (hasYesDescCol) matchedYesColumns++;
        if (hasYesWithdrawalCol) matchedYesColumns++;
        if (hasYesDepositCol) matchedYesColumns++;
        if (hasYesBalanceCol || combinedText.Contains("Balance", StringComparison.OrdinalIgnoreCase)) matchedYesColumns++;

        if (matchedYesColumns >= 5)
        {
            yesSignals.Add($"Detected {matchedYesColumns}/7 YES BANK table column headers.");
        }

        // YES BANK Account Number Extraction (e.g. "Statement of account: 113763300000920" or "account number 113763300000920")
        string? yesAccountNumber = null;
        var yesAccMatch = Regex.Match(combinedText, @"(?:Statement\s+of\s+account|account\s*(?:number|no\.?|num))\s*[:.]?\s*(\d{10,18})", RegexOptions.IgnoreCase);
        if (!yesAccMatch.Success)
        {
            yesAccMatch = Regex.Match(combinedText, @"\b(\d{15})\b");
        }
        if (yesAccMatch.Success)
        {
            yesAccountNumber = yesAccMatch.Groups[1].Value.Trim();
            yesSignals.Add($"Identified YES BANK Account Number: {yesAccountNumber}");
        }

        // YES BANK Statement Period Extraction (e.g. "Period: From 01-Mar-2025 To 27-Sep-2025")
        DateTime? yesPeriodStart = null;
        DateTime? yesPeriodEnd = null;
        var yesPeriodMatch = Regex.Match(combinedText, @"Period\s*[:.]?\s*From\s*[:.]?\s*(\d{2}-[A-Za-z]{3}-\d{4})\s*To\s*[:.]?\s*(\d{2}-[A-Za-z]{3}-\d{4})", RegexOptions.IgnoreCase);
        if (yesPeriodMatch.Success)
        {
            string s1 = yesPeriodMatch.Groups[1].Value;
            string s2 = periodMatch.Groups.Count > 2 ? periodMatch.Groups[2].Value : "";
            if (yesPeriodMatch.Groups.Count > 2) s2 = yesPeriodMatch.Groups[2].Value;
            if (DateTime.TryParseExact(s1, "dd-MMM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var yStart))
            {
                yesPeriodStart = yStart;
            }
            if (DateTime.TryParseExact(s2, "dd-MMM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var yEnd))
            {
                yesPeriodEnd = yEnd;
            }
            if (yesPeriodStart.HasValue && yesPeriodEnd.HasValue)
            {
                yesSignals.Add($"Identified YES BANK Statement Period: {yesPeriodStart:dd-MMM-yyyy} to {yesPeriodEnd:dd-MMM-yyyy}");
            }
        }

        // 3. Check Axis Bank Brand & Structural Signals
        bool hasAxisBrand = combinedText.Contains("AXIS BANK", StringComparison.OrdinalIgnoreCase);
        bool hasAxisIfsc = Regex.IsMatch(combinedText, @"\bUTIB0[A-Z0-9]{6}\b", RegexOptions.IgnoreCase);
        bool hasAxisHeader = combinedText.Contains("AXIS BANK LIMITED", StringComparison.OrdinalIgnoreCase) ||
                             combinedText.Contains("AXIS BANK LTD", StringComparison.OrdinalIgnoreCase);
        bool hasAxisStatementLabel = combinedText.Contains("Statement of Axis Bank Account No", StringComparison.OrdinalIgnoreCase) ||
                                     combinedText.Contains("Account Statement Report", StringComparison.OrdinalIgnoreCase) ||
                                     combinedText.Contains("corporate.ib@axisbank.com", StringComparison.OrdinalIgnoreCase) ||
                                     combinedText.Contains("customer.service@axisbank.com", StringComparison.OrdinalIgnoreCase);

        var axisSignals = new List<string>();
        if (hasAxisBrand) axisSignals.Add("Detected 'AXIS BANK' branding text.");
        if (hasAxisIfsc) axisSignals.Add("Detected Axis Bank IFSC code pattern (UTIB0...).");
        if (hasAxisHeader) axisSignals.Add("Detected Axis Bank corporate entity header/footer signature.");
        if (hasAxisStatementLabel) axisSignals.Add("Detected Axis Bank specific statement report signature.");

        // Axis Bank Table Column Structure: S.NO, Transaction Date, Value Date, Particulars, Amount(INR), Debit/Credit, Balance(INR), Cheque Number, Branch Name(SOL)
        bool hasAxisSnoCol = combinedText.Contains("S.NO", StringComparison.OrdinalIgnoreCase) || combinedText.Contains("S.No", StringComparison.OrdinalIgnoreCase);
        bool hasAxisTxDateCol = combinedText.Contains("Transaction Date", StringComparison.OrdinalIgnoreCase);
        bool hasAxisValDateCol = combinedText.Contains("Value Date", StringComparison.OrdinalIgnoreCase);
        bool hasAxisParticularsCol = combinedText.Contains("Particulars", StringComparison.OrdinalIgnoreCase);
        bool hasAxisAmountCol = combinedText.Contains("Amount(INR)", StringComparison.OrdinalIgnoreCase) ||
                                combinedText.Contains("Amount (INR)", StringComparison.OrdinalIgnoreCase) ||
                                combinedText.Contains("Amount", StringComparison.OrdinalIgnoreCase);
        bool hasAxisDrCrCol = combinedText.Contains("Debit/Credit", StringComparison.OrdinalIgnoreCase) ||
                              combinedText.Contains("DR/CR", StringComparison.OrdinalIgnoreCase);
        bool hasAxisBalanceCol = combinedText.Contains("Balance(INR)", StringComparison.OrdinalIgnoreCase) ||
                                 combinedText.Contains("Balance (INR)", StringComparison.OrdinalIgnoreCase) ||
                                 combinedText.Contains("Balance", StringComparison.OrdinalIgnoreCase);
        bool hasAxisChequeCol = combinedText.Contains("Cheque Number", StringComparison.OrdinalIgnoreCase) ||
                                combinedText.Contains("Cheque", StringComparison.OrdinalIgnoreCase);
        bool hasAxisBranchCol = combinedText.Contains("Branch Name(SOL)", StringComparison.OrdinalIgnoreCase) ||
                                combinedText.Contains("Branch Name", StringComparison.OrdinalIgnoreCase) ||
                                combinedText.Contains("(SOL)", StringComparison.OrdinalIgnoreCase);

        int matchedAxisColumns = 0;
        if (hasAxisSnoCol) matchedAxisColumns++;
        if (hasAxisTxDateCol) matchedAxisColumns++;
        if (hasAxisValDateCol) matchedAxisColumns++;
        if (hasAxisParticularsCol) matchedAxisColumns++;
        if (hasAxisAmountCol) matchedAxisColumns++;
        if (hasAxisDrCrCol) matchedAxisColumns++;
        if (hasAxisBalanceCol) matchedAxisColumns++;
        if (hasAxisChequeCol) matchedAxisColumns++;
        if (hasAxisBranchCol) matchedAxisColumns++;

        if (matchedAxisColumns >= 5)
        {
            axisSignals.Add($"Detected {matchedAxisColumns}/9 Axis Bank table column headers.");
        }

        // Axis Bank Account Number Extraction (e.g. "Statement of Axis Bank Account No : 924020025076740" or "Account No : 924020025076740")
        string? axisAccountNumber = null;
        var axisAccMatch = Regex.Match(combinedText, @"(?:Statement\s+of\s+Axis\s+Bank\s+Account\s+No|Account\s*(?:No|Number))\s*[:.]?\s*(\d{10,18})", RegexOptions.IgnoreCase);
        if (axisAccMatch.Success)
        {
            axisAccountNumber = axisAccMatch.Groups[1].Value.Trim();
            axisSignals.Add($"Identified Axis Bank Account Number: {axisAccountNumber}");
        }

        // Axis Bank Statement Period Extraction (e.g. "From : 01/04/2025 To : 31/03/2026")
        DateTime? axisPeriodStart = null;
        DateTime? axisPeriodEnd = null;
        var axisPeriodMatch = Regex.Match(combinedText, @"From\s*[:.]?\s*(\d{2}/\d{2}/\d{4})\s*To\s*[:.]?\s*(\d{2}/\d{2}/\d{4})", RegexOptions.IgnoreCase);
        if (axisPeriodMatch.Success)
        {
            string s1 = axisPeriodMatch.Groups[1].Value;
            string s2 = axisPeriodMatch.Groups[2].Value;
            if (DateTime.TryParseExact(s1, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var aStart))
            {
                axisPeriodStart = aStart;
            }
            if (DateTime.TryParseExact(s2, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var aEnd))
            {
                axisPeriodEnd = aEnd;
            }
            if (axisPeriodStart.HasValue && axisPeriodEnd.HasValue)
            {
                axisSignals.Add($"Identified Axis Bank Statement Period: {axisPeriodStart:dd/MM/yyyy} to {axisPeriodEnd:dd/MM/yyyy}");
            }
        }

        // Axis Bank Customer Name Extraction (e.g. line between "Account Statement Report" and "Joint Holder" / address)
        string? axisCustomerName = null;
        var axisNameMatch = Regex.Match(combinedText, @"Account\s+Statement\s+Report\s*\n+([A-Z0-9\s.&'-]{3,60})\n+(?:Joint\s+Holder|PLOT|Scheme)", RegexOptions.IgnoreCase);
        if (axisNameMatch.Success)
        {
            axisCustomerName = axisNameMatch.Groups[1].Value.Trim();
            axisSignals.Add($"Identified Axis Bank Customer Name: {axisCustomerName}");
        }

        // 4. Check Central Bank of India Brand & Structural Signals
        bool hasCentralBrand = combinedText.Contains("CENTRAL BANK OF INDIA", StringComparison.OrdinalIgnoreCase) ||
                               combinedText.Contains("CENTRAL BANK", StringComparison.OrdinalIgnoreCase);
        bool hasCentralIfsc = Regex.IsMatch(combinedText, @"\bCBIN0[A-Z0-9]{6}\b", RegexOptions.IgnoreCase);
        bool hasCentralDomain = combinedText.Contains("@centralbank.co.in", StringComparison.OrdinalIgnoreCase) ||
                                combinedText.Contains("@centralbank.bank.in", StringComparison.OrdinalIgnoreCase) ||
                                combinedText.Contains("centralbankofindia.co.in", StringComparison.OrdinalIgnoreCase);
        bool hasCentralScheme = combinedText.Contains("Cent Mudra", StringComparison.OrdinalIgnoreCase) ||
                                combinedText.Contains("Cent Sahyog", StringComparison.OrdinalIgnoreCase);

        var centralSignals = new List<string>();
        if (hasCentralBrand) centralSignals.Add("Detected 'CENTRAL BANK OF INDIA' branding text.");
        if (hasCentralIfsc) centralSignals.Add("Detected Central Bank of India IFSC code pattern (CBIN0...).");
        if (hasCentralDomain) centralSignals.Add("Detected Central Bank of India domain signature.");
        if (hasCentralScheme) centralSignals.Add("Detected Central Bank of India product scheme name.");

        // Central Bank Table Column Structure: Value Date, Post Date, Details, Chq.No., Debit, Credit, Balance
        bool hasCentralValDateCol = combinedText.Contains("Value Date", StringComparison.OrdinalIgnoreCase) ||
                                    (combinedText.Contains("Value", StringComparison.OrdinalIgnoreCase) && combinedText.Contains("Date", StringComparison.OrdinalIgnoreCase));
        bool hasCentralPostDateCol = combinedText.Contains("Post Date", StringComparison.OrdinalIgnoreCase) ||
                                     combinedText.Contains("Post", StringComparison.OrdinalIgnoreCase);
        bool hasCentralDetailsCol = combinedText.Contains("Details", StringComparison.OrdinalIgnoreCase);
        bool hasCentralChqCol = combinedText.Contains("Chq.No", StringComparison.OrdinalIgnoreCase) ||
                                combinedText.Contains("Chq.No.", StringComparison.OrdinalIgnoreCase) ||
                                combinedText.Contains("Chq", StringComparison.OrdinalIgnoreCase);
        bool hasCentralDebitCol = combinedText.Contains("Debit", StringComparison.OrdinalIgnoreCase);
        bool hasCentralCreditCol = combinedText.Contains("Credit", StringComparison.OrdinalIgnoreCase);
        bool hasCentralBalanceCol = combinedText.Contains("Balance", StringComparison.OrdinalIgnoreCase);

        int matchedCentralColumns = 0;
        if (hasCentralValDateCol) matchedCentralColumns++;
        if (hasCentralPostDateCol) matchedCentralColumns++;
        if (hasCentralDetailsCol) matchedCentralColumns++;
        if (hasCentralChqCol) matchedCentralColumns++;
        if (hasCentralDebitCol) matchedCentralColumns++;
        if (hasCentralCreditCol) matchedCentralColumns++;
        if (hasCentralBalanceCol) matchedCentralColumns++;

        if (matchedCentralColumns >= 5)
        {
            centralSignals.Add($"Detected {matchedCentralColumns}/7 Central Bank of India table column headers.");
        }

        // Central Bank Account Number Extraction (e.g. "Account No. : 3552886804" or "Account No.: 3552886804")
        string? centralAccountNumber = null;
        var centralAccMatch = Regex.Match(combinedText, @"Account\s*(?:No\.?|Number)?\s*[:.]?\s*(\d{10,18})", RegexOptions.IgnoreCase);
        if (centralAccMatch.Success)
        {
            centralAccountNumber = centralAccMatch.Groups[1].Value.Trim();
            centralSignals.Add($"Identified Central Bank Account Number: {centralAccountNumber}");
        }

        // Central Bank Statement Period Extraction (e.g. "Statement From 01/04/2025 to 31/03/2026")
        DateTime? centralPeriodStart = null;
        DateTime? centralPeriodEnd = null;
        var centralPeriodMatch = Regex.Match(combinedText, @"Statement\s+From\s*[:.]?\s*(\d{2}/\d{2}/\d{4})\s*to\s*[:.]?\s*(\d{2}/\d{2}/\d{4})", RegexOptions.IgnoreCase);
        if (centralPeriodMatch.Success)
        {
            string s1 = centralPeriodMatch.Groups[1].Value;
            string s2 = centralPeriodMatch.Groups[2].Value;
            if (DateTime.TryParseExact(s1, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var cStart))
            {
                centralPeriodStart = cStart;
            }
            if (DateTime.TryParseExact(s2, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var cEnd))
            {
                centralPeriodEnd = cEnd;
            }
            if (centralPeriodStart.HasValue && centralPeriodEnd.HasValue)
            {
                centralSignals.Add($"Identified Central Bank Statement Period: {centralPeriodStart:dd/MM/yyyy} to {centralPeriodEnd:dd/MM/yyyy}");
            }
        }

        // Central Bank Customer Name Extraction (e.g. line after Branch Code before address/account, or line after STATEMENT OF ACCOUNT)
        string? centralCustomerName = null;
        var centralNameMatch = Regex.Match(combinedText, @"Branch\s+Code\s*[:.]?\s*\d+\s*\r?\n\s*([^\r\n]{3,60})", RegexOptions.IgnoreCase);
        if (centralNameMatch.Success)
        {
            var cand = centralNameMatch.Groups[1].Value.Trim();
            if (!cand.StartsWith("Account", StringComparison.OrdinalIgnoreCase) && !cand.StartsWith("Plot", StringComparison.OrdinalIgnoreCase))
            {
                centralCustomerName = cand;
            }
        }

        if (string.IsNullOrWhiteSpace(centralCustomerName))
        {
            var altMatch = Regex.Match(combinedText, @"STATEMENT\s+OF\s+ACCOUNT(?:\s+CENTRAL\s+BANK\s+OF\s+INDIA)?\s*\r?\n\s*([^\r\n]{3,60})", RegexOptions.IgnoreCase);
            if (altMatch.Success)
            {
                var cand = altMatch.Groups[1].Value.Trim();
                if (!cand.StartsWith("Branch", StringComparison.OrdinalIgnoreCase))
                {
                    centralCustomerName = cand;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(centralCustomerName))
        {
            centralSignals.Add($"Identified Central Bank Customer Name: {centralCustomerName}");
        }

        // 5. Check ICICI Bank Brand & Structural Signals
        bool hasIciciBrand = combinedText.Contains("ICICI BANK", StringComparison.OrdinalIgnoreCase) ||
                             combinedText.Contains("ICICI", StringComparison.OrdinalIgnoreCase);
        bool hasIciciHeader = combinedText.Contains("ICICI BANK LIMITED", StringComparison.OrdinalIgnoreCase) ||
                              combinedText.Contains("ICICI Bank Ltd", StringComparison.OrdinalIgnoreCase);
        bool hasIciciDomain = combinedText.Contains("icicibank.com", StringComparison.OrdinalIgnoreCase);
        bool hasIciciIfsc = Regex.IsMatch(combinedText, @"\bICIC0[A-Z0-9]{6}\b", RegexOptions.IgnoreCase);
        bool hasIciciCare = combinedText.Contains("1800 1080", StringComparison.OrdinalIgnoreCase) ||
                            combinedText.Contains("iMobile", StringComparison.OrdinalIgnoreCase);
        bool hasIciciStatementLabel = combinedText.Contains("Statement of Transactions in Savings Account", StringComparison.OrdinalIgnoreCase) ||
                                      combinedText.Contains("Statement of Transactions in Current Account", StringComparison.OrdinalIgnoreCase) ||
                                      combinedText.Contains("STATEMENT SUMMARY", StringComparison.OrdinalIgnoreCase);

        // ICICI-v2 Specific Signals (Corporate / Detailed Statement layout with Tran ID)
        bool hasIciciV2Detailed = combinedText.Contains("Detailed Statement", StringComparison.OrdinalIgnoreCase);
        bool hasIciciV2TranId = Regex.IsMatch(combinedText, @"\bTran\s+ID\b", RegexOptions.IgnoreCase) || combinedText.Contains("Tran", StringComparison.OrdinalIgnoreCase);
        bool hasIciciV2Remarks = Regex.IsMatch(combinedText, @"\bTransaction\s+Remarks\b", RegexOptions.IgnoreCase) || combinedText.Contains("Remarks", StringComparison.OrdinalIgnoreCase);
        bool hasIciciV2Withdrawl = combinedText.Contains("Withdrawl (Dr)", StringComparison.OrdinalIgnoreCase) || combinedText.Contains("Withdrawl", StringComparison.OrdinalIgnoreCase);
        bool hasIciciV2Deposit = combinedText.Contains("Deposit (Cr)", StringComparison.OrdinalIgnoreCase) || combinedText.Contains("Deposit", StringComparison.OrdinalIgnoreCase);
        bool hasIciciV2Cheque = combinedText.Contains("Cheque no/ RefNo", StringComparison.OrdinalIgnoreCase) || combinedText.Contains("Cheque no/", StringComparison.OrdinalIgnoreCase) || combinedText.Contains("RefNo", StringComparison.OrdinalIgnoreCase);
        bool hasIciciV2HeaderRow = combinedText.Contains("Sr Tran Value Transaction Cheque Transaction Withdrawl Deposit Balance", StringComparison.OrdinalIgnoreCase) ||
                                   (combinedText.Contains("Withdrawl", StringComparison.OrdinalIgnoreCase) && combinedText.Contains("Deposit", StringComparison.OrdinalIgnoreCase) && combinedText.Contains("Balance", StringComparison.OrdinalIgnoreCase) && hasIciciV2TranId);

        var iciciSignals = new List<string>();
        if (hasIciciBrand) iciciSignals.Add("Detected 'ICICI' branding text.");
        if (hasIciciIfsc) iciciSignals.Add("Detected ICICI Bank IFSC code pattern (ICIC0...).");
        if (hasIciciHeader) iciciSignals.Add("Detected ICICI Bank corporate entity header signature.");
        if (hasIciciDomain) iciciSignals.Add("Detected ICICI Bank domain signature (icicibank.com).");
        if (hasIciciCare) iciciSignals.Add("Detected ICICI Bank customer care signature (1800 1080 / iMobile).");
        if (hasIciciStatementLabel) iciciSignals.Add("Detected ICICI Bank statement layout title.");
        if (hasIciciV2Detailed) iciciSignals.Add("Detected ICICI Bank Detailed Statement layout title (ICICI-v2).");
        if (hasIciciV2HeaderRow) iciciSignals.Add("Detected ICICI Bank 9-column table header row (ICICI-v2).");

        // ICICI-v1 Table Column Structure: DATE, MODE, PARTICULARS, DEPOSITS, WITHDRAWALS, BALANCE
        bool hasIciciDateCol = combinedText.Contains("DATE", StringComparison.OrdinalIgnoreCase);
        bool hasIciciModeCol = combinedText.Contains("MODE", StringComparison.OrdinalIgnoreCase);
        bool hasIciciParticularsCol = combinedText.Contains("PARTICULARS", StringComparison.OrdinalIgnoreCase);
        bool hasIciciDepositsCol = combinedText.Contains("DEPOSITS", StringComparison.OrdinalIgnoreCase);
        bool hasIciciWithdrawalsCol = combinedText.Contains("WITHDRAWALS", StringComparison.OrdinalIgnoreCase);
        bool hasIciciBalanceCol = combinedText.Contains("BALANCE", StringComparison.OrdinalIgnoreCase);

        int matchedIciciColumns = 0;
        if (hasIciciDateCol) matchedIciciColumns++;
        if (hasIciciModeCol) matchedIciciColumns++;
        if (hasIciciParticularsCol) matchedIciciColumns++;
        if (hasIciciDepositsCol) matchedIciciColumns++;
        if (hasIciciWithdrawalsCol) matchedIciciColumns++;
        if (hasIciciBalanceCol) matchedIciciColumns++;

        if (matchedIciciColumns >= 4)
        {
            iciciSignals.Add($"Detected {matchedIciciColumns}/6 ICICI Bank standard table column headers (ICICI-v1).");
        }

        int matchedIciciV2Columns = 0;
        if (hasIciciV2Detailed) matchedIciciV2Columns++;
        if (hasIciciV2TranId) matchedIciciV2Columns++;
        if (hasIciciV2Remarks) matchedIciciV2Columns++;
        if (hasIciciV2Withdrawl) matchedIciciV2Columns++;
        if (hasIciciV2Deposit) matchedIciciV2Columns++;
        if (hasIciciV2Cheque) matchedIciciV2Columns++;
        if (hasIciciV2HeaderRow) matchedIciciV2Columns += 2;

        if (matchedIciciV2Columns >= 4)
        {
            iciciSignals.Add($"Detected {matchedIciciV2Columns} ICICI-v2 table column and header signatures.");
        }

        // Determine if format is ICICI-v2 or ICICI-v1
        bool isIciciV2 = hasIciciV2HeaderRow || (hasIciciV2Detailed && matchedIciciV2Columns >= 4) || (hasIciciV2Withdrawl && hasIciciV2Deposit && hasIciciV2TranId && (hasIciciIfsc || hasIciciBrand));
        string detectedFormat = isIciciV2 ? "ICICI-v2" : "ICICI-v1";
        iciciSignals.Add($"Classified ICICI format as '{detectedFormat}'.");

        // Metadata extraction for ICICI-v1
        string? iciciAccountNumber = null;
        var iciciAccMatch = Regex.Match(combinedText, @"(?:Savings\s+(?:Account|A/c)|Account\s+Number)\s*[:.]?\s*([X\d]{8,18})", RegexOptions.IgnoreCase);
        if (iciciAccMatch.Success)
        {
            iciciAccountNumber = iciciAccMatch.Groups[1].Value.Trim();
            iciciSignals.Add($"Identified ICICI Bank Account Number: {iciciAccountNumber}");
        }

        DateTime? iciciPeriodStart = null;
        DateTime? iciciPeriodEnd = null;
        var iciciPeriodMatch = Regex.Match(combinedText, @"for\s+the\s+period\s+([A-Za-z]+\s+\d{1,2},\s+\d{4})\s*-\s*([A-Za-z]+\s+\d{1,2},\s+\d{4})", RegexOptions.IgnoreCase);
        if (iciciPeriodMatch.Success)
        {
            string s1 = iciciPeriodMatch.Groups[1].Value;
            string s2 = iciciPeriodMatch.Groups[2].Value;
            if (DateTime.TryParseExact(s1, "MMMM dd, yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var iStart) ||
                DateTime.TryParseExact(s1, "MMM dd, yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out iStart))
            {
                iciciPeriodStart = iStart;
            }
            if (DateTime.TryParseExact(s2, "MMMM dd, yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var iEnd) ||
                DateTime.TryParseExact(s2, "MMM dd, yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out iEnd))
            {
                iciciPeriodEnd = iEnd;
            }
            if (iciciPeriodStart.HasValue && iciciPeriodEnd.HasValue)
            {
                iciciSignals.Add($"Identified ICICI Bank Statement Period: {iciciPeriodStart:dd/MM/yyyy} to {iciciPeriodEnd:dd/MM/yyyy}");
            }
        }

        string? iciciCustomerName = null;
        var iciciNameMatch = Regex.Match(combinedText, @"ACCOUNT\s+HOLDERS?\s*[:.]?\s*([A-Z0-9\s.&'-]{3,60})", RegexOptions.IgnoreCase);
        if (iciciNameMatch.Success)
        {
            iciciCustomerName = iciciNameMatch.Groups[1].Value.Trim();
            iciciSignals.Add($"Identified ICICI Bank Customer Name: {iciciCustomerName}");
        }

        // Metadata extraction for ICICI-v2
        if (isIciciV2)
        {
            var v2AccMatch = Regex.Match(combinedText, @"(?:A/C\s*No|Account\s*(?:No|Number))\s*[:.]?\s*(\d{10,18})", RegexOptions.IgnoreCase);
            if (!v2AccMatch.Success)
            {
                v2AccMatch = Regex.Match(combinedText, @"\b(\d{12})\s+(?:ICIC0[A-Z0-9]{6})", RegexOptions.IgnoreCase);
            }
            if (v2AccMatch.Success)
            {
                iciciAccountNumber = v2AccMatch.Groups[1].Value.Trim();
                iciciSignals.Add($"Identified ICICI-v2 Account Number: {iciciAccountNumber}");
            }

            var v2TwoLineName = Regex.Match(combinedText, @"(?:\r?\n|^)([A-Z0-9\t .&'-]+?)\s+CAA\s*\r?\n+Name:\s*A/C\s*Type:\s*\r?\n+([A-Z0-9\t .&'-]+?)\s*\r?\n+Address", RegexOptions.IgnoreCase);
            if (v2TwoLineName.Success)
            {
                iciciCustomerName = $"{v2TwoLineName.Groups[1].Value.Trim()} {v2TwoLineName.Groups[2].Value.Trim()}";
                iciciSignals.Add($"Identified ICICI-v2 Customer Name: {iciciCustomerName}");
            }
            else
            {
                var v2NameMatch = Regex.Match(combinedText, @"(?:Detailed\s+Statement\s*\n+)?([A-Z0-9\s.&'-]+?)\s+CAA\s*\n+Name:", RegexOptions.IgnoreCase);
                if (v2NameMatch.Success)
                {
                    iciciCustomerName = v2NameMatch.Groups[1].Value.Trim();
                    iciciSignals.Add($"Identified ICICI-v2 Customer Name: {iciciCustomerName}");
                }
                else
                {
                    var nameFallback = Regex.Match(combinedText, @"Name\s*[:.]?\s*([A-Z0-9\s.&'-]{3,60})", RegexOptions.IgnoreCase);
                    if (nameFallback.Success)
                    {
                        iciciCustomerName = nameFallback.Groups[1].Value.Trim();
                        iciciSignals.Add($"Identified ICICI-v2 Customer Name: {iciciCustomerName}");
                    }
                }
            }

            var v2PeriodMatch = Regex.Match(combinedText, @"From\s*[:.]?\s*(\d{2}/\d{2}/\d{4})\s*To\s*[:.]?\s*(?:Statement\s+)?(\d{2}/\d{2}/\d{4})", RegexOptions.IgnoreCase);
            if (v2PeriodMatch.Success)
            {
                if (DateTime.TryParseExact(v2PeriodMatch.Groups[1].Value, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d1)) iciciPeriodStart = d1;
                if (DateTime.TryParseExact(v2PeriodMatch.Groups[2].Value, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d2)) iciciPeriodEnd = d2;
                if (iciciPeriodStart.HasValue && iciciPeriodEnd.HasValue)
                {
                    iciciSignals.Add($"Identified ICICI-v2 Statement Period: {iciciPeriodStart:dd/MM/yyyy} to {iciciPeriodEnd:dd/MM/yyyy}");
                }
            }
        }

        // 6. Check State Bank of India (SBI) Brand & Structural Signals
        var sbiSignals = new List<string>();
        bool hasSbiBrand = combinedText.Contains("STATE BANK OF INDIA", StringComparison.OrdinalIgnoreCase) ||
                           Regex.IsMatch(combinedText, @"\b(?:STATE\s+BANK\s+OF\s+INDIA|OSBI)\b", RegexOptions.IgnoreCase);
        bool hasSbiIfsc = Regex.IsMatch(combinedText, @"\bSBIN0[A-Z0-9]{6}\b", RegexOptions.IgnoreCase);
        bool hasSbiDomain = combinedText.Contains("sbi.co.in", StringComparison.OrdinalIgnoreCase) ||
                            combinedText.Contains("statebankofindia.com", StringComparison.OrdinalIgnoreCase);
        bool hasSbiPostDateCol = combinedText.Contains("Post Date", StringComparison.OrdinalIgnoreCase);
        bool hasSbiValueDateCol = combinedText.Contains("Value Date", StringComparison.OrdinalIgnoreCase);
        bool hasSbiDescCol = combinedText.Contains("Description", StringComparison.OrdinalIgnoreCase) ||
                             combinedText.Contains("Particulars", StringComparison.OrdinalIgnoreCase);
        bool hasSbiRefCol = combinedText.Contains("Cheque", StringComparison.OrdinalIgnoreCase) ||
                            combinedText.Contains("No/Reference", StringComparison.OrdinalIgnoreCase) ||
                            combinedText.Contains("Ref No", StringComparison.OrdinalIgnoreCase);
        bool hasSbiDebitCol = combinedText.Contains("Debit", StringComparison.OrdinalIgnoreCase);
        bool hasSbiCreditCol = combinedText.Contains("Credit", StringComparison.OrdinalIgnoreCase);
        bool hasSbiBalanceCol = combinedText.Contains("Balance", StringComparison.OrdinalIgnoreCase);
        bool hasSbiBroughtForward = combinedText.Contains("BROUGHT FORWARD", StringComparison.OrdinalIgnoreCase) ||
                                    combinedText.Contains("Brought Forward", StringComparison.OrdinalIgnoreCase);
        bool hasSbiStatementSummary = combinedText.Contains("Statement Summary", StringComparison.OrdinalIgnoreCase);
        bool hasSbiNotice = combinedText.Contains("In Case Your Account Is Operated", StringComparison.OrdinalIgnoreCase);

        int matchedSbiColumns = 0;
        if (hasSbiPostDateCol) matchedSbiColumns++;
        if (hasSbiValueDateCol) matchedSbiColumns++;
        if (hasSbiDescCol) matchedSbiColumns++;
        if (hasSbiRefCol) matchedSbiColumns++;
        if (hasSbiDebitCol) matchedSbiColumns++;
        if (hasSbiCreditCol) matchedSbiColumns++;
        if (hasSbiBalanceCol) matchedSbiColumns++;

        if (hasSbiBrand) sbiSignals.Add("Detected 'STATE BANK OF INDIA' branding text.");
        if (hasSbiIfsc) sbiSignals.Add("Detected SBI IFSC pattern (SBIN0...).");
        if (hasSbiDomain) sbiSignals.Add("Detected SBI email or web domain.");
        if (hasSbiBroughtForward) sbiSignals.Add("Detected SBI 'BROUGHT FORWARD' ledger anchor.");
        if (hasSbiStatementSummary) sbiSignals.Add("Detected SBI 'Statement Summary' block.");
        if (matchedSbiColumns >= 4) sbiSignals.Add($"Detected {matchedSbiColumns}/7 SBI table columns.");

        string? sbiAccountNumber = null;
        var sbiAccMatch = Regex.Match(combinedText, @"Account\s*(?:No|Number)\s*[:.]?\s*(\d{11,18})", RegexOptions.IgnoreCase);
        if (sbiAccMatch.Success)
        {
            sbiAccountNumber = sbiAccMatch.Groups[1].Value.Trim();
            sbiSignals.Add($"Identified SBI Account Number: {sbiAccountNumber}");
        }

        string? sbiCustomerName = null;
        var sbiNameMatch = Regex.Match(combinedText, @"(?:S\s+OF\s+ACCOUNT[^\n]*\n+)?(?:Mr\.|Mrs\.|Ms\.|Shri|Smt\.)\s+([A-Z\s.&'-]{3,60})", RegexOptions.IgnoreCase);
        if (sbiNameMatch.Success)
        {
            sbiCustomerName = sbiNameMatch.Value.Trim();
            sbiSignals.Add($"Identified SBI Customer Name: {sbiCustomerName}");
        }

        DateTime? sbiPeriodStart = null;
        DateTime? sbiPeriodEnd = null;
        var sbiPeriodMatch = Regex.Match(combinedText, @"Statement\s+From\s*[:.]?\s*(\d{2}-\d{2}-\d{4})\s*To\s*[:.]?\s*(\d{2}-\d{2}-\d{4})", RegexOptions.IgnoreCase);
        if (sbiPeriodMatch.Success)
        {
            if (DateTime.TryParseExact(sbiPeriodMatch.Groups[1].Value, "dd-MM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var sp1)) sbiPeriodStart = sp1;
            if (DateTime.TryParseExact(sbiPeriodMatch.Groups[2].Value, "dd-MM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var sp2)) sbiPeriodEnd = sp2;
            if (sbiPeriodStart.HasValue && sbiPeriodEnd.HasValue)
            {
                sbiSignals.Add($"Identified SBI Statement Period: {sbiPeriodStart:dd-MM-yyyy} to {sbiPeriodEnd:dd-MM-yyyy}");
            }
        }

        // 7. Check Bank of India (BOI) Brand & Structural Signals
        var boiSignals = new List<string>();
        bool hasBoiBrand = combinedText.Contains("BANK OF INDIA", StringComparison.OrdinalIgnoreCase) ||
                           Regex.IsMatch(combinedText, @"\b(?:BANK\s+OF\s+INDIA|BOI)\b", RegexOptions.IgnoreCase);
        bool hasBoiIfsc = Regex.IsMatch(combinedText, @"\bBKID0[A-Z0-9]{6}\b", RegexOptions.IgnoreCase);
        bool hasBoiDomain = combinedText.Contains("bankofindia.co.in", StringComparison.OrdinalIgnoreCase) ||
                            combinedText.Contains("bankofindia.com", StringComparison.OrdinalIgnoreCase);
        bool hasBoiSlogan = combinedText.Contains("RELATIONSHIP BEYOND BANKING", StringComparison.OrdinalIgnoreCase);
        bool hasBoiHelpline = combinedText.Contains("022-40919191", StringComparison.OrdinalIgnoreCase) ||
                              combinedText.Contains("1800 220 229", StringComparison.OrdinalIgnoreCase);
        bool hasBoiSnoCol = combinedText.Contains("SNO", StringComparison.OrdinalIgnoreCase);
        bool hasBoiTranDateCol = combinedText.Contains("TRAN DATE", StringComparison.OrdinalIgnoreCase);
        bool hasBoiInstNoCol = combinedText.Contains("INST NO", StringComparison.OrdinalIgnoreCase);
        bool hasBoiDescCol = combinedText.Contains("DESCRIPTION", StringComparison.OrdinalIgnoreCase);
        bool hasBoiDebitsCol = combinedText.Contains("DEBITS", StringComparison.OrdinalIgnoreCase);
        bool hasBoiCreditsCol = combinedText.Contains("CREDITS", StringComparison.OrdinalIgnoreCase);
        bool hasBoiBalanceCol = combinedText.Contains("BALANCE", StringComparison.OrdinalIgnoreCase);
        bool hasBoiOpeningBalance = combinedText.Contains("OPENING BALANCE", StringComparison.OrdinalIgnoreCase);

        int matchedBoiColumns = 0;
        if (hasBoiSnoCol) matchedBoiColumns++;
        if (hasBoiTranDateCol) matchedBoiColumns++;
        if (hasBoiInstNoCol) matchedBoiColumns++;
        if (hasBoiDescCol) matchedBoiColumns++;
        if (hasBoiDebitsCol) matchedBoiColumns++;
        if (hasBoiCreditsCol) matchedBoiColumns++;
        if (hasBoiBalanceCol) matchedBoiColumns++;

        if (hasBoiBrand) boiSignals.Add("Detected 'BANK OF INDIA' branding text.");
        if (hasBoiIfsc) boiSignals.Add("Detected BOI IFSC pattern (BKID0...).");
        if (hasBoiDomain) boiSignals.Add("Detected BOI email or web domain.");
        if (hasBoiSlogan) boiSignals.Add("Detected BOI 'RELATIONSHIP BEYOND BANKING' slogan.");
        if (hasBoiHelpline) boiSignals.Add("Detected BOI customer care helpline contact.");
        if (hasBoiOpeningBalance) boiSignals.Add("Detected BOI 'OPENING BALANCE' ledger anchor.");
        if (matchedBoiColumns >= 4) boiSignals.Add($"Detected {matchedBoiColumns}/7 BOI table columns.");

        string? boiAccountNumber = null;
        var boiAccMatch = Regex.Match(combinedText, @"(?:Account|A/C)(?:\s*(?:No|Number))?[^\d\r\n]{0,30}(\d{15})", RegexOptions.IgnoreCase);
        if (!boiAccMatch.Success)
        {
            boiAccMatch = Regex.Match(combinedText, @"Account\s*(?:No|Number)\s*[:.]?\s*(\d{11,18})", RegexOptions.IgnoreCase);
        }
        if (!boiAccMatch.Success)
        {
            boiAccMatch = Regex.Match(combinedText, @"\b(\d{15})\b");
        }
        if (boiAccMatch.Success)
        {
            boiAccountNumber = boiAccMatch.Groups[1].Value.Trim();
            boiSignals.Add($"Identified BOI Account Number: {boiAccountNumber}");
        }

        string? boiCustomerName = null;
        var boiNameMatch = Regex.Match(combinedText, @"(?:M/S|Mr\.|Mrs\.|Ms\.|Shri|Smt\.)\s+([A-Z\s.&'-]{3,60})", RegexOptions.IgnoreCase);
        if (boiNameMatch.Success)
        {
            boiCustomerName = boiNameMatch.Value.Trim();
            boiSignals.Add($"Identified BOI Customer Name: {boiCustomerName}");
        }

        DateTime? boiPeriodStart = null;
        DateTime? boiPeriodEnd = null;
        var boiPeriodMatch = Regex.Match(combinedText, @"FROM\s*[:.]?\s*(\d{2}-\d{2}-\d{4})\s*TO\s*[:.]?\s*(\d{2}-\d{2}-\d{4})", RegexOptions.IgnoreCase);
        if (boiPeriodMatch.Success)
        {
            if (DateTime.TryParseExact(boiPeriodMatch.Groups[1].Value, "dd-MM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var bp1)) boiPeriodStart = bp1;
            if (DateTime.TryParseExact(boiPeriodMatch.Groups[2].Value, "dd-MM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var bp2)) boiPeriodEnd = bp2;
        }
        if (!boiPeriodStart.HasValue || !boiPeriodEnd.HasValue)
        {
            var dates = Regex.Matches(combinedText, @"\b(\d{2}-\d{2}-\d{4})\b")
                             .Select(m => m.Value)
                             .Distinct()
                             .ToList();
            if (dates.Count >= 2)
            {
                if (DateTime.TryParseExact(dates[0], "dd-MM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var bp1)) boiPeriodStart = bp1;
                if (DateTime.TryParseExact(dates[1], "dd-MM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var bp2)) boiPeriodEnd = bp2;
            }
        }
        if (boiPeriodStart.HasValue && boiPeriodEnd.HasValue)
        {
            boiSignals.Add($"Identified BOI Statement Period: {boiPeriodStart:dd-MM-yyyy} to {boiPeriodEnd:dd-MM-yyyy}");
        }

        // 8. Check Kotak Mahindra Bank Brand & Structural Signals
        bool hasKotakBrand = combinedText.Contains("Kotak Mahindra Bank", StringComparison.OrdinalIgnoreCase) ||
                             combinedText.Contains("Kotak 811", StringComparison.OrdinalIgnoreCase) ||
                             combinedText.Contains("Kotak Bank", StringComparison.OrdinalIgnoreCase) ||
                             combinedText.Contains("KMBL", StringComparison.OrdinalIgnoreCase) ||
                             combinedText.Contains("Kotak", StringComparison.OrdinalIgnoreCase);
        bool hasKotakIfsc = Regex.IsMatch(combinedText, @"\bKKBK0[A-Z0-9]{6}\b", RegexOptions.IgnoreCase);
        bool hasKotakHeader = combinedText.Contains("Kotak Mahindra Bank Limited", StringComparison.OrdinalIgnoreCase) ||
                              combinedText.Contains("Kotak Mahindra Bank Ltd", StringComparison.OrdinalIgnoreCase) ||
                              combinedText.Contains("27 BKC", StringComparison.OrdinalIgnoreCase) ||
                              combinedText.Contains("kotak.com", StringComparison.OrdinalIgnoreCase);
        bool hasKotakCrn = combinedText.Contains("CRN", StringComparison.OrdinalIgnoreCase) ||
                           combinedText.Contains("Customer Relationship Number", StringComparison.OrdinalIgnoreCase);

        // Kotak Table Column Structure (#, Date, Description, Chq/Ref. No., Withdrawal (Dr.), Deposit (Cr.), Balance)
        bool hasKotakDateCol = combinedText.Contains("Date", StringComparison.OrdinalIgnoreCase);
        bool hasKotakDescCol = combinedText.Contains("Description", StringComparison.OrdinalIgnoreCase) ||
                               combinedText.Contains("Narration", StringComparison.OrdinalIgnoreCase) ||
                               combinedText.Contains("Particulars", StringComparison.OrdinalIgnoreCase);
        bool hasKotakRefCol = combinedText.Contains("Chq/Ref. No.", StringComparison.OrdinalIgnoreCase) ||
                              combinedText.Contains("Chq/Ref.No", StringComparison.OrdinalIgnoreCase) ||
                              combinedText.Contains("Chq / Ref No", StringComparison.OrdinalIgnoreCase) ||
                              combinedText.Contains("Chq/Ref No", StringComparison.OrdinalIgnoreCase) ||
                              combinedText.Contains("Cheque No", StringComparison.OrdinalIgnoreCase) ||
                              combinedText.Contains("Ref. No", StringComparison.OrdinalIgnoreCase) ||
                              combinedText.Contains("Ref No", StringComparison.OrdinalIgnoreCase);
        bool hasKotakWithdrawalCol = combinedText.Contains("Withdrawal (Dr.)", StringComparison.OrdinalIgnoreCase) ||
                                     combinedText.Contains("Withdrawal (Dr)", StringComparison.OrdinalIgnoreCase) ||
                                     combinedText.Contains("Withdrawal", StringComparison.OrdinalIgnoreCase) ||
                                     combinedText.Contains("Dr.", StringComparison.OrdinalIgnoreCase);
        bool hasKotakDepositCol = combinedText.Contains("Deposit (Cr.)", StringComparison.OrdinalIgnoreCase) ||
                                  combinedText.Contains("Deposit (Cr)", StringComparison.OrdinalIgnoreCase) ||
                                  combinedText.Contains("Deposit", StringComparison.OrdinalIgnoreCase) ||
                                  combinedText.Contains("Cr.", StringComparison.OrdinalIgnoreCase);
        bool hasKotakBalanceCol = combinedText.Contains("Balance", StringComparison.OrdinalIgnoreCase) ||
                                  combinedText.Contains("Closing Balance", StringComparison.OrdinalIgnoreCase);

        int matchedKotakColumns = 0;
        if (hasKotakDateCol) matchedKotakColumns++;
        if (hasKotakDescCol) matchedKotakColumns++;
        if (hasKotakRefCol) matchedKotakColumns++;
        if (hasKotakWithdrawalCol) matchedKotakColumns++;
        if (hasKotakDepositCol) matchedKotakColumns++;
        if (hasKotakBalanceCol) matchedKotakColumns++;

        var kotakSignals = new List<string>();
        if (hasKotakBrand) kotakSignals.Add("Detected 'Kotak Mahindra Bank' branding text.");
        if (hasKotakIfsc) kotakSignals.Add("Detected Kotak Mahindra Bank IFSC code pattern (KKBK0...).");
        if (hasKotakHeader) kotakSignals.Add("Detected Kotak Mahindra Bank corporate entity / domain signature.");
        if (hasKotakCrn) kotakSignals.Add("Detected Kotak CRN (Customer Relationship Number) signature.");
        if (matchedKotakColumns >= 4) kotakSignals.Add($"Detected {matchedKotakColumns}/6 Kotak standard table column headers.");

        // Kotak Account Number Extraction (e.g. "Account No. 5046856741" or "Account Number : ...")
        string? kotakAccountNumber = null;
        var kotakAccMatch = Regex.Match(combinedText, @"Account\s*(?:No|Number|#)\s*[:.]?\s*(\d{10,18})", RegexOptions.IgnoreCase);
        if (kotakAccMatch.Success)
        {
            kotakAccountNumber = kotakAccMatch.Groups[1].Value.Trim();
            kotakSignals.Add($"Identified Kotak Account Number: {kotakAccountNumber}");
        }

        // Kotak Statement Period Extraction (e.g. "01 Apr 2025 - 31 Mar 2026" or "17 Feb 2021 - 31 Dec 2021" or "From : 01/01/2022 To : 31/12/2022")
        DateTime? kotakPeriodStart = null;
        DateTime? kotakPeriodEnd = null;
        var kotakPeriodMatch = Regex.Match(combinedText, @"(\d{1,2}\s+[A-Za-z]{3}\s+\d{4})\s*-\s*(\d{1,2}\s+[A-Za-z]{3}\s+\d{4})", RegexOptions.IgnoreCase);
        if (kotakPeriodMatch.Success)
        {
            if (DateTime.TryParseExact(kotakPeriodMatch.Groups[1].Value, "dd MMM yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var kp1) ||
                DateTime.TryParseExact(kotakPeriodMatch.Groups[1].Value, "d MMM yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out kp1))
            {
                kotakPeriodStart = kp1;
            }
            if (DateTime.TryParseExact(kotakPeriodMatch.Groups[2].Value, "dd MMM yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var kp2) ||
                DateTime.TryParseExact(kotakPeriodMatch.Groups[2].Value, "d MMM yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out kp2))
            {
                kotakPeriodEnd = kp2;
            }
        }
        if (!kotakPeriodStart.HasValue || !kotakPeriodEnd.HasValue)
        {
            var kotakDateRangeMatch = Regex.Match(combinedText, @"(?:Period|From)\s*[:.]?\s*(\d{2}[/-]\d{2}[/-]\d{4})\s*(?:To|-)\s*(\d{2}[/-]\d{2}[/-]\d{4})", RegexOptions.IgnoreCase);
            if (kotakDateRangeMatch.Success)
            {
                var s1 = kotakDateRangeMatch.Groups[1].Value.Replace('/', '-');
                var s2 = kotakDateRangeMatch.Groups[2].Value.Replace('/', '-');
                if (DateTime.TryParseExact(s1, "dd-MM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var kp1)) kotakPeriodStart = kp1;
                if (DateTime.TryParseExact(s2, "dd-MM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var kp2)) kotakPeriodEnd = kp2;
            }
        }
        if (kotakPeriodStart.HasValue && kotakPeriodEnd.HasValue)
        {
            kotakSignals.Add($"Identified Kotak Statement Period: {kotakPeriodStart:dd-MM-yyyy} to {kotakPeriodEnd:dd-MM-yyyy}");
        }

        // Kotak Customer Name Extraction (from Page 2 top header or Page 1 header block)
        string? kotakCustomerName = null;
        if (samplePages.Count > 1)
        {
            var p2 = samplePages[1];
            if (p2.TextBlocks != null && p2.TextBlocks.Count > 0)
            {
                var topBlocks = p2.TextBlocks.Where(b => b.Y <= 25 && b.X < 300).OrderBy(b => b.X).ToList();
                if (topBlocks.Count > 0)
                {
                    kotakCustomerName = string.Join(" ", topBlocks.Select(b => b.Text)).Trim().TrimEnd('.', ' ');
                }
            }
        }
        if (string.IsNullOrWhiteSpace(kotakCustomerName) && samplePages.Count > 0)
        {
            var p1 = samplePages[0];
            if (p1.TextBlocks != null && p1.TextBlocks.Count > 0)
            {
                var nameBlocks = p1.TextBlocks.Where(b => b.Y >= 160 && b.Y <= 185 && b.X < 250).OrderBy(b => b.X).ToList();
                if (nameBlocks.Count > 0)
                {
                    kotakCustomerName = string.Join(" ", nameBlocks.Select(b => b.Text)).Trim().TrimEnd('.', ' ');
                }
            }
        }
        if (string.IsNullOrWhiteSpace(kotakCustomerName))
        {
            var p1Raw = samplePages.Count > 0 ? (samplePages[0].RawText ?? "") : combinedText;
            var kotakNameMatch = Regex.Match(p1Raw, @"(?:Customer\s+Name|Name|Account\s+Holder\s*Name)\s*[:.]?\s*([A-Za-z\s.]{3,40})(?:\r?\n|\t|$)", RegexOptions.IgnoreCase);
            if (kotakNameMatch.Success)
            {
                kotakCustomerName = kotakNameMatch.Groups[1].Value.Trim().TrimEnd('.', ' ');
            }
        }
        if (!string.IsNullOrWhiteSpace(kotakCustomerName))
        {
            kotakSignals.Add($"Identified Kotak Customer Name: {kotakCustomerName}");
        }

        // 9. Disambiguation and Decision Thresholds
        // Genuine HDFC statements require HDFC IFSC, HDFC corporate header, or HDFC brand combined with HDFC-specific table column headers (Narration or Closing Balance)
        bool hasHdfcSpecificStructure = hasHdfcIfsc || hasHdfcHeader || (hasHdfcBrand && (hasNarrCol || hasClosingBalanceCol));
        bool isHdfcCandidate = hasHdfcSpecificStructure && matchedHdfcColumns >= 5;

        // Genuine YES BANK statements require YES BANK IFSC, YES BANK corporate entity header, or YES BANK brand combined with YES BANK table columns or layout markers
        bool hasYesSpecificStructure = hasYesIfsc || hasYesHeader || (hasYesBrand && (hasYesDescCol || hasYesBalanceCol || hasYesStatementLabel || hasYesService));
        bool isYesCandidate = hasYesSpecificStructure && matchedYesColumns >= 5;

        // Genuine Axis Bank statements require Axis IFSC, Axis corporate entity header, or Axis brand combined with Axis table columns or layout markers
        bool hasAxisSpecificStructure = hasAxisIfsc || hasAxisHeader || (hasAxisBrand && (hasAxisParticularsCol || hasAxisDrCrCol || hasAxisStatementLabel));
        bool isAxisCandidate = hasAxisSpecificStructure && matchedAxisColumns >= 5;

        // Genuine Central Bank statements require Central Bank IFSC, Central Bank domain, or Central Bank brand combined with Central Bank table columns or scheme
        bool hasCentralSpecificStructure = hasCentralIfsc || hasCentralDomain || (hasCentralBrand && (hasCentralPostDateCol || hasCentralDetailsCol || hasCentralScheme));
        bool isCentralCandidate = hasCentralSpecificStructure && matchedCentralColumns >= 5;

        // Genuine ICICI Bank statements require ICICI IFSC, ICICI header, ICICI domain, ICICI-v2 structure, or ICICI brand combined with ICICI table columns or statement label
        bool hasIciciSpecificStructure = hasIciciIfsc || hasIciciHeader || hasIciciDomain || isIciciV2 || (hasIciciBrand && (hasIciciParticularsCol || hasIciciWithdrawalsCol || hasIciciStatementLabel || hasIciciV2HeaderRow));
        bool isIciciCandidate = hasIciciSpecificStructure && (matchedIciciColumns >= 4 || isIciciV2);

        // Genuine State Bank of India (SBI) statements require SBI IFSC, SBI domain, or SBI brand combined with SBI table columns or Brought Forward
        bool hasSbiSpecificStructure = hasSbiIfsc || hasSbiDomain || (hasSbiBrand && (hasSbiBroughtForward || hasSbiStatementSummary || hasSbiPostDateCol));
        bool isSbiCandidate = hasSbiSpecificStructure && matchedSbiColumns >= 4;

        // Genuine Bank of India (BOI) statements require BOI IFSC, BOI domain, or BOI brand combined with BOI table columns or slogan
        bool hasBoiSpecificStructure = hasBoiIfsc || hasBoiDomain || hasBoiSlogan || (hasBoiBrand && (hasBoiTranDateCol || hasBoiInstNoCol || hasBoiOpeningBalance));
        bool isBoiCandidate = hasBoiSpecificStructure && (matchedBoiColumns >= 4 || hasBoiIfsc);

        // Genuine Kotak Mahindra Bank statements require Kotak IFSC, Kotak header/domain, or Kotak brand combined with Kotak table columns or CRN
        bool hasKotakSpecificStructure = hasKotakIfsc || hasKotakHeader || hasKotakCrn || (hasKotakBrand && (hasKotakRefCol || (hasKotakWithdrawalCol && hasKotakDepositCol)));
        bool isKotakCandidate = hasKotakSpecificStructure && (matchedKotakColumns >= 4 || hasKotakIfsc);

        int hdfcScore = (hasHdfcIfsc ? 40 : 0) + (hasHdfcHeader ? 40 : 0) + (hasNarrCol ? 20 : 0) + (hasClosingBalanceCol ? 15 : 0) + (hasHdfcBrand ? 15 : 0) + matchedHdfcColumns * 5;
        int yesScore = (hasYesIfsc ? 40 : 0) + (hasYesHeader ? 40 : 0) + (hasYesDescCol ? 20 : 0) + (hasYesBalanceCol ? 20 : 0) + (hasYesBrand ? 15 : 0) + (hasYesStatementLabel ? 20 : 0) + matchedYesColumns * 5;
        int axisScore = (hasAxisIfsc ? 40 : 0) + (hasAxisHeader ? 40 : 0) + (hasAxisParticularsCol ? 20 : 0) + (hasAxisDrCrCol ? 20 : 0) + (hasAxisBrand ? 15 : 0) + (hasAxisStatementLabel ? 20 : 0) + matchedAxisColumns * 5;
        int centralScore = (hasCentralIfsc ? 40 : 0) + (hasCentralDomain ? 40 : 0) + (hasCentralBrand ? 25 : 0) + (hasCentralDetailsCol ? 20 : 0) + (hasCentralScheme ? 20 : 0) + matchedCentralColumns * 5;
        int iciciScore = (hasIciciIfsc ? 40 : 0) + (hasIciciHeader ? 40 : 0) + (hasIciciDomain ? 30 : 0) + (hasIciciBrand ? 25 : 0) + (hasIciciStatementLabel ? 25 : 0) + (hasIciciV2Detailed ? 30 : 0) + (hasIciciV2HeaderRow ? 40 : 0) + Math.Max(matchedIciciColumns, matchedIciciV2Columns) * 5;
        int sbiScore = (hasSbiIfsc ? 40 : 0) + (hasSbiDomain ? 30 : 0) + (hasSbiBrand ? 30 : 0) + (hasSbiBroughtForward ? 25 : 0) + (hasSbiStatementSummary ? 20 : 0) + (hasSbiNotice ? 15 : 0) + matchedSbiColumns * 5;
        int boiScore = (hasBoiIfsc ? 40 : 0) + (hasBoiDomain ? 30 : 0) + (hasBoiBrand ? 30 : 0) + (hasBoiSlogan ? 25 : 0) + (hasBoiOpeningBalance ? 20 : 0) + (hasBoiHelpline ? 15 : 0) + matchedBoiColumns * 5;
        int kotakScore = (hasKotakIfsc ? 40 : 0) + (hasKotakHeader ? 40 : 0) + (hasKotakBrand ? 30 : 0) + (hasKotakCrn ? 25 : 0) + (hasKotakRefCol ? 20 : 0) + matchedKotakColumns * 5;

        if (isKotakCandidate && kotakScore > hdfcScore && kotakScore > yesScore && kotakScore > axisScore && kotakScore > centralScore && kotakScore > iciciScore && kotakScore > sbiScore && kotakScore > boiScore)
        {
            double confidence = 0.90;
            if (hasKotakBrand && (hasKotakIfsc || hasKotakHeader || hasKotakCrn)) confidence += 0.05;
            if (matchedKotakColumns >= 5) confidence += 0.03;
            if (!string.IsNullOrEmpty(kotakAccountNumber)) confidence += 0.02;

            confidence = Math.Min(1.0, confidence);

            _logger.LogInformation("Successfully detected Kotak Mahindra Bank statement with format KOTAK-v1 and confidence {Confidence:P0}. Account: {Account}",
                confidence, kotakAccountNumber ?? "N/A");

            return new BankDetectionResult
            {
                DetectedBank = BankType.Kotak,
                BankName = "Kotak Mahindra Bank",
                Confidence = confidence,
                IsSupported = true,
                DetectedFormat = "KOTAK-v1",
                AccountNumber = kotakAccountNumber ?? accountNumber,
                CustomerName = kotakCustomerName ?? customerName,
                StatementFrom = kotakPeriodStart ?? periodStart,
                StatementTo = kotakPeriodEnd ?? periodEnd,
                DetectionSignals = kotakSignals
            };
        }

        if (isBoiCandidate && boiScore > hdfcScore && boiScore > yesScore && boiScore > axisScore && boiScore > centralScore && boiScore > iciciScore && boiScore > sbiScore && boiScore > kotakScore)
        {
            double confidence = 0.90;
            if (hasBoiBrand && (hasBoiIfsc || hasBoiDomain || hasBoiSlogan)) confidence += 0.05;
            if (matchedBoiColumns >= 5) confidence += 0.03;
            if (!string.IsNullOrEmpty(boiAccountNumber)) confidence += 0.02;

            confidence = Math.Min(1.0, confidence);

            _logger.LogInformation("Successfully detected Bank of India statement with format BOI-v1 and confidence {Confidence:P0}. Account: {Account}",
                confidence, boiAccountNumber ?? "N/A");

            return new BankDetectionResult
            {
                DetectedBank = BankType.BOI,
                BankName = "Bank of India",
                Confidence = confidence,
                IsSupported = true,
                DetectedFormat = "BOI-v1",
                AccountNumber = boiAccountNumber ?? accountNumber,
                CustomerName = boiCustomerName ?? customerName,
                StatementFrom = boiPeriodStart ?? periodStart,
                StatementTo = boiPeriodEnd ?? periodEnd,
                DetectionSignals = boiSignals
            };
        }

        if (isSbiCandidate && sbiScore > hdfcScore && sbiScore > yesScore && sbiScore > axisScore && sbiScore > centralScore && sbiScore > iciciScore && sbiScore > boiScore && sbiScore > kotakScore)
        {
            double confidence = 0.90;
            if (hasSbiBrand && (hasSbiIfsc || hasSbiDomain)) confidence += 0.05;
            if (matchedSbiColumns >= 5) confidence += 0.03;
            if (!string.IsNullOrEmpty(sbiAccountNumber)) confidence += 0.02;

            confidence = Math.Min(1.0, confidence);

            _logger.LogInformation("Successfully detected State Bank of India statement with format SBI-v1 and confidence {Confidence:P0}. Account: {Account}",
                confidence, sbiAccountNumber ?? "N/A");

            return new BankDetectionResult
            {
                DetectedBank = BankType.SBI,
                BankName = "State Bank of India",
                Confidence = confidence,
                IsSupported = true,
                DetectedFormat = "SBI-v1",
                AccountNumber = sbiAccountNumber ?? accountNumber,
                CustomerName = sbiCustomerName ?? customerName,
                StatementFrom = sbiPeriodStart ?? periodStart,
                StatementTo = sbiPeriodEnd ?? periodEnd,
                DetectionSignals = sbiSignals
            };
        }

        if (isIciciCandidate && iciciScore > hdfcScore && iciciScore > yesScore && iciciScore > axisScore && iciciScore > centralScore && iciciScore > sbiScore && iciciScore > boiScore && iciciScore > kotakScore)
        {
            double confidence = 0.90;
            if (hasIciciBrand && (hasIciciIfsc || hasIciciHeader || hasIciciDomain || isIciciV2)) confidence += 0.05;
            if (matchedIciciColumns >= 5 || matchedIciciV2Columns >= 5) confidence += 0.03;
            if (!string.IsNullOrEmpty(iciciAccountNumber)) confidence += 0.02;

            confidence = Math.Min(1.0, confidence);

            _logger.LogInformation("Successfully detected ICICI Bank statement with format {Format} and confidence {Confidence:P0}. Account: {Account}",
                detectedFormat, confidence, iciciAccountNumber ?? "N/A");

            return new BankDetectionResult
            {
                DetectedBank = BankType.ICICI,
                BankName = "ICICI Bank",
                Confidence = confidence,
                IsSupported = true,
                DetectedFormat = detectedFormat,
                AccountNumber = iciciAccountNumber ?? accountNumber,
                CustomerName = iciciCustomerName ?? customerName,
                StatementFrom = iciciPeriodStart ?? periodStart,
                StatementTo = iciciPeriodEnd ?? periodEnd,
                DetectionSignals = iciciSignals
            };
        }

        if (isCentralCandidate && centralScore > hdfcScore && centralScore > yesScore && centralScore > axisScore && centralScore > iciciScore && centralScore > sbiScore && centralScore > boiScore && centralScore > kotakScore)
        {
            double confidence = 0.90;
            if (hasCentralBrand && (hasCentralIfsc || hasCentralDomain)) confidence += 0.05;
            if (matchedCentralColumns >= 6) confidence += 0.03;
            if (!string.IsNullOrEmpty(centralAccountNumber)) confidence += 0.02;

            confidence = Math.Min(1.0, confidence);

            _logger.LogInformation("Successfully detected Central Bank of India statement with confidence {Confidence:P0}. Account: {Account}",
                confidence, centralAccountNumber ?? "N/A");

            return new BankDetectionResult
            {
                DetectedBank = BankType.CentralBank,
                BankName = "Central Bank of India",
                Confidence = confidence,
                IsSupported = true,
                AccountNumber = centralAccountNumber ?? accountNumber,
                CustomerName = centralCustomerName ?? customerName,
                StatementFrom = centralPeriodStart ?? periodStart,
                StatementTo = centralPeriodEnd ?? periodEnd,
                DetectionSignals = centralSignals
            };
        }

        if (isAxisCandidate && axisScore > hdfcScore && axisScore > yesScore && axisScore > iciciScore && axisScore > sbiScore && axisScore > boiScore && axisScore > kotakScore)
        {
            double confidence = 0.90;
            if (hasAxisBrand && (hasAxisIfsc || hasAxisHeader)) confidence += 0.05;
            if (matchedAxisColumns >= 6) confidence += 0.03;
            if (!string.IsNullOrEmpty(axisAccountNumber)) confidence += 0.02;

            confidence = Math.Min(1.0, confidence);

            _logger.LogInformation("Successfully detected Axis Bank statement with confidence {Confidence:P0}. Account: {Account}",
                confidence, axisAccountNumber ?? "N/A");

            return new BankDetectionResult
            {
                DetectedBank = BankType.Axis,
                BankName = "Axis Bank",
                Confidence = confidence,
                IsSupported = true,
                AccountNumber = axisAccountNumber ?? accountNumber,
                CustomerName = axisCustomerName ?? customerName,
                StatementFrom = axisPeriodStart ?? periodStart,
                StatementTo = axisPeriodEnd ?? periodEnd,
                DetectionSignals = axisSignals
            };
        }

        if (isYesCandidate && (!isHdfcCandidate || yesScore > hdfcScore) && (!isIciciCandidate || yesScore > iciciScore) && (!isSbiCandidate || yesScore > sbiScore) && (!isBoiCandidate || yesScore > boiScore) && (!isKotakCandidate || yesScore > kotakScore))
        {
            double confidence = 0.90;
            if (hasYesBrand && (hasYesIfsc || hasYesHeader)) confidence += 0.05;
            if (matchedYesColumns >= 6) confidence += 0.03;
            if (!string.IsNullOrEmpty(yesAccountNumber)) confidence += 0.02;

            confidence = Math.Min(1.0, confidence);

            _logger.LogInformation("Successfully detected YES BANK statement with confidence {Confidence:P0}. Account: {Account}",
                confidence, yesAccountNumber ?? "N/A");

            return new BankDetectionResult
            {
                DetectedBank = BankType.YesBank,
                BankName = "YES BANK",
                Confidence = confidence,
                IsSupported = true,
                AccountNumber = yesAccountNumber ?? accountNumber,
                CustomerName = customerName,
                StatementFrom = yesPeriodStart ?? periodStart,
                StatementTo = yesPeriodEnd ?? periodEnd,
                DetectionSignals = yesSignals
            };
        }

        if (isHdfcCandidate && (!isYesCandidate || hdfcScore >= yesScore) && (!isIciciCandidate || hdfcScore >= iciciScore) && (!isSbiCandidate || hdfcScore >= sbiScore) && (!isBoiCandidate || hdfcScore >= boiScore) && (!isKotakCandidate || hdfcScore >= kotakScore))
        {
            double confidence = 0.90;
            if (hasHdfcBrand && (hasHdfcIfsc || hasHdfcHeader)) confidence += 0.05;
            if (matchedHdfcColumns >= 6) confidence += 0.03;
            if (!string.IsNullOrEmpty(accountNumber)) confidence += 0.02;

            confidence = Math.Min(1.0, confidence);

            _logger.LogInformation("Successfully detected HDFC Bank statement with confidence {Confidence:P0}. Account: {Account}",
                confidence, accountNumber ?? "N/A");

            return new BankDetectionResult
            {
                DetectedBank = BankType.Hdfc,
                BankName = "HDFC Bank",
                Confidence = confidence,
                IsSupported = true,
                AccountNumber = accountNumber,
                CustomerName = customerName,
                StatementFrom = periodStart,
                StatementTo = periodEnd,
                DetectionSignals = signals
            };
        }

        // Document does not exhibit deterministic bank signals -> Reject cleanly without creating fake transactions
        _logger.LogWarning("Document not recognized as supported bank. HDFC matched: {HdfcCols}, YES BANK matched: {YesCols}, Axis matched: {AxisCols}, Central Bank matched: {CentralCols}, SBI matched: {SbiCols}, BOI matched: {BoiCols}, Kotak matched: {KotakCols}",
            matchedHdfcColumns, matchedYesColumns, matchedAxisColumns, matchedCentralColumns, matchedSbiColumns, matchedBoiColumns, matchedKotakColumns);

        signals.Add("Insufficient evidence to classify document as a supported bank format.");

        return new BankDetectionResult
        {
            DetectedBank = BankType.Unknown,
            BankName = "Unknown",
            Confidence = 0.0,
            IsSupported = false,
            AccountNumber = accountNumber ?? yesAccountNumber ?? axisAccountNumber ?? centralAccountNumber ?? sbiAccountNumber ?? boiAccountNumber ?? kotakAccountNumber,
            CustomerName = customerName ?? axisCustomerName ?? centralCustomerName ?? sbiCustomerName ?? boiCustomerName ?? kotakCustomerName,
            StatementFrom = periodStart ?? yesPeriodStart ?? axisPeriodStart ?? centralPeriodStart ?? sbiPeriodStart ?? boiPeriodStart ?? kotakPeriodStart,
            StatementTo = periodEnd ?? yesPeriodEnd ?? axisPeriodEnd ?? centralPeriodEnd ?? sbiPeriodEnd ?? boiPeriodEnd ?? kotakPeriodEnd,
            DetectionSignals = signals
        };
    }
}
