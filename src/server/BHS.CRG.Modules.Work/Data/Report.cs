using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Data;

namespace BHS.CRG.Modules.Work.Data;

/// <summary>Смена отчёта (ТЗ MOB-35): дневная и ночная работа одной даты — разные отчёты.</summary>
public enum ReportShift
{
    Day,
    Night,
}

/// <summary>
/// Состояние отчёта (ТЗ WORK-9): черновик → подан → принят либо возвращён, и возвращённый снова подаётся. Подачу
/// приносит <c>work-15</c>, приёмку и возврат — <c>work-16</c>.
/// </summary>
public enum ReportStatus
{
    Draft,
    Submitted,
    Accepted,
    Returned,
}

/// <summary>Кто подал отчёт (ТЗ WORK-10, WORK-54): человек или автоподача по концу окна.</summary>
public enum ReportSubmitter
{
    Manual,
    Auto,
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

    /// <summary>
    /// Кто ввёл отчёт за сотрудника (ТЗ WORK-31, WORK-2): учётная запись менеджера; <c>null</c> — отчёт завёл сам
    /// сотрудник. Ставится при заведении через адрес ввода и не меняется: автор ввода — факт заведения, а правки
    /// пишет журнал (<c>work-14</c>).
    /// </summary>
    public Guid? EnteredByUserId { get; private set; }

    /// <summary>Имя автора ввода снимком на момент заведения: учётную запись могут переименовать или удалить.</summary>
    public string? EnteredByName { get; private set; }

    /// <summary>
    /// Момент подачи отчёта (ТЗ WORK-11, WORK-54); <c>null</c> — отчёт не подавался, это черновик. Пишет подача
    /// (<c>work-15</c>) — одной записью со статусом, чтобы «черновик» по статусу (удаление) и по моменту (журнал) не
    /// разошлись; возврат на правку (<c>work-16</c>) момент не стирает. Журнал отчёта (<c>work-14</c>) сравнивает с ним
    /// время правки на устройстве — «правка после подачи» — и прячет журнал черновика от всех, кроме сотрудника и автора
    /// ввода.
    /// </summary>
    public DateTimeOffset? SubmittedAt { get; private set; }

    /// <summary>
    /// Конец окна подачи — мгновение (ТЗ WORK-52, <c>work-15</c>): считается правилом
    /// <see cref="Submission.SubmissionWindow" /> при заведении и при смене даты, смены или стройки и записывается.
    /// Смена пояса стройки задним числом его не двигает.
    /// </summary>
    public DateTimeOffset WindowEndsAt { get; private set; }

    /// <summary>Кто подал: человек или автоподача; <c>null</c> — отчёт не подавался.</summary>
    public ReportSubmitter? SubmittedBy { get; private set; }

    /// <summary>
    /// Что не заполнено к автоподаче (ТЗ WORK-6, WORK-10) — JSON-массив <c>[{ line, lineId, field }]</c>; <c>null</c> —
    /// всё заполнено или подал человек (ручная подача без обязательных полей не проходит).
    /// </summary>
    public string? RequiredMissing { get; private set; }

    /// <summary>Строки работ в порядке ввода (<see cref="ReportLine.Position" />).</summary>
    public List<ReportLine> Lines { get; private set; } = [];

    public static Report Create(
        Guid id, Guid employeeId, Guid constructionId, DateOnly date, ReportShift shift, decimal? hours,
        string? note, long seq, DateTimeOffset now, DateTimeOffset windowEndsAt)
    {
        if (id == Guid.Empty) throw new InvalidRequestException("Не назван идентификатор отчёта (id): его создаёт клиент.");
        if (constructionId == Guid.Empty) throw new InvalidRequestException(ReportRules.NoSite);
        ReportRules.EnsureHours(hours);
        return new Report
        {
            Id = id, EmployeeId = employeeId, ConstructionId = constructionId, Date = date, Shift = shift,
            Hours = hours, Status = ReportStatus.Draft, Note = ReportRules.Text(note), Seq = seq, CreatedAt = now, UpdatedAt = now,
            WindowEndsAt = windowEndsAt.ToUniversalTime(),
        };
    }

    /// <summary>Отметить, что отчёт ввёл за сотрудника другой пользователь (ТЗ WORK-31) — только при заведении.</summary>
    public void EnteredBy(Guid userId, string name)
    {
        EnteredByUserId = userId;
        EnteredByName = string.IsNullOrWhiteSpace(name) ? userId.ToString() : name.Trim();
    }

    /// <summary>Отказ сменить ключ поданного или принятого отчёта (ТЗ MOB-35).</summary>
    public const string KeyLocked =
        "Дату, смену и стройку поданного отчёта меняет только возврат на правку: после подачи ключ отчёта не меняется.";

    /// <summary>Сменятся ли этой правкой дата, смена или стройка — ключ отчёта и основа окна подачи.</summary>
    public bool KeyChanges(Guid constructionId, DateOnly date, ReportShift shift) =>
        constructionId != ConstructionId || date != Date || shift != Shift;

    /// <summary>Ключ отчёта меняется только у черновика и возвращённого (ТЗ MOB-35, WORK-9).</summary>
    public bool KeyEditable() => Status is ReportStatus.Draft or ReportStatus.Returned;

    /// <summary>
    /// Правка шапки: стройка, дата, смена, часы и примечание. Сотрудник не меняется — это другой отчёт.
    /// <paramref name="windowEndsAt" /> — конец окна по новому ключу; записывается, только если ключ сменился: правка
    /// часов окна не трогает, и пояс, исправленный задним числом, тоже (ТЗ WORK-52).
    /// </summary>
    public void ChangeHeader(
        Guid constructionId, DateOnly date, ReportShift shift, decimal? hours, string? note, long seq, DateTimeOffset now,
        DateTimeOffset windowEndsAt)
    {
        if (constructionId == Guid.Empty) throw new InvalidRequestException(ReportRules.NoSite);
        ReportRules.EnsureHours(hours);
        if (KeyChanges(constructionId, date, shift))
        {
            if (!KeyEditable()) throw new ConflictException(KeyLocked);
            WindowEndsAt = windowEndsAt.ToUniversalTime();
        }
        ConstructionId = constructionId;
        Date = date;
        Shift = shift;
        Hours = hours;
        Note = ReportRules.Text(note);
        Touch(seq, now);
    }

    /// <summary>
    /// Отказ перехода «подан» (ТЗ WORK-9) с названием текущего статуса; <c>null</c> — подать можно. Поданный здесь
    /// отказом не назван: повтор подачи отвечает 200 <c>alreadySubmitted</c>, и решает это адрес.
    /// </summary>
    public string? SubmitRefusal() => Status switch
    {
        ReportStatus.Draft or ReportStatus.Returned => null,
        ReportStatus.Accepted => "Отчёт уже принят: подавать его снова нечего.",
        _ => "Отчёт уже подан.",
    };

    /// <summary>
    /// Подать отчёт (ТЗ WORK-9, WORK-10): статус, момент подачи и кто подал — одной записью, чтобы «черновик» по статусу
    /// (удаление) и по моменту (журнал, <c>work-14</c>) не разошлись. Повторная подача возвращённого пишет новый
    /// момент.
    /// </summary>
    /// <param name="requiredMissing">Незаполненное к автоподаче — JSON-массив; у ручной подачи <c>null</c>.</param>
    public void Submit(DateTimeOffset at, ReportSubmitter by, string? requiredMissing, long seq, DateTimeOffset now)
    {
        if (SubmitRefusal() is { } refusal) throw new ConflictException(refusal);
        Status = ReportStatus.Submitted;
        SubmittedAt = at.ToUniversalTime();
        SubmittedBy = by;
        RequiredMissing = requiredMissing;
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

    /// <summary>
    /// Состояние кодом ответа и колонки: <c>draft</c> | <c>submitted</c> | <c>accepted</c> | <c>returned</c>. Тот же вид
    /// пишет база (<c>HasConversion</c>).
    /// </summary>
    public static string StatusCode(ReportStatus status) => status.ToString().ToLowerInvariant();

    /// <summary>Кто подал кодом ответа и колонки: <c>manual</c> | <c>auto</c>.</summary>
    public static string SubmitterCode(ReportSubmitter by) => by.ToString().ToLowerInvariant();

    /// <summary>Разобрать смену из запроса: не названа — дневная (ТЗ MOB-35), неизвестная — отказ с названием поля.</summary>
    public static ReportShift ParseShift(string? code) => code?.Trim().ToLowerInvariant() switch
    {
        null or "" or "day" => ReportShift.Day,
        "night" => ReportShift.Night,
        _ => throw new InvalidRequestException($"Смена (shift) «{code}» неизвестна: ожидается day или night."),
    };
}
