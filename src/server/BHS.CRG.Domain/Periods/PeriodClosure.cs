using BHS.CRG.Domain.Common;

namespace BHS.CRG.Domain.Periods;

/// <summary>Что записано: закрытие периода или отмена ошибочного закрытия.</summary>
public enum PeriodClosureKind
{
    Close = 0,
    Reopen = 1,
}

/// <summary>Чей период: компании целиком или одной стройки (ТЗ CORE-35).</summary>
public enum PeriodContourKind
{
    Company = 0,
    Construction = 1,
}

/// <summary>
/// Контур закрытия — явным значением, а не «стройка или null»: граница стройки всегда не раньше
/// границы компании, и стройка, потерянная по дороге (<c>null</c> по ошибке), молча дала бы более
/// РАННЮЮ границу — запись легла бы в период, закрытый для её стройки.
/// </summary>
public readonly record struct PeriodContour
{
    private PeriodContour(PeriodContourKind kind, Guid? constructionId)
    {
        Kind = kind;
        ConstructionId = constructionId;
    }

    public PeriodContourKind Kind { get; }

    /// <summary>Стройка контура; у компании — <c>null</c>.</summary>
    public Guid? ConstructionId { get; }

    public static PeriodContour Company { get; } = new(PeriodContourKind.Company, null);

    public static PeriodContour Construction(Guid id) => new(PeriodContourKind.Construction, id);
}

/// <summary>
/// Запись о закрытии учётного периода или об отмене закрытия (ТЗ CORE-35; задача E1a, issue #1081).
///
/// <para><b>Закрыто — префикс, а не набор отрезков.</b> Запись говорит «закрыто по
/// <see cref="Through" /> включительно», и закрытым считается всё до этой даты, включая дни раньше
/// <see cref="From" />. Начало хранится ради человека («закрыли сентябрь»), а у первого закрытия
/// контура оно справочное: до него тоже закрыто.</para>
///
/// <para>⚠️ Запись НЕИЗМЕНЯЕМА и не удаляется (<see cref="IAppendOnlyRecord" />). Ошибочное закрытие
/// не стирают, а отменяют второй записью — <see cref="PeriodClosureKind.Reopen" />, называющей
/// отменённую. Стёртое закрытие не оставило бы ответа на вопрос «почему счёт лёг в октябрь»: учётные
/// даты, записанные, пока период был закрыт, остаются как легли.</para>
///
/// <para>⚠️ Внешнего ключа на стройку НЕТ, и закрытие стройку не держит: строки неудаляемы, и ключ
/// сделал бы неудаляемой всякую стройку, у которой было своё закрытие. Закрытие удалённой стройки
/// остаётся в таблице записью о прошлом и ни на что не влияет.</para>
///
/// <para>Не наследует <c>Entity</c> по той же причине, что и запись журнала: у той есть
/// <c>UpdatedAt</c> — время правки у того, что не правят.</para>
/// </summary>
public class PeriodClosure : IAppendOnlyRecord
{
    public const int ByNameMax = 256;
    public const int ReasonMax = 1000;

    // ReSharper disable once UnusedMember.Local — конструктор для EF.
    private PeriodClosure() { }

    public Guid Id { get; private set; }

    public PeriodClosureKind Kind { get; private set; }

    public PeriodContourKind Contour { get; private set; }

    /// <summary>Стройка контура; у компании — <c>null</c>.</summary>
    public Guid? ConstructionId { get; private set; }

    /// <summary>Первый день закрываемого отрезка. У отмены — начало отменённого закрытия.</summary>
    public DateOnly From { get; private set; }

    /// <summary>Последний закрытый день. У отмены — граница отменённого закрытия.</summary>
    public DateOnly Through { get; private set; }

    /// <summary>Когда решено. Время сервера в UTC.</summary>
    public DateTimeOffset At { get; private set; }

    /// <summary>Кто решил. <c>null</c> — учётной записи нет (запись приехала копией без неё).</summary>
    public Guid? ById { get; private set; }

    /// <summary>Имя решившего НА МОМЕНТ решения — снимок: запись переживает учётную запись.</summary>
    public string ByName { get; private set; } = "";

    /// <summary>Причина. У отмены обязательна: отменяют ошибку, и ошибку называют.</summary>
    public string? Reason { get; private set; }

    /// <summary>Какое закрытие отменено. Только у отмены.</summary>
    public Guid? CancelsId { get; private set; }

    public PeriodContour ContourRef => Contour == PeriodContourKind.Company
        ? PeriodContour.Company
        : PeriodContour.Construction(ConstructionId!.Value);

    string IAppendOnlyRecord.AppendOnlyLabel =>
        $"{(Kind == PeriodClosureKind.Close ? "закрытие" : "отмена закрытия")} периода по {Through:dd.MM.yyyy}";

    public static PeriodClosure Close(
        PeriodContour contour, DateOnly from, DateOnly through, Guid? byId, string byName, string? reason,
        DateTimeOffset at) => new()
        {
            Id = Guid.NewGuid(),
            Kind = PeriodClosureKind.Close,
            Contour = contour.Kind,
            ConstructionId = contour.ConstructionId,
            From = from,
            Through = through,
            At = at,
            ById = byId,
            ByName = Fit(byName, ByNameMax),
            Reason = Clean(reason),
        };

    public static PeriodClosure Reopen(
        PeriodClosure cancelled, Guid? byId, string byName, string reason, DateTimeOffset at)
    {
        if (cancelled.Kind != PeriodClosureKind.Close)
            throw new InvalidRequestException("Отменить можно только закрытие периода, а не отмену.");
        if (string.IsNullOrWhiteSpace(reason))
            throw new InvalidRequestException(
                "Назовите причину отмены закрытия: по ней потом понимают, почему период открывали заново.");

        return new()
        {
            Id = Guid.NewGuid(),
            Kind = PeriodClosureKind.Reopen,
            Contour = cancelled.Contour,
            ConstructionId = cancelled.ConstructionId,
            From = cancelled.From,
            Through = cancelled.Through,
            At = at,
            ById = byId,
            ByName = Fit(byName, ByNameMax),
            Reason = Clean(reason),
            CancelsId = cancelled.Id,
        };
    }

    /// <summary>Запись из резервной копии — как была, со своим временем и автором.</summary>
    public static PeriodClosure Restore(
        Guid id, PeriodClosureKind kind, PeriodContourKind contour, Guid? constructionId,
        DateOnly from, DateOnly through, DateTimeOffset at, Guid? byId, string byName, string? reason,
        Guid? cancelsId) => new()
        {
            Id = id,
            Kind = kind,
            Contour = contour,
            ConstructionId = constructionId,
            From = from,
            Through = through,
            At = at,
            ById = byId,
            ByName = Fit(byName, ByNameMax),
            Reason = Clean(reason),
            CancelsId = cancelsId,
        };

    private static string? Clean(string? reason)
    {
        var text = reason?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        return text.Length > ReasonMax ? text[..(ReasonMax - 1)] + "…" : text;
    }

    private static string Fit(string value, int max) =>
        value.Length > max ? value[..(max - 1)] + "…" : value;
}
