using EasyFin_Tech.Server.DTOs;
using EasyFin_Tech.Server.Parsing.Models;

namespace EasyFin_Tech.Server.Parsing.Interfaces;

public interface IBankDetector
{
    BankDetectionResult DetectBank(PdfExtractionResult extraction);
}
