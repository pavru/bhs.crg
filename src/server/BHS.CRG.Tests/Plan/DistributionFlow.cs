using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Documents;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Plan;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Tests.Integration;
using BHS.CRG.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Plan;

/// <summary>
/// Распределение так, как его делает экран (change <c>plan-10-distribution</c>): чтение одним ответом и запись
/// сразу, с версией распределения заголовком <c>If-Match</c>.
/// </summary>
internal static class DistributionFlow
{
    /// <summary>Сметчик: чтение и правка смет.</summary>
    public static Task<HttpClient> EditorAsync(PlanOnlyHost host) => EstimateImportFlow.EditorAsync(host);

    public static Task<HttpClient> ReaderAsync(PlanOnlyHost host) =>
        GrantedSignIn.WithPermissionsAsync(host, PlanModule.EstimateRead);

    public static Task<JsonElement> GetAsync(HttpClient client, Guid revision) =>
        client.GetFromJsonAsync<JsonElement>($"/api/plan/revisions/{revision}/distribution");

    public static async Task<string> VersionAsync(HttpClient client, Guid revision) =>
        (await GetAsync(client, revision)).GetProperty("version").GetString()!;

    public static HttpRequestMessage Rules(Guid revision, string? version, object body) =>
        RateMatchFlow.With(HttpMethod.Put, $"/api/plan/revisions/{revision}/distribution/rules", version, body);

    public static HttpRequestMessage Lines(Guid revision, string? version, object body) =>
        RateMatchFlow.With(HttpMethod.Put, $"/api/plan/revisions/{revision}/distribution/lines", version, body);

    public static HttpRequestMessage Decide(Guid revision, Guid item, string? version, object body) =>
        RateMatchFlow.With(HttpMethod.Post, $"/api/plan/revisions/{revision}/distribution/carry-over/{item}", version, body);

    /// <summary>Записать и ждать успеха; ответ — тело записи.</summary>
    public static async Task<JsonElement> OkAsync(HttpClient client, HttpRequestMessage request)
    {
        var response = await client.SendAsync(request);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Записать правило текущей версией.</summary>
    public static async Task<JsonElement> RuleAsync(HttpClient client, Guid revision, string? code, object? target,
        bool reset = false, object? lines = null) =>
        await OkAsync(client, Rules(revision, await VersionAsync(client, revision),
            new { lsrSectionCode = code, target, resetExceptions = reset, lines }));

    public static async Task<JsonElement> LinesAsync(HttpClient client, Guid revision, params object[] lines) =>
        await OkAsync(client, Lines(revision, await VersionAsync(client, revision), new { lines }));

    public static object Section(Guid id) => new { kind = "section", sectionId = id };

    public static object Whole => new { kind = "whole" };

    public static object Line(Guid row, string unit, params (Guid Section, decimal Quantity)[] parts) =>
        new { rowId = row, unit, parts = parts.Select(p => new { sectionId = p.Section, quantity = p.Quantity }).ToArray() };

    public static object ToRule(Guid row) => new { rowId = row, unit = "row", parts = (object?)null };

    public static JsonElement LineOf(JsonElement distribution, Guid row) =>
        Assert.Single(distribution.GetProperty("lines").EnumerateArray(), l => l.GetProperty("rowId").GetGuid() == row);

    public static JsonElement RuleOf(JsonElement distribution, string? code) =>
        Assert.Single(distribution.GetProperty("rules").EnumerateArray(), r => r.GetProperty("lsrSectionCode").GetString() == code);

    /// <summary>Счётчики распределения редакции в ответе сметы — как их видит полоса «распределено N из M».</summary>
    public static async Task<JsonElement> CountsAsync(HttpClient client, Guid estimate, Guid revision) =>
        (await client.GetFromJsonAsync<JsonElement>($"/api/plan/estimates/{estimate}"))
        .GetProperty("revisions").EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == revision)
        .GetProperty("distribution");

    /// <summary>Второй раздел стройки — у каждого теста свой.</summary>
    public static async Task<Guid> SectionAsync(PlanOnlyHost host, Guid construction, string name) =>
        (await new WorkTypeSeed(host).SendAsync(new CreateSectionCommand(construction, name))).Id;

    /// <summary>Строка раздела ЛСР: (номер главы, название, шифр, объём в единице строки).</summary>
    public static EstimateRowDraft Row(string code, string section, string rate, decimal? volume, string? number = null) =>
        new($"Позиция {rate}", LsrSectionCode: code, LsrSectionName: section, NumberInSection: number, RateCode: rate,
            SourceVolume: volume, SourceUnit: "100 м", Number: number);

    public static async Task<List<Guid>> RowIdsAsync(PlanOnlyHost host, Guid revision)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PlanDbContext>().EstimateRows
            .Where(r => r.RevisionId == revision).OrderBy(r => r.Order).Select(r => r.Id).ToListAsync();
    }

    /// <summary>Записи журнала распределения редакции.</summary>
    public static async Task<List<BHS.CRG.Domain.Activity.ActivityRecord>> JournalAsync(PlanOnlyHost host, Guid revision, string action)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().ActivityRecords
            .Where(r => r.Action == action && r.TargetId == revision.ToString()).ToListAsync();
    }

    public static async Task FreezeAsync(PlanOnlyHost host, Guid revision)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlanDbContext>();
        (await db.EstimateRevisions.SingleAsync(r => r.Id == revision))
            .Freeze(new EstimateFreeze("schedule", "график от 12.11.2026"), DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
    }
}
