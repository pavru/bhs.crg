using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Application.Activity;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using BHS.CRG.Modules.Costs;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Принудительное удаление уровня — стройки, раздела, комплекта, — на содержимое которого ссылаются
/// только данные выключенного или снятого модуля (issue #1243).
/// </summary>
public partial class RecordPurgeTests
{
    /// <summary>
    /// СТОРОЖ ЗАДАЧИ. Стройку держит снятый модуль — и её саму, и запись, заведённую на её разделе:
    /// число в предложении — ОБЩЕЕ, по всему уходящему. Неверное число — отказ со свежим
    /// предложением, стройка на месте; верное — стройка удалена со всем поддеревом, а в журнале —
    /// кто держал, сколько ссылок потеряно и что ушло вместе.
    /// </summary>
    [Fact]
    public async Task Стройку_занятую_данными_снятого_модуля_удаляют_назвав_общее_число_ссылок()
    {
        var (client, _) = await SignInAsync(SystemRoles.Admin);
        var (site, sections) = await SiteAsync($"Пробная {Guid.NewGuid().ToString()[..8]}", "1 эт.", "2 эт.");
        var set = await SetInAsync(client, sections[0]);
        var inside = await RecordAtAsync(CatalogScope.Section, sections[1]);
        // Профили уровней — служебные объекты на той же оси: уходят со стройкой, но в счёт
        // «документов и записей» не входят (ревью PR #1309) — человек их не заводил.
        // Тип профиля на тестовом хосте не настроен, поэтому профиль назначается напрямую — тем же
        // полем, которое проставляет служба профилей.
        var siteProfile = await RecordAtAsync(CatalogScope.Construction, site);
        var sectionProfile = await RecordAtAsync(CatalogScope.Section, sections[0]);
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.Constructions.SingleAsync(c => c.Id == site)).SetProfileObject(siteProfile);
            (await db.Sections.SingleAsync(s => s.Id == sections[0])).SetProfileObject(sectionProfile);
            await db.SaveChangesAsync();
        }
        await using var onSite = await HeldByProbeAsync(site, rows: 2);
        await using var onRecord = await HeldByProbeAsync(inside, rows: 1);

        var refusal = await client.DeleteAsync($"/api/constructions/{site}");
        Assert.Equal(HttpStatusCode.Conflict, refusal.StatusCode);
        var body = await refusal.Content.ReadFromJsonAsync<JsonElement>();
        var offer = body.GetProperty("purge");
        Assert.True(offer.GetProperty("allowed").GetBoolean());
        Assert.Equal(3, offer.GetProperty("references").GetInt32());
        Assert.Equal(3, offer.GetProperty("untraceable").GetInt32());
        Assert.Equal(2, offer.GetProperty("holders").GetArrayLength());
        var leaving = body.GetProperty("leaving");
        Assert.Equal(2, leaving.GetProperty("sections").GetInt32());
        Assert.Equal(1, leaving.GetProperty("sets").GetInt32());
        Assert.Equal(1, leaving.GetProperty("objects").GetInt32());

        // Число одной из записей поддерева — не общее: отказ, и ничего не тронуто.
        var wrong = await client.PostAsJsonAsync($"/api/constructions/{site}/purge", new { references = 2 });
        Assert.Equal(HttpStatusCode.Conflict, wrong.StatusCode);
        var again = await wrong.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("Число ссылок не совпало", again.GetProperty("error").GetString());
        Assert.Equal(3, again.GetProperty("purge").GetProperty("references").GetInt32());
        Assert.Equal(2, again.GetProperty("leaving").GetProperty("sections").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/common-data/{inside}")).StatusCode);
        Assert.Empty(await ScopePurgedAsync(site));

        var done = await client.PostAsJsonAsync($"/api/constructions/{site}/purge", new { references = 3 });
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        Assert.Equal(3, (await done.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("references").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/constructions/{site}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/common-data/{inside}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/document-sets/{set}")).StatusCode);

        var written = Assert.Single(await ScopePurgedAsync(site));
        Assert.StartsWith("Стройка: Пробная", written.TargetLabel);
        Assert.Contains($"{onSite.Schema}.things.held", written.Before);
        Assert.Contains($"{onRecord.Schema}.things.held", written.Before);
        Assert.Equal(
            "потеряно ссылок: 3; удалено вместе: разделов — 2, комплектов — 1, документов и записей — 1",
            written.After);
        Assert.NotEqual("", written.ActorName);
    }

    /// <summary>Раздел и комплект идут тем же телом: у каждого свой адрес и своя подпись в журнале.</summary>
    [Fact]
    public async Task Раздел_и_комплект_удаляются_тем_же_путём()
    {
        var (client, _) = await SignInAsync(SystemRoles.Admin);
        var (site, sections) = await SiteAsync($"Жилая {Guid.NewGuid().ToString()[..8]}", "A", "B");
        var set = await SetInAsync(client, sections[0]);
        var other = await SetInAsync(client, sections[0]);
        await using var onSet = await HeldByProbeAsync(set, rows: 1);
        await using var onSection = await HeldByProbeAsync(sections[1], rows: 2);

        var refused = await client.DeleteAsync($"/api/document-sets/{set}");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(1, (await refused.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("purge").GetProperty("references").GetInt32());
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsJsonAsync($"/api/document-sets/{set}/purge", new { references = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/document-sets/{set}")).StatusCode);
        Assert.StartsWith("Комплект: ", Assert.Single(await ScopePurgedAsync(set)).TargetLabel);

        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/sections/{sections[1]}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsJsonAsync($"/api/sections/{sections[1]}/purge", new { references = 2 })).StatusCode);
        Assert.Equal("Раздел: B", Assert.Single(await ScopePurgedAsync(sections[1])).TargetLabel);

        // Соседи на месте: удаление не шире названного уровня.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/document-sets/{other}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/constructions/{site}")).StatusCode);
    }

    /// <summary>
    /// Без права терять ссылки адрес закрыт — и тому, у кого есть правка строек. Отказ обычного
    /// удаления называет выход без разбивки и без состава уходящего.
    /// </summary>
    [Fact]
    public async Task Без_права_уровень_принудительно_не_удаляют()
    {
        var (client, _) = await SignInAsync(SystemRoles.IdEngineer);
        var (site, sections) = await SiteAsync($"Чужая {Guid.NewGuid().ToString()[..8]}", "1 эт.");
        await using var probe = await HeldByProbeAsync(site, rows: 1);

        foreach (var address in new[] { $"/api/constructions/{site}", $"/api/sections/{sections[0]}" })
            Assert.Equal(HttpStatusCode.Forbidden,
                (await client.PostAsJsonAsync($"{address}/purge", new { references = 1 })).StatusCode);

        var refusal = await client.DeleteAsync($"/api/constructions/{site}");
        Assert.Equal(HttpStatusCode.Conflict, refusal.StatusCode);
        var body = await refusal.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("purge").GetProperty("allowed").GetBoolean());
        Assert.Empty(body.GetProperty("purge").GetProperty("holders").EnumerateArray());
        Assert.False(body.TryGetProperty("leaving", out _));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/constructions/{site}")).StatusCode);
    }

    /// <summary>
    /// Стройку, на которую разнесён счёт ВКЛЮЧЁННОГО модуля, не удаляет и право с верным числом —
    /// даже если её держит ещё и снятый: одного включённого держателя достаточно.
    /// </summary>
    [Fact]
    public async Task Уровень_занятый_включённым_модулем_право_не_удаляет()
    {
        var (client, _) = await SignInAsync(SystemRoles.Admin);
        var invoice = await CreateAsync(client, complete: true);
        var view = await LinesAsync(client, invoice, [Line(cable, quantity: 10, price: 10)]);
        var (site, _) = await SiteAsync($"Занятая {Guid.NewGuid().ToString()[..8]}", "1 эт.");
        await AllocateAsync(client, invoice, LineId(view, 1), [Part(site, quantity: 10)]);
        await using var probe = await HeldByProbeAsync(site, rows: 1);

        var refusal = await client.DeleteAsync($"/api/constructions/{site}");
        Assert.Equal(JsonValueKind.Null,
            (await refusal.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("purge").ValueKind);

        var purge = await client.PostAsJsonAsync($"/api/constructions/{site}/purge", new { references = 2 });
        Assert.Equal(HttpStatusCode.Conflict, purge.StatusCode);
        Assert.Equal(JsonValueKind.Null,
            (await purge.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("purge").ValueKind);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/constructions/{site}")).StatusCode);
        Assert.Empty(await ScopePurgedAsync(site));
    }

    /// <summary>
    /// Отказ ядра принудительное удаление не обходит и предложения не получает: на запись стройки
    /// ссылается запись снаружи — и снятый модуль рядом этого не меняет.
    /// </summary>
    [Fact]
    public async Task Отказ_ядра_уровню_остаётся_и_предложения_не_несёт()
    {
        var (client, _) = await SignInAsync(SystemRoles.Admin);
        var (site, _) = await SiteAsync($"Со ссылкой {Guid.NewGuid().ToString()[..8]}", "1 эт.");
        var inside = await RecordAtAsync(CatalogScope.Construction, site);
        await ReferringAsync(inside);
        await using var probe = await HeldByProbeAsync(site, rows: 1);

        var refusal = await client.DeleteAsync($"/api/constructions/{site}");
        Assert.Equal(HttpStatusCode.Conflict, refusal.StatusCode);
        Assert.Contains("ссылаются извне", await refusal.Content.ReadAsStringAsync());

        var purge = await client.PostAsJsonAsync($"/api/constructions/{site}/purge", new { references = 1 });
        Assert.Equal(HttpStatusCode.Conflict, purge.StatusCode);
        var body = await purge.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("ссылаются извне", body.GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("purge").ValueKind);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/constructions/{site}")).StatusCode);
    }

    /// <summary>Свободный уровень принудительно не удаляют: право не второе обычное удаление.</summary>
    [Fact]
    public async Task Свободный_уровень_принудительно_не_удаляют()
    {
        var (client, _) = await SignInAsync(SystemRoles.Admin);
        var (site, _) = await SiteAsync($"Свободная {Guid.NewGuid().ToString()[..8]}");

        var refusal = await client.PostAsJsonAsync($"/api/constructions/{site}/purge", new { references = 0 });

        Assert.Equal(HttpStatusCode.Conflict, refusal.StatusCode);
        Assert.Contains("обычным путём", await refusal.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/constructions/{site}")).StatusCode);
    }

    // ── Помощники ─────────────────────────────────────────────────────────────

    private static async Task<Guid> SetInAsync(HttpClient client, Guid section)
    {
        var created = await client.PostAsJsonAsync("/api/document-sets",
            new { sectionId = section, name = $"Комплект {Guid.NewGuid().ToString()[..8]}" });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    /// <summary>Запись общих данных, заведённая на уровне: она уходит вместе с ним.</summary>
    private async Task<Guid> RecordAtAsync(CatalogScope level, Guid levelId)
    {
        using var scope = host.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new CreateCommonDataEntryCommand(
            $"На уровне {Guid.NewGuid().ToString()[..8]}",
            await TypeAsync(TestDirectories.NomenclatureCode, "Номенклатура"),
            JsonDocument.Parse("""{"Наименование":"x"}"""), level, levelId))).Id;
    }

    private async Task<IReadOnlyList<BHS.CRG.Domain.Activity.ActivityRecord>> ScopePurgedAsync(Guid level)
    {
        using var scope = host.Services.CreateScope();
        var all = await scope.ServiceProvider.GetRequiredService<IActivityLog>()
            .ReadAsync(0, 200, ActivityVisibility.Whole, ActivityActions.ScopePurged.Code);
        return [.. all.Where(r => r.TargetId == level.ToString())];
    }
}
