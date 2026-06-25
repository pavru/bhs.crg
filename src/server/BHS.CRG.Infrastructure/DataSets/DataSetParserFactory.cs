using BHS.CRG.Application.DataSets;
using BHS.CRG.Domain.DataSets;

namespace BHS.CRG.Infrastructure.DataSets;

public class DataSetParserFactory(IEnumerable<IDataSetParser> parsers)
{
    public IDataSetParser GetParser(DataSetFormat format)
        => parsers.FirstOrDefault(p => p.CanParse(format))
            ?? throw new InvalidOperationException($"Нет парсера для формата {format}");
}
