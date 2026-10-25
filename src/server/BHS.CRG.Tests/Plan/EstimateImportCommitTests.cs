using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Plan.Import;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Plan;

/// <summary>
/// Создание редакции из импорта (change <c>plan-07-estimate-import</c>, PR 2; ТЗ PLAN-6, PLAN-7, STG-6.1,
/// CORE-4, CORE-28): согласие на частичный файл, повтор и двойное нажатие, сбой между позициями и строками,
/// отказ ключа позиции, файл без разобранных позиций, следующий номер в смете с редакциями.
/// </summary>
[Collection(PlanOnlyCollection.Name)]
public class EstimateImportCommitTests(PlanOnlyHost host)
{
    private const string Lsr = "lsr-16.2.0-vn";

    /// <summary>
    /// Частичный файл без согласия — 409 с числами «N из M», редакции нет; с согласием — редакция из N строк,
    /// а отчёт импорта остаётся и называет её (ТЗ PLAN-7).
    /// </summary>
    [Fact]
    public async Task Частичный_файл_без_согласия_409_с_числами_с_согласием_редакция()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var client = await EstimateImportFlow.EditorAsync(host);
        var report = await EstimateImportFlow.OutcomeAsync(client, await EstimateImportFlow.UploadAsync(client,
            EstimateImportFlow.Form(EstimateImportFlow.WithBrokenPositions(Lsr, 2), "Data.xml", site.Construction,
                newName: "Частичная", sectionId: site.Section, kind: "additional")));
        var id = EstimateImportFlow.Id(report);
        var parsed = report.GetProperty("parsed").GetInt32();
        var inFile = report.GetProperty("inFile").GetInt32();

        var refused = await client.SendAsync(EstimateImportFlow.Commit(id, Version(report)));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("partial", body.GetProperty("code").GetString());
        Assert.Equal(parsed, body.GetProperty("parsed").GetInt32());
        Assert.Equal(inFile, body.GetProperty("inFile").GetInt32());
        Assert.Equal(2, body.GetProperty("unparsed").GetInt32());
        Assert.Contains($"разобрано {parsed} из {inFile}", body.GetProperty("error").GetString());
        Assert.Equal("parsed", (await EstimateImportFlow.GetAsync(client, id)).GetProperty("state").GetString());

        var committed = await client.SendAsync(EstimateImportFlow.Commit(id, Version(report), acceptPartial: true));
        Assert.Equal(HttpStatusCode.Created, committed.StatusCode);
        var done = await committed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(done.GetProperty("estimateCreated").GetBoolean());
        Assert.Equal(1, done.GetProperty("revisionNumber").GetInt32());
        var estimate = done.GetProperty("estimate");
        Assert.Equal("Частичная", estimate.GetProperty("name").GetString());
        Assert.Equal(site.Section, estimate.GetProperty("sectionId").GetGuid());
        Assert.Equal("additional", estimate.GetProperty("kind").GetString());
        var revisionId = done.GetProperty("revisionId").GetGuid();
        Assert.Equal(revisionId, estimate.GetProperty("currentRevisionId").GetGuid());
        var revision = Assert.Single(estimate.GetProperty("revisions").EnumerateArray());
        Assert.Equal(parsed, revision.GetProperty("rows").GetInt32());

        // Отчёт у редакции: числа и не разобранное — на месте, модель разбора — очищена.
        var after = await EstimateImportFlow.GetAsync(client, id);
        Assert.Equal("committed", after.GetProperty("state").GetString());
        Assert.Equal(revisionId, after.GetProperty("revisionId").GetGuid());
        Assert.Equal(parsed, after.GetProperty("parsed").GetInt32());
        Assert.Equal(inFile, after.GetProperty("inFile").GetInt32());
        Assert.Equal(2, after.GetProperty("unparsed").GetArrayLength());
        Assert.Equal(estimate.GetProperty("id").GetGuid(), after.GetProperty("target").GetProperty("estimateId").GetGuid());
        using var scope = host.Services.CreateScope();
        Assert.Null(await scope.ServiceProvider.GetRequiredService<PlanDbContext>().EstimateImports
            .Where(i => i.Id == id).Select(i => i.Parsed).SingleAsync());

        // Журнал: смета заведена импортом и импортирована — с числами (ТЗ CORE-28).
        var records = await scope.ServiceProvider.GetRequiredService<AppDbContext>().ActivityRecords
            .Where(r => r.TargetId == estimate.GetProperty("id").GetGuid().ToString()).ToListAsync();
        Assert.Contains(records, r => r.Action == "plan.estimate.created");
        var imported = Assert.Single(records, r => r.Action == "plan.estimate.imported");
        Assert.Equal($"редакция 1, строк {parsed}, не разобрано 2", imported.After);
    }

    /// <summary>
    /// Две вкладки: вторая, видевшая импорт до создания редакции, — 409; повтор с новой версией возвращает ту же
    /// редакцию; два одновременных создания дают одну редакцию (ТЗ STG-6.1, PLAN-2).
    /// </summary>
    [Fact]
    public async Task Повтор_и_двойное_нажатие_дают_одну_редакцию()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var client = await EstimateImportFlow.EditorAsync(host);
        var report = await ParsedAsync(client, site);
        var id = EstimateImportFlow.Id(report);

        var first = await client.SendAsync(EstimateImportFlow.Commit(id, Version(report)));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var revisionId = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("revisionId").GetGuid();

        var stale = await client.SendAsync(EstimateImportFlow.Commit(id, Version(report)));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        var fresh = await EstimateImportFlow.GetAsync(client, id);
        var again = await client.SendAsync(EstimateImportFlow.Commit(id, Version(fresh)));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var repeated = await again.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(repeated.GetProperty("repeated").GetBoolean());
        Assert.Equal(revisionId, repeated.GetProperty("revisionId").GetGuid());

        // Одновременно, с одной версией: создаётся одна, второй ответ — она же либо 409 устаревшей версии.
        var other = await ParsedAsync(client, site);
        var otherId = EstimateImportFlow.Id(other);
        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
            client.SendAsync(EstimateImportFlow.Commit(otherId, Version(other)))));
        var succeeded = responses.Where(r => r.IsSuccessStatusCode).ToList();
        Assert.NotEmpty(succeeded);
        Assert.All(responses.Except(succeeded), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        var ids = await Task.WhenAll(succeeded.Select(async r =>
            (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("revisionId").GetGuid()));
        Assert.Single(ids.Distinct());
        using var scope = host.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<PlanDbContext>().EstimateRevisions
            .CountAsync(r => r.ImportKey == otherId.ToString()));
    }

    /// <summary>
    /// Импорт в смету с редакциями 1 и 2 — редакция 3; прежние не меняются, действующая остаётся прежней
    /// (действующей становится только первая, решение В-5; ТЗ PLAN-6).
    /// </summary>
    [Fact]
    public async Task Импорт_в_смету_с_двумя_редакциями_даёт_третью()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        var first = await EstimateSeed.RevisionAsync(host, estimate, new EstimateRowDraft("Первая"));
        await EstimateSeed.RevisionAsync(host, estimate, new EstimateRowDraft("Вторая"));
        var client = await EstimateImportFlow.EditorAsync(host);
        var report = await EstimateImportFlow.OutcomeAsync(client, await EstimateImportFlow.UploadAsync(client,
            EstimateImportFlow.Form(GrandSmetaSamples.Gsfx(Lsr), Lsr + ".gsfx", site.Construction, estimateId: estimate)));

        var response = await client.SendAsync(EstimateImportFlow.Commit(EstimateImportFlow.Id(report), Version(report)));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var done = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(done.GetProperty("estimateCreated").GetBoolean());
        Assert.Equal(3, done.GetProperty("revisionNumber").GetInt32());
        Assert.Equal(first.Id, await EstimateSeed.CurrentRevisionAsync(host, estimate));
        var revisions = done.GetProperty("estimate").GetProperty("revisions").EnumerateArray().ToList();
        Assert.Equal([1, 1, report.GetProperty("parsed").GetInt32()],
            revisions.OrderBy(r => r.GetProperty("number").GetInt32()).Select(r => r.GetProperty("rows").GetInt32()));
        var imported = revisions.Single(r => r.GetProperty("number").GetInt32() == 3);
        Assert.Equal(Lsr + ".gsfx", imported.GetProperty("sourceFileName").GetString());
    }

    /// <summary>
    /// Из ВОР, отвергнутого файла и без версии редакция не создаётся: ВОР — предложение приложить к смете,
    /// отказ разбора — его слова, без <c>If-Match</c> — 400 (ТЗ PLAN-7, PLAN-2).
    /// </summary>
    [Fact]
    public async Task Не_ЛСР_отказ_и_без_версии_редакции_нет()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var client = await EstimateImportFlow.EditorAsync(host);
        var vor = await EstimateImportFlow.OutcomeAsync(client,
            await EstimateImportFlow.UploadAsync(client, site.Construction, "vor-15.3.7-no-attachments"));
        var rejected = await EstimateImportFlow.OutcomeAsync(client, await EstimateImportFlow.UploadAsync(client,
            EstimateImportFlow.Form("просто текст"u8.ToArray(), "смета.txt", site.Construction, newName: "Никакая")));
        var lsr = await ParsedAsync(client, site);

        var toVor = await client.SendAsync(EstimateImportFlow.Commit(EstimateImportFlow.Id(vor), Version(vor)));
        Assert.Equal(HttpStatusCode.Conflict, toVor.StatusCode);
        Assert.Contains("Приложите файл к смете", await toVor.Content.ReadAsStringAsync());
        var toRejected = await client.SendAsync(EstimateImportFlow.Commit(EstimateImportFlow.Id(rejected), Version(rejected)));
        Assert.Equal(HttpStatusCode.Conflict, toRejected.StatusCode);
        Assert.Contains("не разобран", await toRejected.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.SendAsync(EstimateImportFlow.Commit(EstimateImportFlow.Id(lsr), null))).StatusCode);

        using var scope = host.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<PlanDbContext>().Estimates
            .AnyAsync(e => e.ConstructionId == site.Construction));
    }

    /// <summary>
    /// Сбой между позициями перечня и записью редакции (шаги 2 и 3): позиции заведены, редакции нет вовсе —
    /// ни половины. Повтор находит те же позиции, второй не заводит, и редакция одна (ТЗ STG-6.1, CORE-4).
    /// </summary>
    [Fact]
    public async Task Сбой_между_позициями_и_строками_повтор_без_второй_позиции()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var c = await EstimateSeed.ClassifierAsync(host);
        var client = await EstimateImportFlow.EditorAsync(host);
        var importId = EstimateImportFlow.Id(await ParsedAsync(client, site));
        var matcher = new EveryRowMatcher(c.WorkType, c.Unit);

        using (var scope = host.Services.CreateScope())
        {
            var planItems = new FailingAfter(scope.ServiceProvider.GetRequiredService<IModulePlanItems>());
            var commit = ActivatorUtilities.CreateInstance<EstimateImportCommit>(scope.ServiceProvider, matcher, (IModulePlanItems)planItems);
            var import = await scope.ServiceProvider.GetRequiredService<PlanDbContext>().EstimateImports.SingleAsync(i => i.Id == importId);
            await Assert.ThrowsAsync<InjectedFailure>(() => commit.RunAsync(import, acceptPartial: false, CancellationToken.None));
            Assert.Contains(planItems.Seen.Single().Items.Values, i => i.Created);
        }
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlanDbContext>();
            Assert.False(await db.EstimateRevisions.AnyAsync(r => r.ImportKey == importId.ToString()));
            Assert.False(await db.Estimates.AnyAsync(e => e.ConstructionId == site.Construction));
            Assert.Equal("Parsed", (await db.EstimateImports.SingleAsync(i => i.Id == importId)).State.ToString());
        }

        using (var scope = host.Services.CreateScope())
        {
            var planItems = new Recording(scope.ServiceProvider.GetRequiredService<IModulePlanItems>());
            var commit = ActivatorUtilities.CreateInstance<EstimateImportCommit>(scope.ServiceProvider, matcher, (IModulePlanItems)planItems);
            var import = await scope.ServiceProvider.GetRequiredService<PlanDbContext>().EstimateImports.SingleAsync(i => i.Id == importId);
            var done = await commit.RunAsync(import, acceptPartial: false, CancellationToken.None);

            var batch = planItems.Seen.Single();
            Assert.All(batch.Items.Values, i => Assert.False(i.Created));
            Assert.Empty(done.Unmatched);
            var rows = await scope.ServiceProvider.GetRequiredService<PlanDbContext>().EstimateRows
                .Where(r => r.RevisionId == done.Revision.Id).Select(r => r.PlanItemId).Distinct().ToListAsync();
            Assert.Equal(batch.Items.Values.Select(i => (Guid?)i.Id).Distinct(), rows);
        }
        using (var scope = host.Services.CreateScope())
        {
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<AppDbContext>().WorkPlanItems
                .CountAsync(p => p.ConstructionId == site.Construction));
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<PlanDbContext>().EstimateRevisions
                .CountAsync(r => r.ImportKey == importId.ToString()));
        }
    }

    /// <summary>
    /// Отказ ключа позиции перечня не роняет создание редакции: строка ложится несопоставленной — без
    /// отказавшего вида работы и без позиции, — а причина уходит в ответ; остальные строки сопоставлены.
    /// Номер в разделе — порядковый в разделе ЛСР, а не сквозной номер файла (ключ переноса, D9 зонтика).
    /// </summary>
    [Fact]
    public async Task Отказ_ключа_оставляет_строку_несопоставленной_с_причиной()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var c = await EstimateSeed.ClassifierAsync(host);
        var client = await EstimateImportFlow.EditorAsync(host);
        var importId = EstimateImportFlow.Id(await ParsedAsync(client, site));
        var unknown = Guid.NewGuid();

        using var scope = host.Services.CreateScope();
        var commit = ActivatorUtilities.CreateInstance<EstimateImportCommit>(scope.ServiceProvider,
            (IEstimateRowMatcher)new FirstRowUnknownMatcher(unknown, c.WorkType, c.Unit));
        var db = scope.ServiceProvider.GetRequiredService<PlanDbContext>();
        var done = await commit.RunAsync(await db.EstimateImports.SingleAsync(i => i.Id == importId), acceptPartial: false,
            CancellationToken.None);

        var refused = Assert.Single(done.Unmatched);
        Assert.Equal(1, refused.Row);
        Assert.Contains("такой записи нет", refused.Reason);
        var rows = await db.EstimateRows.AsNoTracking().Where(r => r.RevisionId == done.Revision.Id)
            .OrderBy(r => r.Order).ToListAsync();
        Assert.Null(rows[0].WorkTypeId);
        Assert.Null(rows[0].PlanItemId);
        Assert.Equal(c.Unit, rows[0].UnitId);
        Assert.All(rows.Skip(1), r => Assert.NotNull(r.PlanItemId));
        foreach (var section in rows.GroupBy(r => r.LsrSectionCode))
            Assert.Equal(Enumerable.Range(1, section.Count()).Select(i => i.ToString()), section.Select(r => r.NumberInSection));
    }

    /// <summary>
    /// Ни одной разобранной позиции — редакции нет и с согласием на частичный файл: пустая первая редакция стала
    /// бы действующей, и план стройки молча вышел бы пустым (ТЗ PLAN-7).
    /// </summary>
    [Fact]
    public async Task Ни_одной_разобранной_позиции_редакции_нет_и_с_согласием()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var client = await EstimateImportFlow.EditorAsync(host);
        var all = GrandSmetaSamples.DataXmlText(Lsr).Split("<Position ").Length - 1;
        var report = await EstimateImportFlow.OutcomeAsync(client, await EstimateImportFlow.UploadAsync(client,
            EstimateImportFlow.Form(EstimateImportFlow.WithBrokenPositions(Lsr, all), "Data.xml", site.Construction,
                newName: "Пустая")));
        Assert.Equal("parsed", report.GetProperty("state").GetString());
        Assert.Equal(0, report.GetProperty("parsed").GetInt32());

        var response = await client.SendAsync(
            EstimateImportFlow.Commit(EstimateImportFlow.Id(report), Version(report), acceptPartial: true));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("не разобрана ни одна позиция", await response.Content.ReadAsStringAsync());
        using var scope = host.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<PlanDbContext>().Estimates
            .AnyAsync(e => e.ConstructionId == site.Construction));
    }

    private async Task<JsonElement> ParsedAsync(HttpClient client, EstimateSeed.Site site)
    {
        var report = await EstimateImportFlow.OutcomeAsync(client, await EstimateImportFlow.UploadAsync(client, site.Construction, Lsr));
        Assert.Equal("parsed", report.GetProperty("state").GetString());
        Assert.Empty(report.GetProperty("unparsed").EnumerateArray());
        return report;
    }

    private static string Version(JsonElement import) => import.GetProperty("version").GetString()!;

    /// <summary>Подстановка, которая каждой строке даёт один вид работы и единицу, — как запомненное у plan-08.</summary>
    private sealed class EveryRowMatcher(Guid workType, Guid unit) : IEstimateRowMatcher
    {
        public Task<IReadOnlyList<EstimateRowDraft>> ApplyAsync(
            EstimateRowMatchTarget target, IReadOnlyList<EstimateRowDraft> rows, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<EstimateRowDraft>>(
                [.. rows.Select(r => r with { WorkTypeId = workType, UnitId = unit, Factor = 1m, Volume = r.SourceVolume })]);
    }

    /// <summary>Подстановка, у которой первая строка получает вид работы, которого в классификаторе нет.</summary>
    private sealed class FirstRowUnknownMatcher(Guid unknown, Guid workType, Guid unit) : IEstimateRowMatcher
    {
        public Task<IReadOnlyList<EstimateRowDraft>> ApplyAsync(
            EstimateRowMatchTarget target, IReadOnlyList<EstimateRowDraft> rows, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<EstimateRowDraft>>([.. rows.Select((r, i) =>
                r with { WorkTypeId = i == 0 ? unknown : workType, UnitId = unit, Factor = 1m, Volume = r.SourceVolume })]);
    }

    private sealed class InjectedFailure() : Exception("сбой между позициями перечня и записью редакции");

    /// <summary>Порт позиций, запоминающий ответы пакетом.</summary>
    private class Recording(IModulePlanItems inner) : IModulePlanItems
    {
        public List<PlanItemBatch> Seen { get; } = [];

        public Task<PlanItemOutcome> FindOrCreateAsync(PlanItemKey key, CancellationToken ct = default) =>
            inner.FindOrCreateAsync(key, ct);

        public virtual async Task<PlanItemBatch> FindOrCreateManyAsync(IReadOnlyCollection<PlanItemKey> keys, CancellationToken ct = default)
        {
            var batch = await inner.FindOrCreateManyAsync(keys, ct);
            Seen.Add(batch);
            return batch;
        }

        public Task<IReadOnlyList<ModulePlanItem>> GetAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) =>
            inner.GetAsync(ids, ct);
    }

    /// <summary>Порт, который позиции заводит — и падает сразу после: сбой между шагами 2 и 3.</summary>
    private sealed class FailingAfter(IModulePlanItems inner) : Recording(inner)
    {
        public override async Task<PlanItemBatch> FindOrCreateManyAsync(IReadOnlyCollection<PlanItemKey> keys, CancellationToken ct = default)
        {
            await base.FindOrCreateManyAsync(keys, ct);
            throw new InjectedFailure();
        }
    }
}
