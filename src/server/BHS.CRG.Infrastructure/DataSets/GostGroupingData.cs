namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>
/// Сериализуемое содержимое <see cref="Domain.DataSets.DataSetSource.GostGrouping"/> (JSONB) —
/// группировка страниц источника "Документы" ГОСТ-профиля по документам. Персистентность
/// маппинга страница→документ — задел под ручную корректировку разбиения: пользователь может
/// перенести страницу между документами/разделить/объединить группы, применить (PUT
/// .../grouping) без повторного вызова vision-LLM (см. архитектурный отчёт, «Ручная
/// корректировка разбиения PDF»).
/// </summary>
/// <param name="Documents">Группы страниц, в порядке появления.</param>
/// <param name="ManuallyEdited">
/// true — пользователь применил ручную правку через PUT .../grouping. Повторное автораспознавание
/// затирает это состояние (frontend обязан спросить подтверждение — см. 409 Conflict в
/// эндпоинте, если ManuallyEdited=true и запрос не содержит confirm=true).
/// </param>
public record GostGroupingData(IReadOnlyList<GostGroupingDocument> Documents, bool ManuallyEdited);

/// <param name="Code">Шифр документа (или "(без шифра)").</param>
/// <param name="Name">Наименование документа, если распознано — может быть null.</param>
/// <param name="PageIndices">Индексы страниц исходного PDF (0-based), входящих в документ.</param>
public record GostGroupingDocument(string Code, string? Name, IReadOnlyList<int> PageIndices);
