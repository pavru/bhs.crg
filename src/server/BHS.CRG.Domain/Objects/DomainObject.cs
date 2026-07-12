using System.Text.Json;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Domain.Objects;

/// <summary>
/// Единый объект предметной области (issue #84, Фаза 1) — экземпляр составного типа на единой оси
/// расположения. Заменяет прежние <c>CommonDataEntry</c> (общие данные) и <c>DocumentInstance</c>
/// (документ комплекта): различие — только в <see cref="DocumentType.Kind"/> типа и в наличии
/// <see cref="Facet"/> (документная фасета есть ⟺ тип — Document). Расположение — единая пара
/// (<see cref="ScopeLevel"/>, <see cref="ScopeId"/>); документ живёт в (Set, setId).
/// </summary>
public class DomainObject : Entity
{
    /// <summary>Имя экземпляра. Null — отображается имя типа (для документов = прежний Name).</summary>
    public string? DisplayName { get; private set; }

    /// <summary>Альтернативные имена (issue #74) — участвуют в сопоставлении по имени наравне с DisplayName.</summary>
    public List<string> Aliases { get; private set; } = [];

    /// <summary>ID типа (<see cref="DocumentType"/>) — Composite или Document.</summary>
    public Guid CompositeTypeId { get; private set; }

    /// <summary>Данные полей экземпляра (реквизиты) + возможный «_baseRef».</summary>
    public JsonDocument Data { get; private set; } = JsonDocument.Parse("{}");

    /// <summary>Уровень расположения на единой оси.</summary>
    public CatalogScope ScopeLevel { get; private set; }

    /// <summary>Носитель уровня (null для System). Для документов = SetId.</summary>
    public Guid? ScopeId { get; private set; }

    /// <summary>Документная фасета — только у объектов Document-типа. Null ⇒ объект общих данных.</summary>
    public DocumentFacet? Facet { get; private set; }

    public bool IsDocument => Facet is not null;

    private DomainObject() { }

    public static DomainObject Create(
        Guid compositeTypeId, string? displayName, JsonDocument data,
        CatalogScope scopeLevel, Guid? scopeId, IReadOnlyList<string>? aliases = null)
        => new()
        {
            CompositeTypeId = compositeTypeId,
            DisplayName = Norm(displayName),
            Data = data,
            ScopeLevel = scopeLevel,
            ScopeId = scopeId,
            Aliases = NormalizeAliases(aliases),
        };

    public static DomainObject Restore(
        Guid id, Guid compositeTypeId, string? displayName, JsonDocument data,
        CatalogScope scopeLevel, Guid? scopeId, DateTimeOffset createdAt, DateTimeOffset updatedAt,
        IReadOnlyList<string>? aliases = null)
        => new()
        {
            Id = id, CompositeTypeId = compositeTypeId, DisplayName = Norm(displayName),
            Data = data, ScopeLevel = scopeLevel, ScopeId = scopeId,
            Aliases = NormalizeAliases(aliases), CreatedAt = createdAt, UpdatedAt = updatedAt,
        };

    /// <summary>Делает объект документом (создаёт фасету, если ещё нет). Возвращает фасету.</summary>
    public DocumentFacet EnsureFacet()
    {
        Facet ??= DocumentFacet.Create(Id);
        return Facet;
    }

    // ── Общие изменения (обе разновидности) ─────────────────────────────────────
    public void Update(string? displayName, JsonDocument data, IReadOnlyList<string>? aliases = null)
    {
        DisplayName = Norm(displayName);
        Data = data;
        Aliases = NormalizeAliases(aliases);
        TouchUpdatedAt();
    }

    public void Rename(string? displayName) { DisplayName = Norm(displayName); TouchUpdatedAt(); }
    public void SetData(JsonDocument data) { Data = data; TouchUpdatedAt(); }

    // ── Документные изменения (через фасету; TouchUpdatedAt на объекте) ──────────
    private DocumentFacet Doc => Facet ?? throw new InvalidOperationException("Объект не является документом (нет фасеты).");

    public DocumentStatus Status => Doc.Status;
    public int SortOrder => Doc.SortOrder;
    public Guid? TemplateId => Doc.TemplateId;
    public string? TemplateIds => Doc.TemplateIds;
    public string? TemplateParams => Doc.TemplateParams;
    public JsonDocument PluginData => Doc.PluginData;
    public IReadOnlyList<GeneratedFile> GeneratedFiles => Doc.GeneratedFiles;

    public void MarkGenerating() { Doc.Status = DocumentStatus.Generating; TouchUpdatedAt(); }
    public void MarkFailed() { Doc.Status = DocumentStatus.Failed; TouchUpdatedAt(); }
    public void SetSortOrder(int order) { Doc.SortOrder = order; TouchUpdatedAt(); }
    public void SetTemplate(Guid? templateId) { Doc.TemplateId = templateId; TouchUpdatedAt(); }
    public void SetTemplateIds(string? json) { Doc.TemplateIds = json; TouchUpdatedAt(); }
    public void SetTemplateParams(string? json) { Doc.TemplateParams = json; TouchUpdatedAt(); }
    public void UpdatePluginData(JsonDocument pluginData) { Doc.PluginData = pluginData; TouchUpdatedAt(); }

    public GeneratedFile AddGeneratedFile(OutputFormat format, string blobPath, Guid? templateId = null)
    {
        var file = GeneratedFile.Create(Id, format, blobPath, templateId);
        // Один файл на пару (формат, шаблон); повторная генерация тем же шаблоном заменяет его файл.
        Doc.Files.RemoveAll(f => f.Format == format && f.TemplateId == templateId);
        Doc.Files.Add(file);
        Doc.Status = DocumentStatus.Generated;
        TouchUpdatedAt();
        return file;
    }

    /// <summary>Сбрасывает документ в черновик, возвращает blob-пути удалённых файлов.</summary>
    public IReadOnlyList<string> ResetToDraft()
    {
        if (Doc.Status == DocumentStatus.Draft) return [];
        var paths = Doc.Files.Select(f => f.BlobPath).ToList();
        Doc.Files.Clear();
        Doc.Status = DocumentStatus.Draft;
        TouchUpdatedAt();
        return paths;
    }

    private static string? Norm(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// Убираем пустые/дублирующиеся алиасы (без учёта регистра), сохраняя порядок.
    private static List<string> NormalizeAliases(IReadOnlyList<string>? aliases)
    {
        if (aliases is null) return [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var a in aliases)
        {
            var t = a?.Trim();
            if (!string.IsNullOrEmpty(t) && seen.Add(t)) result.Add(t);
        }
        return result;
    }
}
