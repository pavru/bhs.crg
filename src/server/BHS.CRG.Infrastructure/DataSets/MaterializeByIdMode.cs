using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>
/// Материализация «существующий документ по Ид» (issue #725): строка источника целиком становится
/// ссылкой <c>{"$ref":"instance","instanceId":…}</c> на уже существующий документ, а не объектом,
/// собранным из колонок. Живые данные подставляет второй проход <c>EntityResolver</c>.
///
/// <para>Режим нужен там, где ссылаться надо НЕ полем: у типа-документа (напр. «Протокол измерения
/// сопротивления изоляции») полей <c>doc-ref</c> нет, а реестр обязан ссылаться на сам документ.
/// Маппинг <c>doc-ref</c>-полей (issue #715) этот случай не покрывает.</para>
///
/// <para>Одно место на трёх потребителей — генерацию, предпросмотр привязки и предпросмотр
/// материализации: разойдись чтение колонки хоть в одном, и экран показывал бы не то, что уедет в
/// документ. Ровно это уже случалось с <c>@@ref</c> до issue #374.</para>
/// </summary>
public static class MaterializeByIdMode
{
    /// <summary>Режим включён — колонка задана. Пустая строка = режима нет (флаг и есть колонка).</summary>
    public static bool IsOn(string? column) => !string.IsNullOrWhiteSpace(column);

    /// <summary>Идентификатор документа из строки; при неудаче — код причины (см. <see cref="MaterializeSkipReason"/>)
    /// и само значение ячейки, чтобы предпросмотр мог показать, что именно там лежит.</summary>
    public static (Guid? Id, string? SkipReason, string? Cell) ReadId(
        IReadOnlyDictionary<string, string?> row, string column)
    {
        row.TryGetValue(column, out var cell);

        // Пустая ячейка — «строки в реестре нет», а не битая ссылка: молча положить {$ref} без Ид
        // значило бы выдать несуществующую проблему за существующую (та же идиома, что в #544).
        if (string.IsNullOrWhiteSpace(cell)) return (null, MaterializeSkipReason.RefIdEmpty, cell);

        return Guid.TryParse(cell.Trim(), out var id)
            ? (id, null, cell)
            : (null, MaterializeSkipReason.RefIdNotGuid, cell);
    }

    /// <summary>Ссылка на экземпляр документа — та же форма, что строит <c>DataSetValueCoercion</c>
    /// для <c>doc-ref</c>-поля: обе разворачивает один и тот же проход резолвера.</summary>
    public static Dictionary<string, object?> RefValue(Guid instanceId) => new()
    {
        ["$ref"] = "instance",
        ["instanceId"] = instanceId.ToString(),
    };

    /// <summary>Показывается в предпросмотре вместо наименования, когда документа по Ид нет.</summary>
    public const string NotFoundLabel = "документ не найден";

    /// <summary>
    /// Наименования документов по идентификаторам — ОДНИМ запросом на страницу строк (реестр на сотню
    /// документов иначе дал бы сотню). Отсутствующие в словаре — удалённые или чужие: предпросмотр
    /// обязан отличать их от рабочих, иначе битая ссылка выглядит как исправная ровно до генерации.
    /// </summary>
    public static async Task<Dictionary<Guid, string>> ResolveLabelsAsync(
        AppDbContext db, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];

        var docs = await db.DomainObjects.AsNoTracking()
            .Where(o => ids.Contains(o.Id))
            .Select(o => new { o.Id, o.DisplayName, o.CompositeTypeId })
            .ToListAsync(ct);
        if (docs.Count == 0) return [];

        var typeIds = docs.Select(d => d.CompositeTypeId).Distinct().ToList();
        var typeNames = await db.DocumentTypes.AsNoTracking()
            .Where(t => typeIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Name, ct);

        return docs.ToDictionary(
            d => d.Id,
            d => d.DisplayName ?? typeNames.GetValueOrDefault(d.CompositeTypeId) ?? "Документ");
    }
}
