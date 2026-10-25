using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Plan.Import;
using BHS.CRG.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Plan;

/// <summary>Ключ запомненного ресурса: код старше наименования, приведение осторожное (задача 2.1).</summary>
public class ResourceMatchKeyTests
{
    [Fact]
    public void Код_старше_наименования_а_ищут_по_обоим()
    {
        var key = ResourceMatchKey.Of(" 21.1.08.03-0582 ", "Кабель ВВГнг-LS 3х2,5")!;
        Assert.Equal(ResourceMatchKind.Code, key.Kind);
        Assert.Equal("21.1.08.03-0582", key.Source);
        Assert.Equal(ResourceMatchKind.Name, ResourceMatchKey.Of("  ", "Кабель")!.Kind);
        Assert.Null(ResourceMatchKey.Of(null, " "));
        Assert.Equal([ResourceMatchKind.Code, ResourceMatchKind.Name],
            ResourceMatchKey.Sought("21.1.08.03-0582", "Кабель").Select(k => k.Kind));
    }

    /// <summary>Регистр, «ё» и пробелы — не различие; «х» и «x», «,» и «.» — различие нарочно.</summary>
    [Fact]
    public void Приведение_только_регистр_ё_и_пробелы()
    {
        var plain = ResourceMatchKey.Of(ResourceMatchKind.Name, "Кабель ВВГнг 3х2,5")!.Hash;
        Assert.Equal(plain, ResourceMatchKey.Of(ResourceMatchKind.Name, "  кабель   ввгнг 3Х2,5 ")!.Hash);
        Assert.NotEqual(plain, ResourceMatchKey.Of(ResourceMatchKind.Name, "Кабель ВВГнг 3x2.5")!.Hash);
        Assert.Equal(ResourceMatchKey.Of(ResourceMatchKind.Name, "Ёрш")!.Hash, ResourceMatchKey.Of(ResourceMatchKind.Name, "ерш")!.Hash);
    }
}

/// <summary>
/// Сопоставление материалов с номенклатурой (change <c>plan-08-matching</c>, задачи 2.1–2.2; ТЗ PLAN-9.1, CORE-9,
/// COST-7.1): похожие для незнакомого ресурса, запоминание и подстановка с пометкой, архивная позиция,
/// зафиксированная редакция.
/// </summary>
[Collection(PlanOnlyCollection.Name)]
public class ResourceMatchingTests(PlanOnlyHost host)
{
    /// <summary>Единственность ключа — правило таблицы (задача 2.1).</summary>
    [Fact]
    public async Task Второй_ключ_с_тем_же_отпечатком_отвергнут_единственным_индексом()
    {
        var c = await EstimateSeed.ClassifierAsync(host);
        var code = UniqueCode();
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlanDbContext>();
            db.ResourceMatches.Add(ResourceMatch.Create(ResourceMatchKey.Of(code, null)!, c.Nomenclature, null, null));
            await db.SaveChangesAsync();
        }

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlanDbContext>();
            db.ResourceMatches.Add(ResourceMatch.Create(ResourceMatchKey.Of(" " + code.ToLowerInvariant(), "другое")!,
                c.Nomenclature, null, null));
            var refused = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Contains("ux_resource_matches_key", refused.InnerException!.Message);
        }
    }

    /// <summary>Незнакомый ресурс ждёт выбора, а окно выбора получает похожие позиции — та же по названию лучшая.</summary>
    [Fact]
    public async Task Незнакомый_ресурс_несёт_похожие()
    {
        var name = $"Провод ПуГВ {Guid.NewGuid():N}";
        var position = await NomenclatureAsync(name);
        var (_, revision, material) = await RevisionAsync(new EstimateMaterialDraft(name, 5m, UniqueCode(), "м"));
        var editor = await EstimateImportFlow.EditorAsync(host);

        var row = Assert.Single(await RateMatchFlow.RowsAsync(editor, revision));
        Assert.Equal(["unmatched"], Marks(row.GetProperty("materials")[0]));

        var response = await editor.GetAsync($"/api/plan/revisions/{revision}/resources/{material}/candidates");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var best = body.GetProperty("items")[0];
        Assert.Equal(position, best.GetProperty("id").GetGuid());
        Assert.True(best.GetProperty("strong").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("recalledUnusable").ValueKind);
    }

    /// <summary>
    /// Выбор сметчика запоминается по коду ресурса, и следующая редакция получает позицию сама — с пометкой
    /// «запомнено». Запомненная позиция ушла в архив — не подставляется, а похожие говорят почему.
    /// </summary>
    [Fact]
    public async Task Знакомый_ресурс_подставлен_с_пометкой_архивная_позиция_нет()
    {
        var code = UniqueCode();
        var position = await NomenclatureAsync($"Кабель {Guid.NewGuid():N}");
        var (estimate, revision, material) = await RevisionAsync(new EstimateMaterialDraft("Кабель силовой", 5m, code, "м"));
        var editor = await EstimateImportFlow.EditorAsync(host);

        var response = await editor.SendAsync(Match(revision, material,
            await RateMatchFlow.VersionAsync(editor, estimate, revision), new { nomenclatureId = position }));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("memory").GetProperty("remembered").GetInt32());
        var matched = body.GetProperty("row").GetProperty("materials")[0];
        Assert.Equal(position, matched.GetProperty("nomenclatureId").GetGuid());
        Assert.Empty(Marks(matched));

        // Следующий файл: тот же код, наименование написано иначе.
        var next = await RecallAsync(new EstimateMaterialDraft("КАБЕЛЬ силовой ВВГ", 7m, code, "м"));
        Assert.Equal(position, next.NomenclatureId);
        var (_, recalled, _) = await RevisionAsync(next);
        var view = Assert.Single(await RateMatchFlow.RowsAsync(editor, recalled)).GetProperty("materials")[0];
        Assert.Equal(["remembered"], Marks(view));
        Assert.Contains(code, view.GetProperty("marks")[0].GetProperty("text").GetString());

        // Позиция в архиве: подстановки нет, редакция записывается, похожие называют запомненное.
        await new WorkTypeSeed(host).ArchiveAsync(position);
        var stale = await RecallAsync(new EstimateMaterialDraft("Кабель силовой", 1m, code, "м"));
        Assert.Null(stale.NomenclatureId);
        var (_, third, waiting) = await RevisionAsync(stale);
        var candidates = await editor.GetFromJsonAsync<JsonElement>($"/api/plan/revisions/{third}/resources/{waiting}/candidates");
        Assert.Equal("archived", candidates.GetProperty("recalledUnusable").GetProperty("issue").GetString());
    }

    /// <summary>
    /// Зафиксированная редакция (решение В-6, на материалы — решение владельца 25.10.2026): не сопоставленный
    /// материал сопоставить можно, пересопоставить или снять — 409 с названием производной.
    /// </summary>
    [Fact]
    public async Task Зафиксированная_несопоставленный_можно_пересопоставить_нельзя()
    {
        var c = await EstimateSeed.ClassifierAsync(host);
        var other = await NomenclatureAsync($"Труба {Guid.NewGuid():N}");
        var (estimate, revision, _) = await RevisionAsync(
            new EstimateMaterialDraft("Кабель", 1m, UniqueCode(), "м", NomenclatureId: c.Nomenclature),
            new EstimateMaterialDraft("Труба", 2m, UniqueCode(), "м"));
        await FreezeAsync(revision);
        var editor = await EstimateImportFlow.EditorAsync(host);
        var materials = Assert.Single(await RateMatchFlow.RowsAsync(editor, revision)).GetProperty("materials");

        var free = await editor.SendAsync(Match(revision, materials[1].GetProperty("id").GetGuid(),
            await RateMatchFlow.VersionAsync(editor, estimate, revision), new { nomenclatureId = other, remember = false }));
        Assert.True(free.StatusCode == HttpStatusCode.OK, await free.Content.ReadAsStringAsync());

        var version = await RateMatchFlow.VersionAsync(editor, estimate, revision);
        foreach (var body in new object[] { new { nomenclatureId = other, remember = false }, new { clear = true } })
        {
            var refused = await editor.SendAsync(Match(revision, materials[0].GetProperty("id").GetGuid(), version, body));
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            var text = await refused.Content.ReadAsStringAsync();
            Assert.Contains("график от 12.11.2026", text);
            Assert.Contains("новая редакция", text);
        }
    }

    /// <summary>Архивная позиция — не новая ссылка материала (ТЗ CORE-34.4): 400, материал не тронут.</summary>
    [Fact]
    public async Task Архивная_позиция_материалу_не_ставится()
    {
        var archived = await NomenclatureAsync($"Снятая позиция {Guid.NewGuid():N}");
        await new WorkTypeSeed(host).ArchiveAsync(archived);
        var (estimate, revision, material) = await RevisionAsync(new EstimateMaterialDraft("Кабель", 1m, UniqueCode(), "м"));
        var editor = await EstimateImportFlow.EditorAsync(host);

        var response = await editor.SendAsync(Match(revision, material,
            await RateMatchFlow.VersionAsync(editor, estimate, revision), new { nomenclatureId = archived }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("в архиве", await response.Content.ReadAsStringAsync());
        using var scope = host.Services.CreateScope();
        Assert.Null(scope.ServiceProvider.GetRequiredService<PlanDbContext>().EstimateRowMaterials.Single(m => m.Id == material).NomenclatureId);
    }

    internal static string UniqueCode() => $"21.1.{Guid.NewGuid():N}"[..20];

    internal static HttpRequestMessage Match(Guid revision, Guid material, string? version, object body) =>
        RateMatchFlow.With(HttpMethod.Put, $"/api/plan/revisions/{revision}/resources/{material}/match", version, body);

    private static string[] Marks(JsonElement material) =>
        [.. material.GetProperty("marks").EnumerateArray().Select(m => m.GetProperty("code").GetString()!)];

    /// <summary>Редакция из одной строки с этими материалами — смета, редакция и первый материал.</summary>
    internal async Task<(Guid Estimate, Guid Revision, Guid Material)> RevisionAsync(params EstimateMaterialDraft[] materials)
    {
        var site = await EstimateSeed.SiteAsync(host);
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        var revision = (await EstimateSeed.RevisionAsync(host, estimate, new EstimateRowDraft(
            "Прокладка кабеля", RateCode: RateMatchFlow.UniqueCode(), SourceVolume: 1m, SourceUnit: "100 м",
            Materials: materials))).Id;
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlanDbContext>();
        var row = await db.EstimateRows.Where(r => r.RevisionId == revision).Select(r => r.Id).SingleAsync();
        var first = await db.EstimateRowMaterials.Where(m => m.RowId == row).OrderBy(m => m.Order).Select(m => m.Id).FirstAsync();
        return (estimate, revision, first);
    }

    /// <summary>Материал после подстановки запомненного — как его подставит создание редакции из импорта.</summary>
    private async Task<EstimateMaterialDraft> RecallAsync(EstimateMaterialDraft material)
    {
        using var scope = host.Services.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<IEstimateRowMatcher>().ApplyAsync(
            new EstimateRowMatchTarget(Guid.NewGuid(), null), [new EstimateRowDraft("Строка", Materials: [material])],
            CancellationToken.None);
        return Assert.Single(Assert.Single(rows).Materials!);
    }

    internal async Task<Guid> NomenclatureAsync(string name)
    {
        await EstimateSeed.ClassifierAsync(host);
        Guid typeId;
        using (var scope = host.Services.CreateScope())
            typeId = (await scope.ServiceProvider.GetRequiredService<IRepository<DocumentType>>()
                .FindAsync(t => t.Code == TestDirectories.NomenclatureCode)).Single().Id;
        return (await new WorkTypeSeed(host).RecordAsync(typeId, name)).Id;
    }

    internal async Task FreezeAsync(Guid revisionId)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlanDbContext>();
        (await db.EstimateRevisions.SingleAsync(r => r.Id == revisionId))
            .Freeze(new EstimateFreeze("schedule", "график от 12.11.2026"), DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
    }
}
