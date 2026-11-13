using BHS.CRG.Application.Common;
using BHS.CRG.Application.Settings;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Modules.Ports;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Порт строек отдаёт модулю действующий пояс стройки (ТЗ CORE-5, WORK-8; change <c>work-11-assignments</c>):
/// свой, а не задан — пояс компании с признаком «унаследован». Модуль настроек ядра не видит и угадывать
/// пояс не должен.
/// </summary>
[Collection("Integration")]
public class ModuleConstructionsPortTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Стройка_без_пояса_приходит_с_поясом_компании_и_признаком()
    {
        using var scope = fixture.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IAppSettingsStore>()
            .SetAsync(AppSettingKeys.CompanyTimeZone, "Europe/Moscow");
        var repo = scope.ServiceProvider.GetRequiredService<IRepository<Construction>>();
        var moscow = Construction.Create("Склад на Каширке", Guid.NewGuid());
        var vladivostok = Construction.Create("Порт Владивосток", Guid.NewGuid());
        vladivostok.SetTimeZone("Asia/Vladivostok");
        await repo.AddAsync(moscow);
        await repo.AddAsync(vladivostok);
        await repo.SaveChangesAsync();

        using var read = fixture.Services.CreateScope();
        var sites = (await read.ServiceProvider.GetRequiredService<IModuleConstructions>().ListAsync())
            .ToDictionary(c => c.Id);

        Assert.Equal("Europe/Moscow", sites[moscow.Id].TimeZoneId);
        Assert.True(sites[moscow.Id].TimeZoneInherited);
        Assert.Equal("Asia/Vladivostok", sites[vladivostok.Id].TimeZoneId);
        Assert.False(sites[vladivostok.Id].TimeZoneInherited);
    }
}
