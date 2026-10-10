using System.Text.Json;

namespace BHS.CRG.Modules.Costs.Data;

/// <summary>Чем кончилось распознавание скана счёта.</summary>
public enum InvoiceRecognitionOutcome
{
    /// <summary>Поставлено и ещё не кончилось. Идёт ли оно на самом деле, знает фоновая задача:
    /// задача, которой больше нет или которая упала, значит «прервано», а не «идёт».</summary>
    Pending,

    /// <summary>Прочитано и разложено по счёту.</summary>
    Done,

    /// <summary>Не прочитано; причина названа.</summary>
    Failed,
}

/// <summary>
/// Распознавание скана счёта: что поставлено, чем кончилось и что из прочитанного в счёт не легло
/// (ТЗ COST-8, задача B1b, issue #1077).
///
/// <para><b>Почему отдельной таблицей, а не колонками счёта.</b> У счёта есть версия строки, и её
/// называет каждая правка формы. Постановка задачи и её отказ счёта не меняют — и двигать его версию
/// не должны: иначе человек, который заполняет черновик руками, пока скан читается, получил бы «счёт
/// тем временем изменили» из-за распознавания, которое не удалось. Версию счёта двигает только
/// удачное распознавание — в тот момент, когда оно действительно что-то записало.</para>
///
/// <para>Запись одна на счёт: повтор заменяет прежний исход, а не копит историю — её ведёт журнал
/// действий.</para>
/// </summary>
public sealed class InvoiceRecognition
{
    public const int ErrorLength = 2000;


    private InvoiceRecognition() { }

    public Guid InvoiceId { get; private set; }

    /// <summary>Фоновая задача. Пусто — постановка не дошла до очереди.</summary>
    public Guid? JobId { get; private set; }

    /// <summary>
    /// Какой файл читается. Скан у счёта можно заменить, и поля, пришедшие от ПРЕЖНЕЙ бумаги, в счёт
    /// лечь не должны: обработчик сверяет путь перед записью.
    /// </summary>
    public string ScanBlobPath { get; private set; } = string.Empty;

    /// <summary>
    /// По какому читаемому образу прочитано (issue #1270). Пусто — движку ушёл сам файл: PDF и
    /// изображения читаются как есть.
    ///
    /// <para>Образ Excel и Word можно перестроить, и тогда у файла новый вид, а прочитанное — от
    /// прежнего. Бумага та же, поэтому распознанное не прячется, а помечается: сверять его с тем,
    /// что сейчас на экране, надо внимательнее.</para>
    ///
    /// <para>⚠️ Колонка держит прежний образ живым: уборка осиротевших файлов считает путь в
    /// таблице модуля держателем. Это осознанно и недолго — до следующего распознавания счёта.</para>
    /// </summary>
    public string? ImageBlobPath { get; private set; }

    public InvoiceRecognitionOutcome Outcome { get; private set; }

    /// <summary>Вид причины отказа — имя <c>RecognitionRefusal</c> либо <see cref="Interrupted" />.</summary>
    public string? Reason { get; private set; }

    /// <summary>Причина словами — наш текст, для человека.</summary>
    public string? Error { get; private set; }

    /// <summary>Кто распознал — движок и модель.</summary>
    public string? Engine { get; private set; }

    /// <summary>
    /// Что прочитано в шапке, как есть: «ключ профиля → текст». Хранится целиком, а не только то, что
    /// легло в поля: по этому сопоставляются стороны (название и ИНН) и отвечается на вопрос «а что
    /// было в скане».
    /// </summary>
    public JsonDocument? Values { get; private set; }

    /// <summary>
    /// Что прочитано, но в поле не записано: «ключ реквизита → текст из скана». Поле было занято —
    /// человек успел заполнить его сам, и его значение побеждает, — либо значение не разобрать как
    /// дату или сумму. Форма показывает это под полем; запись не затирается.
    /// </summary>
    public JsonDocument? Offers { get; private set; }

    /// <summary>
    /// Распознанные строки, которые в счёт НЕ легли: у счёта уже были свои. Сами не добавляются —
    /// распознанное есть предложение. Пусто — строки легли в счёт либо их не было.
    /// </summary>
    public JsonDocument? Lines { get; private set; }

    /// <summary>Оговорки исхода словами: «таблица не разобрана», «дата не прочитана».</summary>
    // Массивом, а не списком «только для чтения» над полем: EF заполняет свойство через найденное по
    // имени поле, и значение иного вида, чем само поле, при чтении строки даёт отказ приведения.
    public string[] Notes { get; private set; } = [];

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset? FinishedAt { get; private set; }

    /// <summary>Вид причины «задача прервана»: перезапуск сервера или постановка, не дошедшая до очереди.</summary>
    public const string Interrupted = "Interrupted";

    public static InvoiceRecognition Start(Guid invoiceId, string scanBlobPath)
    {
        var recognition = new InvoiceRecognition { InvoiceId = invoiceId };
        recognition.Restart(scanBlobPath);
        return recognition;
    }

    /// <summary>Новая попытка: прежний исход стирается целиком — он был про другую попытку.</summary>
    public void Restart(string scanBlobPath)
    {
        ScanBlobPath = scanBlobPath;
        ImageBlobPath = null;
        JobId = null;
        Outcome = InvoiceRecognitionOutcome.Pending;
        Reason = Error = Engine = null;
        Values = Offers = Lines = null;
        Notes = [];
        StartedAt = DateTimeOffset.UtcNow;
        FinishedAt = null;
    }

    public void Queued(Guid jobId) => JobId = jobId;

    public void Fail(string reason, string error)
    {
        Outcome = InvoiceRecognitionOutcome.Failed;
        Reason = reason;
        Error = error.Length > ErrorLength ? error[..ErrorLength] : error;
        FinishedAt = DateTimeOffset.UtcNow;
    }

    public void Finish(
        string? engine, JsonDocument values, JsonDocument? offers, JsonDocument? lines, IEnumerable<string> remarks,
        string? imageBlobPath = null)
    {
        Outcome = InvoiceRecognitionOutcome.Done;
        ImageBlobPath = imageBlobPath;
        Engine = engine;
        Values = values;
        Offers = offers;
        Lines = lines;
        Notes = [.. remarks];
        FinishedAt = DateTimeOffset.UtcNow;
    }
}
