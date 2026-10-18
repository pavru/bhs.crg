using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace BHS.CRG.Infrastructure.Renditions;

/// <summary>
/// Постусловия на готовый PDF (issue #1268): то, что отличает образ от файла, который только
/// называется PDF.
///
/// <para>Нужны потому, что конвертер отвечает успехом и тогда, когда успеха нет: пустая книга даёт
/// код 200 и страницу без единой буквы, файл с обрезанной ячейкой — страницу без её хвоста. Такой
/// «образ» дальше честно распознаётся в пустой счёт, и отказа не видит уже никто.</para>
/// </summary>
public static class RenditionPostconditions
{
    /// <summary>
    /// Начало пометки о потерянном тексте. Константой — по ней порт модулей узнаёт пометку и дописывает
    /// к ней у книги со своей раскладкой, почему профиль печати не помог (issue #1456).
    /// </summary>
    public const string LostTextNote = "Часть текста не попала на страницу";

    /// <param name="pdf">Ответ конвертера.</param>
    /// <param name="cellWords">Слова ячеек книги Excel; <c>null</c> — файл не книга, сверять не с чем.</param>
    public static Rendition Check(byte[] pdf, IReadOnlyList<string>? cellWords)
    {
        List<string> pages;
        var sizes = new List<double>();
        try
        {
            using var document = PdfDocument.Open(pdf);
            if (document.NumberOfPages > RenditionLimits.MaxPages)
                return new Rendition.Refused(RenditionRefusal.TooLarge,
                    $"Файл слишком велик для читаемого вида: получилось {document.NumberOfPages} страниц, " +
                    $"а предел — {RenditionLimits.MaxPages}.");
            pages = [.. document.GetPages().Select(page => VisibleText(page, sizes))];
        }
        // Разборщик PDF на чужом содержимом падает чем угодно; для нас это одно: ответ — не PDF.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Failed("конвертер вернул файл, который не открывается как PDF");
        }

        if (pages.Count == 0) return Failed("в полученном PDF нет ни одной страницы");

        var text = string.Concat(pages);
        if (text.Length == 0)
            // У книги со словами в ячейках текст потерян по дороге. А если слов не было — одни
            // прочерки и галочки, — букв нет и в исходном файле, и это не сбой, а пустой файл.
            // У документа Word знать этого нельзя вовсе: он мог быть пуст и сам.
            return cellWords is { Count: > 0 }
                ? Failed("в полученном PDF нет текста, хотя в ячейках книги он есть")
                : new Rendition.Refused(RenditionRefusal.Empty,
                    "В файле нет текста — читаемый вид получился бы пустым. Если в документ вставлен скан, приложите сам скан.");

        var notes = new List<string>();
        var blank = pages.Count(page => page.Length == 0);
        // Страницу без текста не выбрасываем, а называем: на ней может стоять печать или подпись
        // картинкой, и по текстовому слою пустую от такой не отличить.
        if (blank > 0) notes.Add($"Страниц: {pages.Count}, из них без текста: {blank}.");

        if (cellWords is { Count: > 0 })
        {
            var found = Coverage(cellWords, text);
            if (found < RenditionLimits.CoverageRefuseBelow)
                return Failed("в полученный PDF попало меньше половины текста ячеек");
            if (found < RenditionLimits.CoverageNoteBelow)
                // Доля — вниз до целого: округлённая как обычно, 97,96 % стала бы «98 %» — числом
                // порога, под которым пометки быть не должно.
                notes.Add($"{LostTextNote}: из слов в ячейках найдено {(int)Math.Floor(found * 100)} %. " +
                          "Сверяйте с исходным файлом.");
        }

        sizes.Sort();
        return new Rendition.Built(pdf, pages.Count, notes) { MedianPointSize = sizes[sizes.Count / 2] };
    }

    /// <summary>
    /// Доля слов ячеек, найденных в тексте страниц, с учётом повторов.
    ///
    /// <para>Каждое РАЗНОЕ слово ищется один раз, и разных берётся не больше предела — через равные
    /// промежутки по всей книге. Поиск слова — проход по всему тексту, и прайс-лист на сотни тысяч
    /// слов против сотни страниц давал бы сотни миллиардов сравнений на потоке запроса. Вопрос
    /// «образ ли это этого файла» две тысячи слов решают не хуже двухсот тысяч.</para>
    /// </summary>
    private static double Coverage(IReadOnlyList<string> cellWords, string text)
    {
        var repeats = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var word in cellWords) repeats[word] = repeats.GetValueOrDefault(word) + 1;

        var step = Math.Max(1, repeats.Count / RenditionLimits.MaxCheckedWords);
        long asked = 0, found = 0;
        var index = 0;
        foreach (var (word, count) in repeats)
        {
            if (index++ % step != 0) continue;
            asked += count;
            if (text.Contains(word, StringComparison.Ordinal)) found += count;
        }
        return (double)found / asked;
    }

    /// <summary>
    /// Текст страницы, который на ней ВИДЕН, — без всего, кроме букв и цифр, строчными. Слова ищутся
    /// в нём подстрокой: PDF хранит буквы, а не слова, и где в нём пробел, а где перенос, зависит
    /// от вёрстки.
    ///
    /// <para>⚠️ Буква, записанная в PDF, ещё не напечатана. Ячейку, не поместившуюся внизу листа,
    /// LibreOffice на следующий лист не переносит: её нижние строки он пишет в ту же страницу, ниже
    /// её края. В файле они есть, на странице их нет — ни для человека, ни для модели, читающей
    /// картинку. Поэтому считаются только буквы, стоящие в пределах листа: иначе сверка находила бы
    /// текст, которого никто не видит (так и было: обрезанный счёт из пробы проходил чистым).</para>
    ///
    /// <para>Тем же проходом собирается кегль видимых букв (<paramref name="sizes" />): второй разбор
    /// того же PDF ради медианы стоил бы столько же, сколько вся сверка. Кегль — видимый
    /// (<c>PointSize</c>, с учётом матриц текста и страницы), а не записанный в шрифте: LibreOffice 8.37
    /// пишет вписанный текст уже уменьшенным кеглем, но другой построитель вправе уменьшать масштабом —
    /// и тогда записанный кегль остался бы прежним.</para>
    /// </summary>
    private static string VisibleText(Page page, List<double> sizes)
    {
        var sheet = page.CropBox.Bounds;
        var glued = new StringBuilder();
        foreach (var letter in page.Letters)
        {
            var at = letter.StartBaseLine;
            if (at.X < sheet.Left || at.X > sheet.Right || at.Y < sheet.Bottom || at.Y > sheet.Top) continue;
            var visible = false;
            foreach (var symbol in letter.Value)
                if (char.IsLetterOrDigit(symbol)) { glued.Append(char.ToLowerInvariant(symbol)); visible = true; }
            if (visible) sizes.Add(letter.PointSize);
        }
        return glued.ToString();
    }

    private static Rendition.Refused Failed(string why) =>
        new(RenditionRefusal.Failed, $"Привести файл к читаемому виду не удалось: {why}.");
}
