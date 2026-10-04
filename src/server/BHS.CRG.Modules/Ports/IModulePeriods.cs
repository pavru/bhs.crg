namespace BHS.CRG.Modules.Ports;

/// <summary>
/// Учётный период: до какой даты учёт закрыт (ТЗ CORE-35; задача E1a, issue #1081).
///
/// <para>Служба ядра, а не модуля: закрытый период запирает записи ВСЕХ модулей сразу — иначе
/// «закрыто» у счетов и у выработки означало бы разное, и сводить их было бы нечем. Закрывают период
/// по контуру: компанию целиком или одну стройку.</para>
///
/// <para>Ответ — СНИМОК границ с чистыми функциями, а не вопрос на каждую дату: реестр и отчёты
/// спрашивают про сотни записей разом, и вопрос на каждую был бы запросом на каждую.</para>
///
/// <para>⚠️ Отказывать на записи в закрытый период — дело модуля: только он знает, какая дата у его
/// записи считается учётной (дата счёта, дата оплаты, дата разноски). Порт отвечает на вопрос, а
/// решение принимает вызывающий.</para>
///
/// <para>⚠️ Снимок, прочитанный просто так, годится для ПОКАЗА. Для записи его мало: между «проверил,
/// что период открыт» и «записал» период могут закрыть. Пишущий путь берёт снимок через
/// <see cref="Data.OpenPeriodWrite.InOpenPeriodAsync{T}" /> — там закрытие ждёт конца записи.</para>
/// </summary>
public interface IModulePeriods
{
    /// <summary>Границы закрытия на сейчас — компании и каждой стройки со своим закрытием.</summary>
    Task<PeriodBoundaries> BoundariesAsync(CancellationToken ct = default);
}

/// <summary>
/// Чей период: компании или стройки. Явным типом, а не «стройка или null»: граница стройки всегда
/// не раньше границы компании, и стройка, потерянная по дороге (<c>null</c> по ошибке), молча дала
/// бы более РАННЮЮ границу — запись легла бы в период, закрытый для её стройки.
/// </summary>
public abstract record PeriodContour
{
    private PeriodContour() { }

    /// <summary>Учёт компании: записи вне строек и неразнесённые остатки.</summary>
    public sealed record Company : PeriodContour;

    /// <summary>Учёт одной стройки.</summary>
    public sealed record Construction(Guid Id) : PeriodContour;
}

/// <summary>
/// Снимок границ закрытия.
/// </summary>
/// <param name="Company">Граница компании: закрыто всё по эту дату включительно. <c>null</c> — не закрыто ничего.</param>
/// <param name="Constructions">
/// Стройки со СВОИМ закрытием. Стройки, которой здесь нет, закрытие не миновало: она закрыта так же,
/// как компания.
/// </param>
public sealed record PeriodBoundaries(DateOnly? Company, IReadOnlyDictionary<Guid, DateOnly> Constructions)
{
    /// <summary>Ничего не закрыто.</summary>
    public static PeriodBoundaries None { get; } = new(null, new Dictionary<Guid, DateOnly>());

    /// <summary>
    /// Последний закрытый день контура. У стройки — позднейшая из двух границ, своей и компании:
    /// закрытие компании закрывает все стройки.
    /// </summary>
    public DateOnly? ClosedThrough(PeriodContour contour)
    {
        if (contour is not PeriodContour.Construction construction) return Company;
        if (!Constructions.TryGetValue(construction.Id, out var own)) return Company;
        return Company is null || own > Company ? own : Company;
    }

    /// <summary>
    /// Закрыт ли день. Отдельной функцией, чтобы сравнение дат жило в одном месте: «по эту дату
    /// включительно» — ровно то условие, которое каждый второй напишет со сдвигом.
    /// </summary>
    public bool IsClosed(DateOnly date, PeriodContour contour) =>
        ClosedThrough(contour) is { } through && date <= through;

    /// <summary>
    /// Учётный день (ТЗ COST-16): сама дата, если её период открыт, иначе первый открытый день
    /// контура. Так оплата, проведённая задним числом в закрытый месяц, ложится в первый открытый.
    /// </summary>
    public DateOnly AccountingDate(DateOnly date, PeriodContour contour) =>
        ClosedThrough(contour) is { } through && date <= through ? through.AddDays(1) : date;
}
