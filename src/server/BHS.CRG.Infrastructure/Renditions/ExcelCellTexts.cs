using System.Text;
using ExcelDataReader;
using ExcelDataReader.Exceptions;

namespace BHS.CRG.Infrastructure.Renditions;

/// <summary>
/// Слова из ячеек книги Excel — то, с чем сверяется текстовый слой образа (issue #1268).
///
/// <para>Берутся только текстовые ячейки. Число и дата на странице записаны по формату ячейки
/// («3 834,16», «20.02.2026»), а разборщик отдаёт значение — сравнивать их значило бы писать здесь
/// второй LibreOffice. Берётся только то, что печатается: скрытые листы, строки и колонки и всё, что
/// лежит вне области печати, на страницу не попадает, и считать это потерей нельзя.</para>
///
/// <para>⚠️ Это сверка, а не чтение данных: слова нужны, чтобы убедиться, что образ — образ ЭТОГО
/// файла. Значения из книги по-прежнему читает разборщик наборов данных.</para>
///
/// <para>⚠️ <b>Разборщик здесь — помощник сверки, а не ворота перед конвертером.</b> Книгу, которую
/// он прочесть не смог, открывает LibreOffice — и нередко успешно: разборщиков два, и понимают они
/// не одно и то же. Поэтому «не прочёл» — это не отказ, а ответ «сверять не с чем»
/// (<see cref="Read.Unreadable" />). Отказами остаются только те три вещи, которые разборщик знает
/// наверняка: пароль, пустота и размер.</para>
/// </summary>
internal static class ExcelCellTexts
{
    /// <summary>Слово короче не сверяется: «и», «шт», «№ 1» найдутся в любом тексте.</summary>
    private const int MinWordLength = 3;

    static ExcelCellTexts() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <param name="Words">Слова текстовых ячеек, строчными, с повторами.</param>
    /// <param name="Refusal">Почему из книги образа не будет; тогда слов нет.</param>
    /// <param name="Unreadable">Чем разборщик упал — тип исключения, для журнала. Текст исключения не
    /// берётся: в нём бывают куски содержимого. Не <c>null</c> — книга идёт к конвертеру без сверки.</param>
    public sealed record Read(IReadOnlyList<string> Words, Rendition.Refused? Refusal, string? Unreadable = null);

    public static Read From(byte[] file, OfficeFormat format, CancellationToken ct)
    {
        try
        {
            var areas = format == OfficeFormat.Xlsx ? ExcelPrintAreas.Of(file) : ExcelPrintAreas.None;
            using var reader = ExcelReaderFactory.CreateReader(new MemoryStream(file, writable: false));
            var words = new List<string>();
            var cells = 0;
            long visited = 0;
            var sheet = -1;
            do
            {
                sheet++;
                if (!string.Equals(reader.VisibleState, "visible", StringComparison.OrdinalIgnoreCase)) continue;
                var row = -1;
                while (reader.Read())
                {
                    row++;
                    ct.ThrowIfCancellationRequested();
                    // Считается КАЖДАЯ пройденная ячейка, а не только заполненная: пропуски между
                    // далёкими ячейками разборщик отдаёт пустыми строками во всю ширину листа, и две
                    // ячейки по углам листа — это миллиарды шагов при счётчике заполненных «два».
                    visited += reader.FieldCount;
                    if (visited > RenditionLimits.MaxVisitedCells)
                        return Refuse(RenditionRefusal.TooLarge,
                            "Файл слишком велик для читаемого вида: лист книги занят до слишком далёких строк и колонок.");
                    if (reader.RowHeight == 0) continue;
                    for (var column = 0; column < reader.FieldCount; column++)
                    {
                        var value = reader.GetValue(column);
                        if (value is null || (value is string text && string.IsNullOrWhiteSpace(text))) continue;
                        if (reader.GetColumnWidth(column) == 0) continue;
                        if (++cells > RenditionLimits.MaxCells)
                            return Refuse(RenditionRefusal.TooLarge,
                                $"Файл слишком велик для читаемого вида: в книге больше {RenditionLimits.MaxCells} заполненных ячеек.");
                        if (value is string cell && areas.Prints(sheet, row, column)) words.AddRange(WordsOf(cell));
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
        // границы массива. Повреждён ли файл, отсюда не видно: это скажет конвертер.
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        {
            return new Read([], null, ex.GetType().Name);
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
