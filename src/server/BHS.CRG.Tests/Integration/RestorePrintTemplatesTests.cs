using BHS.CRG.Api.Modules;
using BHS.CRG.Domain.Templates;
using BHS.CRG.Infrastructure.Backup;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Plan;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

public class RestorePrintTemplatesTests(RestorePrintTemplatesHost host) : IClassFixture<RestorePrintTemplatesHost>
{
    [Theory]
    [InlineData("unchanged", true)]
    [InlineData("edited", false)]
    [InlineData("asset", false)]
    [InlineData("template-missing-from-copy", true)]
    public async Task Restore_replaces_a_startup_type_only_if_its_local_template_is_unused(string destination, bool expectedSuccess)
    {
        await FreshAsync();
        var source = await ReadTemplateAsync();
        const string sourceContent = "= Оформление из копии";
        await using var sourceScope = host.Services.CreateAsyncScope();
        var sourceDb = sourceScope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (destination == "template-missing-from-copy")
            await sourceDb.Templates.Where(t => t.Id == source.Id).ExecuteDeleteAsync();
        else
        {
            var edited = await sourceDb.Templates.SingleAsync(t => t.Id == source.Id);
            edited.UpdateContent(sourceContent);
            await sourceDb.SaveChangesAsync();
        }
        var (zip, _) = await sourceScope.ServiceProvider.GetRequiredService<BackupService>().ExportAsync();
        await using var copy = zip;
        copy.Position = 0;

        await FreshAsync();
        var started = await ReadTemplateAsync();
        Assert.NotEqual(source.DocumentTypeId, started.DocumentTypeId);
        Assert.NotEqual(source.Id, started.Id);
        Assert.True(started.IsUntouchedFactoryDefault());

        await using var targetScope = host.Services.CreateAsyncScope();
        var targetDb = targetScope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (destination == "edited")
        {
            var local = await targetDb.Templates.SingleAsync(t => t.Id == started.Id);
            local.UpdateContent("= Пользовательское оформление");
        }
        if (destination == "asset")
            targetDb.TemplateAssets.Add(TemplateAsset.Create(TemplateAssetScope.Template, started.Id,
                TemplateAssetKind.Image, "logo", "logo.png", "image/png", "offer-logo.png"));
        await targetDb.SaveChangesAsync();
        targetDb.ChangeTracker.Clear();

        var report = await targetScope.ServiceProvider.GetRequiredService<BackupService>().ImportAsync(copy);
        Assert.True(report.Success == expectedSuccess, string.Join("\n", report.Warnings));

        var result = await ReadTemplateAsync();
        if (!expectedSuccess)
        {
            Assert.Contains(report.Warnings, warning => warning.Contains("здешний уже занят"));
            Assert.Equal(started.Id, result.Id);
            if (destination == "edited") Assert.Equal("= Пользовательское оформление", result.Content);
            if (destination == "asset") Assert.True(await targetDb.TemplateAssets.AnyAsync(a => a.ScopeId == started.Id));
            return;
        }

        Assert.Equal(source.DocumentTypeId, result.DocumentTypeId);
        Assert.False(await targetDb.Templates.AnyAsync(t => t.Id == started.Id));
        if (destination == "template-missing-from-copy")
            Assert.True(result.IsUntouchedFactoryDefault());
        else
        {
            Assert.Equal(source.Id, result.Id);
            Assert.Equal(sourceContent, result.Content);
        }
    }

    private async Task FreshAsync()
    {
        await host.ResetDatabaseAsync();
        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.ProjectModuleTypesAsync();
        await scope.ServiceProvider.SeedModulePrintTemplatesAsync();
    }

    private async Task<Template> ReadTemplateAsync()
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var type = await db.DocumentTypes.SingleAsync(t => t.Code == PlanRecordTypes.OfferCode);
        return await db.Templates.AsNoTracking().SingleAsync(t => t.DocumentTypeId == type.Id);
    }
}

public sealed class RestorePrintTemplatesHost : ModuleOnlyHost
{
    protected override string ModuleCode => PlanModule.ModuleCode;
    protected override string DatabaseSuffix => "restore_print";
}
