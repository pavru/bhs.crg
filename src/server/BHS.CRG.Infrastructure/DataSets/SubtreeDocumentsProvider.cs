using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Domain.Schema;
using BHS.CRG.Infrastructure.Common;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>
/// Консолидация «Документы раздела/стройки»: документы ВСЕХ комплектов поддерева — реестр уровнем
/// выше комплекта.
///
/// Отдельный маркер, а не расширение «Документов комплекта»: колонок больше, и у уже созданных
/// источников состав колонок меняться не должен — привязка к полям сделана по именам.
///
/// Метаданные сборки (количество листов, дата генерации) есть только у собранных документов:
/// «проход 1.5», дописывающий их, живёт в сборке КОМПЛЕКТА, а сборки раздела или стройки нет и не
/// планируется (решение по эпику #622). Поэтому реестр показывает то, что известно на момент
/// генерации, и предупреждает, сколько комплектов поддерева ещё не собрано, — молчаливые пустые
/// ячейки читались бы как «листов нет».
/// </summary>
public class SubtreeDocumentsProvider(AppDbContext db, IDomainObjectRepository objects) : ISystemDataProvider
{
    /// <summary>Колонки «Документов комплекта» плюс адрес документа в поддереве.</summary>
    private static readonly string[] Columns =
    [
        "НомерПП", "Ид", "Наименование", "ТипКод", "ТипИмя", "Группа", "Статус",
        "НомерДокумента", "ДатаДокумента", "ДатаГенерации", "КоличествоЛистов", "ПорядокВКомплекте",
        "Комплект", "ИдКомплекта", "Раздел", "ИдРаздела",
    ];

    public bool Handles(string marker) => marker == SystemDataSets.SubtreeDocumentsMarker;

    public async Task<IReadOnlyList<DataSetSourceInfo>> GetCandidatesAsync(
        CatalogScope scope, Guid? scopeId, CancellationToken ct)
    {
        // На комплекте не предлагаем: там «Документы комплекта», и два похожих кандидата в одном
        // списке сбивают — выбирать пришлось бы по догадке, чем они отличаются.
        if (scope is not (CatalogScope.Section or CatalogScope.Construction) || scopeId is null) return [];

        var (rows, warning) = await RowsAsync(scope, scopeId, ct);
        return rows.Count == 0
            ? []
            : [new DataSetSourceInfo(NameFor(scope), SystemDataSets.SubtreeDocumentsMarker,
                ColumnsOf(rows), rows.Count, Warning: warning)];
    }

    public async Task<DataSetParseResult> ProvideAsync(
        string marker, CatalogScope scope, Guid? scopeId, CancellationToken ct)
    {
        if (scope is not (CatalogScope.Section or CatalogScope.Construction) || scopeId is null)
            throw new ArgumentException(
                "Перечень документов поддерева доступен у набора уровня «Раздел» или «Стройка».");

        var (rows, warning) = await RowsAsync(scope, scopeId, ct);
        return new DataSetParseResult(ColumnsOf(rows), rows, warning);
    }

    private static string NameFor(CatalogScope scope) =>
        scope == CatalogScope.Section ? "Документы раздела" : "Документы стройки";

    private async Task<(List<IReadOnlyDictionary<string, string?>> Rows, string? Warning)> RowsAsync(
        CatalogScope scope, Guid? scopeId, CancellationToken ct)
    {
        var sets = await ScopeSubtree.SetsUnderAsync(db, scope, scopeId, ct);
        if (sets.Count == 0) return ([], null);

        var setById = sets.ToDictionary(s => s.SetId);
        var documents = await objects.GetDocumentsInSetsAsync([.. setById.Keys], ct);
        if (documents.Count == 0) return ([], null);

        var allTypes = await db.DocumentTypes.AsNoTracking().ToListAsync(ct);
        var typeById = allTypes.ToDictionary(t => t.Id);

        var taggedKeys = new Dictionary<Guid, Dictionary<string, string>>();
        Dictionary<string, string> KeysOf(Guid typeId)
        {
            if (taggedKeys.TryGetValue(typeId, out var cached)) return cached;
            var map = new Dictionary<string, string>();
            if (typeById.TryGetValue(typeId, out var type))
                foreach (var (key, tag) in SchemaTags.TaggedFields(type, allTypes))
                    map.TryAdd(tag, key);
            taggedKeys[typeId] = map;
            return map;
        }

        // Порядок реестра: раздел → комплект → место документа в своём комплекте. Сквозной НомерПП
        // нумерует уже отсортированное, поэтому читается как номер строки реестра, а не документа.
        var ordered = documents
            .Where(d => d.ScopeId is { } id && setById.ContainsKey(id))
            .OrderBy(d => setById[d.ScopeId!.Value].SectionName, StringComparer.CurrentCulture)
            .ThenBy(d => setById[d.ScopeId!.Value].SetName, StringComparer.CurrentCulture)
            .ThenBy(d => d.SortOrder)
            .ToList();

        var rows = new List<IReadOnlyDictionary<string, string?>>(ordered.Count);
        var ordinal = 1;
        foreach (var doc in ordered)
        {
            var place = setById[doc.ScopeId!.Value];
            var type = typeById.GetValueOrDefault(doc.CompositeTypeId);
            var keys = KeysOf(doc.CompositeTypeId);

            string? Tagged(string tag) => keys.TryGetValue(tag, out var key) ? ScalarOf(doc.Data, key) : null;

            rows.Add(new Dictionary<string, string?>
            {
                ["НомерПП"] = ordinal.ToString(),
                ["Ид"] = doc.Id.ToString(),
                ["Наименование"] = doc.DisplayName ?? type?.Name ?? "Документ",
                ["ТипКод"] = type?.Code,
                ["ТипИмя"] = type?.Name,
                ["Группа"] = type?.Group,
                ["Статус"] = StatusLabel(doc.Status),
                ["НомерДокумента"] = Tagged(FunctionalTag.DocNumber),
                ["ДатаДокумента"] = Tagged(FunctionalTag.DocDate),
                ["ДатаГенерации"] = Tagged(FunctionalTag.DocGeneratedAt),
                ["КоличествоЛистов"] = Tagged(FunctionalTag.DocPageCount),
                ["ПорядокВКомплекте"] = doc.SortOrder.ToString(),
                ["Комплект"] = place.SetName,
                ["ИдКомплекта"] = place.SetId.ToString(),
                ["Раздел"] = place.SectionName,
                ["ИдРаздела"] = place.SectionId.ToString(),
            });
            ordinal++;
        }

        return (rows, UnassembledWarning(scope, sets, ordered));
    }

    /// <summary>
    /// Сколько комплектов поддерева ещё не собрано. Именно комплектов, а не документов: метаданные
    /// дописывает сборка комплекта целиком, и «полсобранного» комплекта не бывает.
    /// </summary>
    private static string? UnassembledWarning(
        CatalogScope scope, IReadOnlyList<ScopeSubtree.SetInSubtree> sets, List<DomainObject> documents)
    {
        var assembled = documents
            .Where(d => d.Status == DocumentStatus.Generated)
            .Select(d => d.ScopeId!.Value)
            .ToHashSet();
        var pending = sets.Count(s => !assembled.Contains(s.SetId));
        if (pending == 0) return null;

        var where = scope == CatalogScope.Section ? "разделе" : "стройке";
        return $"В {where} не собрано комплектов: {pending} из {sets.Count}. "
             + "Количество листов и дата генерации известны только у собранных документов — "
             + "у остальных эти ячейки пусты.";
    }

    /// <summary>Значение поля реквизитов как строка. Составное/массив — не ячейка таблицы, пропускаем.</summary>
    private static string? ScalarOf(JsonDocument data, string key)
    {
        if (data.RootElement.ValueKind != JsonValueKind.Object) return null;
        if (!data.RootElement.TryGetProperty(key, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "да",
            JsonValueKind.False => "нет",
            _ => null,
        };
    }

    private static string StatusLabel(DocumentStatus status) => status switch
    {
        DocumentStatus.Draft => "Черновик",
        DocumentStatus.Generating => "Генерируется",
        DocumentStatus.Generated => "Сгенерирован",
        DocumentStatus.Failed => "Ошибка",
        _ => status.ToString(),
    };

    private static IReadOnlyList<DataSetColumnInfo> ColumnsOf(
        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows) =>
        [.. Columns.Select(name => new DataSetColumnInfo(name,
            [.. rows.Take(3).Select(r => r.GetValueOrDefault(name) ?? "")]))];
}
