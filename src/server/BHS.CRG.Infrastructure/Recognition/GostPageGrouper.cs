using BHS.CRG.Infrastructure.DataSets;

namespace BHS.CRG.Infrastructure.Recognition;

/// <summary>
/// Одна распознанная группа документа — несколько исходных СМЕЖНЫХ страниц одного документа
/// (первый лист формы 3/4/5 + его продолжения формы 6, либо несколько листов формы 3 с одним и
/// тем же наименованием).
/// </summary>
public record GostDocumentGroup(
    string Code, IReadOnlyList<int> PageIndices, IReadOnlyDictionary<string, string?> Fields);

/// <summary>Результат маршрутизации распознанных страниц по типу (см. GostTitleBlockFields.PageTypeField).
/// Обложка/титул несут индексы страниц (<see cref="GostGroupingPage"/>) — нужно для единой
/// постраничной группировки (см. GostUnifiedGroupingBuilder).</summary>
public record GostPageGroupingResult(
    IReadOnlyList<GostGroupingPage> Cover,
    IReadOnlyList<GostGroupingPage> TitlePage,
    IReadOnlyList<GostDocumentGroup> Documents);

/// <summary>
/// Чистая логика маршрутизации и группировки постранично распознанных строк (см.
/// GostTitleBlockFields.AllWithClassifiers) — вынесена отдельно от DataSetService ради
/// юнит-тестируемости без БД/blob/LLM. Никогда не бросает.
///
/// <para>Последовательный проход. Обложка/титул уходят в свои вёдра по <c>ТипСтраницы</c>.
/// Для документов граница определяется по ФОРМЕ штампа + паре Шифр/Наименование:</para>
/// <list type="bullet">
/// <item><b>Форма 6 (последующий лист) — ВСЕГДА продолжает текущую группу</b> (Шифр/Наименование на
/// продолжении игнорируются, это шум мелкого штампа). Наименование на форме 6 принудительно
/// обнуляется — по ГОСТ его там нет, а модель иногда берёт под него текст из тела листа.</item>
/// <item><b>Форма 3/4/5 (или форма не распознана) — новый документ, если Шифр ИЛИ Наименование
/// отличаются от текущей группы; продолжение — только когда совпадают ОБА.</b> Сравнение с
/// нормализацией пустых: пустое==пустое совпадает, пустое≠непустое различаются.</item>
/// </list>
///
/// <para>
/// <b>История границы:</b> в правке 2026-07-05 граница временно бралась ТОЛЬКО по Наименованию, т.к.
/// Шифр распознавался ненадёжно (OCR-шум «ЕЦДМ»↔«ЕЦ.ДМ», «241101 - ЭМ»↔«241101 - ЭОМ» разбивал
/// один документ). После внедрения извлечения точного текста штампа из PDF (текстовый слой +
/// аннотации, см. GostStampTextExtractor) + text-grounding Шифр стал надёжным на реальных данных,
/// поэтому по решению пользователя возвращено строгое правило «Шифр И Наименование» — оно строже
/// (различает документы с общим именем, но разным шифром) и соответствует исходной спецификации.
/// </para>
///
/// <para>
/// Форма 6 первой страницей-документом (нет открытой группы) → группа-маркер аномалии «Некорректная
/// форма 6». Шифр группы (<see cref="GostDocumentGroup.Code"/>) — с первого листа. См. память
/// проекта project_pdf_gost_split_documents.
/// </para>
/// </summary>
public static class GostPageGrouper
{
    private const string DefaultCode = "(без шифра)";
    private const string IncorrectForm6Name = "Некорректная форма 6";
    private const string DocumentNameKey = "НаименованиеДокумента";

    public static GostPageGroupingResult Group(IReadOnlyList<IReadOnlyDictionary<string, string?>> pages)
    {
        var cover = new List<GostGroupingPage>();
        var titlePage = new List<GostGroupingPage>();
        var documents = new List<GostDocumentGroup>();

        List<int>? currentPages = null;
        Dictionary<string, string?>? currentFields = null;
        string? currentCode = null;
        string? currentShifr = null; // сырой шифр текущей группы (для сравнения границы)
        string? currentName = null;

        static string Norm(string? s) => string.IsNullOrWhiteSpace(s) ? "" : s.Trim();

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
            var stripped = Strip(page, GostTitleBlockFields.PageTypePath, GostTitleBlockFields.StampFormPath);

            if (pageType == "Обложка") { cover.Add(new GostGroupingPage(i, stripped)); continue; }
            if (pageType == "ТитульныйЛист") { titlePage.Add(new GostGroupingPage(i, stripped)); continue; }

            // "Документ", пусто или что-то неожиданное от модели — всё считаем документом.
            var isContinuation = page.GetValueOrDefault(GostTitleBlockFields.StampFormPath) == "Форма6";
            if (isContinuation)
                // На последующих листах наименования документа по ГОСТ нет — не тащим в группу
                // ошибочно распознанный текст под этим полем.
                stripped.Remove(DocumentNameKey);

            var shifr = page.GetValueOrDefault("Шифр");
            var name = page.GetValueOrDefault(DocumentNameKey);
            var hasShifr = !string.IsNullOrWhiteSpace(shifr);
            var hasName = !string.IsNullOrWhiteSpace(name);

            var forcedIncorrectForm6 = false;
            bool isNewDocument;
            if (isContinuation)
            {
                if (currentPages is null)
                {
                    // Форма 6 без предшествующего первого листа — по ГОСТ невозможно, но страницу
                    // не теряем: открываем группу-маркер аномалии.
                    isNewDocument = true;
                    forcedIncorrectForm6 = true;
                }
                else
                {
                    isNewDocument = false; // форма 6 всегда продолжает текущую группу
                }
            }
            else
            {
                // Форма 3/4/5 или форма не распознана: новый документ, если Шифр ИЛИ Наименование
                // отличаются от текущей группы (продолжение — только когда совпадают оба).
                var codeMatches = Norm(shifr) == Norm(currentShifr);
                var nameMatches = Norm(name) == Norm(currentName);
                isNewDocument = currentPages is null || !codeMatches || !nameMatches;
            }

            if (isNewDocument)
            {
                FlushCurrent();
                currentPages = [];
                currentFields = [];
                currentShifr = shifr;
                currentCode = hasShifr ? shifr : DefaultCode;
                currentName = forcedIncorrectForm6 ? IncorrectForm6Name : (hasName ? name : null);
            }

            currentPages!.Add(i);
            foreach (var (k, v) in stripped)
                if (!string.IsNullOrEmpty(v) && !currentFields!.ContainsKey(k))
                    currentFields[k] = v;
            if (forcedIncorrectForm6)
                currentFields![DocumentNameKey] = IncorrectForm6Name;
        }
        FlushCurrent();

        return new GostPageGroupingResult(cover, titlePage, documents);
    }

    private static Dictionary<string, string?> Strip(IReadOnlyDictionary<string, string?> row, params string[] keys)
    {
        var copy = new Dictionary<string, string?>(row);
        foreach (var key in keys) copy.Remove(key);
        return copy;
    }
}
