namespace BHS.CRG.Application.Generation;

public interface IMetadataExtractor
{
    /// <summary>
    /// Извлекает метаданные из файла и формирует словарь tag→value. <paramref name="isPdf"/> —
    /// признак содержимого (PDF-специфичные метаданные вроде числа страниц извлекаются только
    /// для PDF), не связан с форматом генерации документа.
    /// </summary>
    Dictionary<string, object?> Extract(byte[] bytes, bool isPdf, string? generatedBy);
}
