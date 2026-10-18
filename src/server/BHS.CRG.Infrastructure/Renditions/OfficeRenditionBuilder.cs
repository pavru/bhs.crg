using BHS.CRG.Domain.Storage;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Infrastructure.Renditions;

/// <summary>
/// Построение читаемого образа офисного файла (issue #1268): PDF с текстовым слоем или отказ с
/// причиной. Вид файла к этому месту уже определён по содержимому — заявленному расширению здесь
/// не верят, потому что его сюда не приносят.
///
/// <para>Порядок шагов — от дешёвого к дорогому, и к конвертеру файл попадает последним: бюджет →
/// (для книги) чтение ячеек → раскладка по профилю печати (копия, issue #1453) → конвертер →
/// постусловия. Каждый шаг вправе ответить отказом, и ни один не отвечает «на всякий случай
/// образом». Раскладка отказом не отвечает никогда: худшее — книга уходит как есть, с пометкой.</para>
///
/// <para>⚠️ Бюджет и слова ячеек — по ОРИГИНАЛУ, конвертер — по копии. Вписанная книга — первый
/// случай, когда к конвертеру идёт не тот массив, по которому считались слова: сверка тем и ценна, что
/// ловит потерю текста в правке.</para>
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

        // Версию спрашиваем одновременно с преобразованием, а не после: отметка необязательна, и
        // ждать её отдельно — значит держать готовый образ ради строки «чем построен». Вызов не
        // бросает, поэтому брошенный без ответа (образа не вышло) он ничего не оставляет.
        var laid = format == OfficeFormat.Xlsx
            ? ExcelPageLayout.Apply(file, PageProfile.Default, ct)
            : new LaidOut(RenditionLayout.NotBook, file, [], null);

        var mark = converter.MarkAsync(ct);
        var reply = await converter.ConvertAsync(laid.File, format, ct);
        var result = reply.Refusal ?? RenditionPostconditions.Check(reply.Pdf!, cellWords);
        if (result is not Rendition.Built built) return result;

        var notes = new List<string>(built.Notes);
        notes.AddRange(laid.Notes);
        // Мелкий текст — только у вписанной книги: уменьшили её мы, и сказать об этом обязаны мы. Кегль
        // автора — его решение, и сверять его с файлом незачем.
        if (laid.Outcome == RenditionLayout.Applied && RenditionPostconditions.MedianPointSize(built.Pdf) is { } median
            && median < RenditionLimits.SmallTextBelowPt)
            notes.Add(SmallTextNote);
        if (uncheckedBook) notes.Add(UncheckedNote);
        return built with
        {
            Notes = notes,
            Converter = await mark,
            Layout = laid.Outcome,
            LayoutApplied = laid.Applied,
        };
    }

    /// <summary>Пометка вписанного образа, текст которого вышел мельче порога.</summary>
    public const string SmallTextNote = "Вид уменьшен — мелкий текст сверяйте с файлом.";

    /// <inheritdoc cref="OfficeFileBudget.MayBeOffice" />
    public static bool MayBeOffice(ReadOnlySpan<byte> head) => OfficeFileBudget.MayBeOffice(head);

    /// <inheritdoc cref="OfficeFileBudget.WhyUnknown" />
    public static RenditionRefusal? WhyNotOffice(byte[] file) => OfficeFileBudget.WhyUnknown(file);
}
