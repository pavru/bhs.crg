using BHS.CRG.Modules.Data;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Modules.Work.Data;

/// <summary>Дошёл ли файл фото: объявленное строкой фото ждёт файла, пришедшее — хранится (ТЗ WORK-34).</summary>
public enum LinePhotoState
{
    /// <summary>Строка назвала фото, файла ещё нет: отчёт ушёл, не дожидаясь снимков.</summary>
    Pending,

    /// <summary>Файл и превью лежат в хранилище модуля.</summary>
    Stored,
}

/// <summary>
/// Фото строки работы (ТЗ WORK-33, WORK-34, WORK-35, WORK-55; change <c>work-17-photos</c>) — дочерняя запись строки
/// отчёта (D7 зонтика).
///
/// <para><b>Изолированная</b> (<see cref="IIsolatedRecord" />): фото — доказательство работы, на нём лицо монтажника,
/// место и геометка. Читается только файлом чтения отчёта (<see cref="ReportReads" />) с охватом читателя, а файл
/// выдают только адреса модуля после чтения отчёта (<c>PhotoEndpoints</c>).</para>
///
/// <para><b>Идентификатор — от клиента</b> (ТЗ MOB-15): повтор загрузки с тем же идентификатором не заводит второго фото.
/// Строка объявляет фото при сохранении — запись в <see cref="LinePhotoState.Pending" />, — файл догоняет отдельной
/// очередью.</para>
///
/// <para><b>Геометка — колонками</b>, а не в файле: порт фото (<c>work-05</c>) перекодирует снимок всегда и метаданных
/// не переносит, а координаты и время съёмки отдаёт полями. Их несёт резервная копия вместе со строкой. Показ
/// координат отдельным полем — вне этапа.</para>
/// </summary>
public sealed class LinePhoto : IIsolatedRecord
{
    public const int OriginalNameLimit = 255;

    private LinePhoto() { }

    /// <summary>Идентификатор — от клиента (ТЗ MOB-15).</summary>
    public Guid Id { get; private set; }

    public Guid LineId { get; private set; }

    public LinePhotoState State { get; private set; }

    /// <summary>Имя исходного файла, объявленное при сохранении строки. Подсказка для досылки.</summary>
    public string? OriginalName { get; private set; }

    /// <summary>Путь сжатого снимка в хранилище модуля; у ждущего — <c>null</c>.</summary>
    public string? BlobPath { get; private set; }

    /// <summary>Путь превью в хранилище модуля; у ждущего — <c>null</c>.</summary>
    public string? PreviewBlobPath { get; private set; }

    /// <summary>Байты сжатого снимка (без превью).</summary>
    public long? Size { get; private set; }

    public int? Width { get; private set; }

    public int? Height { get; private set; }

    public double? GeoLat { get; private set; }

    public double? GeoLon { get; private set; }

    public double? GeoAlt { get; private set; }

    /// <summary>Время съёмки из снимка — данными, как геометка.</summary>
    public DateTimeOffset? TakenAt { get; private set; }

    /// <summary>Когда строка назвала фото (или когда файл пришёл без объявления).</summary>
    public DateTimeOffset DeclaredAt { get; private set; }

    /// <summary>Когда файл лёг в хранилище.</summary>
    public DateTimeOffset? StoredAt { get; private set; }

    // Пустой UUID означает открытый период, null — запись до появления этих колонок.
    public Guid? DeclaredInClosingId { get; private set; }

    public Guid? StoredInClosingId { get; private set; }

    /// <summary>
    /// Номер изменения отчётов сотрудника, под которым пришёл файл (ТЗ MOB-16). Доставка объявленного фото отчёт не
    /// трогает — ни номера, ни версии, — и лента <c>?since</c> (<see cref="ReportReads.SinceAsync" />) узнаёт о ней по
    /// этому номеру.
    /// </summary>
    public long? StoredSeq { get; private set; }

    /// <summary>Фото, названное строкой при сохранении, — ждёт файла.</summary>
    public static LinePhoto Declared(Guid id, Guid lineId, DateTimeOffset now, Guid? closingId = null, string? originalName = null) =>
        new()
        {
            Id = id, LineId = lineId, State = LinePhotoState.Pending, DeclaredAt = Stamp(now),
            DeclaredInClosingId = closingId ?? Guid.Empty, OriginalName = originalName,
        };

    /// <summary>Фото, файл которого пришёл, а строка его не называла (правка, ТЗ WORK-11).</summary>
    public static LinePhoto Arrived(Guid id, Guid lineId, PhotoFiles files, long seq, DateTimeOffset now)
    {
        var photo = Declared(id, lineId, now);
        photo.Store(files, seq, now);
        return photo;
    }

    /// <summary>Файл дошёл: пути, размеры и данные снимка. Пишет только путь загрузки (<c>LinePhotos</c>).</summary>
    public void Store(PhotoFiles files, long seq, DateTimeOffset now, Guid? closingId = null)
    {
        State = LinePhotoState.Stored;
        BlobPath = files.BlobPath;
        PreviewBlobPath = files.PreviewBlobPath;
        Size = files.Prepared.Image.LongLength;
        Width = files.Prepared.Width;
        Height = files.Prepared.Height;
        GeoLat = files.Prepared.Geo?.Lat;
        GeoLon = files.Prepared.Geo?.Lon;
        GeoAlt = files.Prepared.Geo?.Alt;
        // Пояс съёмки — в колонке UTC: timestamptz пояса не хранит, а момент тот же.
        TakenAt = files.Prepared.TakenAt?.ToUniversalTime();
        StoredAt = Stamp(now);
        StoredInClosingId = closingId ?? Guid.Empty;
        StoredSeq = seq;
    }

    internal bool DeclaredBeforeClosing(Guid closingId, DateTimeOffset at) =>
        DeclaredInClosingId is { } declared ? declared != closingId : DeclaredAt < at;

    /// <summary>
    /// Момент с точностью базы — до микросекунды, в UTC: ответ первой загрузки и её повтора (прочитанный из базы) обязаны
    /// совпадать до знака (ТЗ MOB-15).
    /// </summary>
    internal static DateTimeOffset Stamp(DateTimeOffset at)
    {
        var utc = at.ToUniversalTime();
        return utc.AddTicks(-(utc.Ticks % 10));
    }

    /// <summary>Пути файлов фото — чтобы удалить их после того, как ушла ссылка.</summary>
    public IEnumerable<string> Paths() => new[] { BlobPath, PreviewBlobPath }.OfType<string>();

    /// <summary>Код состояния в ответе: <c>pending</c> | <c>stored</c>.</summary>
    public static string StateCode(LinePhotoState state) => state.ToString().ToLowerInvariant();
}

/// <summary>Подготовленный снимок и пути, по которым он и превью легли в хранилище модуля.</summary>
public sealed record PhotoFiles(PreparedPhoto Prepared, string BlobPath, string PreviewBlobPath);

/// <summary>
/// След снятого фото (<c>work-17</c>, ТЗ WORK-34, MOB-15): файл фото догоняет отчёт своей очередью и может прийти уже
/// после того, как фото сняли со строки. Без следа такой файл выглядел бы новым фото и вернул бы снятое. По следу
/// загрузка отвечает 410: фото снято, файл не нужен. Снова названное строкой фото след снимает.
///
/// <para>След живёт со строкой (каскад): строку удалили — загрузка отвечает 404 по самой строке.</para>
/// </summary>
public sealed class LinePhotoRemoval
{
    private LinePhotoRemoval() { }

    /// <summary>Идентификатор снятого фото — ключ: идентификатор от клиента один на фото.</summary>
    public Guid PhotoId { get; private set; }

    public Guid LineId { get; private set; }

    public DateTimeOffset RemovedAt { get; private set; }

    public static LinePhotoRemoval Of(LinePhoto photo, DateTimeOffset now) =>
        new() { PhotoId = photo.Id, LineId = photo.LineId, RemovedAt = LinePhoto.Stamp(now) };
}
