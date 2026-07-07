namespace BHS.CRG.Application.Documents;

/// <summary>
/// Результат поиска документа across комплектов: сам документ + «хлебные крошки»
/// (стройка › раздел › комплект) для перехода. Отображаемое имя вычисляется на фронте
/// как <c>Name ?? TypeName</c>.
/// </summary>
public class DocumentSearchResult
{
    public Guid InstanceId { get; set; }
    public string? Name { get; set; }
    public string TypeName { get; set; } = "";
    public string Status { get; set; } = "";
    public bool HasPdf { get; set; }
    public Guid ConstructionId { get; set; }
    public string ConstructionName { get; set; } = "";
    public string SectionName { get; set; } = "";
    public Guid SetId { get; set; }
    public string SetName { get; set; } = "";
}

/// <summary>
/// Поиск документов по всем комплектам (по имени документа, имени типа и тексту реквизитов).
/// Видимость — как у списка строек (сейчас общий: аутентифицированный пользователь видит все стройки),
/// отдельного row-level scoping в системе нет.
/// </summary>
public interface IDocumentSearch
{
    Task<IReadOnlyList<DocumentSearchResult>> SearchAsync(
        string text, Guid? constructionId, CancellationToken ct = default);
}
