namespace BHS.CRG.Application.Templates;

/// <summary>Готовый к материализации графический ассет — Name уже победил по приоритету
/// (Template &gt; DocumentType &gt; System) среди всех уровней с этим же Name.</summary>
public record ResolvedImageAsset(string Name, string FileName, string MimeType, string BlobPath);

/// <summary>Готовый к материализации шрифтовой ассет — семейство уже победило по приоритету
/// среди всех уровней с этим же именем семейства (или Name-fallback, если имя не распознано).</summary>
public record ResolvedFontAsset(string FileName, string BlobPath);

public record ResolvedTemplateAssets(IReadOnlyList<ResolvedImageAsset> Images, IReadOnlyList<ResolvedFontAsset> Fonts)
{
    public static readonly ResolvedTemplateAssets Empty = new([], []);
}

/// <summary>
/// Резолвит эффективный набор ассетов шаблона (issue #62) — собирает TemplateAsset со всех трёх
/// уровней (Template/DocumentType/System) и схлопывает конфликты по приоритету
/// (Template &gt; DocumentType &gt; System): для картинок — по Name, для шрифтов — по FontFamilyName
/// (fallback на Name, если имя семейства не распознано при загрузке).
/// </summary>
public interface ITemplateAssetResolver
{
    Task<ResolvedTemplateAssets> ResolveAsync(Guid templateId, Guid documentTypeId, CancellationToken ct = default);
}
