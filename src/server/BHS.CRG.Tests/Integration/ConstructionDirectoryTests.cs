using System.Net;
using System.Net.Http.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Tests.Support;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Лёгкий справочник строек <c>GET /api/constructions/directory</c> (issue #1218, ревью PR #1331):
/// id и имя, по названию. Его читает фильтр «Избранные стройки» на каждом экране, поэтому и право
/// у него то же, что у полного списка, — чтение справочника, а не правка.
/// </summary>
[Collection("Integration")]
public class ConstructionDirectoryTests(IntegrationTestFixture fixture)
{
    private sealed record Item(Guid Id, string Name);

    [Fact]
    public async Task Справочник_отдаёт_id_и_имя_по_названию_с_правом_чтения()
    {
        var editor = await GrantedSignIn.WithPermissionsAsync(fixture, CorePermissions.ConstructionsEdit);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var b = await CreateAsync(editor, $"Справочник Б {suffix}");
        var a = await CreateAsync(editor, $"Справочник А {suffix}");

        var reader = await GrantedSignIn.WithPermissionsAsync(fixture, CorePermissions.ConstructionsRead);
        var items = await reader.GetFromJsonAsync<List<Item>>("/api/constructions/directory");

        var mine = items!.Where(x => x.Name.EndsWith(suffix, StringComparison.Ordinal)).ToList();
        Assert.Equal([a, b], mine.Select(x => x.Id));
        Assert.Equal($"Справочник А {suffix}", mine[0].Name);
    }

    [Fact]
    public async Task Без_права_чтения_справочника_нет()
    {
        var stranger = await GrantedSignIn.WithPermissionsAsync(fixture, CorePermissions.FilesUse);
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync("/api/constructions/directory")).StatusCode);
    }

    private static async Task<Guid> CreateAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/constructions", new { name });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Item>())!.Id;
    }
}
