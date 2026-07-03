using BHS.CRG.Application.Generation;
using BHS.CRG.Domain.Schema;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig;

namespace BHS.CRG.Infrastructure.Generation;

public class MetadataExtractor(ILogger<MetadataExtractor> logger) : IMetadataExtractor
{
    public Dictionary<string, object?> Extract(byte[] bytes, bool isPdf, string? generatedBy)
    {
        var meta = new Dictionary<string, object?>();

        meta[FunctionalTag.DocGeneratedAt] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
        meta[FunctionalTag.DocGeneratedBy] = generatedBy ?? string.Empty;

        if (isPdf && bytes.Length > 0)
        {
            try
            {
                using var doc = PdfDocument.Open(bytes);
                meta[FunctionalTag.DocPageCount] = doc.NumberOfPages;
            }
            catch (Exception ex)
            {
                // Если PDF не распознан — pageCount не записываем
                logger.LogWarning(ex, "Не удалось извлечь число страниц из PDF ({Bytes} байт)", bytes.Length);
            }
        }

        return meta;
    }
}
