using BHS.CRG.Domain.Common;

namespace BHS.CRG.Domain.Storage;

/// <summary>Чем кончилось приведение файла к читаемому виду.</summary>
public enum RenditionState
{
    /// <summary>Файл читается сам: PDF или изображение. Образа нет, и он не нужен.</summary>
    AsIs,

    /// <summary>Образ построен и лежит в хранилище.</summary>
    Built,

    /// <summary>Образа не будет; запись несёт вид причины и текст для человека.</summary>
    Refused,
}

/// <summary>
/// Читаемый образ файла — запись ядра по ключу «путь оригинала» (issue #1269).
///
/// <para><b>Почему у ядра, а не у владельца файла.</b> Образ — функция от оригинала и конвертера, а
/// не свойство счёта. Положи его в строку счёта — и построение меняло бы версию этой строки: человек
/// приложил файл, правит шапку и получает «счёт тем временем изменили» от собственной загрузки. А
/// каждый следующий владелец файлов заново писал бы «построить, удалить при замене, хранить отказ».</para>
///
/// <para><b>Запись отдаётся как есть</b>, каким бы старым конвертером она ни сделана: пока её не
/// тронули руками, то, что видел человек, и то, что читала модель, — один и тот же образ.
/// Перестроить — явное действие, и новый образ ложится под новым путём.</para>
///
/// <para>⚠️ <b>Две колонки с путями уборка осиротевших файлов читает по-разному</b>
/// (<c>LiveBlobPathScan</c>). <see cref="ImageBlobPath" /> — держатель: пока запись есть, образ
/// живой. <see cref="OriginalBlobPath" /> из отбора исключён ЯВНО, по имени: считай уборка его
/// ссылкой — и ни один оригинал, у которого есть образ, не стал бы осиротевшим никогда. Жизнь
/// оригинала решает его владелец, а не эта таблица.</para>
/// </summary>
public class RenditionRecord : Entity
{
    /// <summary>Путь оригинала в хранилище — ключ. Уникален и неизменяем: внутри пути guid.</summary>
    public string OriginalBlobPath { get; private set; } = default!;

    public RenditionState State { get; private set; }

    /// <summary>Вид файла, который читается сам (<see cref="RenditionState.AsIs" />).</summary>
    public string? Mime { get; private set; }

    /// <summary>Путь построенного PDF (<see cref="RenditionState.Built" />).</summary>
    public string? ImageBlobPath { get; private set; }

    public int? Pages { get; private set; }

    /// <summary>Что человеку стоит знать об образе: «страниц: 2, из них без текста: 1».</summary>
    public List<string> Notes { get; private set; } = [];

    /// <summary>Вид причины отказа — имя из перечня службы образов.</summary>
    public string? RefusalKind { get; private set; }

    public string? RefusalReason { get; private set; }

    /// <summary>Чем построен образ: «gotenberg 8.37.0». <c>null</c> — конвертер себя не назвал.</summary>
    public string? Converter { get; private set; }

    private RenditionRecord() { }

    public static RenditionRecord AsIs(string originalPath, string mime) =>
        new() { OriginalBlobPath = originalPath, State = RenditionState.AsIs, Mime = mime };

    public static RenditionRecord Built(
        string originalPath, string imagePath, int pages, IEnumerable<string> notes, string? converter) =>
        new()
        {
            OriginalBlobPath = originalPath,
            State = RenditionState.Built,
            ImageBlobPath = imagePath,
            Pages = pages,
            Notes = [.. notes],
            Converter = converter,
        };

    public static RenditionRecord Refused(string originalPath, string kind, string reason) =>
        new() { OriginalBlobPath = originalPath, State = RenditionState.Refused, RefusalKind = kind, RefusalReason = reason };

    /// <summary>
    /// Заменить содержимое записи результатом нового построения. Ключ и сама строка остаются:
    /// у пути оригинала запись одна, и «какой образ сейчас» всегда отвечает одним местом.
    /// </summary>
    public void ReplaceWith(RenditionRecord fresh)
    {
        State = fresh.State;
        Mime = fresh.Mime;
        ImageBlobPath = fresh.ImageBlobPath;
        Pages = fresh.Pages;
        Notes = [.. fresh.Notes];
        RefusalKind = fresh.RefusalKind;
        RefusalReason = fresh.RefusalReason;
        Converter = fresh.Converter;
        TouchUpdatedAt();
    }
}
