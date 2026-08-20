using BHS.CRG.Application.DataSets;
using BHS.CRG.Infrastructure.Recognition;

namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>
/// Собирает единую постраничную группировку (<see cref="GostGroupingData"/>) из результата
/// маршрутизации <see cref="GostPageGrouper"/> и построчно распознанных полей: обложка/титул/
/// документы становятся группами с <see cref="GostGroupKind"/>, каждая страница хранит свои
/// распознанные поля (для проекции без потерь при ручной правке).
/// </summary>
public static class GostUnifiedGroupingBuilder
{
    /// <param name="pagesWithoutAnswer">
    /// Индексы листов, по которым ответа не было (issue #803). Пустая строка в <paramref name="rows" />
    /// сама по себе об этом не говорит: так же выглядит лист, на котором модель честно не нашла штампа.
    /// </param>
    public static GostGroupingData Build(
        GostPageGroupingResult result,
        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows,
        bool manuallyEdited,
        IReadOnlySet<int>? pagesWithoutAnswer = null)
    {
        var silent = pagesWithoutAnswer ?? new HashSet<int>();
        var groups = new List<GostGroupingGroup>();
        if (result.Cover.Count > 0)
            groups.Add(new GostGroupingGroup(GostGroupKind.Cover, null, null, MarkSilent(result.Cover, silent)));
        if (result.TitlePage.Count > 0)
            groups.Add(new GostGroupingGroup(GostGroupKind.TitlePage, null, null, MarkSilent(result.TitlePage, silent)));
        foreach (var doc in result.Documents)
        {
            var pages = doc.PageIndices
                .Select(idx => new GostGroupingPage(idx, StripPerPage(rows[idx]), silent.Contains(idx)))
                .ToList();
            var name = doc.Fields.GetValueOrDefault("НаименованиеДокумента");
            // Авто-подсказка тэга типа таблицы по наименованию (пользователь правит в редакторе).
            var tags = GostDocumentTagger.DetectTableTag(name) is { } tag ? new[] { tag } : null;
            groups.Add(new GostGroupingGroup(GostGroupKind.Document, doc.Code, name, pages, tags));
        }
        return new GostGroupingData(groups, manuallyEdited);
    }

    /// <summary>Обложка и титул приходят готовыми страницами — признак «ответа не было» проставляем им тем же правилом.</summary>
    private static List<GostGroupingPage> MarkSilent(IReadOnlyList<GostGroupingPage> pages, IReadOnlySet<int> silent)
        => pages.Select(p => p.NoAnswer || !silent.Contains(p.PageIndex) ? p : p with { NoAnswer = true }).ToList();

    /// <summary>Убирает служебные классификаторы (как это делает GostPageGrouper), а на листах формы 6
    /// — и НаименованиеДокумента (по ГОСТ его там нет). Public — переиспользуется точечным
    /// перераспознаванием документа (RecognizeDocumentAsync).</summary>
    public static Dictionary<string, string?> StripPerPage(IReadOnlyDictionary<string, string?> row)
    {
        var copy = new Dictionary<string, string?>(row);
        var isForm6 = copy.GetValueOrDefault(GostTitleBlockFields.StampFormPath) == "Форма6";
        copy.Remove(GostTitleBlockFields.PageTypePath);
        copy.Remove(GostTitleBlockFields.StampFormPath);
        if (isForm6) copy.Remove("НаименованиеДокумента");
        return copy;
    }
}
