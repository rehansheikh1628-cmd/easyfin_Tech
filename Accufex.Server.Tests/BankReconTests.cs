using System;
using System.IO;
using System.Linq;
using UglyToad.PdfPig;
using Xunit;
using Xunit.Abstractions;

namespace Accufex.Server.Tests;

public class BankReconTests
{
    private readonly ITestOutputHelper _output;

    public BankReconTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private void InspectPdf(string filePath, string bankName)
    {
        InspectPdfPages(filePath, bankName, 1, 2);
    }

    private void InspectPdfPages(string filePath, string bankName, int startPage, int endPage)
    {
        _output.WriteLine($"================================================================================");
        _output.WriteLine($"BANK: {bankName} | FILE: {Path.GetFileName(filePath)}");
        _output.WriteLine($"================================================================================");

        if (!File.Exists(filePath))
        {
            _output.WriteLine($"[NOT FOUND] {filePath}");
            return;
        }

        try
        {
            using var doc = PdfDocument.Open(filePath);
            _output.WriteLine($"Total Pages: {doc.NumberOfPages}");

            int pStart = Math.Max(1, startPage);
            int pEnd = Math.Min(endPage, doc.NumberOfPages);
            for (int p = pStart; p <= pEnd; p++)
            {
                var page = doc.GetPage(p);
                var words = page.GetWords().ToList();
                _output.WriteLine($"--- PAGE {p} (W: {page.Width:F1}, H: {page.Height:F1}, WordCount: {words.Count}) ---");
                
                if (words.Count == 0)
                {
                    _output.WriteLine("  [NO WORDS EXTRACTED - Check if scanned or image-based]");
                    continue;
                }

                var lines = words
                    .GroupBy(w => Math.Round(w.BoundingBox.Bottom / 3.0) * 3.0)
                    .OrderByDescending(g => g.Key)
                    .Take(45);

                foreach (var line in lines)
                {
                    var text = string.Join(" ", line.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text));
                    _output.WriteLine($"  [Y={line.Key,5:F0}] {text}");
                }
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[ERROR] {ex.GetType().Name}: {ex.Message}");
        }
    }

    [Fact]
    public void Recon_1_HDFC()
    {
        InspectPdf(@"E:\Bank Statements\HDFC Pass-134173633_unlocked.pdf", "1. HDFC Bank (Unlocked)");
        InspectPdf(@"E:\Bank Statements\New 07-08-2026\hdfc bank 8753 01.04.24 to 31.3.25.pdf", "1b. HDFC Bank 8753");
    }

    [Fact]
    public void Recon_2_YES()
    {
        InspectPdf(@"E:\Bank Statements\Iris-YesBank-09 Sat,2025 14-21-46 pm.pdf", "2. YES BANK");
    }

    [Fact]
    public void Recon_3_ICICI()
    {
        InspectPdfPages(@"E:\Bank Statements\Icici statement.pdf", "3. ICICI Bank", 3, 5);
    }

    [Fact]
    public void Recon_4_Axis()
    {
        InspectPdf(@"E:\Bank Statements\AXIS APRIL TO MARCH.PDF", "4. Axis Bank");
    }

    [Fact]
    public void Recon_5_BOI()
    {
        InspectPdf(@"E:\Bank Statements\BOI-182.pdf", "5. Bank of India (BOI)");
    }

    [Fact]
    public void Recon_6_Canara()
    {
        var filePath = @"E:\Bank Statements\F.Y. Statement 2025-2026 Canara Bank.pdf";
        _output.WriteLine($"Examining: {filePath}");
        if (!File.Exists(filePath))
        {
            _output.WriteLine("File does not exist!");
            return;
        }

        using var doc = PdfDocument.Open(filePath);
        _output.WriteLine($"Total Pages: {doc.NumberOfPages}");
        int totalWords = 0;
        for (int p = 1; p <= doc.NumberOfPages; p++)
        {
            var page = doc.GetPage(p);
            var words = page.GetWords().ToList();
            totalWords += words.Count;
            if (words.Count > 0)
            {
                _output.WriteLine($"Page {p} HAS WORDS: {words.Count}");
            }
        }
        _output.WriteLine($"Total words across all {doc.NumberOfPages} pages: {totalWords}");
    }

    [Fact]
    public void Recon_AprilToMarchPass()
    {
        var filePath = @"E:\Bank Statements\april to march pass-SHAF0101.pdf";
        try
        {
            using var doc = PdfDocument.Open(filePath, new ParsingOptions { Password = "SHAF0101" });
            _output.WriteLine($"Opened successfully! Pages: {doc.NumberOfPages}");
            var page = doc.GetPage(1);
            var words = page.GetWords().ToList();
            _output.WriteLine($"Page 1 Words: {words.Count}");
            var lines = words
                .GroupBy(w => Math.Round(w.BoundingBox.Bottom / 3.0) * 3.0)
                .OrderByDescending(g => g.Key)
                .Take(25);
            foreach (var line in lines)
            {
                var text = string.Join(" ", line.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text));
                _output.WriteLine($"  {text}");
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Error: {ex.Message}");
        }
    }
}
