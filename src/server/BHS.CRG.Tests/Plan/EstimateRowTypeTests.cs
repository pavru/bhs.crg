using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Modules.Plan;
using BHS.CRG.Tests.Integration;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Plan;

/// <summary>
/// Тип <c>ПозицияСметы</c> — открытый составной тип модуля <c>plan</c> в таблице модуля (change
/// <c>plan-06-estimate-model</c>, ТЗ TYPE-16): есть при включённом модуле и только тогда.
/// </summary>
[Collection(PlanOnlyCollection.Name)]
public class EstimateRowTypeTests(PlanOnlyHost host)
{
    [Fact]
    public async Task Тип_есть_при_включённом_модуле_составной_открытый_в_таблице_модуля()
    {
        await EstimateSeed.ClassifierAsync(host);

        using var scope = host.Services.CreateScope();
        var type = Assert.Single(await scope.ServiceProvider.GetRequiredService<IRepository<DocumentType>>()
            .FindAsync(t => t.Code == PlanRecordTypes.EstimateRowCode));

        Assert.Equal(DocumentTypeKind.Composite, type.Kind);
        Assert.Equal(PlanModule.ModuleCode, type.Module);
        Assert.Equal(SchemaEditLevel.Open, type.EditLevel);
        Assert.Equal(TypeStorage.ModuleTable, type.Storage);
    }

    /// <summary>Тэгов <c>estimate.*</c> модуль не объявляет: деньги и расценка — колонки (ТЗ TYPE-16).</summary>
    [Fact]
    public void Тэгов_estimate_модуль_не_объявляет() =>
        Assert.DoesNotContain(((BHS.CRG.Modules.IAppModule)new PlanModule()).Tags, t => t.Code.StartsWith("estimate.", StringComparison.Ordinal));
}

/// <summary>Тот же тип на умолчательном хосте, где модуль <c>plan</c> выключен: его нет (ТЗ TYPE-16, AUTH-19).</summary>
[Collection("Integration")]
public class EstimateRowTypeWithoutModuleTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task Без_модуля_типа_нет()
    {
        _ = fixture.CreateClient();
        using var scope = fixture.Services.CreateScope();

        var registry = scope.ServiceProvider.GetRequiredService<BHS.CRG.Modules.ModuleRegistry>();
        Assert.DoesNotContain(registry.Enabled, m => m.Code == PlanModule.ModuleCode);
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<IRepository<DocumentType>>()
            .FindAsync(t => t.Code == PlanRecordTypes.EstimateRowCode));
    }
}
