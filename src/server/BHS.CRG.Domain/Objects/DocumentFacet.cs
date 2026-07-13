using System.Text.Json;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Domain.Objects;

/// <summary>
/// Документная фасета <see cref="DomainObject"/> (issue #84, Фаза 1): существует ⟺ объект —
/// экземпляр Document-типа. Держит поля, осмысленные только для документа комплекта (статус,
/// шаблоны, порядок, кэш плагинов, сгенерированные файлы). Отдельная 1:1-таблица
/// (<c>document_facets</c>, PK = ObjectId) закрепляет инвариант «у общих данных нет статуса»
/// на уровне схемы. Мутируется через методы <see cref="DomainObject"/> (единая точка поведения
/// + TouchUpdatedAt на объекте) — сеттеры <c>internal</c>.
/// </summary>
public class DocumentFacet
{
    public Guid ObjectId { get; internal set; }
    public DocumentStatus Status { get; internal set; } = DocumentStatus.Draft;

    /// <summary>Порядок документа в комплекте (сборка комплекта одним файлом).</summary>
    public int SortOrder { get; internal set; }

    /// <summary>Явно выбранный шаблон (одиночная генерация / первый для параметров). Null — по умолчанию.</summary>
    public Guid? TemplateId { get; internal set; }

    /// <summary>Набор шаблонов для мульти-генерации (JSON-массив Guid) или null.</summary>
    public string? TemplateIds { get; internal set; }

    /// <summary>Переопределения параметров шаблона (JSON-объект) или null.</summary>
    public string? TemplateParams { get; internal set; }

    /// <summary>Кэш данных из плагинов на момент последней генерации.</summary>
    public JsonDocument PluginData { get; internal set; } = JsonDocument.Parse("{}");

    internal readonly List<GeneratedFile> Files = [];
    public IReadOnlyList<GeneratedFile> GeneratedFiles => Files.AsReadOnly();

    private DocumentFacet() { }

    internal static DocumentFacet Create(Guid objectId) => new() { ObjectId = objectId };
}
