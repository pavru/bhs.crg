namespace BHS.CRG.Infrastructure.Recognition;

/// <summary>
/// Одна распознанная группа документа — несколько исходных СМЕЖНЫХ страниц одного документа
/// (см. правку 2026-07-03 ниже про смежность).
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
///
/// <para>
/// Группировка — ПОСЛЕДОВАТЕЛЬНЫЙ проход по страницам (не глобальный словарь по Шифру, как было
/// раньше): новый документ начинается, когда меняется Шифр, либо (если Шифр не распознан) когда
/// меняется НаименованиеДокумента. Иначе страница считается продолжением текущего документа.
/// </para>
///
/// <para>
/// <b>Правка 2026-07-03 (2):</b> первая версия (группировка глобальным словарём строго по Шифру)
/// вскрыла регрессию — форма 3 (штамп чертежа, обычно мелкая табличка в правом нижнем углу листа)
/// распознаётся заметно хуже формы 5/6 (текстовый документ с крупным штампом): промпт
/// (BuildTitleBlockPrompt) явно требует "если значения нет — пустая строка, не выдумывай", и модель
/// нередко honestly возвращает пустой Шифр для чертёжных листов. При группировке ТОЛЬКО по Шифру
/// это схлопывало РАЗНЫЕ чертежи (с разными НаименованиеДокумента) в один "(без шифра)" файл —
/// ровно баг, о котором сообщил пользователь. Исправлено: Шифр остаётся основным ключом (форма
/// 5/6 по-прежнему группируется корректно — см. регрессионный тест форма5/6), но когда Шифр не
/// распознан, вместо общего "мусорного" bucket'а используется НаименованиеДокумента как признак
/// нового документа. Плюс переход на последовательный (не глобальный по словарю) проход —
/// страницы одного документа в реальном PDF всегда идут подряд, повторное появление того же
/// Шифра/имени далеко не рядом не должно склеивать страницы обратно в старую группу.
/// </para>
/// </summary>
public static class GostPageGrouper
{
    private const string DefaultCode = "(без шифра)";

    public static GostPageGroupingResult Group(IReadOnlyList<IReadOnlyDictionary<string, string?>> pages)
    {
        var cover = new List<IReadOnlyDictionary<string, string?>>();
        var titlePage = new List<IReadOnlyDictionary<string, string?>>();
        var documents = new List<GostDocumentGroup>();

        List<int>? currentPages = null;
        Dictionary<string, string?>? currentFields = null;
        string? currentCode = null;
        string? currentName = null;

        void FlushCurrent()
        {
            if (currentPages is null) return;
            currentFields!["КоличествоЛистов"] = currentPages.Count.ToString();
            documents.Add(new GostDocumentGroup(currentCode!, currentPages, currentFields));
        }

        for (var i = 0; i < pages.Count; i++)
        {
            var page = pages[i];
            var pageType = page.GetValueOrDefault(GostTitleBlockFields.PageTypePath);
            var withoutPageType = Strip(page, GostTitleBlockFields.PageTypePath);

            if (pageType == "Обложка") { cover.Add(withoutPageType); continue; }
            if (pageType == "ТитульныйЛист") { titlePage.Add(withoutPageType); continue; }

            // "Документ", пусто или что-то неожиданное от модели — всё считаем документом.
            var shifr = page.GetValueOrDefault("Шифр");
            var name = page.GetValueOrDefault("НаименованиеДокумента");
            var hasShifr = !string.IsNullOrWhiteSpace(shifr);
            var hasName = !string.IsNullOrWhiteSpace(name);

            // Новый документ: Шифр распознан и отличается от текущего — Шифр авторитетнее имени
            // (форма 6 продолжения корректно домержится, даже если у неё случайно "другое" пустое
            // имя). Если же Шифр НЕ распознан на этой странице — опираемся на имя: другое
            // непустое имя при уже известном имени текущей группы означает новый документ (именно
            // это чинит баг с разными чертежами формы 3, схлопывавшимися при пустом Шифре).
            var isNewDocument = currentPages is null
                || (hasShifr && shifr != currentCode)
                || (!hasShifr && hasName && currentName != null && name != currentName);

            if (isNewDocument)
            {
                FlushCurrent();
                currentPages = [];
                currentFields = [];
                currentCode = hasShifr ? shifr : DefaultCode;
                currentName = hasName ? name : null;
            }
            else if (hasName && currentName is null)
            {
                currentName = name; // донабираем имя, если раньше не было (напр. Шифр был на 1-м листе, имя — на 2-м)
            }

            currentPages!.Add(i);
            foreach (var (k, v) in withoutPageType)
                if (!string.IsNullOrEmpty(v) && !currentFields!.ContainsKey(k))
                    currentFields[k] = v;
        }
        FlushCurrent();

        return new GostPageGroupingResult(cover, titlePage, documents);
    }

    private static Dictionary<string, string?> Strip(IReadOnlyDictionary<string, string?> row, string key)
    {
        var copy = new Dictionary<string, string?>(row);
        copy.Remove(key);
        return copy;
    }
}
