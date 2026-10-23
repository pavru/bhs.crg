using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Plan;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Tests.Integration;
using BHS.CRG.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Plan;

/// <summary>
/// Редакции сметы: фиксация, действующая редакция и план стройки по действующим (change
/// <c>plan-06-estimate-model</c>, ТЗ PLAN-5, PLAN-5.2, CORE-28).
/// </summary>
[Collection(PlanOnlyCollection.Name)]
public class EstimateRevisionTests(PlanOnlyHost host)
{
    [Fact]
    public async Task Первая_редакция_становится_действующей_сама_а_следующая_нет()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);

        var first = await EstimateSeed.RevisionAsync(host, estimate, new EstimateRowDraft("Строка 1"));
        Assert.Equal(1, first.Number);
        Assert.Equal(first.Id, await EstimateSeed.CurrentRevisionAsync(host, estimate));

        var second = await EstimateSeed.RevisionAsync(host, estimate, new EstimateRowDraft("Строка 1"));
        Assert.Equal(2, second.Number);
        // Решение В-5: повторный импорт не меняет план молча — действующую выбирает человек.
        Assert.Equal(first.Id, await EstimateSeed.CurrentRevisionAsync(host, estimate));
    }

    /// <summary>
    /// Смена действующей меняет план стройки и пишется в журнал «редакция 1 → редакция 2» (ТЗ PLAN-5.2).
    /// </summary>
    [Fact]
    public async Task Смена_действующей_меняет_план_и_пишет_журнал()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        await EstimateSeed.RevisionAsync(host, estimate, await EstimateSeed.MatchedRowAsync(host, site, 10m));
        var second = await EstimateSeed.RevisionAsync(host, estimate, await EstimateSeed.MatchedRowAsync(host, site, 30m));
        Assert.Equal(10m, await PlanVolumeAsync(site.Construction));

        var editor = await EditorAsync();
        var response = await MakeCurrentAsync(editor, estimate, second.Id, await VersionAsync(editor, estimate));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(second.Id, await EstimateSeed.CurrentRevisionAsync(host, estimate));
        Assert.Equal(30m, await PlanVolumeAsync(site.Construction));

        using var scope = host.Services.CreateScope();
        var record = await scope.ServiceProvider.GetRequiredService<AppDbContext>().ActivityRecords
            .SingleAsync(r => r.Action == "plan.estimate.current" && r.TargetId == estimate.ToString());
        Assert.Equal("редакция 1", record.Before);
        Assert.Equal("редакция 2", record.After);
    }

    [Fact]
    public async Task Смена_без_версии_отвергается_и_действующая_не_меняется()
    {
        var (estimate, first, second) = await TwoRevisionsAsync();
        var editor = await EditorAsync();

        var response = await MakeCurrentAsync(editor, estimate, second, version: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(first, await EstimateSeed.CurrentRevisionAsync(host, estimate));
    }

    [Fact]
    public async Task Смена_по_устаревшей_версии_409()
    {
        var (estimate, first, second) = await TwoRevisionsAsync();
        var editor = await EditorAsync();
        var stale = await VersionAsync(editor, estimate);
        var rename = new HttpRequestMessage(HttpMethod.Put, $"/api/plan/estimates/{estimate}")
        {
            Content = JsonContent.Create(new { name = "Основная, переименована" }),
        };
        rename.Headers.TryAddWithoutValidation("If-Match", stale);
        Assert.Equal(HttpStatusCode.OK, (await editor.SendAsync(rename)).StatusCode);

        var response = await MakeCurrentAsync(editor, estimate, second, stale);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("тем временем изменили", await response.Content.ReadAsStringAsync());
        Assert.Equal(first, await EstimateSeed.CurrentRevisionAsync(host, estimate));
    }

    [Fact]
    public async Task Действующую_редакцию_удалить_нельзя()
    {
        var (_, first, _) = await TwoRevisionsAsync();
        var editor = await EditorAsync();

        var response = await DeleteRevisionAsync(editor, first);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("сначала сделайте действующей другую", (await response.Content.ReadAsStringAsync()).ToLowerInvariant());
        Assert.True(await RevisionExistsAsync(first));
    }

    /// <summary>Правка и удаление зафиксированной редакции — 409 с названием производной (ТЗ PLAN-5).</summary>
    [Fact]
    public async Task Зафиксированную_редакцию_удалить_нельзя_и_отказ_называет_производную()
    {
        var (_, _, second) = await TwoRevisionsAsync();
        await FreezeAsync(second, new EstimateFreeze("offer", "КП № КП-2026-014"));
        var editor = await EditorAsync();

        var response = await DeleteRevisionAsync(editor, second);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("зафиксирована", text);
        Assert.Contains("из неё сделано КП № КП-2026-014", text);
        Assert.True(await RevisionExistsAsync(second));
    }

    /// <summary>Повторная фиксация не меняет первую производную: её и называет отказ.</summary>
    [Fact]
    public async Task Повторная_фиксация_помнит_первую_производную()
    {
        var (_, _, second) = await TwoRevisionsAsync();
        await FreezeAsync(second, new EstimateFreeze("offer", "КП № КП-2026-014"));
        await FreezeAsync(second, new EstimateFreeze("schedule", "график от 12.11.2026"));

        using var scope = host.Services.CreateScope();
        var revision = await scope.ServiceProvider.GetRequiredService<PlanDbContext>().EstimateRevisions.SingleAsync(r => r.Id == second);
        Assert.Equal("offer", revision.FrozenCode);
        var refusal = Assert.Throws<BHS.CRG.Domain.Common.ConflictException>(() => revision.EnsureEditable("Сопоставить строку"));
        Assert.Contains("КП № КП-2026-014", refusal.Message);
    }

    [Fact]
    public async Task Не_действующая_и_не_зафиксированная_редакция_удаляется_со_строками_и_в_журнал()
    {
        var (estimate, _, second) = await TwoRevisionsAsync();
        var editor = await EditorAsync();

        var response = await DeleteRevisionAsync(editor, second);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(await RevisionExistsAsync(second));
        using var scope = host.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<PlanDbContext>().EstimateRows.AnyAsync(r => r.RevisionId == second));
        Assert.True(await scope.ServiceProvider.GetRequiredService<AppDbContext>().ActivityRecords
            .AnyAsync(r => r.Action == "plan.revision.deleted" && r.TargetId == second.ToString()));
        Assert.NotNull(await EstimateSeed.CurrentRevisionAsync(host, estimate));
    }

    /// <summary>
    /// План стройки — сумма действующих редакций ВСЕХ её смет: основная и дополнительная входят, прежние
    /// редакции и несопоставленные строки — нет (ТЗ PLAN-4, PLAN-5.2).
    /// </summary>
    [Fact]
    public async Task План_стройки_сумма_действующих_редакций_всех_смет()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var main = await EstimateSeed.EstimateAsync(host, site.Construction);
        await EstimateSeed.RevisionAsync(host, main, await EstimateSeed.MatchedRowAsync(host, site, 10m), new EstimateRowDraft("Без вида работы", Volume: 1000m));
        await EstimateSeed.RevisionAsync(host, main, await EstimateSeed.MatchedRowAsync(host, site, 500m));
        var extra = await EstimateSeed.EstimateAsync(host, site.Construction, "Дополнительная", EstimateKind.Additional);
        await EstimateSeed.RevisionAsync(host, extra, await EstimateSeed.MatchedRowAsync(host, site, 7m));

        // Чужая стройка в план не входит.
        var other = await EstimateSeed.SiteAsync(host);
        await EstimateSeed.RevisionAsync(host, await EstimateSeed.EstimateAsync(host, other.Construction),
            await EstimateSeed.MatchedRowAsync(host, other, 100m));

        Assert.Equal(17m, await PlanVolumeAsync(site.Construction));
    }

    /// <summary>
    /// Новая ссылка на запись в архиве не записывается (ТЗ CORE-34.4): редакция заводится целиком, и все
    /// её ссылки новые. Отказ называет строку, а редакции не появляется вовсе.
    /// </summary>
    [Fact]
    public async Task Новая_ссылка_на_архивный_вид_работы_отвергается_и_редакции_нет()
    {
        var c = await EstimateSeed.ClassifierAsync(host);
        var seed = new WorkTypeSeed(host);
        var archived = await seed.RecordAsync(c.WorkTypeClass, $"Снятый вид {Guid.NewGuid():N}");
        await seed.ArchiveAsync(archived.Id);
        var site = await EstimateSeed.SiteAsync(host);
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);

        var refusal = await Assert.ThrowsAsync<BHS.CRG.Domain.Common.InvalidRequestException>(() =>
            EstimateSeed.RevisionAsync(host, estimate,
                new EstimateRowDraft("Строка 1"), new EstimateRowDraft("Строка 2", WorkTypeId: archived.Id)));

        Assert.Contains("Строка 2, вид работы: запись в архиве", refusal.Message);
        Assert.Null(await EstimateSeed.CurrentRevisionAsync(host, estimate));
        using var scope = host.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<PlanDbContext>().EstimateRevisions
            .AnyAsync(r => r.EstimateId == estimate));
    }

    /// <summary>Ссылка на запись, которой нет, — тоже отказ, а не строка с потерянной ссылкой.</summary>
    [Fact]
    public async Task Ссылка_на_несуществующую_позицию_номенклатуры_отвергается()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);

        var refusal = await Assert.ThrowsAsync<BHS.CRG.Domain.Common.InvalidRequestException>(() =>
            EstimateSeed.RevisionAsync(host, estimate, new EstimateRowDraft("Строка 1",
                Materials: [new EstimateMaterialDraft("Кабель", 1m, NomenclatureId: Guid.NewGuid())])));

        Assert.Contains("Строка 1, позиция номенклатуры материала: записи нет", refusal.Message);
    }

    private async Task<decimal> PlanVolumeAsync(Guid construction)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlanDbContext>();
        return await PlanRows.Current(db, construction).SumAsync(r => r.Volume ?? 0m);
    }

    private async Task<(Guid Estimate, Guid First, Guid Second)> TwoRevisionsAsync()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        var first = await EstimateSeed.RevisionAsync(host, estimate, new EstimateRowDraft("Строка 1"));
        var second = await EstimateSeed.RevisionAsync(host, estimate, new EstimateRowDraft("Строка 1"), new EstimateRowDraft("Строка 2"));
        return (estimate, first.Id, second.Id);
    }

    private async Task FreezeAsync(Guid revisionId, EstimateFreeze reason)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlanDbContext>();
        (await db.EstimateRevisions.SingleAsync(r => r.Id == revisionId)).Freeze(reason, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
    }

    private async Task<bool> RevisionExistsAsync(Guid id)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PlanDbContext>().EstimateRevisions.AnyAsync(r => r.Id == id);
    }

    private Task<HttpClient> EditorAsync() =>
        GrantedSignIn.WithPermissionsAsync(host, PlanModule.EstimateRead, PlanModule.EstimateEdit);

    internal static async Task<string> VersionAsync(HttpClient client, Guid estimate) =>
        (await client.GetFromJsonAsync<JsonElement>($"/api/plan/estimates/{estimate}")).GetProperty("version").GetString()!;

    private static Task<HttpResponseMessage> MakeCurrentAsync(HttpClient client, Guid estimate, Guid revision, string? version)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/plan/estimates/{estimate}/current-revision")
        {
            Content = JsonContent.Create(new { revisionId = revision }),
        };
        if (version is not null) request.Headers.TryAddWithoutValidation("If-Match", version);
        return client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> DeleteRevisionAsync(HttpClient client, Guid revision)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/plan/revisions/{revision}");
        request.Headers.TryAddWithoutValidation("If-Match", await RevisionVersionAsync(client, revision));
        return await client.SendAsync(request);
    }

    /// <summary>Версия редакции — из ответа сметы, как её получит экран.</summary>
    private async Task<string> RevisionVersionAsync(HttpClient client, Guid revision)
    {
        Guid estimate;
        using (var scope = host.Services.CreateScope())
            estimate = await scope.ServiceProvider.GetRequiredService<PlanDbContext>().EstimateRevisions
                .Where(r => r.Id == revision).Select(r => r.EstimateId).SingleAsync();

        var body = await client.GetFromJsonAsync<JsonElement>($"/api/plan/estimates/{estimate}");
        return body.GetProperty("revisions").EnumerateArray()
            .Single(r => r.GetProperty("id").GetGuid() == revision).GetProperty("version").GetString()!;
    }
}
