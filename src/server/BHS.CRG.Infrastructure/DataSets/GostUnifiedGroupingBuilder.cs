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
    public static GostGroupingData Build(
        GostPageGroupingResult result,
        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows,
        bool manuallyEdited)
    {
        var groups = new List<GostGroupingGroup>();
        if (result.Cover.Count > 0)
            groups.Add(new GostGroupingGroup(GostGroupKind.Cover, null, null, result.Cover));
        if (result.TitlePage.Count > 0)
            groups.Add(new GostGroupingGroup(GostGroupKind.TitlePage, null, null, result.TitlePage));
        foreach (var doc in result.Documents)
        {
            var pages = doc.PageIndices
                .Select(idx => new GostGroupingPage(idx, StripPerPage(rows[idx])))
                .ToList();
            var name = doc.Fields.GetValueOrDefault("НаименованиеДокумента");
            // Авто-подсказка тэга типа таблицы по наименованию (пользователь правит в редакторе).
            var tags = GostDocumentTagger.DetectTableTag(name) is { } tag ? new[] { tag } : null;
            groups.Add(new GostGroupingGroup(GostGroupKind.Document, doc.Code, name, pages, tags));
        }
        return new GostGroupingData(groups, manuallyEdited);
    }

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
