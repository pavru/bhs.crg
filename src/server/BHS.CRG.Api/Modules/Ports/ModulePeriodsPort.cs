using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Api.Modules.Ports;

/// <summary>
/// Учётный период: пока закрывать его нечем — не закрыто ничего (ТЗ COST-Q8).
///
/// <para>⚠️ Это НЕ заглушка и не «пока так»: на любом сегодняшнем экземпляре закрытых периодов
/// действительно нет, потому что службы закрытия в системе нет вовсе — она приезжает задачей E1a
/// этапа 2 (issue #1081). Ответ «не закрыто ничего» — правда об этом экземпляре, и модуль, который
/// на него опирается, ведёт себя верно.</para>
///
/// <para>Порт заведён раньше службы нарочно. Первый же модуль, которому понадобился период, спросил
/// бы про него базу напрямую — то есть завёл бы своё представление о закрытии, и потом их стало бы
/// два. Цена решения названа: этот класс обязан УМЕРЕТЬ вместе с E1a, а не остаться рядом со
/// службой. Что он умер, проверит тест самой службы; что он ещё жив и почему — говорит
/// <c>ModulePortsTests.Периоды_пока_не_закрываются</c>, и он назовёт задачу.</para>
/// </summary>
public sealed class NoClosedPeriods : IModulePeriods
{
    public Task<DateOnly?> ClosedThroughAsync(CancellationToken ct = default) =>
        Task.FromResult<DateOnly?>(null);

    public Task<bool> IsClosedAsync(DateOnly date, CancellationToken ct = default) =>
        Task.FromResult(false);
}
