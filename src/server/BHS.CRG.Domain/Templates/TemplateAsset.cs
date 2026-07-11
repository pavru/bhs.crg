using BHS.CRG.Domain.Common;

namespace BHS.CRG.Domain.Templates;

/// <summary>Уровень области видимости ассета шаблона (issue #62). Приоритет при совпадении Name/
/// FontFamilyName на разных уровнях (убывание): Template > DocumentType > System.</summary>
public enum TemplateAssetScope { Template = 1, DocumentType = 2, System = 3 }

/// <summary>Тип ассета — определяет, как он подключается при генерации: Image через
/// image("assets/{Name}.{ext}") в коде шаблона, Font через --font-path компилятора Typst.</summary>
public enum TemplateAssetKind { Image = 1, Font = 2 }

/// <summary>
/// Переиспользуемый статический файл для Typst-шаблонов (issue #62) — графика (PNG/JPEG/WebP/GIF/
/// SVG) или шрифт (TTF/OTF/TTC), на одном из трёх уровней: конкретная версия шаблона, тип
/// документа (общий для всех его шаблонов), система (общий для всех шаблонов).
/// </summary>
public class TemplateAsset : Entity
{
    public TemplateAssetScope Scope { get; private set; }

    /// <summary>Id шаблона/типа документа — null только для Scope=System.</summary>
    public Guid? ScopeId { get; private set; }

    public TemplateAssetKind Kind { get; private set; }

    /// <summary>Для Image — стабильный ключ, используемый в коде шаблона (image("assets/{Name}.{ext}")).
    /// Для Font — информационное поле (не ключ поиска): Typst резолвит шрифт по имени семейства,
    /// встроенному в сам файл (см. FontFamilyName), а не по этому имени.</summary>
    public string Name { get; private set; } = default!;

    public string FileName { get; private set; } = default!;
    public string MimeType { get; private set; } = default!;
    public string BlobPath { get; private set; } = default!;

    /// <summary>Только для Kind=Font — имя семейства, распознанное из файла при загрузке
    /// (SixLabors.Fonts). Null, если распознать не удалось — резолвер приоритета тогда
    /// сравнивает по Name (менее надёжный fallback).</summary>
    public string? FontFamilyName { get; private set; }

    private TemplateAsset() { }

    public static TemplateAsset Create(
        TemplateAssetScope scope, Guid? scopeId, TemplateAssetKind kind,
        string name, string fileName, string mimeType, string blobPath, string? fontFamilyName = null)
        => new()
        {
            Scope = scope, ScopeId = scopeId, Kind = kind,
            Name = name, FileName = fileName, MimeType = mimeType, BlobPath = blobPath,
            FontFamilyName = fontFamilyName,
        };

    /// <summary>Явная замена файла на этой же строке (та же версия/уровень) — не создаёт новую
    /// строку. Другие уровни/версии, дублировавшие этот ассет по ссылке, не затрагиваются.</summary>
    public void Replace(string fileName, string mimeType, string blobPath, string? fontFamilyName)
    {
        FileName = fileName; MimeType = mimeType; BlobPath = blobPath; FontFamilyName = fontFamilyName;
        TouchUpdatedAt();
    }

    public static TemplateAsset Restore(
        Guid id, TemplateAssetScope scope, Guid? scopeId, TemplateAssetKind kind,
        string name, string fileName, string mimeType, string blobPath, string? fontFamilyName,
        DateTimeOffset createdAt, DateTimeOffset updatedAt)
        => new()
        {
            Id = id, Scope = scope, ScopeId = scopeId, Kind = kind,
            Name = name, FileName = fileName, MimeType = mimeType, BlobPath = blobPath,
            FontFamilyName = fontFamilyName, CreatedAt = createdAt, UpdatedAt = updatedAt,
        };
}
