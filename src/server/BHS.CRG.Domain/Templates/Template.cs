using BHS.CRG.Domain.Common;

namespace BHS.CRG.Domain.Templates;

public class Template : Entity
{
    public Guid DocumentTypeId { get; private set; }
    public string Name { get; private set; } = default!;
    public int Version { get; private set; } = 1;

    /// <summary>Typst-исходник шаблона.</summary>
    public string Content { get; private set; } = default!;

    public bool IsActive { get; private set; }

    /// <summary>Шаблон по умолчанию для данного типа документа.</summary>
    public bool IsDefault { get; private set; }

    /// <summary>ISO код формата листа: A0–A6 (default A4).</summary>
    public string PageSize { get; private set; } = "A4";

    /// <summary>"portrait" или "landscape".</summary>
    public string PageOrientation { get; private set; } = "portrait";

    // Поля печати в мм
    public int MarginTop { get; private set; } = 20;
    public int MarginRight { get; private set; } = 15;
    public int MarginBottom { get; private set; } = 20;
    public int MarginLeft { get; private set; } = 30;

    private Template() { }

    public static Template Create(Guid documentTypeId, string name, string content)
        => new() { DocumentTypeId = documentTypeId, Name = name, Content = content, IsActive = true };

    public static Template Restore(
        Guid id, Guid documentTypeId, string name, string content, int version,
        bool isActive, bool isDefault, string pageSize, string pageOrientation,
        int marginTop, int marginRight, int marginBottom, int marginLeft,
        DateTimeOffset createdAt, DateTimeOffset updatedAt)
        => new()
        {
            Id = id, DocumentTypeId = documentTypeId, Name = name, Content = content, Version = version,
            IsActive = isActive, IsDefault = isDefault, PageSize = pageSize, PageOrientation = pageOrientation,
            MarginTop = marginTop, MarginRight = marginRight, MarginBottom = marginBottom, MarginLeft = marginLeft,
            CreatedAt = createdAt, UpdatedAt = updatedAt,
        };

    public Template CreateNewVersion(string content)
    {
        var wasDefault = IsDefault;
        IsActive = false;
        IsDefault = false;
        TouchUpdatedAt();
        return new Template
        {
            DocumentTypeId = DocumentTypeId,
            Name = Name,
            Content = content,
            Version = Version + 1,
            IsActive = true,
            IsDefault = wasDefault,
            PageSize = PageSize,
            PageOrientation = PageOrientation,
            MarginTop = MarginTop,
            MarginRight = MarginRight,
            MarginBottom = MarginBottom,
            MarginLeft = MarginLeft,
        };
    }

    /// <summary>
    /// Создаёт независимую копию шаблона как новый шаблон (version 1, активный, не по умолчанию)
    /// с тем же содержимым и настройками страницы. Применяется для дублирования.
    /// </summary>
    public Template Duplicate(string newName)
        => new()
        {
            DocumentTypeId = DocumentTypeId,
            Name = newName,
            Content = Content,
            Version = 1,
            IsActive = true,
            IsDefault = false,
            PageSize = PageSize,
            PageOrientation = PageOrientation,
            MarginTop = MarginTop,
            MarginRight = MarginRight,
            MarginBottom = MarginBottom,
            MarginLeft = MarginLeft,
        };

    public void SetPageSettings(string pageSize, string pageOrientation, int top, int right, int bottom, int left)
    {
        PageSize = pageSize;
        PageOrientation = pageOrientation;
        MarginTop = top;
        MarginRight = right;
        MarginBottom = bottom;
        MarginLeft = left;
        TouchUpdatedAt();
    }

    public void SetDefault(bool isDefault)
    {
        IsDefault = isDefault;
        TouchUpdatedAt();
    }
}
