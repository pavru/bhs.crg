using Microsoft.Extensions.Logging;

namespace BHS.CRG.Infrastructure.Renditions;

/// <summary>
/// Построение читаемого образа офисного файла (issue #1268): PDF с текстовым слоем или отказ с
/// причиной. Вид файла к этому месту уже определён по содержимому — заявленному расширению здесь
/// не верят, потому что его сюда не приносят.
///
/// <para>Порядок шагов — от дешёвого к дорогому, и к конвертеру файл попадает последним: бюджет →
/// (для книги) чтение ячеек → конвертер → постусловия. Каждый шаг вправе ответить отказом, и ни
/// один не отвечает «на всякий случай образом».</para>
///
/// <para>Ничего не хранит и состояния не держит: хранит образ и отдаёт его модулям отдельная часть
/// (issue #1269).</para>
/// </summary>
public sealed class OfficeRenditionBuilder(OfficeConverterClient converter, ILogger<OfficeRenditionBuilder> log)
{
    /// <summary>Пометка образа книги, ячейки которой прочесть не удалось: сверки не было.</summary>
    public const string UncheckedNote =
        "Сверить образ с ячейками книги не удалось: книга читается не полностью. Сверяйте с исходным файлом.";

    /// <returns><see cref="Rendition.Built" /> или <see cref="Rendition.Refused" />.</returns>
    public async Task<Rendition> BuildAsync(byte[] file, OfficeFormat format, CancellationToken ct)
    {
        if (OfficeFileBudget.Check(format, file) is { } overBudget) return overBudget;

        IReadOnlyList<string>? cellWords = null;
        var uncheckedBook = false;
        if (format is OfficeFormat.Xlsx or OfficeFormat.Xls)
        {
            var cells = ExcelCellTexts.From(file, format, ct);
            if (cells.Refusal is not null) return cells.Refusal;
            if (cells.Unreadable is null) cellWords = cells.Words;
            else
            {
                // Наш разборщик книгу не прочёл — это ещё не «повреждена»: LibreOffice понимает
                // больше. Книга идёт к нему без сверки слов, и образ об этом скажет.
                log.LogWarning("Книга {Format} не прочитана для сверки ({Error}): образ строится без неё", format, cells.Unreadable);
                uncheckedBook = true;
            }
        }

        var reply = await converter.ConvertAsync(file, format, ct);
        var result = reply.Refusal ?? RenditionPostconditions.Check(reply.Pdf!, cellWords);
        if (result is not Rendition.Built built) return result;
        // Отметка берётся тут же, у того же сервиса: образ и сведения о том, чем он построен,
        // расходиться не должны.
        return built with
        {
            Notes = uncheckedBook ? [.. built.Notes, UncheckedNote] : built.Notes,
            Converter = await converter.MarkAsync(ct),
        };
    }

    /// <inheritdoc cref="OfficeFileBudget.MayBeOffice" />
    public static bool MayBeOffice(ReadOnlySpan<byte> head) => OfficeFileBudget.MayBeOffice(head);

    /// <inheritdoc cref="OfficeFileBudget.WhyUnknown" />
    public static RenditionRefusal? WhyNotOffice(byte[] file) => OfficeFileBudget.WhyUnknown(file);
}
