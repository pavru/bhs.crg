using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Application.Generation;

public interface IMetadataExtractor
{
    /// <summary>
    /// Извлекает метаданные из сгенерированного файла и формирует словарь tag→value.
    /// </summary>
    Dictionary<string, object?> Extract(byte[] bytes, OutputFormat format, string? generatedBy);
}
