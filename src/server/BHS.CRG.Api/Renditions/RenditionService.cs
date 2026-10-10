using BHS.CRG.Infrastructure.Renditions;
using BHS.CRG.Modules.Files;

namespace BHS.CRG.Api.Renditions;

/// <summary>
/// Читаемый образ файла (issue #1268): PDF с текстовым слоем, по которому работают просмотр и
/// распознавание. Служба отвечает на один вопрос — «дай читаемый образ этого файла» — и ответов у
/// неё три: читается как есть, образ построен, отказ с причиной (<see cref="Rendition" />).
///
/// <para>⚠️ <b>Образ никогда не источник значений для детерминированного кода.</b> Это картинка для
/// человека и для модели: числа в нём записаны по формату ячейки, текст может быть обрезан высотой
/// строки, а порядок слов задаёт вёрстка. Наборы данных читают Excel своим разборщиком; сумму,
/// ИНН или дату из образа не берёт никто, кроме распознавания, чей ответ проверяет человек.</para>
///
/// <para>Вид файла определяется здесь, по содержимому, и только потом файл уходит строителю: тот
/// живёт слоем ниже, реестра видов не знает и заявленному расширению поверить не может — его туда
/// не приносят. Здесь же только выбор ответа; хранит образ и отдаёт его модулям отдельная часть
/// (issue #1269).</para>
/// </summary>
public sealed class RenditionService(OfficeRenditionBuilder office)
{
    /// <param name="content">Файл целиком. Поток обязан уметь перемотку: у офисного файла читается
    /// оглавление, а оно в конце.</param>
    /// <summary>
    /// Ответ по одному началу файла (<see cref="FileKinds.HeadBytes" /> байт), если его хватает;
    /// <c>null</c> — нужен файл целиком (issue #1269).
    ///
    /// <para>Хватает его двум ответам из трёх: «читается сам» (PDF, изображение) и «файл другого
    /// вида». Скан на десятки мегабайт ради них из хранилища целиком не тянут. Офисный файл и всё,
    /// что на него похоже, — архив или контейнер, и о них начало не говорит ничего.</para>
    /// </summary>
    public async Task<Rendition?> ByHeadAsync(ReadOnlyMemory<byte> head, CancellationToken ct)
    {
        var kind = await FileKinds.DetectAsync(new MemoryStream(head.ToArray(), writable: false), ct);
        var reading = FileKindCatalog.Find(kind)?.Reading ?? FileReading.None;
        if (reading == FileReading.AsIs) return new Rendition.AsIs(kind);
        return reading == FileReading.None && !OfficeRenditionBuilder.MayBeOffice(head.Span) ? WrongFormat() : null;
    }

    public async Task<Rendition> BuildAsync(Stream content, CancellationToken ct)
    {
        if (!content.CanSeek) throw new ArgumentException("Нужен поток с перемоткой.", nameof(content));

        content.Position = 0;
        var kind = await FileKinds.DetectAsync(content, ct);
        var reading = FileKindCatalog.Find(kind)?.Reading ?? FileReading.None;
        if (reading == FileReading.AsIs) return new Rendition.AsIs(kind);

        // Файл больше предела к строителю не пойдёт, и целиком в память его не берём.
        if (content.Length > RenditionLimits.OfficeMaxBytes)
            return reading == FileReading.Rendition
                ? new Rendition.Refused(RenditionRefusal.TooLarge,
                    $"Файл слишком велик для читаемого вида: он больше {RenditionLimits.OfficeMaxBytes / (1024 * 1024)} МБ.")
                : WrongFormat();

        if (reading == FileReading.Rendition) return await office.BuildAsync(await WholeAsync(content, ct), FormatOf(kind), ct);

        // Вид не определился — но причина может быть в самом файле: под паролем он уже не архив,
        // а обрезанный — архив без оглавления. Человеку это не «файл другого вида». Целиком ради
        // этого читается только то, что начинается как архив или контейнер: остальным хватает начала.
        var head = new byte[FileKinds.HeadBytes];
        content.Position = 0;
        var read = await content.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct);
        if (!OfficeRenditionBuilder.MayBeOffice(head.AsSpan(0, read))) return WrongFormat();

        return OfficeRenditionBuilder.WhyNotOffice(await WholeAsync(content, ct)) switch
        {
            // Что под паролем, не видно — может быть, и не то, что мы читаем. Поэтому совет снять
            // пароль идёт вместе с перечнем: иначе человек снял бы его с презентации и получил
            // второй отказ, уже другой.
            RenditionRefusal.Protected => new(RenditionRefusal.Protected,
                "Файл защищён паролем, и что в нём, не видно. Снимите пароль и приложите файл заново — " +
                $"если это {FileKindCatalog.Words(ReadViaRendition, "или")}, он будет прочитан."),
            RenditionRefusal.Corrupted => new(RenditionRefusal.Corrupted,
                "Файл повреждён: он начинается как офисный, но прочитать его нельзя."),
            _ => WrongFormat(),
        };
    }

    private static async Task<byte[]> WholeAsync(Stream content, CancellationToken ct)
    {
        var file = new byte[content.Length];
        content.Position = 0;
        await content.ReadExactlyAsync(file, ct);
        return file;
    }

    private static IEnumerable<FileKind> ReadViaRendition =>
        FileKindCatalog.All.Where(known => known.Reading == FileReading.Rendition);

    /// <summary>
    /// Вид реестра — в формат строителя. Вид «через образ», которого здесь нет, — ошибка программы,
    /// а не файла: реестр пообещал образ, строить который некому. Открыт ради сторожа, который
    /// проходит по реестру: новый вид обязан появиться здесь тем же изменением.
    /// </summary>
    public static OfficeFormat FormatOf(string kind) => kind switch
    {
        FileKinds.Xlsx => OfficeFormat.Xlsx,
        FileKinds.Xls => OfficeFormat.Xls,
        FileKinds.Docx => OfficeFormat.Docx,
        _ => throw new InvalidOperationException($"Вид «{kind}» читается через образ, но строить образ из него нечем."),
    };

    private static Rendition.Refused WrongFormat()
    {
        var readable = FileKindCatalog.All.Where(known => known.Reading != FileReading.None);
        return new(RenditionRefusal.WrongFormat,
            $"Файл такого вида не читается. Читаются {FileKindCatalog.Words(readable)}.");
    }
}
