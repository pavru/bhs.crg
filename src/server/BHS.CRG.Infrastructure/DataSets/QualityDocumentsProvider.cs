using System.Text.Json;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Schema;
using BHS.CRG.Infrastructure.Common;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>
/// Консолидация «Документы качества»: строка на каждый документ библиотеки, ВИДИМЫЙ с уровня
/// набора — по цепочке вверх (свой уровень → раздел → стройка → система). Тот же предикат
/// видимости, что у резолвера связок при генерации (<c>QualityLinkResolver</c>): иначе набор
/// показывал бы одно, а в PDF подставлялось другое.
///
/// Реквизиты у подтипов разные (сертификат, декларация, паспорт), поэтому номер, дата, срок и
/// изготовитель ищутся по функциональным тэгам, а не по именам полей. Незаполненное поле или тип
/// без тэга дают пустую ячейку — строка документа при этом не пропадает.
///
/// Тип берётся из реестра типов по <c>DocumentTypeId</c>, а НЕ из реквизита «ТипДокумента»: на
/// живой базе реквизит противоречит собственному типу документа (см. комментарий в
/// <c>QualityDocHandlers.Handle(ListMaterialLinksQuery)</c>), и доверять надо тому, что определяет
/// схему.
/// </summary>
public class QualityDocumentsProvider(AppDbContext db) : ISystemDataProvider
{
    private const string Name = "Документы качества";

    /// <summary>Колонки в порядке отображения. Значения — строки, как и у всех источников.</summary>
    private static readonly string[] Columns =
    [
        "НомерПП", "Ид", "Наименование", "ТипКод", "ТипИмя",
        "НомерДокумента", "ДатаДокумента", "СрокДействия", "Изготовитель",
        "Уровень", "Источник", "ЕстьСкан", "ИмяФайлаСкана",
    ];

    public bool Handles(string marker) => marker == SystemDataSets.QualityDocumentsMarker;

    public async Task<IReadOnlyList<DataSetSourceInfo>> GetCandidatesAsync(
        CatalogScope scope, Guid? scopeId, CancellationToken ct)
    {
        // Уровни все, но кандидата предлагаем только при живых строках: иначе кнопка «Данные
        // системы» появлялась бы и там, где модулем качества не пользуются вовсе.
        if (scope != CatalogScope.System && scopeId is null) return [];
        var rows = await RowsAsync(scope, scopeId, ct);
        return rows.Count == 0
            ? []
            : [new DataSetSourceInfo(Name, SystemDataSets.QualityDocumentsMarker, ColumnsOf(rows), rows.Count)];
    }

    public async Task<DataSetParseResult> ProvideAsync(
        string marker, CatalogScope scope, Guid? scopeId, CancellationToken ct)
    {
        // Единственный вид отказа — ArgumentException: KeyNotFoundException из провайдера уронил бы
        // весь список наборов, а не одну строку в нём.
        if (scope != CatalogScope.System && scopeId is null)
            throw new InvalidRequestException(
                $"Источнику «{Name}» нужен уровень с объектом: комплект, раздел или стройка.");

        var rows = await RowsAsync(scope, scopeId, ct);
        return new DataSetParseResult(ColumnsOf(rows), rows);
    }

    private async Task<List<IReadOnlyDictionary<string, string?>>> RowsAsync(
        CatalogScope scope, Guid? scopeId, CancellationToken ct)
    {
        // Цепочка вверх от места набора: с комплекта видны и его раздел, и стройка, и система.
        var chain = await ScopeChains.LoadForScopeAsync(db, scope, scopeId, ct);

        var visible = await db.QualityDocuments.AsNoTracking()
            .Where(d => d.Scope == CatalogScope.System
                        || (d.Scope == CatalogScope.Set && d.ScopeId == chain.SetId)
                        || (d.Scope == CatalogScope.Section && d.ScopeId == chain.SectionId)
                        || (d.Scope == CatalogScope.Construction && d.ScopeId == chain.ConstructionId))
            .OrderBy(d => d.DisplayName).ThenBy(d => d.Id)   // как в ListQualityDocumentsQuery
            .ToListAsync(ct);
        if (visible.Count == 0) return [];

        var allTypes = await db.DocumentTypes.AsNoTracking().ToListAsync(ct);
        var typeById = allTypes.ToDictionary(t => t.Id);

        // Ключ тэгированного поля зависит от ТИПА (у каждого своя схема) — карта строится на тип,
        // а не на документ.
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

        var rows = new List<IReadOnlyDictionary<string, string?>>(visible.Count);
        var ordinal = 1;
        foreach (var doc in visible)
        {
            var type = typeById.GetValueOrDefault(doc.DocumentTypeId);
            var keys = KeysOf(doc.DocumentTypeId);

            string? Tagged(string tag) =>
                keys.TryGetValue(tag, out var key) ? ScalarOf(doc.Requisites, key) : null;

            rows.Add(new Dictionary<string, string?>
            {
                ["НомерПП"] = ordinal.ToString(),
                ["Ид"] = doc.Id.ToString(),
                ["Наименование"] = doc.DisplayName,
                ["ТипКод"] = type?.Code,
                ["ТипИмя"] = type?.Name,
                ["НомерДокумента"] = Tagged(FunctionalTag.DocNumber),
                ["ДатаДокумента"] = Tagged(FunctionalTag.DocDate),
                ["СрокДействия"] = Tagged(FunctionalTag.QualityValidUntil),
                ["Изготовитель"] = Tagged(FunctionalTag.QualityManufacturer),
                ["Уровень"] = ScopeLabel(doc.Scope),
                ["Источник"] = SourceLabel(doc.Source),
                ["ЕстьСкан"] = string.IsNullOrWhiteSpace(doc.ScanBlobPath) ? "нет" : "да",
                ["ИмяФайлаСкана"] = doc.ScanFileName,
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

    private static string SourceLabel(QualityDocSource source) => source switch
    {
        QualityDocSource.Manual => "Вручную",
        QualityDocSource.Fgis => "ФГИС",
        QualityDocSource.Manufacturer => "Изготовитель",
        QualityDocSource.Web => "Веб",
        _ => source.ToString(),
    };

    private static IReadOnlyList<DataSetColumnInfo> ColumnsOf(
        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows) =>
        [.. Columns.Select(name => new DataSetColumnInfo(name,
            [.. rows.Take(3).Select(r => r.GetValueOrDefault(name) ?? "")]))];
}
