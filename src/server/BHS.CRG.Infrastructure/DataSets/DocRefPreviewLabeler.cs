using BHS.CRG.Application.Schema;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>
/// Ячейки <c>doc-ref</c>-полей в предпросмотрах — наименованием документа, а не идентификатором
/// (issue #715, пункт проверки; #725).
///
/// <para>Сырой GUID человеку не говорит ничего, а главное — по нему НЕ ВИДНО, разрешится ли ссылка:
/// удалённый документ выглядит точно так же, как рабочий, и расходятся они только при генерации.
/// Условия разрешимости здесь те же, что у резолвера (см. <see cref="MaterializeByIdMode.ResolveLabelsAsync"/>).</para>
///
/// <para>Одно место на оба предпросмотра — материализации (диалог) и привязки (вкладка данных):
/// это два экрана про одни и те же данные, и разойдись они, второй показывал бы исправной ссылку,
/// которую первый уже назвал битой.</para>
/// </summary>
public static class DocRefPreviewLabeler
{
    /// <summary>
    /// Заменяет в строках предпросмотра значения <c>doc-ref</c>-полей типа <paramref name="rowTypeId"/>
    /// на «🔗 наименование» либо на объяснение, почему ссылка не развернётся. Значения, не
    /// разобравшиеся в идентификатор, остаются как есть: «в колонке не то» видно только так
    /// (философия issue #466), и подменять это на «не найден» значило бы назвать другую беду.
    /// </summary>
    public static async Task LabelAsync(
        AppDbContext db, IReadOnlyList<Dictionary<string, object?>> rows,
        Guid? rowTypeId, Guid? setId, CancellationToken ct)
    {
        if (rows.Count == 0 || rowTypeId is not { } typeId) return;

        var typesById = await db.DocumentTypes.AsNoTracking().ToDictionaryAsync(t => t.Id, ct);
        if (!typesById.ContainsKey(typeId)) return;

        var docRefKeys = DocumentTypeSchemaReader.EffectiveFields(typeId, typesById)
            .Where(f => f.Type == "doc-ref")
            .Select(f => f.Key)
            .ToHashSet(StringComparer.Ordinal);
        if (docRefKeys.Count == 0) return;

        // Идентификаторы всей страницы разрешаются ОДНИМ запросом — построчный дал бы по запросу на
        // каждый документ реестра.
        var ids = new List<Guid>();
        foreach (var row in rows)
            foreach (var key in docRefKeys)
                if (CellId(row, key) is { } id) ids.Add(id);
        if (ids.Count == 0) return;

        var labels = await MaterializeByIdMode.ResolveLabelsAsync(db, [.. ids.Distinct()], setId, ct);
        foreach (var row in rows)
            foreach (var key in docRefKeys)
                if (CellId(row, key) is { } id)
                    row[key] = labels.TryGetValue(id, out var label)
                        ? MaterializeByIdMode.IsProblem(label) ? label : $"🔗 {label}"
                        : MaterializeByIdMode.NotFoundLabel;
    }

    private static Guid? CellId(Dictionary<string, object?> row, string key)
        => row.TryGetValue(key, out var v) && v is string s && Guid.TryParse(s.Trim(), out var id)
            ? id
            : null;
}
