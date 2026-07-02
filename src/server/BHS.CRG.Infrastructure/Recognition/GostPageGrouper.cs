namespace BHS.CRG.Infrastructure.Recognition;

/// <summary>Одна распознанная группа документа — несколько исходных страниц с общим НаименованиемДокумента.</summary>
public record GostDocumentGroup(
    string DocumentName, IReadOnlyList<int> PageIndices, IReadOnlyDictionary<string, string?> Fields);

/// <summary>Результат маршрутизации распознанных страниц по типу (см. GostTitleBlockFields.PageTypeField).</summary>
public record GostPageGroupingResult(
    IReadOnlyList<IReadOnlyDictionary<string, string?>> Cover,
    IReadOnlyList<IReadOnlyDictionary<string, string?>> TitlePage,
    IReadOnlyList<GostDocumentGroup> Documents);

/// <summary>
/// Чистая логика маршрутизации и группировки постранично распознанных строк (см.
/// GostTitleBlockFields.AllWithPageType) — вынесена отдельно от DataSetService ради
/// юнит-тестируемости без БД/blob/LLM. Никогда не бросает — некорректный/отсутствующий
/// ТипСтраницы трактуется как "Документ" (см. правило "не роняем строку" из BuildTitleBlockPrompt).
/// </summary>
public static class GostPageGrouper
{
    private const string DefaultDocumentName = "(без названия)";

    public static GostPageGroupingResult Group(IReadOnlyList<IReadOnlyDictionary<string, string?>> pages)
    {
        var cover = new List<IReadOnlyDictionary<string, string?>>();
        var titlePage = new List<IReadOnlyDictionary<string, string?>>();

        // Порядок появления имён документов сохраняется (Dictionary в .NET сохраняет порядок вставки
        // на практике, но полагаться на это не будем — ведём отдельный список порядка).
        var order = new List<string>();
        var byName = new Dictionary<string, (List<int> Pages, Dictionary<string, string?> Fields)>();

        for (var i = 0; i < pages.Count; i++)
        {
            var page = pages[i];
            var pageType = page.GetValueOrDefault(GostTitleBlockFields.PageTypePath);
            var withoutPageType = Strip(page, GostTitleBlockFields.PageTypePath);

            if (pageType == "Обложка") { cover.Add(withoutPageType); continue; }
            if (pageType == "ТитульныйЛист") { titlePage.Add(withoutPageType); continue; }

            // "Документ", пусто или что-то неожиданное от модели — всё считаем документом.
            var name = page.GetValueOrDefault("НаименованиеДокумента");
            var key = string.IsNullOrWhiteSpace(name) ? DefaultDocumentName : name;

            if (!byName.TryGetValue(key, out var group))
            {
                group = ([], []);
                byName[key] = group;
                order.Add(key);
            }
            group.Pages.Add(i);
            foreach (var (k, v) in withoutPageType)
                if (!string.IsNullOrEmpty(v) && !group.Fields.ContainsKey(k))
                    group.Fields[k] = v;
        }

        var documents = order.Select(name =>
        {
            var (pageIndices, fields) = byName[name];
            fields["КоличествоЛистов"] = pageIndices.Count.ToString();
            return new GostDocumentGroup(name, pageIndices, fields);
        }).ToList();

        return new GostPageGroupingResult(cover, titlePage, documents);
    }

    private static Dictionary<string, string?> Strip(IReadOnlyDictionary<string, string?> row, string key)
    {
        var copy = new Dictionary<string, string?>(row);
        copy.Remove(key);
        return copy;
    }
}
