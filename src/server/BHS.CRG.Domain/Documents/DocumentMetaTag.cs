namespace BHS.CRG.Domain.Documents;

/// <summary>
/// Системные теги для реквизитов, которые автоматически перезаписываются после генерации.
/// </summary>
public static class DocumentMetaTag
{
    /// <summary>Количество страниц сгенерированного PDF.</summary>
    public const string PageCount = "pageCount";

    /// <summary>Дата генерации документа (YYYY-MM-DD).</summary>
    public const string GeneratedAt = "generatedAt";

    /// <summary>Имя пользователя, запустившего генерацию.</summary>
    public const string GeneratedBy = "generatedBy";

    /// <summary>
    /// Поле типа file, содержащее загруженную пользователем печатную форму.
    /// При загрузке файла система автоматически извлекает и обновляет метаданные
    /// (pageCount и др.) из этого файла.
    /// </summary>
    public const string PrintForm = "printForm";
}
