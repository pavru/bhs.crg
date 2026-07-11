using BHS.CRG.Domain.Common;

namespace BHS.CRG.Domain.Templates;

public class Template : Entity
{
    public Guid DocumentTypeId { get; private set; }
    public string Name { get; private set; } = default!;
    public int Version { get; private set; } = 1;

    /// <summary>Typst-исходник шаблона.</summary>
    public string Content { get; private set; } = default!;

    /// <summary>
    /// Объявленные параметры шаблона (JSON-массив [{name,label,type,default}]) — чтобы не плодить
    /// почти одинаковые шаблоны. Значения по умолчанию задаются здесь, переопределяются на документе
    /// (<see cref="Documents.DocumentInstance.TemplateParams"/>). Эффективные значения подмешиваются
    /// в контекст генерации под ключ «params» (доступ в Typst: data.params.имя). Null/пусто — без параметров.
    /// </summary>
    public string? Parameters { get; private set; }

    public bool IsActive { get; private set; }

    /// <summary>Шаблон по умолчанию для данного типа документа.</summary>
    public bool IsDefault { get; private set; }

    private Template() { }

    public static Template Create(Guid documentTypeId, string name, string content)
        => new() { DocumentTypeId = documentTypeId, Name = name, Content = content, IsActive = true };

    public static Template Restore(
        Guid id, Guid documentTypeId, string name, string content, int version,
        bool isActive, bool isDefault,
        DateTimeOffset createdAt, DateTimeOffset updatedAt, string? parameters = null)
        => new()
        {
            Id = id, DocumentTypeId = documentTypeId, Name = name, Content = content, Version = version,
            IsActive = isActive, IsDefault = isDefault,
            Parameters = parameters, CreatedAt = createdAt, UpdatedAt = updatedAt,
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
            Parameters = Parameters,
        };
    }

    /// <summary>
    /// Создаёт независимую копию шаблона как новый шаблон (version 1, активный, не по умолчанию)
    /// с тем же содержимым. Применяется для дублирования.
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
            Parameters = Parameters,
        };

    public void SetDefault(bool isDefault)
    {
        IsDefault = isDefault;
        TouchUpdatedAt();
    }

    /// <summary>Объявление параметров шаблона (JSON-массив [{name,label,type,default}] или null).</summary>
    public void SetParameters(string? parametersJson)
    {
        Parameters = parametersJson;
        TouchUpdatedAt();
    }
}
