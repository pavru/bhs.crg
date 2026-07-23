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

    /// <summary>
    /// Клон как документ (issue #283, дублировать/копировать/перенести): новый объект того же типа
    /// в целевом Set-scope с переданными (уже клонированными/обработанными вызывающим) данными.
    /// Копирует конфиг шаблонов и алиасы; фасета свежая — Draft, без сгенерированных файлов,
    /// PluginData пуст (это generation-кэш, при Draft невалиден). SortOrder выставляет вызывающий.
    /// </summary>
    public static DomainObject CloneAsDocument(DomainObject source, Guid targetSetId, JsonDocument data, string? displayName)
    {
        var clone = Create(source.CompositeTypeId, displayName, data, CatalogScope.Set, targetSetId, source.Aliases);
        clone.EnsureFacet();
        if (source.Facet is not null)
        {
            clone.SetTemplate(source.Facet.TemplateId);
            clone.SetTemplateIds(source.Facet.TemplateIds);
            clone.SetTemplateParams(source.Facet.TemplateParams);
        }
        return clone;
    }

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

    /// <summary>Перенос документа в другой комплект (issue #283, фаза D): смена носителя Set-scope.</summary>
    public void MoveToSet(Guid targetSetId) { ScopeLevel = CatalogScope.Set; ScopeId = targetSetId; TouchUpdatedAt(); }

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

    // ── Пины шаблонов (issue #362/#364) — единая точка семантики «на что запиннут документ» ──
    private List<Guid> ParsedTemplateIds()
    {
        if (string.IsNullOrWhiteSpace(Doc.TemplateIds)) return [];
        try { return JsonSerializer.Deserialize<List<Guid>>(Doc.TemplateIds) ?? []; }
        catch (JsonException) { return []; }
    }

    /// <summary>Документ запиннут на версию — явным одиночным <see cref="TemplateId"/> или в наборе <see cref="TemplateIds"/>.</summary>
    public bool PinsTemplate(Guid templateId) => Doc.TemplateId == templateId || ParsedTemplateIds().Contains(templateId);

    /// <summary>Нет пина — документ резолвится в default-active шаблон типа.</summary>
    public bool HasNoTemplatePin => Doc.TemplateId is null && ParsedTemplateIds().Count == 0;

    /// <summary>Убирает пин на версию (issue #364): из одиночного <see cref="TemplateId"/> и из набора
    /// <see cref="TemplateIds"/> (пустой набор → null). После снятия документ резолвится в дефолт.</summary>
    public void UnpinTemplate(Guid templateId)
    {
        var changed = false;
        if (Doc.TemplateId == templateId) { Doc.TemplateId = null; changed = true; }
        var ids = ParsedTemplateIds();
        if (ids.Remove(templateId))
        {
            Doc.TemplateIds = ids.Count == 0 ? null : JsonSerializer.Serialize(ids);
            changed = true;
        }
        if (changed) TouchUpdatedAt();
    }
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
