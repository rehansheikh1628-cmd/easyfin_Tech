using System.Collections.Generic;
using System.Linq;
using Accufex.Server.Parsing.Interfaces;
using Accufex.Server.Parsing.Models;

namespace Accufex.Server.Parsing;

public class BankParserRegistry : IBankParserRegistry
{
    private readonly IEnumerable<IBankStatementParser> _parsers;

    public BankParserRegistry(IEnumerable<IBankStatementParser> parsers)
    {
        _parsers = parsers;
    }

    public IBankStatementParser? GetParser(int bankCode)
    {
        return _parsers.FirstOrDefault(p => p.BankCode == bankCode);
    }

    public IBankStatementParser? ResolveParser(BankDetectionResult detection)
    {
        if (detection == null || !detection.IsSupported) return null;
        return _parsers.FirstOrDefault(p => p.CanParse(detection));
    }

    public IEnumerable<IBankStatementParser> GetAllParsers()
    {
        return _parsers;
    }
}
