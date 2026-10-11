using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Modules.Costs.Data;

/// <summary>
/// Закрытый период запирает проведённую накладную (issue #1285, ТЗ COST-16, CORE-35).
///
/// <para><b>По учётной дате, а не по дате отпуска</b> (решение владельца 11.10.2026). Учётную дату
/// накладная получает при проведении — первый открытый день не раньше даты отпуска — и хранит: так
/// накладная, введённая после закрытия датой закрытого месяца, ложится в открытый период и остаётся
/// исправимой. Замок по дате отпуска запирал бы её в самый момент проведения, и ошибку в ней было бы
/// не исправить без отмены закрытия.</para>
///
/// <para>Черновик не принадлежит ни одному периоду — как неоплаченный счёт: его дата ещё ничего не
/// утверждает.</para>
/// </summary>
public static class WaybillLock
{
    public static PeriodLock? Of(Waybill waybill, PeriodBoundaries boundaries)
    {
        // Проведённая без учётной даты — накладная из резервной копии, снятой до появления колонки:
        // её относили к периоду по дате отпуска, так и остаётся. Без этого запасного правила такую
        // накладную не запирало бы ничто.
        if (waybill.State != WaybillState.Posted || (waybill.AccountingOn ?? waybill.IssuedOn) is not { } date)
            return null;

        // Закрытие компании называем раньше закрытия стройки — как у счёта: оно закрывает всё, и «у
        // стройки А» про день, закрытый для всех, отправило бы человека отменять не то закрытие.
        var company = new PeriodContour.Company();
        if (boundaries.IsClosed(date, company))
            return new PeriodLock(null, boundaries.ClosedThrough(company)!.Value);

        if (waybill.ConstructionId is { } site && new PeriodContour.Construction(site) is var contour
            && boundaries.IsClosed(date, contour))
            return new PeriodLock(site, boundaries.ClosedThrough(contour)!.Value);

        return null;
    }

    /// <summary>Контур, по которому накладная получает учётную дату: её стройка.</summary>
    public static DateOnly AccountedOn(DateOnly issuedOn, Guid site, PeriodBoundaries boundaries) =>
        boundaries.AccountingDate(issuedOn, new PeriodContour.Construction(site));

    /// <summary>Теми же словами, что у счёта (<c>PaymentViews.Text</c>): экран периодов один.</summary>
    public static string Text(PeriodLock locked, string? siteName) =>
        locked.ConstructionId is null ? $"период компании закрыт по {locked.Through:dd.MM.yyyy}"
        : siteName is null ? $"период закрыт по {locked.Through:dd.MM.yyyy} у стройки накладной"
        : $"период закрыт по {locked.Through:dd.MM.yyyy} у стройки «{siteName}»";
}
