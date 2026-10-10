using System.Text;
using ExcelDataReader;
using ExcelDataReader.Exceptions;

namespace BHS.CRG.Infrastructure.Renditions;

/// <summary>
/// Слова из ячеек книги Excel — то, с чем сверяется текстовый слой образа (issue #1268).
///
/// <para>Берутся только текстовые ячейки. Число и дата на странице записаны по формату ячейки
/// («3 834,16», «20.02.2026»), а разборщик отдаёт значение — сравнивать их значило бы писать здесь
/// второй LibreOffice. Берётся только то, что печатается: скрытые листы, строки и колонки на
/// страницу не попадают, и считать их потерей нельзя.</para>
///
/// <para>⚠️ Это сверка, а не чтение данных: слова нужны, чтобы убедиться, что образ — образ ЭТОГО
/// файла. Значения из книги по-прежнему читает разборщик наборов данных.</para>
/// </summary>
internal static class ExcelCellTexts
{
    /// <summary>Слово короче не сверяется: «и», «шт», «№ 1» найдутся в любом тексте.</summary>
    private const int MinWordLength = 3;

    static ExcelCellTexts() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <param name="Words">Слова текстовых ячеек, строчными, с повторами.</param>
    /// <param name="Refusal">Почему книгу не прочитать; тогда слов нет.</param>
    public sealed record Read(IReadOnlyList<string> Words, Rendition.Refused? Refusal);

    public static Read From(byte[] file)
    {
        try
        {
            using var reader = ExcelReaderFactory.CreateReader(new MemoryStream(file, writable: false));
            var words = new List<string>();
            var cells = 0;
            do
            {
                if (!string.Equals(reader.VisibleState, "visible", StringComparison.OrdinalIgnoreCase)) continue;
                while (reader.Read())
                {
                    if (reader.RowHeight == 0) continue;
                    for (var column = 0; column < reader.FieldCount; column++)
                    {
                        var value = reader.GetValue(column);
                        if (value is null || (value is string text && string.IsNullOrWhiteSpace(text))) continue;
                        if (reader.GetColumnWidth(column) == 0) continue;
                        if (++cells > RenditionLimits.MaxCells)
                            return Refuse(RenditionRefusal.TooLarge,
                                $"Файл слишком велик для читаемого вида: в книге больше {RenditionLimits.MaxCells} заполненных ячеек.");
                        if (value is string cell) words.AddRange(WordsOf(cell));
                    }
                }
            } while (reader.NextResult());

            return cells == 0
                ? Refuse(RenditionRefusal.Empty, "В книге нет ни одной заполненной ячейки — показывать нечего.")
                : new Read(words, null);
        }
        catch (InvalidPasswordException)
        {
            return Refuse(RenditionRefusal.Protected,
                "Файл защищён паролем — привести его к читаемому виду нельзя. Снимите пароль и приложите файл заново.");
        }
        // Разборщик на чужом файле падает чем угодно — от своего «не тот заголовок» до выхода за
        // границы массива. Для нас это одно и то же: книгу не прочитать. Текст исключения человеку
        // не уходит — в нём бывают куски содержимого.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Refuse(RenditionRefusal.Corrupted, "Файл повреждён: книга Excel в нём не читается.");
        }
    }

    /// <summary>Слова текста: буквы и цифры подряд, строчными. Тем же способом режется текст образа.</summary>
    public static IEnumerable<string> WordsOf(string text)
    {
        var word = new StringBuilder();
        foreach (var symbol in text)
        {
            if (char.IsLetterOrDigit(symbol)) { word.Append(char.ToLowerInvariant(symbol)); continue; }
            if (word.Length >= MinWordLength) yield return word.ToString();
            word.Clear();
        }
        if (word.Length >= MinWordLength) yield return word.ToString();
    }

    private static Read Refuse(RenditionRefusal kind, string reason) => new([], new(kind, reason));
}
