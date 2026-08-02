using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Domain.Schema;

namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>
/// Консолидация «Документы комплекта»: строка на каждый документ комплекта, в границах которого
/// живёт набор. Первое применение — реестр документов, но источник обычный: строки можно
/// фильтровать, сортировать, дополнять вычисляемыми колонками и мапить в поля любого типа.
///
/// Реквизиты у типов разные, поэтому «номер», «дата», «кол-во листов» ищутся по функциональным
/// тэгам, а не по именам полей. Незаполненное поле или тип без такого тэга дают пустую ячейку —
/// строка документа не пропадает.
/// </summary>
public class SetDocumentsProvider(
    IDomainObjectRepository objects,
    IRepository<DocumentType> types) : ISystemDataProvider
{
    private const string Name = "Документы комплекта";

    /// <summary>Колонки в порядке отображения. Значения — строки, как и у всех источников.</summary>
    private static readonly string[] Columns =
    [
        "НомерПП", "Ид", "Наименование", "ТипКод", "ТипИмя", "Группа", "Статус",
        "НомерДокумента", "ДатаДокумента", "ДатаГенерации", "КоличествоЛистов", "ПорядокВКомплекте",
    ];

    public bool Handles(string marker) => marker == SystemDataSets.SetDocumentsMarker;

    public async Task<IReadOnlyList<DataSetSourceInfo>> GetCandidatesAsync(
        CatalogScope scope, Guid? scopeId, CancellationToken ct)
    {
        if (scope != CatalogScope.Set || scopeId is null) return [];
        var rows = await RowsAsync(scopeId.Value, ct);
        return [new DataSetSourceInfo(Name, SystemDataSets.SetDocumentsMarker, ColumnsOf(rows), rows.Count)];
    }

    public async Task<DataSetParseResult> ProvideAsync(
        string marker, CatalogScope scope, Guid? scopeId, CancellationToken ct)
    {
        if (scope != CatalogScope.Set || scopeId is null)
            throw new ArgumentException("Источник «Документы комплекта» доступен только у набора уровня «Комплект».");

        var rows = await RowsAsync(scopeId.Value, ct);
        return new DataSetParseResult(ColumnsOf(rows), rows);
    }

    private async Task<List<IReadOnlyDictionary<string, string?>>> RowsAsync(Guid setId, CancellationToken ct)
    {
        var documents = (await objects.GetSetDocumentsAsync(setId, tracked: false, ct))
            .OrderBy(d => d.SortOrder).ToList();
        if (documents.Count == 0) return [];

        var allTypes = (await types.GetAllAsync(ct)).ToList();
        var typeById = allTypes.ToDictionary(t => t.Id);

        // Ключ тэгированного поля зависит от ТИПА документа (у каждого своя схема), поэтому карта
        // «тип → ключи» строится один раз на тип, а не на документ.
        var taggedKeys = new Dictionary<Guid, Dictionary<string, string>>();
        Dictionary<string, string> KeysOf(Guid typeId)
        {
            if (taggedKeys.TryGetValue(typeId, out var cached)) return cached;
            var map = new Dictionary<string, string>();
            if (typeById.TryGetValue(typeId, out var type))
                foreach (var (key, tag) in SchemaTags.TaggedFields(type, allTypes))
                    map.TryAdd(tag, key); // ближний тип в цепочке наследования уже победил
            taggedKeys[typeId] = map;
            return map;
        }

        var rows = new List<IReadOnlyDictionary<string, string?>>(documents.Count);
        var ordinal = 1;
        foreach (var doc in documents)
        {
            var type = typeById.GetValueOrDefault(doc.CompositeTypeId);
            var keys = KeysOf(doc.CompositeTypeId);

            string? Tagged(string tag) =>
                keys.TryGetValue(tag, out var key) ? ScalarOf(doc.Data, key) : null;

            rows.Add(new Dictionary<string, string?>
            {
                ["НомерПП"] = ordinal.ToString(),
                ["Ид"] = doc.Id.ToString(),
                // Документ без своего имени показывается именем типа — как в сборке комплекта.
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
            });
            ordinal++;
        }
        return rows;
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
