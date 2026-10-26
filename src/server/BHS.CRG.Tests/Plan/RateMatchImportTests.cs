using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Tests.Integration;

namespace BHS.CRG.Tests.Plan;

/// <summary>
/// Запомненное в импорте (change <c>plan-08-matching</c>, задача 1.4; ТЗ PLAN-6.1, D17 зонтика): выбор сметчика
/// запоминается по шифру и базе и при следующем импорте подставляется сам с пометкой «запомнено»; знакомый шифр в
/// другой единице расценки не подставляется (<c>unit-changed</c>); забытое не подставляется и уже сопоставленных
/// строк не меняет.
/// </summary>
[Collection(PlanOnlyCollection.Name)]
public class RateMatchImportTests(PlanOnlyHost host)
{
    [Fact]
    public async Task Повторный_импорт_подставляет_изменённая_единица_нет_забытое_нет()
    {
        var c = await EstimateSeed.ClassifierAsync(host);
        var site = await EstimateSeed.SiteAsync(host);
        var editor = await EstimateImportFlow.EditorAsync(host);
        var code = RateMatchFlow.UniqueCode();

        // Первый импорт: шифр незнаком — строка ждёт выбора. Сметчик выбирает вид работы и другую единицу, «шт».
        var (estimate, first) = await RateMatchFlow.ImportAsync(editor, site.Construction, RateMatchFlow.Sample(code));
        var row = await RateMatchFlow.RowAsync(editor, first, code);
        Assert.Equal(["unmatched"], RateMatchFlow.Marks(row));
        var matched = await editor.SendAsync(RateMatchFlow.MatchRow(first, row.GetProperty("id").GetGuid(),
            await RateMatchFlow.VersionAsync(editor, estimate, first), new { workTypeId = c.WorkType, unitId = c.Piece }));
        Assert.True(matched.StatusCode == HttpStatusCode.OK, await matched.Content.ReadAsStringAsync());
        var memory = (await matched.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("memory");
        Assert.Equal(1, memory.GetProperty("remembered").GetInt32());
        Assert.False(memory.GetProperty("failed").GetBoolean());
        var remembered = RateMatchFlow.Remembered(host, code, RateMatchFlow.SampleBase);
        Assert.NotNull(remembered);
        Assert.Equal((c.WorkType, c.Piece, 1m, "шт"), (remembered.WorkTypeId, remembered.UnitId, remembered.Factor, remembered.SourceUnit));

        // Второй импорт того же шифра — подставлено с пометкой «запомнено».
        var (_, second) = await RateMatchFlow.ImportAsync(editor, site.Construction, RateMatchFlow.Sample(code));
        var substituted = await RateMatchFlow.RowAsync(editor, second, code);
        Assert.Equal(["remembered"], RateMatchFlow.Marks(substituted));
        Assert.Equal(c.WorkType, substituted.GetProperty("workTypeId").GetGuid());
        Assert.Equal(c.Piece, substituted.GetProperty("unitId").GetGuid());
        Assert.Equal(row.GetProperty("sourceVolume").GetDecimal(), substituted.GetProperty("volume").GetDecimal());
        Assert.Equal(JsonValueKind.String, substituted.GetProperty("planItemId").ValueKind);
        Assert.Equal(remembered.Id, RateMatchFlow.Stored(host, substituted.GetProperty("id").GetGuid()).RateMatchId);

        // Тот же шифр в другой единице расценки — не подставлено, строка помечена unit-changed.
        var (thirdEstimate, third) = await RateMatchFlow.ImportAsync(editor, site.Construction, RateMatchFlow.Sample(code, "10 шт"));
        var changed = await RateMatchFlow.RowAsync(editor, third, code);
        Assert.Equal(["unmatched", "unit-changed"], RateMatchFlow.Marks(changed));
        Assert.Equal(JsonValueKind.Null, changed.GetProperty("workTypeId").ValueKind);
        // Счётчик редакции — тем же правилом, что пометка (D4 plan-31).
        Assert.Equal(1, Assert.Single((await editor.GetFromJsonAsync<JsonElement>($"/api/plan/estimates/{thirdEstimate}"))
            .GetProperty("revisions").EnumerateArray()).GetProperty("unitChanged").GetInt32());

        // Забыть: следующий импорт не сопоставляет, а сопоставленная запомненным строка остаётся как была.
        var list = await editor.GetFromJsonAsync<JsonElement>($"/api/plan/rate-matches?query={Uri.EscapeDataString(code)}");
        var item = Assert.Single(list.GetProperty("items").EnumerateArray());
        var forgot = await editor.SendAsync(RateMatchFlow.With(HttpMethod.Delete, $"/api/plan/rate-matches/{item.GetProperty("id").GetGuid()}",
            item.GetProperty("version").GetString()));
        Assert.Equal(HttpStatusCode.NoContent, forgot.StatusCode);

        var (_, fourth) = await RateMatchFlow.ImportAsync(editor, site.Construction, RateMatchFlow.Sample(code));
        Assert.Equal(["unmatched"], RateMatchFlow.Marks(await RateMatchFlow.RowAsync(editor, fourth, code)));
        var kept = await RateMatchFlow.RowAsync(editor, second, code);
        Assert.Equal(c.WorkType, kept.GetProperty("workTypeId").GetGuid());
        var mark = Assert.Single(kept.GetProperty("marks").EnumerateArray());
        Assert.Equal("remembered", mark.GetProperty("code").GetString());
        Assert.Contains("забыли", mark.GetProperty("text").GetString());
    }

    /// <summary>Отказ человека — <c>remember: false</c>: строка сопоставлена, а шифр не запомнен.</summary>
    [Fact]
    public async Task Без_запоминания_шифр_не_запомнен()
    {
        var c = await EstimateSeed.ClassifierAsync(host);
        var site = await EstimateSeed.SiteAsync(host);
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        var code = RateMatchFlow.UniqueCode();
        var revision = (await EstimateSeed.RevisionAsync(host, estimate, RateMatchingTests.Row(code, 3m, "100 м"))).Id;
        var editor = await EstimateImportFlow.EditorAsync(host);
        var row = Assert.Single(await RateMatchFlow.RowsAsync(editor, revision));

        var response = await editor.SendAsync(RateMatchFlow.MatchRow(revision, row.GetProperty("id").GetGuid(),
            await RateMatchFlow.VersionAsync(editor, estimate, revision), new { workTypeId = c.WorkType, remember = false }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("memory").GetProperty("remembered").GetInt32());
        Assert.Null(RateMatchFlow.Remembered(host, code, null));
    }
}
