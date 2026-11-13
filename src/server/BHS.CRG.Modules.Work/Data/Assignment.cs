using System.Globalization;
using BHS.CRG.Domain.Common;

namespace BHS.CRG.Modules.Work.Data;

/// <summary>Вид назначения (ТЗ WORK-21, WORK-46).</summary>
public enum AssignmentKind
{
    /// <summary>Монтажник: подаёт отчёты по стройке. Период закрыт всегда.</summary>
    Installer,

    /// <summary>Менеджер: «своя» стройка — назначает на неё и принимает её отчёты. Конец может быть открытым.</summary>
    Manager,
}

/// <summary>
/// Назначение сотрудника на стройку на период (ТЗ WORK-21, WORK-46; change <c>work-11-assignments</c>).
///
/// <para><b>Даты — даты стройки</b> (ТЗ WORK-8): «назначен с 01.11» значит с полуночи 01.11 по поясу
/// стройки, а не сервера. Поэтому «действует ли сегодня» спрашивается у часов ядра по стройке
/// (<c>IModuleClock.TodayOnSiteAsync</c>), а не здесь.</para>
///
/// <para><b>Сотрудник, стройка и вид не меняются.</b> Другое назначение — это снятие и заведение: правка
/// вида переписала бы прошлое — отчёты, поданные монтажником, стали бы отчётами менеджера.</para>
///
/// <para>Конец периода у монтажника обязателен: бессрочный монтажник подавал бы отчёты на стройку, с
/// которой давно ушёл. Это правило стоит и в базе (<c>ck_assignments_installer_end</c>) — на запись
/// мимо этого класса.</para>
/// </summary>
public sealed class Assignment
{
    private Assignment() { }

    public Guid Id { get; private set; }

    /// <summary>Сотрудник — запись справочника ядра «Сотрудник» (держащая ссылка, CORE-34.1).</summary>
    public Guid EmployeeId { get; private set; }

    /// <summary>Стройка ядра (держащая ссылка, CORE-34.1).</summary>
    public Guid ConstructionId { get; private set; }

    public AssignmentKind Kind { get; private set; }

    /// <summary>Первый день назначения — дата стройки.</summary>
    public DateOnly DateFrom { get; private set; }

    /// <summary>Последний день назначения включительно; <c>null</c> — без срока (только менеджер).</summary>
    public DateOnly? DateTo { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Assignment Create(
        Guid employeeId, Guid constructionId, AssignmentKind kind, DateOnly from, DateOnly? to, DateTimeOffset now)
    {
        EnsurePeriod(kind, from, to);
        return new Assignment
        {
            Id = Guid.NewGuid(), EmployeeId = employeeId, ConstructionId = constructionId, Kind = kind,
            DateFrom = from, DateTo = to, CreatedAt = now, UpdatedAt = now,
        };
    }

    /// <summary>Правка периода — единственная правка назначения.</summary>
    public void ChangePeriod(DateOnly from, DateOnly? to, DateTimeOffset now)
    {
        EnsurePeriod(Kind, from, to);
        DateFrom = from;
        DateTo = to;
        UpdatedAt = now;
    }

    /// <summary>Действует ли назначение в этот день стройки.</summary>
    public bool Covers(DateOnly day) => DateFrom <= day && (DateTo is not { } to || day <= to);

    /// <summary>Пересекается ли с периодом <paramref name="from" />–<paramref name="to" /> (<c>null</c> — без срока).</summary>
    public bool Overlaps(DateOnly from, DateOnly? to) =>
        (DateTo is not { } end || from <= end) && (to is not { } until || DateFrom <= until);

    /// <summary>Период словами: «01.11.2026–15.11.2026» или «с 01.11.2026 без срока».</summary>
    public string Period => PeriodText(DateFrom, DateTo);

    public static string PeriodText(DateOnly from, DateOnly? to) =>
        to is { } end ? $"{Day(from)}–{Day(end)}" : $"с {Day(from)} без срока";

    /// <summary>Вид словами — для отказов и журнала.</summary>
    public static string KindText(AssignmentKind kind) => kind == AssignmentKind.Manager ? "менеджер" : "монтажник";

    /// <summary>Вид кодом ответа: <c>installer</c> | <c>manager</c>.</summary>
    public static string KindCode(AssignmentKind kind) => kind == AssignmentKind.Manager ? "manager" : "installer";

    /// <summary>Разобрать вид из запроса; неизвестный — отказ с названием поля.</summary>
    public static AssignmentKind ParseKind(string? code) => code?.Trim().ToLowerInvariant() switch
    {
        "installer" => AssignmentKind.Installer,
        "manager" => AssignmentKind.Manager,
        null or "" => throw new InvalidRequestException("Не назван вид назначения (kind): installer или manager."),
        _ => throw new InvalidRequestException($"Вид назначения «{code}» неизвестен: ожидается installer или manager."),
    };

    public static string Day(DateOnly day) => day.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

    private static void EnsurePeriod(AssignmentKind kind, DateOnly from, DateOnly? to)
    {
        if (to is null && kind == AssignmentKind.Installer)
            throw new InvalidRequestException(
                "Не задан конец периода (dateTo): у назначения монтажника период закрыт — без срока назначается только менеджер.");
        if (to is { } end && end < from)
            throw new InvalidRequestException(
                $"Конец периода (dateTo) {Day(end)} раньше начала (dateFrom) {Day(from)}.");
    }
}
