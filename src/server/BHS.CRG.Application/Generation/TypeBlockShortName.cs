namespace BHS.CRG.Application.Generation;

/// <summary>
/// Короткое имя блока после перехода на адресацию <c>Код.Имя</c> (issue #773).
///
/// <para>До перехода имена блоков были глобальны, и пользователь разводил их вручную, приписывая
/// тип: <c>org-full-info</c>, <c>signatory-full</c>, <c>addr-contacts</c>. С префиксом типа этот
/// приписанный кусок начинает повторяться дважды — <c>Организация.org-full-info</c>, — то есть
/// выигрыш от префикса съедается заиканием. Поэтому миграция срезает его.</para>
///
/// <para><b>Правило намеренно узкое: только у типов, где блоков ДВА И БОЛЬШЕ.</b> У типа с одним
/// блоком «общий префикс» тривиален — им оказывается первое слово какого угодно имени, и правило
/// срезало бы смысл: <c>unit-typst → typst</c>, <c>actual-draw-num-name → draw-num-name</c>. На
/// живых данных условие отсекает ровно все сомнительные случаи (10 одноблочных типов) и оставляет
/// 12 бесспорных срезаний у 5 типов.</para>
///
/// <para>Отвергнут более «умный» вариант — сверять срезаемое слово с кодом или именем типа: имена
/// блоков латиницей, коды кириллицей (<c>Адрес</c>/<c>addr</c>, <c>Подписант</c>/<c>signatory</c>,
/// <c>Работа</c>/<c>job</c>), так что сходство почти нигде не сработает, а где сработает — добавит
/// случайности.</para>
/// </summary>
public static class TypeBlockShortName
{
    /// <summary>
    /// Встроенные имена Typst, которые короткое имя блока занимать не должно.
    ///
    /// <para>Затенение здесь особенно коварно: `#let text(it) = …` в модуле перекрывает builtin
    /// `text` для ВСЕХ блоков, определённых ниже (проверено на CLI), а проверка блоков тел функций
    /// не вызывает — компиляционный гейт миграции остаётся зелёным, и подмена вылезает только при
    /// генерации документа. Список — самые ходовые в оформлении; полный реестр Typst сюда тащить
    /// незачем, правило и так отменяет переименование целиком при любом совпадении.</para>
    /// </summary>
    public static readonly IReadOnlyCollection<string> TypstBuiltins = new HashSet<string>(StringComparer.Ordinal)
    {
        "text", "table", "image", "grid", "link", "box", "block", "stack", "place", "rect", "line",
        "circle", "square", "polygon", "path", "curve", "list", "enum", "terms", "figure", "raw",
        "par", "page", "heading", "footnote", "cite", "ref", "label", "columns", "colbreak",
        "pagebreak", "parbreak", "linebreak", "strong", "emph", "underline", "overline", "strike",
        "highlight", "smallcaps", "sub", "super", "align", "pad", "move", "rotate", "scale", "hide",
        "repeat", "layout", "measure", "context", "style", "numbering", "counter", "state", "query",
        "locate", "calc", "sym", "emoji", "str", "int", "float", "bool", "array", "dictionary",
        "datetime", "duration", "type", "repr", "eval", "assert", "panic", "range", "read", "json",
        "csv", "yaml", "toml", "xml", "cbor", "bytes", "luma", "rgb", "cmyk", "color", "gradient",
        "pattern", "tiling", "length", "angle", "ratio", "fraction", "alignment", "direction",
        "stroke", "regex", "selector", "symbol", "content", "function", "module", "arguments",
        "space", "h", "v", "d",
    };

    /// <summary>
    /// Новые имена блоков одного типа: <c>старое → новое</c>. Пустой словарь означает «переименовывать
    /// нечего» — так и должно быть у большинства типов.
    /// </summary>
    /// <param name="fnNames">Имена блоков ОДНОГО типа, в порядке схемы.</param>
    /// <param name="reserved">Имена, которые занимать нельзя: диспетч-часть плюс верхнеуровневые
    /// имена Typst-библиотеки. Библиотека импортируется внутрь тела блока и перекрывает одноимённое
    /// короткое имя соседа по модулю — редкий, но совершенно немой отказ, поэтому проверяем заранее.</param>
    public static IReadOnlyDictionary<string, string> Shorten(
        IReadOnlyList<string> fnNames, IReadOnlyCollection<string> reserved)
    {
        var empty = new Dictionary<string, string>();
        if (fnNames.Count < 2) return empty;                       // см. доккоммент: главное условие

        var prefix = CommonPrefix(fnNames);
        if (prefix is null) return empty;

        var result = new Dictionary<string, string>();
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in fnNames)
        {
            var candidate = name[prefix.Length..];
            // Любая заминка отменяет переименование ЦЕЛИКОМ, а не по одному блоку: половинчатый
            // результат («у типа два блока, у одного префикс срезан, у другого нет») выглядел бы
            // как ошибка миграции, а не как её осторожность.
            if (!TypstPreambleBuilder.IsTypstIdentifier(candidate)
                || TypstBuiltins.Contains(candidate)
                || reserved.Contains(candidate)
                || fnNames.Contains(candidate)
                || !taken.Add(candidate))
                return empty;
            result[name] = candidate;
        }
        return result;
    }

    /// <summary>Общий первый сегмент (до дефиса) — если он есть у ВСЕХ имён и одинаков.</summary>
    private static string? CommonPrefix(IReadOnlyList<string> names)
    {
        string? head = null;
        foreach (var n in names)
        {
            var cut = n.IndexOf('-');
            if (cut <= 0 || cut == n.Length - 1) return null;      // нет сегмента либо нечего оставить
            var h = n[..(cut + 1)];
            if (head is null) head = h;
            else if (!string.Equals(head, h, StringComparison.Ordinal)) return null;
        }
        return head;
    }
}
