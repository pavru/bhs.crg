using BHS.CRG.Application.DataSets;
using BHS.CRG.Domain.DataSets;

namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>
/// PDF — единственный формат, где Extraction не детерминированный парсинг, а распознавание
/// через vision-LLM (<see cref="BHS.CRG.Application.QualityDocs.IDocumentRecognizer"/>): дорого,
/// небыстро, недетерминированно. Поэтому источники создаются вручную (без авто-детекта, как и
/// для XML), а реальные данные не идут через <see cref="ParseAsync"/> — распознавание запускается
/// явным действием пользователя, результат кэшируется на DataSetSource.CachedData и оттуда же
/// читается пайплайном (см. DataSetBindingProcessor.LoadRowsAsync), минуя повторный парсинг.
/// </summary>
public class PdfDataSetParser : IDataSetParser
{
    public bool CanParse(DataSetFormat format) => format is DataSetFormat.Pdf;

    public Task<IReadOnlyList<DataSetSourceInfo>> DetectSourcesAsync(byte[] bytes, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<DataSetSourceInfo>>([]);

    public Task<DataSetParseResult> ParseAsync(byte[] bytes, string sheetOrPath, string? columnExpressions, CancellationToken ct)
        => throw new ArgumentException(
            "PDF-источники не поддерживают Extraction через builder — используйте распознавание (кнопка «Распознать»).");
}
