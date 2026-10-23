using System.Data.Common;
using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BHS.CRG.Tests.Plan;

/// <summary>
/// Отказы базы в гонках называют то, что делал ЭТОТ запрос (change <c>plan-06-estimate-model</c>): один
/// внешний ключ отказывает и удалению, и записи ссылки на удалённое, и слова «нельзя удалить» человеку,
/// который ничего не удалял, отправили бы его чинить не то.
/// </summary>
[Collection(PlanOnlyCollection.Name)]
public class EstimateRefusalRaceTests(PlanOnlyHost host)
{
    [Fact]
    public async Task Сделать_действующей_удалённую_тем_временем_редакцию_отказ_про_удаление_другим()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var estimateId = await EstimateSeed.EstimateAsync(host, site.Construction);
        await EstimateSeed.RevisionAsync(host, estimateId, new EstimateRowDraft("Строка 1"));
        var second = await EstimateSeed.RevisionAsync(host, estimateId, new EstimateRowDraft("Строка 1"));

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlanDbContext>();
        var estimate = await db.Estimates.SingleAsync(e => e.Id == estimateId);
        var revision = await db.EstimateRevisions.SingleAsync(r => r.Id == second.Id);

        using (var other = host.Services.CreateScope())
        {
            var odb = other.ServiceProvider.GetRequiredService<PlanDbContext>();
            odb.EstimateRevisions.Remove(await odb.EstimateRevisions.SingleAsync(r => r.Id == second.Id));
            await odb.SaveChangesAsync();
        }

        estimate.MakeCurrent(revision);
        var refusal = await Assert.ThrowsAsync<ConflictException>(() => db.SaveChangesAsync());

        Assert.Contains("Редакцию тем временем удалили", refusal.Message);
        Assert.DoesNotContain("нельзя удалить", refusal.Message);
    }

    [Fact]
    public async Task Редакция_в_удалённую_тем_временем_смету_отказ_про_удаление_другим()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var estimateId = await EstimateSeed.EstimateAsync(host, site.Construction);

        using var scope = host.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var options = new DbContextOptionsBuilder<PlanDbContext>();
        PlanDbContext.Configure(options, host.ConnectionString);
        options.AddInterceptors(new DeleteEstimateBeforeRevisionInsert(host.ConnectionString, estimateId));
        await using var db = new PlanDbContext(options.Options);
        var writer = new EstimateRevisionWriter(db, sp.GetRequiredService<IModuleTypes>(),
            sp.GetRequiredService<IModuleWriteGuard>(), sp.GetRequiredService<IModuleWorkTypes>(),
            sp.GetRequiredService<IModuleUnits>(), sp.GetRequiredService<IModulePlanItems>(),
            sp.GetRequiredService<IModuleCatalog>(), sp.GetRequiredService<IModuleUser>());

        var refusal = await Assert.ThrowsAsync<ConflictException>(() =>
            writer.AddAsync(estimateId, new EstimateRevisionSource(), [new EstimateRowDraft("Строка 1")]));

        Assert.Contains("Смету тем временем удалили", refusal.Message);
        Assert.DoesNotContain("нельзя удалить", refusal.Message);
    }

    /// <summary>Удаляет смету другим соединением прямо перед записью редакции — гонка без ожиданий.</summary>
    private sealed class DeleteEstimateBeforeRevisionInsert(string connection, Guid estimate) : DbCommandInterceptor
    {
        private bool done;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (!done && command.CommandText.Contains("INSERT INTO plan.estimate_revisions", StringComparison.Ordinal))
            {
                done = true;
                await using var conn = new NpgsqlConnection(connection);
                await conn.OpenAsync(cancellationToken);
                await using var delete = new NpgsqlCommand("DELETE FROM plan.estimates WHERE id = @id", conn);
                delete.Parameters.AddWithValue("id", estimate);
                Assert.Equal(1, await delete.ExecuteNonQueryAsync(cancellationToken));
            }
            return result;
        }
    }
}
