using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Accufex.Server.ExcelToTally.Interfaces;
using Accufex.Server.ExcelToTally.Models;

namespace Accufex.Server.ExcelToTally.Services;

public class TallyXmlGeneratorService : ITallyXmlGenerator
{
    public Task<byte[]> GenerateTallyXmlAsync(ExcelValidationResult validatedStatement, CancellationToken cancellationToken = default)
    {
        if (validatedStatement == null || validatedStatement.ParsedTransactions == null || validatedStatement.ParsedTransactions.Count == 0)
        {
            throw new ArgumentException("No transactions provided for Tally XML generation.", nameof(validatedStatement));
        }

        // Filter out any invalid/fatal rows defensively. Only transactions that passed validation become vouchers.
        var validTransactions = validatedStatement.ParsedTransactions
            .Where(t => t.Status != "Invalid" && (t.Issues == null || !t.Issues.Any(i => i.Severity == ExcelValidationSeverity.Fatal)))
            .ToList();

        if (validTransactions.Count == 0)
        {
            throw new InvalidOperationException("No valid transactions available for XML generation.");
        }

        var requestDataElement = new XElement("REQUESTDATA");

        foreach (var tx in validTransactions)
        {
            bool isDebit = tx.DrAmount.HasValue && tx.DrAmount.Value > 0;
            decimal amount = isDebit ? tx.DrAmount!.Value : tx.CrAmount!.Value;
            string vchType = isDebit ? "Payment" : "Receipt";

            string dateStr = tx.Date!.Value.ToString("yyyyMMdd");
            string effectiveDateStr = (tx.ValueDate ?? tx.Date!.Value).ToString("yyyyMMdd");

            var voucherElement = new XElement("VOUCHER",
                new XAttribute("VCHTYPE", vchType),
                new XAttribute("ACTION", "Create"),
                new XElement("DATE", dateStr),
                new XElement("EFFECTIVEDATE", effectiveDateStr),
                new XElement("VOUCHERTYPENAME", vchType)
            );

            if (!string.IsNullOrWhiteSpace(tx.ChequeRefNo))
            {
                voucherElement.Add(new XElement("REFERENCE", tx.ChequeRefNo.Trim()));
            }

            if (!string.IsNullOrWhiteSpace(tx.Narration))
            {
                voucherElement.Add(new XElement("NARRATION", tx.Narration.Trim()));
            }

            if (isDebit)
            {
                // Debit (Payment Voucher):
                // Party/Nominal ledger is Debited: ISDEEMEDPOSITIVE = Yes, Amount = -amount
                voucherElement.Add(new XElement("ALLLEDGERENTRIES.LIST",
                    new XElement("LEDGERNAME", tx.LedgerName!.Trim()),
                    new XElement("ISDEEMEDPOSITIVE", "Yes"),
                    new XElement("AMOUNT", (-amount).ToString("F2", System.Globalization.CultureInfo.InvariantCulture))
                ));

                // Bank ledger is Credited: ISDEEMEDPOSITIVE = No, Amount = +amount
                voucherElement.Add(new XElement("ALLLEDGERENTRIES.LIST",
                    new XElement("LEDGERNAME", tx.BankName!.Trim()),
                    new XElement("ISDEEMEDPOSITIVE", "No"),
                    new XElement("AMOUNT", amount.ToString("F2", System.Globalization.CultureInfo.InvariantCulture))
                ));
            }
            else
            {
                // Credit (Receipt Voucher):
                // Bank ledger is Debited: ISDEEMEDPOSITIVE = Yes, Amount = -amount
                voucherElement.Add(new XElement("ALLLEDGERENTRIES.LIST",
                    new XElement("LEDGERNAME", tx.BankName!.Trim()),
                    new XElement("ISDEEMEDPOSITIVE", "Yes"),
                    new XElement("AMOUNT", (-amount).ToString("F2", System.Globalization.CultureInfo.InvariantCulture))
                ));

                // Party/Customer ledger is Credited: ISDEEMEDPOSITIVE = No, Amount = +amount
                voucherElement.Add(new XElement("ALLLEDGERENTRIES.LIST",
                    new XElement("LEDGERNAME", tx.LedgerName!.Trim()),
                    new XElement("ISDEEMEDPOSITIVE", "No"),
                    new XElement("AMOUNT", amount.ToString("F2", System.Globalization.CultureInfo.InvariantCulture))
                ));
            }

            var tallyMessage = new XElement("TALLYMESSAGE",
                new XAttribute(XNamespace.Xmlns + "UDF", "TallyUDF"),
                voucherElement
            );

            requestDataElement.Add(tallyMessage);
        }

        var xDoc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("ENVELOPE",
                new XElement("HEADER",
                    new XElement("TALLYREQUEST", "Import Data")
                ),
                new XElement("BODY",
                    new XElement("IMPORTDATA",
                        new XElement("REQUESTDESC",
                            new XElement("REPORTNAME", "Vouchers"),
                            new XElement("STATICVARIABLES",
                                new XElement("SVCURRENTCOMPANY", "Accufex")
                            )
                        ),
                        requestDataElement
                    )
                )
            )
        );

        using var ms = new MemoryStream();
        using (var writer = new StreamWriter(ms, new UTF8Encoding(false)))
        {
            xDoc.Save(writer);
        }

        return Task.FromResult(ms.ToArray());
    }
}
