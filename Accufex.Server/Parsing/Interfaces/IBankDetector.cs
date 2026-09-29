using Accufex.Server.DTOs;
using Accufex.Server.Parsing.Models;

namespace Accufex.Server.Parsing.Interfaces;

public interface IBankDetector
{
    BankDetectionResult DetectBank(PdfExtractionResult extraction);
}
