using BHS.CRG.Modules.Access;

namespace BHS.CRG.Modules.Exchange;

/// <summary>
/// Контракт «Фактические затраты» (change <c>plan-24-actual-costs-contract</c>, ТЗ CORE-34, PLAN-21.1, COST-21):
/// два итога затрат стройки — для «Хода работ», которому схема <c>costs</c> закрыта (COST-29).
///
/// <para><b>Числа — те же, что у отчёта «Затраты по стройке»</b>, а не свой расчёт: «оплачено» — его «Затраты»
/// за всё время, «разнесено» — они же вместе с «К оплате». Полоса, посчитанная своим проходом, разошлась бы с
/// отчётом на первой корректировке закрытого периода.</para>
///
/// <para><b>Валюты в ответе нет:</b> суммы — в валюте учёта экземпляра, её знает ядро-потребитель.</para>
///
/// <para><b>Право — контракта</b> (ТЗ AUTH-8.1): итоги без счетов и поставщиков отдаются и тому, кому модуль
/// «Счета и накладные» закрыт, — «Менеджеру проекта» (решение В-3, PLAN-Q11).</para>
/// </summary>
[ModuleContract("costs", "Фактические затраты")]
public interface IActualCosts
{
    /// <summary>
    /// Права, любое из которых открывает ответ; главное — первым, его называет «скрыто правами». Не читает базу:
    /// из них потребитель собирает ворота своего экрана («Ход работ», <c>plan-25</c>), не перечисляя коды
    /// модулей в ядре.
    /// </summary>
    IReadOnlyList<string> AnswerPermissions { get; }

    /// <summary>
    /// Итоги стройки — или одного её раздела — в правах <paramref name="reader" />. Системе — отказ
    /// (<see cref="ContractAnswer{T}.SystemRefused" />), человеку без права — «скрыто правами» без сумм.
    /// </summary>
    /// <param name="sectionId">Раздел стройки: итоги — только доли этого раздела. <c>null</c> — вся стройка.</param>
    Task<ContractAnswer<ActualCostsTotals>> ReadAsync(
        ModuleReader reader, Guid constructionId, Guid? sectionId = null, CancellationToken ct = default);
}

/// <summary>Итоги затрат стройки (ТЗ COST-21: «разнесено 2 340 000 · из них оплачено 2 100 000»).</summary>
/// <param name="Allocated">
/// Разнесено: доли разноски всех неотклонённых счетов — оплаченных и нет, черновиков тоже, как их берёт отчёт.
/// Суммы с НДС, как в бумаге.
/// </param>
/// <param name="Paid">Из них оплачено — «Затраты» отчёта «Затраты по стройке» за всё время.</param>
/// <param name="LinkedToWorks">Сколько из них привязано к работам (PLAN-21.1) — пока «не ведётся».</param>
public sealed record ActualCostsTotals(decimal Allocated, decimal Paid, WorksLink LinkedToWorks);

/// <summary>
/// Доля затрат, привязанных к работам. Разноски на позицию перечня (COST-14) нет, и доля приходит состоянием с
/// причиной, а не нулём: ноль читался бы как «на работы ничего не потрачено» (решение В-15 (а), PLAN-Q23).
/// </summary>
/// <param name="State">Состояние: <see cref="NotTrackedState" />.</param>
/// <param name="Reason">Что показать на месте доли.</param>
public sealed record WorksLink(string State, string Reason)
{
    public const string NotTrackedState = "not-tracked";

    /// <summary>Разноска по работам не ведётся — числа нет.</summary>
    public static WorksLink NotTracked { get; } = new(NotTrackedState, "разноска затрат по работам не ведётся");
}
