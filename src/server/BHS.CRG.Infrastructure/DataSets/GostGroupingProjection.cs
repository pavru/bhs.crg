using BHS.CRG.Application.DataSets;

namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>Строки одного документа-проекции: стабильный id + код/имя/страницы + агрегированные поля.
/// Fields несёт ФайлПуть/РазмерБайт, если у группы уже вырезан под-PDF (issue #38 — блоб режется при
/// распознавании и лежит в Grouping). Id — стабильный ключ группы (для источника-таблицы gost-table:{id}).</summary>
public record ProjectedDocument(
    Guid Id, string Code, string? Name, IReadOnlyList<int> PageIndices, Dictionary<string, string?> Fields);

/// <summary>Результат проекции единой группировки в строки трёх источников ГОСТ-профиля.</summary>
public record ProjectedRows(
    IReadOnlyList<Dictionary<string, string?>> Cover,
    IReadOnlyList<Dictionary<string, string?>> TitlePage,
    IReadOnlyList<ProjectedDocument> Documents);

/// <summary>
/// Чистая проекция единой постраничной группировки (<see cref="GostGroupingData"/>) в строки трёх
/// источников (обложка/титул/документы). Единственная точка агрегации полей: обложка/титул — по
/// одной строке на страницу (её поля как есть); документ — агрегат «первое непустое значение поля»
/// по страницам группы + КоличествоЛистов. Без I/O — разрезание PDF (ФайлПуть/РазмерБайт) снаружи.
/// Вызывается И при автораспознавании, И при ручной правке — устраняет прежнее расхождение
/// «документы пересобираются, обложка нет».
/// </summary>
public static class GostGroupingProjection
{
    public static ProjectedRows Project(GostGroupingData grouping)
    {
        var cover = new List<Dictionary<string, string?>>();
        var titlePage = new List<Dictionary<string, string?>>();
        var documents = new List<ProjectedDocument>();

        foreach (var group in grouping.Groups)
        {
            switch (group.Kind)
            {
                case GostGroupKind.Cover:
                    foreach (var p in group.Pages) cover.Add(new Dictionary<string, string?>(p.Fields));
                    break;
                case GostGroupKind.TitlePage:
                    foreach (var p in group.Pages) titlePage.Add(new Dictionary<string, string?>(p.Fields));
                    break;
                default: // Document
                    var fields = AggregateFirstNonEmpty(group.Pages);
                    fields["КоличествоЛистов"] = group.Pages.Count.ToString();
                    // Имя группы авторитетно, если в полях страниц наименования нет (напр. маркер
                    // «Некорректная форма 6», у которого страница формы 6 без своего наименования).
                    if (!string.IsNullOrEmpty(group.Name) && string.IsNullOrEmpty(fields.GetValueOrDefault("НаименованиеДокумента")))
                        fields["НаименованиеДокумента"] = group.Name;
                    // ФайлПуть/РазмерБайт — из вырезанного при распознавании под-PDF группы (issue #38);
                    // раньше добавлялись снаружи в кэш источника, теперь несёт сама группировка.
                    if (!string.IsNullOrEmpty(group.BlobPath))
                    {
                        fields["ФайлПуть"] = group.BlobPath;
                        fields["РазмерБайт"] = group.BlobSize?.ToString() ?? "";
                    }
                    documents.Add(new ProjectedDocument(
                        group.Id, group.Code ?? "", group.Name, group.Pages.Select(p => p.PageIndex).ToList(), fields));
                    break;
            }
        }

        return new ProjectedRows(cover, titlePage, documents);
    }

    /// <summary>Первое непустое значение каждого поля по страницам группы (порядок страниц важен).</summary>
    private static Dictionary<string, string?> AggregateFirstNonEmpty(IReadOnlyList<GostGroupingPage> pages)
    {
        var result = new Dictionary<string, string?>();
        foreach (var page in pages)
            foreach (var (k, v) in page.Fields)
                if (!string.IsNullOrEmpty(v) && !result.ContainsKey(k))
                    result[k] = v;
        return result;
    }
}
