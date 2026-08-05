using System.Text.Json;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Objects;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Infrastructure.Common;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>
/// Консолидация «Общие данные: {тип}»: записи выбранного составного типа, видимые с уровня набора,
/// таблицей. Маркер ПАРАМЕТРИЗОВАН — <c>system:objects:{typeId}</c>, по образцу проекций таблиц ГОСТ
/// (<c>gost-table:{id}</c>): типов много, и заводить провайдера на каждый бессмысленно.
///
/// Видно то же, что показывает форма: цепочка вверх от места набора (свой уровень → раздел →
/// стройка → система), сам тип и его подтипы, <c>_baseRef</c> развёрнут. Одноимённые записи разных
/// уровней НЕ схлопываются (решение по эпику #622): какая из них победит при резолве — вопрос
/// другого механизма, а здесь человек должен видеть всё, что есть, и различать колонкой «Уровень».
/// Сузить до одного уровня можно штатным фильтром строк.
/// </summary>
public class DomainObjectsProvider(AppDbContext db) : ISystemDataProvider
{
    /// <summary>
    /// Колонки, которые есть у любой записи независимо от схемы. Имя «ИмяОбъекта», а не
    /// «Наименование», нарочно: «Наименование» — самый частый ключ в схемах, и совпадение имён
    /// заставило бы выбирать, чьё значение показывать.
    /// </summary>
    private static readonly string[] FixedColumns =
    [
        "НомерПП", "Ид", "ИмяОбъекта", "Алиасы", "ТипКод", "ТипИмя", "Уровень",
    ];

    public bool Handles(string marker) => SystemDataSets.TryParseObjectsMarker(marker, out _);

    public async Task<IReadOnlyList<DataSetSourceInfo>> GetCandidatesAsync(
        CatalogScope scope, Guid? scopeId, CancellationToken ct)
    {
        if (scope != CatalogScope.System && scopeId is null) return [];

        // Кандидаты считаются при КАЖДОМ открытии страницы наборов, поэтому строки здесь не
        // собираются: сколько записей каждого типа видно — один групповой запрос, колонки — из
        // схемы без образцов значений. Тип без записей не предлагаем: пустая таблица не выбор.
        var chain = await ScopeChains.LoadForScopeAsync(db, scope, scopeId, ct);
        var counts = await VisibleObjects(chain)
            .GroupBy(o => o.CompositeTypeId)
            .Select(g => new { TypeId = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        if (counts.Count == 0) return [];

        var allTypes = await db.DocumentTypes.AsNoTracking().ToListAsync(ct);
        var byId = allTypes.ToDictionary(t => t.Id);
        var enumsById = await EnumTypesAsync(ct);

        // Записи подтипа принадлежат и предку (DescendantTypeIds), поэтому кандидат предлагается на
        // каждый тип, у которого есть СВОИ записи, а число строк считается по нему и потомкам.
        var byType = counts.ToDictionary(c => c.TypeId, c => c.Count);
        var candidates = new List<DataSetSourceInfo>();
        foreach (var typeId in byType.Keys)
        {
            if (!byId.TryGetValue(typeId, out var type)) continue;
            var rowCount = DescendantTypeIds(typeId, allTypes).Sum(id => byType.GetValueOrDefault(id));
            candidates.Add(new DataSetSourceInfo(
                NameFor(type),
                $"{SystemDataSets.ObjectsMarkerPrefix}{typeId}",
                [.. ColumnNames(typeId, byId, enumsById).Select(name => new DataSetColumnInfo(name, []))],
                rowCount));
        }
        return [.. candidates.OrderBy(c => c.Name, StringComparer.CurrentCulture)];
    }

    public async Task<DataSetParseResult> ProvideAsync(
        string marker, CatalogScope scope, Guid? scopeId, CancellationToken ct)
    {
        if (!SystemDataSets.TryParseObjectsMarker(marker, out var typeId))
            throw new InvalidRequestException($"Маркер «{marker}» не содержит идентификатора типа.");
        if (scope != CatalogScope.System && scopeId is null)
            throw new InvalidRequestException("Источнику общих данных нужен уровень с объектом: комплект, раздел или стройка.");

        var allTypes = await db.DocumentTypes.AsNoTracking().ToListAsync(ct);
        var byId = allTypes.ToDictionary(t => t.Id);
        // Тип удалён — это InvalidRequestException, а НЕ NotFoundException: пересчёт строк в списке
        // наборов глотает только первое, и удалённый тип иначе уронил бы весь список.
        if (!byId.ContainsKey(typeId))
            throw new InvalidRequestException("Тип, по которому собирался источник, удалён — пересоздайте источник.");

        var enumsById = await EnumTypesAsync(ct);
        var chain = await ScopeChains.LoadForScopeAsync(db, scope, scopeId, ct);
        var typeIds = DescendantTypeIds(typeId, allTypes);

        var objects = await VisibleObjects(chain)
            .Where(o => typeIds.Contains(o.CompositeTypeId))
            .ToListAsync(ct);

        var columns = ColumnNames(typeId, byId, enumsById);
        if (objects.Count == 0)
            return new DataSetParseResult([.. columns.Select(n => new DataSetColumnInfo(n, []))], []);

        // Наследование значений (issue #71): запись-наследник показывает то же, что видно в форме,
        // иначе таблица и экран расходились бы на ровном месте.
        var basesById = await LoadBaseChainAsync(objects, ct);

        var fields = ScalarFields(typeId, byId, enumsById)
            .Where(f => !FixedColumns.Contains(f.Key))
            .ToList();
        // Первый вариант с этим кодом побеждает: уникальность кодов внутри перечисления никто не
        // проверяет (а легаси-варианты в схеме и подавно), и падать на дубле — значит уронить
        // предпросмотр, экспорт и живой счётчик строк из-за одной кривой записи справочника.
        var enumLabels = fields
            .Where(f => f.Options is { Count: > 0 })
            .ToDictionary(f => f.Key, f => f.Options!
                .GroupBy(o => o.Code)
                .ToDictionary(g => g.Key, g => g.First().Label));

        var ordered = objects
            .OrderBy(o => o.DisplayName ?? "", StringComparer.CurrentCulture)
            .ThenBy(o => o.Id)
            .ToList();

        var rows = new List<IReadOnlyDictionary<string, string?>>(ordered.Count);
        var ordinal = 1;
        foreach (var obj in ordered)
        {
            var data = EffectiveData(obj, basesById, chain);

            var type = byId.GetValueOrDefault(obj.CompositeTypeId);
            var row = new Dictionary<string, string?>
            {
                ["НомерПП"] = ordinal.ToString(),
                ["Ид"] = obj.Id.ToString(),
                ["ИмяОбъекта"] = obj.DisplayName,
                ["Алиасы"] = obj.Aliases.Count == 0 ? null : string.Join(", ", obj.Aliases),
                ["ТипКод"] = type?.Code,
                ["ТипИмя"] = type?.Name,
                ["Уровень"] = ScopeLabel(obj.ScopeLevel),
            };
            foreach (var f in fields)
            {
                var value = ScalarOf(data, f.Key);
                row[f.Key] = value is not null && enumLabels.TryGetValue(f.Key, out var labels)
                    ? labels.GetValueOrDefault(value, value) // код вне вариантов показываем как есть
                    : value;
            }
            rows.Add(row);
            ordinal++;
        }

        return new DataSetParseResult(ColumnsOf(columns, rows), rows);
    }

    /// <summary>Предел длины цепочки наследования — от патологических данных, не от нормы.</summary>
    private const int MaxBaseDepth = 8;

    /// <summary>
    /// Базы наследования для выборки — ВСЯ цепочка, пакетами: A наследует B, B наследует C, и в
    /// форме видно значения C. Один уровень, как было сначала, показывал бы у внука пустую ячейку
    /// там, где документ печатает значение прадеда.
    /// </summary>
    private async Task<Dictionary<Guid, DomainObject>> LoadBaseChainAsync(
        List<DomainObject> objects, CancellationToken ct)
    {
        var known = new Dictionary<Guid, DomainObject>();
        var wanted = objects
            .Select(o => BaseRefReader.GetBaseRefId(o.Data.RootElement))
            .OfType<Guid>().Distinct().ToList();

        for (var depth = 0; depth < MaxBaseDepth && wanted.Count > 0; depth++)
        {
            var batch = await db.DomainObjects.AsNoTracking().Include(o => o.Facet)
                .Where(o => wanted.Contains(o.Id)).ToListAsync(ct);
            foreach (var o in batch) known[o.Id] = o;

            wanted = [.. batch
                .Select(o => BaseRefReader.GetBaseRefId(o.Data.RootElement))
                .OfType<Guid>().Where(id => !known.ContainsKey(id)).Distinct()];
        }
        return known;
    }

    /// <summary>
    /// Данные записи с учётом наследования — теми же правилами, что у резолвера при выпуске:
    /// цикл рвётся по visited, запись общих данных наследуется только если видна из того же места,
    /// документ-база — только из своего комплекта. Без этих ограничений таблица показала бы
    /// значение, которого в документе не будет.
    /// </summary>
    private static JsonElement EffectiveData(
        DomainObject obj, IReadOnlyDictionary<Guid, DomainObject> bases, ScopeChain chain)
    {
        var visited = new HashSet<Guid> { obj.Id };
        var line = new List<JsonElement> { obj.Data.RootElement };

        var current = obj.Data.RootElement;
        while (line.Count <= MaxBaseDepth
               && BaseRefReader.GetBaseRefId(current) is { } baseId
               && visited.Add(baseId)
               && bases.TryGetValue(baseId, out var baseObj)
               && Inheritable(baseObj, chain))
        {
            current = baseObj.Data.RootElement;
            line.Add(current);
        }

        // Сворачиваем от дальней базы к ближней: своё всегда сильнее унаследованного.
        var effective = line[^1];
        for (var i = line.Count - 2; i >= 0; i--)
            effective = BaseRefReader.MergeObjects(effective, line[i]);
        return effective;
    }

    private static bool Inheritable(DomainObject baseObj, ScopeChain chain) => baseObj.Facet is not null
        ? baseObj.ScopeLevel == CatalogScope.Set && baseObj.ScopeId == chain.SetId // документ — из своего комплекта
        : chain.Contains(baseObj.ScopeLevel, baseObj.ScopeId);                     // запись — из видимого поддерева

    /// <summary>Записи ОБЩИХ ДАННЫХ (Facet == null — документы это не они), видимые с места набора.</summary>
    private IQueryable<DomainObject> VisibleObjects(ScopeChain chain) =>
        db.DomainObjects.AsNoTracking().Where(o => o.Facet == null &&
            ((o.ScopeLevel == CatalogScope.Set && o.ScopeId == chain.SetId) ||
             (o.ScopeLevel == CatalogScope.Section && o.ScopeId == chain.SectionId) ||
             (o.ScopeLevel == CatalogScope.Construction && o.ScopeId == chain.ConstructionId) ||
             o.ScopeLevel == CatalogScope.System));

    private async Task<IReadOnlyDictionary<Guid, EnumType>> EnumTypesAsync(CancellationToken ct) =>
        (await db.EnumTypes.AsNoTracking().ToListAsync(ct)).ToDictionary(t => t.Id);

    private static string NameFor(DocumentType type) => $"Общие данные: {type.Name}";

    /// <summary>Сам тип и его подтипы: запись подтипа — тоже запись этого типа (как у резолвера ссылок).</summary>
    private static HashSet<Guid> DescendantTypeIds(Guid rootId, List<DocumentType> all)
    {
        var result = new HashSet<Guid> { rootId };
        var grew = true;
        while (grew)
        {
            grew = false;
            foreach (var t in all)
                if (t.ParentId is { } p && result.Contains(p) && result.Add(t.Id)) grew = true;
        }
        return result;
    }

    /// <summary>
    /// Скалярные поля эффективной схемы. Расчётные (issue #368) исключены намеренно: считать их
    /// значит гонять Jint на каждую строку списка, а пустая колонка хуже отсутствующей.
    /// </summary>
    private static List<SchemaFieldInfo> ScalarFields(
        Guid typeId, IReadOnlyDictionary<Guid, DocumentType> byId,
        IReadOnlyDictionary<Guid, EnumType> enumsById) =>
        [.. DocumentTypeSchemaReader.EffectiveFields(typeId, byId, enumsById)
            .Where(f => SchemaFieldKinds.IsScalar(f.Type) && !f.Computed)];

    /// <summary>
    /// Имена колонок: фиксированные плюс ключи скалярных полей схемы. Ключ, совпавший с фиксированным
    /// именем, ПРОПУСКАЕТСЯ — иначе значение колонки зависело бы от порядка сборки словаря, а он
    /// деталь реализации.
    /// </summary>
    private static List<string> ColumnNames(
        Guid typeId, IReadOnlyDictionary<Guid, DocumentType> byId,
        IReadOnlyDictionary<Guid, EnumType> enumsById) =>
        [.. FixedColumns,
         .. ScalarFields(typeId, byId, enumsById).Select(f => f.Key).Where(k => !FixedColumns.Contains(k))];

    private static string? ScalarOf(JsonElement data, string key)
    {
        if (data.ValueKind != JsonValueKind.Object) return null;
        if (!data.TryGetProperty(key, out var value)) return null;
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
        IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyDictionary<string, string?>> rows) =>
        [.. columns.Select(name => new DataSetColumnInfo(name,
            [.. rows.Take(3).Select(r => r.GetValueOrDefault(name) ?? "")]))];
}
