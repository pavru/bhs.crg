using System.Text.Json;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Schema;
using BHS.CRG.Infrastructure.Generation;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>
/// Консолидация «Материалы и документы качества»: строка на каждую ПОБЕДИВШУЮ связку материала с
/// сертификатом — то, что подставится в документ, живущий НА УРОВНЕ ЭТОГО НАБОРА.
///
/// Победитель определяется общей цепочкой <see cref="MaterialQualityChain"/> (issue #624), той же,
/// что у резолвера при выпуске и у среза для внешнего агента: узкий уровень выигрывает у широкого.
/// Показать здесь другой набор связок значило бы дать человеку карту, по которой он проверяет
/// документ, расходящуюся с самим документом.
///
/// Уровень набора и уровень документа — не одно и то же, и совпадают они не всегда. Набор уровня
/// «Стройка» можно привязать к документу комплекта, и тогда таблица покажет победителей СТРОЙКИ, а
/// в PDF попадут победители комплекта: связка комплекта уже, и при выпуске она перебьёт строечную.
/// Обещание «то же, что в документе» строго верно для набора уровня «Комплект»; выше по оси таблица
/// отвечает на свой вопрос — «что действует на этом уровне» — и колонка «УровеньСвязки» это говорит.
///
/// В комплекты поддерева при этом не спускаемся: связка действует «отсюда и выше», и набор уровня
/// «Стройка» показывает связки стройки и системы, а не сумму всех своих комплектов.
/// </summary>
public class MaterialQualityProvider(AppDbContext db) : ISystemDataProvider
{
    private const string Name = "Материалы и документы качества";

    /// <summary>Документ связки удалён — строку не прячем: связка существует и требует внимания.</summary>
    private const string DeletedDocument = "(документ удалён)";

    private static readonly string[] Columns =
    [
        "НомерПП", "Материал", "КлючМатериала", "ИдДокумента", "ДокументНаименование", "ТипИмя",
        "НомерДокумента", "ДатаДокумента", "СрокДействия", "Изготовитель", "УровеньСвязки", "ЕстьСкан",
    ];

    public bool Handles(string marker) => marker == SystemDataSets.MaterialQualityMarker;

    public async Task<IReadOnlyList<DataSetSourceInfo>> GetCandidatesAsync(
        CatalogScope scope, Guid? scopeId, CancellationToken ct)
    {
        if (scope != CatalogScope.System && scopeId is null) return [];
        var rows = await RowsAsync(scope, scopeId, ct);
        return rows.Count == 0
            ? []
            : [new DataSetSourceInfo(Name, SystemDataSets.MaterialQualityMarker, ColumnsOf(rows), rows.Count)];
    }

    public async Task<DataSetParseResult> ProvideAsync(
        string marker, CatalogScope scope, Guid? scopeId, CancellationToken ct)
    {
        if (scope != CatalogScope.System && scopeId is null)
            throw new ArgumentException(
                $"Источнику «{Name}» нужен уровень с объектом: комплект, раздел или стройка.");

        var rows = await RowsAsync(scope, scopeId, ct);
        return new DataSetParseResult(ColumnsOf(rows), rows);
    }

    private async Task<List<IReadOnlyDictionary<string, string?>>> RowsAsync(
        CatalogScope scope, Guid? scopeId, CancellationToken ct)
    {
        var winners = await MaterialQualityChain.WinnersAsync(db, scope, scopeId, ct);
        if (winners.Count == 0) return [];

        var docIds = winners.Values.Select(w => w.QualityDocumentId).Distinct().ToList();
        var docs = (await db.QualityDocuments.AsNoTracking()
                .Where(d => docIds.Contains(d.Id))
                .ToListAsync(ct))
            .ToDictionary(d => d.Id);

        var allTypes = await db.DocumentTypes.AsNoTracking().ToListAsync(ct);
        var typeById = allTypes.ToDictionary(t => t.Id);

        // Ключ тэгированного поля зависит от типа документа качества — карта строится на тип.
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

        // Порядок — по имени материала: артикульные семейства встают рядом, и чужак виден глазом.
        var ordered = winners.Values
            .OrderBy(w => w.MaterialLabel ?? w.MaterialKey, StringComparer.CurrentCulture)
            .ThenBy(w => w.MaterialKey, StringComparer.Ordinal)
            .ToList();

        var rows = new List<IReadOnlyDictionary<string, string?>>(ordered.Count);
        var ordinal = 1;
        foreach (var w in ordered)
        {
            var doc = docs.GetValueOrDefault(w.QualityDocumentId);
            var type = doc is null ? null : typeById.GetValueOrDefault(doc.DocumentTypeId);
            var keys = doc is null ? [] : KeysOf(doc.DocumentTypeId);

            string? Tagged(string tag) =>
                doc is not null && keys.TryGetValue(tag, out var key) ? ScalarOf(doc.Requisites, key) : null;

            rows.Add(new Dictionary<string, string?>
            {
                ["НомерПП"] = ordinal.ToString(),
                // Метка — снимок имени на момент привязки (#554); у старых связок её нет, и тогда
                // единственное, чем материал назван, — машинный ключ.
                ["Материал"] = w.MaterialLabel ?? w.MaterialKey,
                ["КлючМатериала"] = w.MaterialKey,
                ["ИдДокумента"] = w.QualityDocumentId.ToString(),
                ["ДокументНаименование"] = doc?.DisplayName ?? DeletedDocument,
                ["ТипИмя"] = type?.Name,
                ["НомерДокумента"] = Tagged(FunctionalTag.DocNumber),
                ["ДатаДокумента"] = Tagged(FunctionalTag.DocDate),
                ["СрокДействия"] = Tagged(FunctionalTag.QualityValidUntil),
                ["Изготовитель"] = Tagged(FunctionalTag.QualityManufacturer),
                ["УровеньСвязки"] = ScopeLabel(w.Scope),
                ["ЕстьСкан"] = doc is null ? "нет" : string.IsNullOrWhiteSpace(doc.ScanBlobPath) ? "нет" : "да",
            });
            ordinal++;
        }
        return rows;
    }

    /// <summary>Значение реквизита как строка. Составное/массив — не ячейка таблицы, пропускаем.</summary>
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

    private static string ScopeLabel(CatalogScope scope) => scope switch
    {
        CatalogScope.Set => "Комплект",
        CatalogScope.Section => "Раздел",
        CatalogScope.Construction => "Стройка",
        CatalogScope.System => "Система",
        _ => scope.ToString(),
    };

    private static IReadOnlyList<DataSetColumnInfo> ColumnsOf(
        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows) =>
        [.. Columns.Select(name => new DataSetColumnInfo(name,
            [.. rows.Take(3).Select(r => r.GetValueOrDefault(name) ?? "")]))];
}
