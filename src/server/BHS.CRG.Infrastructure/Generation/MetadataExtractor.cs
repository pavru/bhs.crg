using BHS.CRG.Application.Generation;
using BHS.CRG.Domain.Documents;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig;

namespace BHS.CRG.Infrastructure.Generation;

public class MetadataExtractor(ILogger<MetadataExtractor> logger) : IMetadataExtractor
{
    public Dictionary<string, object?> Extract(byte[] bytes, OutputFormat format, string? generatedBy)
    {
        var meta = new Dictionary<string, object?>();

        meta[DocumentMetaTag.GeneratedAt] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
        meta[DocumentMetaTag.GeneratedBy] = generatedBy ?? string.Empty;

        if (format == OutputFormat.Pdf && bytes.Length > 0)
        {
            try
            {
                using var doc = PdfDocument.Open(bytes);
                meta[DocumentMetaTag.PageCount] = doc.NumberOfPages;
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
