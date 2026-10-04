using BHS.CRG.Application.Periods;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Api.Modules.Ports;

/// <summary>
/// Учётный период для модулей — границы из службы закрытия ядра (ТЗ CORE-35; задача E1a,
/// issue #1081).
///
/// <para>Переходник не считает ничего: границы сводит <c>PeriodLedger</c> ядра, здесь они
/// перекладываются в снимок контрактов. Стройки отдаются с ДЕЙСТВУЮЩЕЙ границей — позднейшей из
/// своей и границы компании: модуль, прочитавший словарь мимо функций снимка, не должен получить
/// дату раньше настоящей.</para>
///
/// <para>⚠️ Замка переходник не берёт: он читает соединением ядра, а замок записи обязан жить на
/// соединении модуля, в его транзакции (см. <c>OpenPeriodWrite</c>).</para>
/// </summary>
public sealed class ModulePeriodsPort(IPeriodClosures closures) : IModulePeriods
{
    public async Task<PeriodBoundaries> BoundariesAsync(CancellationToken ct = default)
    {
        var ledger = await closures.LedgerAsync(ct);
        var constructions = ledger.ConstructionsWithOwn.ToDictionary(
            id => id,
            id => ledger.ClosedThrough(Domain.Periods.PeriodContour.Construction(id))!.Value);
        return new PeriodBoundaries(ledger.Company, constructions);
    }
}
