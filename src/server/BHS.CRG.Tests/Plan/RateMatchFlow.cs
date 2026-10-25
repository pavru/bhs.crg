using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Tests.Integration;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Plan;

/// <summary>
/// Сопоставление так, как его делает клиент (change <c>plan-08-matching</c>): версия редакции из ответа сметы,
/// адреса строки, раздела и списка запомненного, импорт образца со своим шифром.
///
/// <para>⚠️ <b>Шифр у каждого теста свой</b>: запомненное общее на базу хоста, и знакомый шифр образца, запомненный
/// одним тестом, подставился бы в импорт другого — его строки вдруг оказались бы сопоставленными.</para>
/// </summary>
internal static class RateMatchFlow
{
    private const string Lsr = "lsr-16.2.0-vn";

    /// <summary>Шифр позиции 3 образца — в файле он один, у позиции в «шт».</summary>
    private const string SampleCode = "ФЕРм10-01-001-13";

    public static string UniqueCode() => $"ГЭСНм{Guid.NewGuid():N}"[..20];

    /// <summary>Образец ЛСР, где у позиции 3 свой шифр и, если названа, своя единица.</summary>
    public static byte[] Sample(string code, string units = "шт")
    {
        var text = GrandSmetaSamples.DataXmlText(Lsr);
        var was = $"Code=\"{SampleCode}\" Units=\"шт\"";
        Assert.Single(text.Split(was)[1..]);
        return GrandSmetaSamples.Cp1251.GetBytes(text.Replace(was, $"Code=\"{code}\" Units=\"{units}\""));
    }

    /// <summary>Загрузить файл в новую смету стройки и создать из него редакцию; ответ — смета и редакция.</summary>
    public static async Task<(Guid Estimate, Guid Revision)> ImportAsync(HttpClient client, Guid construction, byte[] file)
    {
        var report = await EstimateImportFlow.OutcomeAsync(client, await EstimateImportFlow.UploadAsync(client,
            EstimateImportFlow.Form(file, "Data.xml", construction, newName: "Импорт")));
        Assert.Equal("parsed", report.GetProperty("state").GetString());
        var committed = await client.SendAsync(
            EstimateImportFlow.Commit(EstimateImportFlow.Id(report), report.GetProperty("version").GetString()));
        Assert.True(committed.StatusCode == HttpStatusCode.Created, await committed.Content.ReadAsStringAsync());
        var done = await committed.Content.ReadFromJsonAsync<JsonElement>();
        return (done.GetProperty("estimate").GetProperty("id").GetGuid(), done.GetProperty("revisionId").GetGuid());
    }

    /// <summary>Строки редакции — одной страницей.</summary>
    public static async Task<List<JsonElement>> RowsAsync(HttpClient client, Guid revision) =>
        [.. (await client.GetFromJsonAsync<JsonElement>($"/api/plan/revisions/{revision}/rows?limit=1000"))
            .GetProperty("items").EnumerateArray()];

    public static async Task<JsonElement> RowAsync(HttpClient client, Guid revision, string code) =>
        Assert.Single(await RowsAsync(client, revision), r => r.GetProperty("rateCode").GetString() == code);

    /// <summary>Версия редакции — из ответа сметы, как её берёт экран.</summary>
    public static async Task<string> VersionAsync(HttpClient client, Guid estimate, Guid revision) =>
        (await client.GetFromJsonAsync<JsonElement>($"/api/plan/estimates/{estimate}"))
        .GetProperty("revisions").EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == revision)
        .GetProperty("version").GetString()!;

    public static HttpRequestMessage MatchRow(Guid revision, Guid row, string? version, object body) =>
        With(HttpMethod.Put, $"/api/plan/revisions/{revision}/lines/{row}/match", version, body);

    public static HttpRequestMessage MatchSection(Guid revision, string? version, object body) =>
        With(HttpMethod.Post, $"/api/plan/revisions/{revision}/sections/match", version, body);

    public static HttpRequestMessage With(HttpMethod method, string url, string? version, object? body = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        if (version is not null) request.Headers.TryAddWithoutValidation("If-Match", version);
        return request;
    }

    public static string[] Marks(JsonElement row) =>
        [.. row.GetProperty("marks").EnumerateArray().Select(m => m.GetProperty("code").GetString()!)];

    /// <summary>Строка сметы, как она лежит.</summary>
    public static EstimateRow Stored(PlanOnlyHost host, Guid row)
    {
        using var scope = host.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<PlanDbContext>().EstimateRows.Single(r => r.Id == row);
    }

    /// <summary>Запомненное по шифру и базе образца — или <c>null</c>.</summary>
    public static RateMatch? Remembered(PlanOnlyHost host, string code, string? regionName)
    {
        using var scope = host.Services.CreateScope();
        var hash = RateMatchKey.Of(code, regionName)!.Hash;
        return scope.ServiceProvider.GetRequiredService<PlanDbContext>().RateMatches.SingleOrDefault(m => m.KeyHash == hash);
    }

    /// <summary>Нормативная база образца, как её называет файл.</summary>
    public const string SampleBase = "ГЭСН-2020, ФЕР-2020 (с Изм. 1-9) с КСР по приказу №969/пр от 17.11.2022";
}
