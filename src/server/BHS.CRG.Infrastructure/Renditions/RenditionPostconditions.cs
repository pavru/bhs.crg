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
internal static class RenditionPostconditions
{
    /// <param name="pdf">Ответ конвертера.</param>
    /// <param name="cellWords">Слова ячеек книги Excel; <c>null</c> — файл не книга, сверять не с чем.</param>
    public static Rendition Check(byte[] pdf, IReadOnlyList<string>? cellWords)
    {
        List<string> pages;
        try
        {
            using var document = PdfDocument.Open(pdf);
            if (document.NumberOfPages > RenditionLimits.MaxPages)
                return new Rendition.Refused(RenditionRefusal.TooLarge,
                    $"Файл слишком велик для читаемого вида: получилось {document.NumberOfPages} страниц, " +
                    $"а предел — {RenditionLimits.MaxPages}.");
            pages = [.. document.GetPages().Select(VisibleText)];
        }
        // Разборщик PDF на чужом содержимом падает чем угодно; для нас это одно: ответ — не PDF.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Failed("конвертер вернул файл, который не открывается как PDF");
        }

        if (pages.Count == 0) return Failed("в полученном PDF нет ни одной страницы");

        var text = string.Concat(pages);
        if (text.Length == 0)
            // У книги заполненные ячейки ЕСТЬ (иначе до конвертера она не дошла бы), значит текст
            // потерян по дороге. У документа Word знать этого нельзя: он мог быть пуст и сам.
            return cellWords is not null
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
            var found = (double)cellWords.Count(text.Contains) / cellWords.Count;
            if (found < RenditionLimits.CoverageRefuseBelow)
                return Failed("в полученный PDF попало меньше половины текста ячеек");
            if (found < RenditionLimits.CoverageNoteBelow)
                notes.Add($"Часть текста не попала на страницу: из слов в ячейках найдено {found:P0}. " +
                          "Сверяйте с исходным файлом.");
        }

        return new Rendition.Built(pdf, pages.Count, notes);
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
    /// </summary>
    private static string VisibleText(Page page)
    {
        var sheet = page.CropBox.Bounds;
        var glued = new StringBuilder();
        foreach (var letter in page.Letters)
        {
            var at = letter.StartBaseLine;
            if (at.X < sheet.Left || at.X > sheet.Right || at.Y < sheet.Bottom || at.Y > sheet.Top) continue;
            foreach (var symbol in letter.Value)
                if (char.IsLetterOrDigit(symbol)) glued.Append(char.ToLowerInvariant(symbol));
        }
        return glued.ToString();
    }

    private static Rendition.Refused Failed(string why) =>
        new(RenditionRefusal.Failed, $"Привести файл к читаемому виду не удалось: {why}.");
}
