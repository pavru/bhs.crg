using BHS.CRG.Application.Periods;
using BHS.CRG.Modules;
using Microsoft.Extensions.DependencyInjection;
using Contour = BHS.CRG.Domain.Periods.PeriodContour;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Закрытие периода — служба ЯДРА и работает на поставке без учётных модулей (ТЗ CORE-35; задача
/// E1a, issue #1081).
///
/// <para>Проверяется поднятым приложением, а не чтением регистраций: служба, случайно заведённая
/// рядом с портами модуля счетов или зависящая от его службы, собиралась бы и проходила остальные
/// тесты периода — они идут со включённым модулем. Отказала бы она на поставке без него, то есть у
/// заказчика.</para>
///
/// <para>«Пустой состав модулей» здесь — состав по умолчанию: пустое <c>Modules__Enabled</c>
/// приложение читает как умолчание (см. <c>ModuleRegistry.ReadEnabledCodes</c>), и совсем без
/// модулей оно не поднимается. Существенно то, что среди включённых нет ни одного, кто пишет в
/// учёт.</para>
/// </summary>
[Collection("Integration")]
public class PeriodClosureWithoutModulesTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Период_закрывается_без_модуля_счетов()
    {
        _ = fixture.CreateClient();
        Assert.DoesNotContain("costs", fixture.Services.GetRequiredService<ModuleRegistry>().Enabled.Select(m => m.Code));

        using var scope = fixture.Services.CreateScope();
        var closures = scope.ServiceProvider.GetRequiredService<IPeriodClosures>();
        var through = (await closures.TodayAsync()).AddDays(-10);

        await closures.CloseAsync(new ClosePeriod(Contour.Company, through.AddDays(-30), through, null, null));

        Assert.Equal(through, (await closures.LedgerAsync()).Company);
    }
}
