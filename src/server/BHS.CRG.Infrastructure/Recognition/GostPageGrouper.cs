namespace BHS.CRG.Infrastructure.Recognition;

/// <summary>
/// Одна распознанная группа документа — несколько исходных страниц с общим Шифром (графа 1).
/// Группируем именно по Шифру, а не по НаименованиюДокумента (графа 5): по ГОСТ Р 21.101-2020
/// форма 6 (последующие листы — как чертежей формы 3, так и текстовых документов формы 5) обычно
/// НЕ повторяет наименование документа, но Шифр остаётся неизменным на всех листах одного
/// документа, включая продолжения. НаименованиеДокумента при этом попадает в Fields как обычное
/// поле (первое непустое значение в группе) — оно там, где реально заполнено (форма 5/титульный
/// лист), просто не используется как ключ группировки.
/// </summary>
public record GostDocumentGroup(
    string Code, IReadOnlyList<int> PageIndices, IReadOnlyDictionary<string, string?> Fields);

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
    private const string DefaultCode = "(без шифра)";

    public static GostPageGroupingResult Group(IReadOnlyList<IReadOnlyDictionary<string, string?>> pages)
    {
        var cover = new List<IReadOnlyDictionary<string, string?>>();
        var titlePage = new List<IReadOnlyDictionary<string, string?>>();

        // Порядок появления шифров сохраняется (Dictionary в .NET сохраняет порядок вставки
        // на практике, но полагаться на это не будем — ведём отдельный список порядка).
        var order = new List<string>();
        var byCode = new Dictionary<string, (List<int> Pages, Dictionary<string, string?> Fields)>();

        for (var i = 0; i < pages.Count; i++)
        {
            var page = pages[i];
            var pageType = page.GetValueOrDefault(GostTitleBlockFields.PageTypePath);
            var withoutPageType = Strip(page, GostTitleBlockFields.PageTypePath);

            if (pageType == "Обложка") { cover.Add(withoutPageType); continue; }
            if (pageType == "ТитульныйЛист") { titlePage.Add(withoutPageType); continue; }

            // "Документ", пусто или что-то неожиданное от модели — всё считаем документом.
            var shifr = page.GetValueOrDefault("Шифр");
            var key = string.IsNullOrWhiteSpace(shifr) ? DefaultCode : shifr;

            if (!byCode.TryGetValue(key, out var group))
            {
                group = ([], []);
                byCode[key] = group;
                order.Add(key);
            }
            group.Pages.Add(i);
            foreach (var (k, v) in withoutPageType)
                if (!string.IsNullOrEmpty(v) && !group.Fields.ContainsKey(k))
                    group.Fields[k] = v;
        }

        var documents = order.Select(code =>
        {
            var (pageIndices, fields) = byCode[code];
            fields["КоличествоЛистов"] = pageIndices.Count.ToString();
            return new GostDocumentGroup(code, pageIndices, fields);
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
