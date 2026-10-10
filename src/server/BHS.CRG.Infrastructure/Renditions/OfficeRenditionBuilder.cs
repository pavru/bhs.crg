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
public sealed class OfficeRenditionBuilder(OfficeConverterClient converter)
{
    /// <returns><see cref="Rendition.Built" /> или <see cref="Rendition.Refused" />.</returns>
    public async Task<Rendition> BuildAsync(byte[] file, OfficeFormat format, CancellationToken ct)
    {
        if (OfficeFileBudget.Check(format, file) is { } overBudget) return overBudget;

        IReadOnlyList<string>? cellWords = null;
        if (format is OfficeFormat.Xlsx or OfficeFormat.Xls)
        {
            var cells = ExcelCellTexts.From(file);
            if (cells.Refusal is not null) return cells.Refusal;
            cellWords = cells.Words;
        }

        var reply = await converter.ConvertAsync(file, format, ct);
        return reply.Refusal ?? RenditionPostconditions.Check(reply.Pdf!, cellWords);
    }

    /// <summary>
    /// Что сказать о файле, вид которого не определился, если причина в нём самом: защищён паролем
    /// или повреждён. <c>null</c> — файл просто другого вида.
    /// </summary>
    public static Rendition.Refused? WhyNotOffice(ReadOnlySpan<byte> file) => OfficeFileBudget.WhyUnknown(file);
}
