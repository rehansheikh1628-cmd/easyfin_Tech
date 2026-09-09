using System;
using System.IO;
using ClosedXML.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using EasyFin_Tech.Server.ExcelToTally.Interfaces;

namespace EasyFin_Tech.Server.ExcelToTally.Services;

public class ExcelTemplateService : IExcelTemplateService
{
    public const string TemplateTypeProperty = "EasyFin_Template_Type";
    public const string TemplateTypeValue = "Tally_Import_XLSM";
    public const string TemplateVersionProperty = "EasyFin_Template_Version";
    public const string TemplateVersionValue = "1.0";
    public const string SampleRowPrefix = "[SAMPLE]";

    public byte[] GenerateTemplate()
    {
        using var workbook = new XLWorkbook();

        // Embed custom template metadata properties for template integrity verification
        workbook.CustomProperties.Add(TemplateTypeProperty, TemplateTypeValue);
        workbook.CustomProperties.Add(TemplateVersionProperty, TemplateVersionValue);

        // 1. Transactions Worksheet (Primary entry sheet for client)
        var ws = workbook.Worksheets.Add("Transactions");

        string[] headers =
        {
            "Date",
            "Narration",
            "Cheque / Ref No.",
            "Value Date",
            "Dr Amount",
            "Cr Amount",
            "Closing Balance",
            "Ledger Name",
            "Bank Name"
        };

        // Header Row Styling
        for (int col = 1; col <= headers.Length; col++)
        {
            var cell = ws.Cell(1, col);
            cell.Value = headers[col - 1];
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Fill.BackgroundColor = XLColor.FromArgb(24, 43, 73); // EasyFin Navy #182B49
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            cell.Style.Border.OutsideBorderColor = XLColor.FromArgb(200, 200, 200);
        }

        // Generic Sample Data Rows (Sample 1: Payment/Debit, Sample 2: Receipt/Credit)
        // Explicitly prefixed with [SAMPLE] so reader automatically ignores them if not deleted by user.
        ws.Cell(2, 1).Value = "01/04/2025";
        ws.Cell(2, 2).Value = $"{SampleRowPrefix} Office Stationery Supplies";
        ws.Cell(2, 3).Value = "UPI-9182347101";
        ws.Cell(2, 4).Value = "01/04/2025";
        ws.Cell(2, 5).Value = 1250.00;
        ws.Cell(2, 6).Value = "";
        ws.Cell(2, 7).Value = 48750.00;
        ws.Cell(2, 8).Value = "Printing & Stationery";
        ws.Cell(2, 9).Value = "HDFC Bank";

        ws.Cell(3, 1).Value = "02/04/2025";
        ws.Cell(3, 2).Value = $"{SampleRowPrefix} Professional Services Retainer";
        ws.Cell(3, 3).Value = "NEFT-0029184";
        ws.Cell(3, 4).Value = "02/04/2025";
        ws.Cell(3, 5).Value = "";
        ws.Cell(3, 6).Value = 25000.00;
        ws.Cell(3, 7).Value = 73750.00;
        ws.Cell(3, 8).Value = "Consulting Fees";
        ws.Cell(3, 9).Value = "HDFC Bank";

        // Format sample rows
        for (int r = 2; r <= 3; r++)
        {
            ws.Cell(r, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(r, 4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(r, 5).Style.NumberFormat.Format = "#,##0.00";
            ws.Cell(r, 6).Style.NumberFormat.Format = "#,##0.00";
            ws.Cell(r, 7).Style.NumberFormat.Format = "#,##0.00";

            for (int c = 1; c <= 9; c++)
            {
                ws.Cell(r, c).Style.Font.Italic = true;
                ws.Cell(r, c).Style.Font.FontColor = XLColor.FromArgb(100, 116, 139); // Slate-500
                ws.Cell(r, c).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                ws.Cell(r, c).Style.Border.OutsideBorderColor = XLColor.FromArgb(220, 220, 220);
            }
        }

        ws.Columns().AdjustToContents();
        ws.Column(2).Width = 38; // Narration width
        ws.Column(8).Width = 25; // Ledger Name width
        ws.Column(9).Width = 22; // Bank Name width

        // 2. Instructions Worksheet
        var instWs = workbook.Worksheets.Add("Instructions");
        instWs.Cell(1, 1).Value = "EasyFin Tech — Official Excel to Tally XML Template (.XLSM) Guide";
        instWs.Cell(1, 1).Style.Font.Bold = true;
        instWs.Cell(1, 1).Style.Font.FontSize = 14;
        instWs.Cell(1, 1).Style.Font.FontColor = XLColor.FromArgb(24, 43, 73);

        string[,] guidelines =
        {
            { "Column", "Required?", "Format / Rule", "Guidance" },
            { "Date", "Yes (Mandatory)", "dd/MM/yyyy or dd-MMM-yyyy", "Transaction voucher date in accounting period." },
            { "Narration", "No (Optional)", "Text", "Description or particulars of the transaction." },
            { "Cheque / Ref No.", "No (Optional)", "Text / Alphanumeric", "Cheque number, UTR, IMPS reference, or transaction ID." },
            { "Value Date", "No (Optional)", "dd/MM/yyyy", "Bank value clearance date if distinct from transaction date." },
            { "Dr Amount", "Conditional", "Numeric, >= 0", "Populate for debit transactions (payments/charges). Leave blank for credits." },
            { "Cr Amount", "Conditional", "Numeric, >= 0", "Populate for credit transactions (receipts/income). Leave blank for debits." },
            { "Closing Balance", "No (Optional)", "Numeric", "Account running balance after transaction." },
            { "Ledger Name", "Yes (Mandatory)", "Text", "Tally accounting ledger name corresponding to the transaction party/head." },
            { "Bank Name", "Yes (Mandatory)", "Text", "Tally bank ledger name (e.g. HDFC Bank, Kotak Bank, SBI Account)." }
        };

        for (int r = 0; r < guidelines.GetLength(0); r++)
        {
            for (int c = 0; c < guidelines.GetLength(1); c++)
            {
                var cell = instWs.Cell(r + 3, c + 1);
                cell.Value = guidelines[r, c];
                if (r == 0)
                {
                    cell.Style.Font.Bold = true;
                    cell.Style.Fill.BackgroundColor = XLColor.FromArgb(230, 235, 245);
                }
                cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                cell.Style.Border.OutsideBorderColor = XLColor.FromArgb(200, 200, 200);
            }
        }

        instWs.Cell(15, 1).Value = "IMPORTANT ACCOUNTING WORKFLOW INSTRUCTIONS:";
        instWs.Cell(15, 1).Style.Font.Bold = true;
        instWs.Cell(16, 1).Value = "1. Enter or paste your transactions into the 'Transactions' sheet.";
        instWs.Cell(17, 1).Value = "2. Rows marked with [SAMPLE] are examples and will be ignored by EasyFin.";
        instWs.Cell(18, 1).Value = "3. Exactly one of Dr Amount or Cr Amount must be populated (> 0) per transaction row.";
        instWs.Cell(19, 1).Value = "4. Ledger Name and Bank Name must match your Chart of Accounts in Tally.";
        instWs.Cell(20, 1).Value = "5. Save the workbook as .XLSM and upload to EasyFin /excel-to-tally.";

        instWs.Columns().AdjustToContents();

        // 3. Package as genuine Macro-Enabled Workbook (.XLSM)
        using var ms = new MemoryStream();
        workbook.SaveAs(ms);
        ms.Position = 0;

        using (var doc = SpreadsheetDocument.Open(ms, true))
        {
            doc.ChangeDocumentType(SpreadsheetDocumentType.MacroEnabledWorkbook);
        }

        return ms.ToArray();
    }
}
