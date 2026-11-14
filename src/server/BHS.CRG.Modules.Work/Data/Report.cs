using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Data;

namespace BHS.CRG.Modules.Work.Data;

/// <summary>Смена отчёта (ТЗ MOB-35): дневная и ночная работа одной даты — разные отчёты.</summary>
public enum ReportShift
{
    Day,
    Night,
}

/// <summary>Состояние отчёта. Пока одно — черновик; подача и приёмка приходят с <c>work-15</c> и <c>work-16</c>.</summary>
public enum ReportStatus
{
    Draft,
}

/// <summary>
/// Отчёт монтажника за день (ТЗ WORK-4, WORK-7, MOB-35, WORK-58; change <c>work-13-report-model</c>): что сотрудник
/// сделал на стройке за дату и смену и сколько часов это заняло.
///
/// <para><b>Ключ — четвёрка «сотрудник, стройка, дата, смена».</b> Без смены ночная работа затирала бы дневную; без
/// стройки день на двух стройках был бы одним отчётом, и менеджер одной стройки видел бы работы другой. Ключ стоит
/// уникальным индексом в базе (<c>ux_reports_key</c>), а не только проверкой: два телефона одного монтажника
/// приходят одновременно.</para>
///
/// <para><b>Идентификатор — от клиента</b> (ТЗ MOB-15): телефон заводит отчёт без связи, и повтор того же запроса
/// обязан найти тот же отчёт, а не завести второй. Поэтому <see cref="Id" /> не генерирует ни база, ни сервер.</para>
///
/// <para><b>Изолированная запись</b> (<see cref="IIsolatedRecord" />, <c>work-07</c>): монтажник видит только свои
/// отчёты, менеджер — отчёты своих строек. Читается запись только в файле чтения с параметром доступа; набора
/// <c>DbSet</c> у неё нет, и навигаций к ней от общих сущностей тоже (сторож — <c>IsolatedReadInventoryTests</c>).</para>
///
/// <para><b>Часы — на отчёт целиком</b> (ТЗ WORK-7): строки их не несут. Ноль часов — сведение, а не пропуск;
/// <c>null</c> — черновик, где часы ещё не названы.</para>
/// </summary>
public sealed class Report : IIsolatedRecord
{
    /// <summary>Предел часов за смену (ТЗ WORK-7): сутки.</summary>
    public const decimal MaxHours = 24m;

    private Report() { }

    public Guid Id { get; private set; }

    /// <summary>Сотрудник — запись справочника ядра «Сотрудник» (держащая ссылка, CORE-34.1).</summary>
    public Guid EmployeeId { get; private set; }

    /// <summary>Стройка ядра (держащая ссылка, CORE-34.1). Обязательна (ТЗ WORK-58).</summary>
    public Guid ConstructionId { get; private set; }

    /// <summary>Дата отчёта — дата стройки по её поясу (ТЗ WORK-8).</summary>
    public DateOnly Date { get; private set; }

    public ReportShift Shift { get; private set; }

    /// <summary>Часы за смену, от 0 до 24; <c>null</c> — ещё не названы.</summary>
    public decimal? Hours { get; private set; }

    public ReportStatus Status { get; private set; }

    public string? Note { get; private set; }

    /// <summary>
    /// Номер последнего изменения в серверной последовательности <c>work.report_seq</c> (ТЗ MOB-16) — тот же, что
    /// у последней записи <see cref="ReportChange" /> отчёта. По нему отдаёт изменения курсор <c>since</c>.
    /// </summary>
    public long Seq { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Строки работ в порядке ввода (<see cref="ReportLine.Position" />).</summary>
    public List<ReportLine> Lines { get; private set; } = [];

    public static Report Create(
        Guid id, Guid employeeId, Guid constructionId, DateOnly date, ReportShift shift, decimal? hours,
        string? note, long seq, DateTimeOffset now)
    {
        if (id == Guid.Empty) throw new InvalidRequestException("Не назван идентификатор отчёта (id): его создаёт клиент.");
        if (constructionId == Guid.Empty) throw new InvalidRequestException(ReportRules.NoSite);
        ReportRules.EnsureHours(hours);
        return new Report
        {
            Id = id, EmployeeId = employeeId, ConstructionId = constructionId, Date = date, Shift = shift,
            Hours = hours, Status = ReportStatus.Draft, Note = ReportRules.Text(note), Seq = seq, CreatedAt = now, UpdatedAt = now,
        };
    }

    /// <summary>Правка шапки: стройка, дата, смена, часы и примечание. Сотрудник не меняется — это другой отчёт.</summary>
    public void ChangeHeader(Guid constructionId, DateOnly date, ReportShift shift, decimal? hours, string? note, long seq, DateTimeOffset now)
    {
        if (constructionId == Guid.Empty) throw new InvalidRequestException(ReportRules.NoSite);
        ReportRules.EnsureHours(hours);
        ConstructionId = constructionId;
        Date = date;
        Shift = shift;
        Hours = hours;
        Note = ReportRules.Text(note);
        Touch(seq, now);
    }

    /// <summary>Отметить изменение: новый номер последовательности и время.</summary>
    public void Touch(long seq, DateTimeOffset now)
    {
        Seq = seq;
        UpdatedAt = now;
    }

    /// <summary>Смена кодом ответа: <c>day</c> | <c>night</c>.</summary>
    public static string ShiftCode(ReportShift shift) => shift == ReportShift.Night ? "night" : "day";

    /// <summary>Состояние кодом ответа и колонки: <c>draft</c>. Тот же вид пишет база (<c>HasConversion</c>).</summary>
    public static string StatusCode(ReportStatus status) => status.ToString().ToLowerInvariant();

    /// <summary>Разобрать смену из запроса: не названа — дневная (ТЗ MOB-35), неизвестная — отказ с названием поля.</summary>
    public static ReportShift ParseShift(string? code) => code?.Trim().ToLowerInvariant() switch
    {
        null or "" or "day" => ReportShift.Day,
        "night" => ReportShift.Night,
        _ => throw new InvalidRequestException($"Смена (shift) «{code}» неизвестна: ожидается day или night."),
    };
}
