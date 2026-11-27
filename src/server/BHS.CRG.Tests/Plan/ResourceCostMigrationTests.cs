using System.Net;
using System.Net.Http.Json;
using BHS.CRG.Modules.Exchange;
using BHS.CRG.Modules.Plan;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Tests.Integration;
using BHS.CRG.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Plan;

/// <summary>Обновление наполненной БД предыдущей версии: история, ручные решения и производные
/// остаются теми же. Отдельный хост и база позволяют остановить фоновые задачи до смены схемы.</summary>
public class ResourceCostMigrationTests
{
    [Fact]
    public async Task Прежние_редакции_деньги_распределение_КП_и_график_сохраняются_без_исходного_блоба()
    {
        var host = new ResourceCostMigrationHost();
        await host.InitializeAsync();
        var connection = host.ConnectionString;
        Guid estimate, first, second, rowId;
        try
        {
            var site = await EstimateSeed.SiteAsync(host);
            var classifier = await EstimateSeed.ClassifierAsync(host);
            estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
            var draft = await EstimateSeed.MatchedRowAsync(host, site, 200m) with
            {
                Materials = [new("Нет стоимости", 1m, NomenclatureId: classifier.Nomenclature),
                    new("Нулевая", 1m, NomenclatureId: classifier.Nomenclature, UnitPrice: 0m, Amount: 0m),
                    new("Прежняя сумма", 1m, NomenclatureId: classifier.Nomenclature, UnitPrice: 5.01m, Amount: 388.38m)],
            };
            first = (await EstimateSeed.RevisionAsync(host, estimate, draft)).Id;
            second = (await EstimateSeed.RevisionAsync(host, estimate, draft with { Name = "Вторая редакция" })).Id;
            var client = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.EstimateRead, PlanModule.EstimateEdit,
                PlanModule.OfferRead, PlanModule.OfferEdit, PlanModule.ScheduleRead, PlanModule.ScheduleEdit);
            using (var scope = host.Services.CreateScope())
                rowId = await scope.ServiceProvider.GetRequiredService<PlanDbContext>().EstimateRows
                    .Where(r => r.RevisionId == first).Select(r => r.Id).SingleAsync();
            await DistributionFlow.LinesAsync(client, first, DistributionFlow.Line(rowId, "row", (site.Section, 1m)));
            await DistributionFlow.RuleAsync(client, second, null, DistributionFlow.Whole);
            var offer = await client.PostAsJsonAsync("/api/plan/offers", new { revisionId = first, markup = 20m });
            Assert.True(offer.StatusCode == HttpStatusCode.Created, await offer.Content.ReadAsStringAsync());
            var schedule = await client.PostAsJsonAsync("/api/plan/schedules", new
            {
                constructionId = site.Construction, revisionId = second, creationKey = Guid.NewGuid(), name = "График до обновления",
            });
            Assert.True(schedule.StatusCode == HttpStatusCode.Created, await schedule.Content.ReadAsStringAsync());
        }
        finally { await host.DisposeAsync(); }

        var options = new DbContextOptionsBuilder<PlanDbContext>();
        PlanDbContext.Configure(options, connection);
        await using var db = new PlanDbContext(options.Options);
        var migrator = db.GetService<IMigrator>();
        var migrations = db.Database.GetMigrations().ToList();
        var at = migrations.FindIndex(m => m.EndsWith("_ResourceCostBasis", StringComparison.Ordinal));
        Assert.True(at > 0);
        await migrator.MigrateAsync(migrations[at - 1]);
        var before = await SnapshotAsync(db);
        Assert.Equal(first, (await db.Estimates.AsNoTracking().SingleAsync(e => e.Id == estimate)).CurrentRevisionId);
        Assert.All(await db.EstimateRevisions.AsNoTracking().ToListAsync(), r => Assert.Null(r.SourceBlobPath));
        Assert.All(await db.EstimateRevisions.AsNoTracking().ToListAsync(), r => Assert.True(r.Frozen));
        // Миграцию выполняет обычный старт приложения над прежней наполненной схемой.
        // Второй хост использует ту же базу, не сбрасывая её и не получая исходные файлы.
        await using (var restarted = new ResourceCostMigrationRestartHost())
        {
            await restarted.InitializeAsync();
            using var probe = restarted.CreateClient();
            Assert.Equal(HttpStatusCode.OK, (await probe.GetAsync("/api/version")).StatusCode);
        }
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal(before, await SnapshotAsync(db));
        Assert.False(db.Database.HasPendingModelChanges());
        await using var reread = new PlanDbContext(options.Options);
        var materials = await reread.EstimateRowMaterials.AsNoTracking().Where(m => m.RowId == rowId).OrderBy(m => m.Order).ToListAsync();
        Assert.Equal(new decimal?[] { null, 0m, 5.01m }, materials.Select(m => m.UnitPrice));
        Assert.Equal(new decimal?[] { null, 0m, 388.38m }, materials.Select(m => m.Amount));
        Assert.All(materials, m => Assert.Equal((ResourceTaxBasis.Unknown, ResourceTaxBasis.Unknown), (m.UnitPriceBasis, m.AmountBasis)));
        // Приложение прежней версии при совместимой схеме может писать без новых колонок.
        await reread.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO plan.estimate_row_materials (id, row_id, "order", source_name, quantity)
            VALUES ({Guid.NewGuid()}, {rowId}, 4, 'Прежний клиент', 1)
            """);
        var legacy = await reread.EstimateRowMaterials.AsNoTracking().SingleAsync(m => m.RowId == rowId && m.Order == 4);
        Assert.Equal(ResourceTaxBasis.Unknown, legacy.UnitPriceBasis);
        Assert.Equal(ResourceTaxBasis.Unknown, legacy.AmountBasis);
    }

    private static async Task<string[]> SnapshotAsync(PlanDbContext db)
    {
        string[] tables = ["estimates", "estimate_revisions", "estimate_rows", "estimate_row_materials", "distribution_parts",
            "distribution_rules", "revision_distributions", "offers", "offer_lines", "offer_versions", "schedules", "schedule_works"];
        var result = new List<string>();
        foreach (var table in tables)
        {
            // Имена — закрытый список выше; данные сортируются целиком, включая идентификаторы и связи.
            var json = await db.Database.SqlQueryRaw<string>($"""
                SELECT coalesce(jsonb_agg(x ORDER BY x::text), '[]'::jsonb)::text AS "Value"
                FROM (SELECT to_jsonb(t) - 'unit_price_basis' - 'amount_basis' AS x FROM plan.{table} t) q
                """).SingleAsync();
            result.Add(table + ":" + json);
        }
        return result.ToArray();
    }
}

internal sealed class ResourceCostMigrationHost : ModuleOnlyHost
{
    protected override string ModuleCode => "plan";
    protected override string DatabaseSuffix => "resource_cost_migrate";
    protected override IReadOnlyList<string> SchemasDroppedBeforeStart => ["plan", "public"];
}

internal sealed class ResourceCostMigrationRestartHost : ModuleOnlyHost
{
    protected override string ModuleCode => "plan";
    protected override string DatabaseSuffix => "resource_cost_migrate";
}
