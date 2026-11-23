using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Modules.Plan;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Tests.Integration;
using BHS.CRG.Tests.Support;

namespace BHS.CRG.Tests.Plan;

internal sealed record OfferDerivativeScenario(
    PlanOnlyHost Host, EstimateSeed.Site Site, Guid EstimateId, HttpClient Manager)
{
    internal static async Task<OfferDerivativeScenario> NewAsync(PlanOnlyHost host)
    {
        var site = await EstimateSeed.SiteAsync(host);
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        var manager = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.OfferRead, PlanModule.OfferEdit,
            PlanModule.OfferSend, PlanModule.EstimateRead, PlanModule.EstimateEdit);
        return new(host, site, estimate, manager);
    }

    internal async Task<Guid> RevisionAsync(params EstimateRowDraft[] rows) =>
        (await EstimateSeed.RevisionAsync(Host, EstimateId, rows)).Id;

    internal Task<JsonElement> CreateAsync(Guid revisionId, decimal markup = 20m) =>
        ReadAsync(Manager.PostAsJsonAsync("/api/plan/offers", new { revisionId, markup }), HttpStatusCode.Created);

    internal async Task MakeCurrentAsync(Guid revisionId)
    {
        var url = $"/api/plan/estimates/{EstimateId}";
        var estimate = await ReadAsync(Manager.GetAsync(url));
        await ReadAsync(SendAsync(Manager, HttpMethod.Post, $"{url}/current-revision", Version(estimate), new { revisionId }));
    }

    internal static string Url(JsonElement offer) => $"/api/plan/offers/{offer.GetProperty("id").GetGuid()}";

    internal static string Version(JsonElement snapshot) => snapshot.GetProperty("version").GetString()!;

    internal static string Fingerprint(JsonElement preview) => preview.GetProperty("fingerprint").GetString()!;

    internal static Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string url, string? version, object body) =>
        client.SendAsync(RateMatchFlow.With(method, url, version, body));

    internal static async Task<JsonElement> ReadAsync(
        Task<HttpResponseMessage> pending, HttpStatusCode status = HttpStatusCode.OK)
    {
        using var response = await pending;
        Assert.True(response.StatusCode == status, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    internal static EstimateRowDraft Row(string name, string section = "1", string number = "1", decimal cost = 100m) =>
        new(name, LsrSectionCode: section, LsrSectionName: $"Раздел {section}", NumberInSection: number,
            RateCode: "ГЭСН 01", SourceVolume: 1m,
            SourceUnit: "100 м", Total: cost);
}
