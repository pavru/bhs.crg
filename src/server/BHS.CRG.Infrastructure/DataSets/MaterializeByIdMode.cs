using BHS.CRG.Domain.Catalog;
using BHS.CRG.Infrastructure.Common;
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
        // Колонки нет вовсе — это ДРУГАЯ беда, чем пустая ячейка, и чинится она иначе: файл
        // перезалили с переименованным заголовком, и искать надо колонку, а не пустоты в данных.
        // Свалив их в одну причину, мы отправили бы человека проверять ячейки, которых нет.
        if (!row.TryGetValue(column, out var cell))
            return (null, MaterializeSkipReason.RefIdColumnMissing, null);

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

    /// <summary>Объект по Ид существует, но ссылка на него НЕ развернётся: это запись общих данных,
    /// а не документ комплекта.</summary>
    public const string NotADocumentLabel = "не документ комплекта — ссылка не развернётся";

    /// <summary>Документ есть, но живёт в другом комплекте — резолвер его не возьмёт.</summary>
    public const string ForeignSetLabel = "документ другого комплекта — ссылка не развернётся";

    /// <summary>Документ качества есть, но вне области видимости комплекта (issue #733): библиотека
    /// качества видна по цепочке System → стройка → раздел → комплект, и чужая ветка в неё не входит.</summary>
    public const string ForeignScopeQualityLabel = "документ качества вне области видимости — ссылка не развернётся";

    /// <summary>Метка объясняет отказ, а не называет документ (значку «🔗» рядом с ней не место).</summary>
    public static bool IsProblem(string label)
        => label == NotFoundLabel || label == NotADocumentLabel || label == ForeignSetLabel
           || label == ForeignScopeQualityLabel;

    /// <summary>
    /// Как предпросмотру назвать каждый идентификатор — ОДНИМ запросом на страницу строк (реестр на
    /// сотню документов иначе дал бы сотню).
    ///
    /// <para><b>Условия те же, что у резолвера</b> (<c>EntityResolver.ResolveDocumentInstanceAsync</c>):
    /// разворачивается только документ (есть фасета), лежащий в ТОМ ЖЕ комплекте. Показывать
    /// наименование по более широкому запросу — значит рисовать исправной ссылку, которая при
    /// генерации останется висячей, а сканер объявит её удалённой записью. Ровно эту ложь
    /// предпросмотра режим и должен снимать, поэтому «чужой» назван чужим, а не найденным.</para>
    ///
    /// <paramref name="setId"/> — комплект, в котором ссылка будет разворачиваться; null означает
    /// «неизвестен» (источник живёт выше комплекта и используется в разных): тогда проверяем только
    /// то, что проверить можно, — что объект вообще документ.
    ///
    /// <para><b>Каскад доменов повторяет резолвер</b> (issue #733): идентификаторы, не найденные
    /// среди документов комплекта, ищутся в библиотеке документов качества — второй домен
    /// instance-ссылки. Не повтори превью каскад, оно объявляло бы «не найден» ровно те ссылки,
    /// которые генерация разворачивает штатно, — та самая ложь предпросмотра, которую этот класс и
    /// снимает, только в другую сторону.</para>
    /// </summary>
    public static async Task<Dictionary<Guid, string>> ResolveLabelsAsync(
        AppDbContext db, IReadOnlyCollection<Guid> ids, Guid? setId, CancellationToken ct)
    {
        if (ids.Count == 0) return [];

        var docs = await db.DomainObjects.AsNoTracking()
            .Where(o => ids.Contains(o.Id))
            .Select(o => new { o.Id, o.DisplayName, o.CompositeTypeId, o.ScopeLevel, o.ScopeId, IsDocument = o.Facet != null })
            .ToListAsync(ct);

        var typeIds = docs.Select(d => d.CompositeTypeId).Distinct().ToList();
        var typeNames = await db.DocumentTypes.AsNoTracking()
            .Where(t => typeIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Name, ct);

        var labels = docs.ToDictionary(d => d.Id, d =>
            !d.IsDocument ? NotADocumentLabel
            : setId is { } sid && (d.ScopeLevel != CatalogScope.Set || d.ScopeId != sid) ? ForeignSetLabel
            : d.DisplayName ?? typeNames.GetValueOrDefault(d.CompositeTypeId) ?? "Документ");

        await AddQualityLabelsAsync(db, [.. ids.Where(id => !labels.ContainsKey(id))], setId, labels, ct);
        return labels;
    }

    /// <summary>
    /// Второй проход каскада — документы качества (issue #733). Отдельным запросом и только по
    /// промахам: страница реестра со ссылками на документы комплекта дополнительного запроса не
    /// получает вовсе.
    ///
    /// <para>Область видимости считается тем же <c>ScopeChain</c>, что и в резолвере. При неизвестном
    /// комплекте (<paramref name="setId"/> = null) цепочки не существует — тогда, как и для
    /// документов комплекта выше, проверяем лишь то, что проверить можно: что запись есть.</para>
    /// </summary>
    private static async Task AddQualityLabelsAsync(
        AppDbContext db, IReadOnlyList<Guid> missing, Guid? setId,
        Dictionary<Guid, string> labels, CancellationToken ct)
    {
        if (missing.Count == 0) return;

        var quality = await db.QualityDocuments.AsNoTracking()
            .Where(q => missing.Contains(q.Id))
            .Select(q => new { q.Id, q.DisplayName, q.Scope, q.ScopeId })
            .ToListAsync(ct);
        if (quality.Count == 0) return;

        ScopeChain? chain = setId is { } sid ? await ScopeChains.LoadAsync(db, sid, ct) : null;
        foreach (var q in quality)
            labels[q.Id] = chain is { } c && !c.Contains(q.Scope, q.ScopeId)
                ? ForeignScopeQualityLabel
                : q.DisplayName;
    }
}
