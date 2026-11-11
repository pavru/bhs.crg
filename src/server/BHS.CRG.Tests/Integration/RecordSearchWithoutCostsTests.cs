using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Поиск записей при выключенном <c>costs</c> (shell-05, задача 1.5; ТЗ AUTH-16.1, AUTH-19). Общий хост прогона
/// поднят без него — то есть ровно в проверяемом состоянии: группы счетов нет, группы ядра отвечают, и ответ — 200,
/// а не отказ всего поиска из-за одного модуля.
/// </summary>
[Collection("Integration")]
public class RecordSearchWithoutCostsTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task Модуль_выключен_группы_ядра_отвечают_а_счетов_нет()
    {
        var mark = Guid.NewGuid().ToString("N")[..8];
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Constructions.Add(Domain.Documents.Construction.Create($"Стройка {mark}", Guid.NewGuid()));
            await db.SaveChangesAsync();
        }
        var client = await Support.GrantedSignIn.WithPermissionsAsync(fixture,
            Api.Auth.CorePermissions.ConstructionsRead, Api.Auth.CorePermissions.DataSetsRead);

        var response = await client.GetAsync($"/api/search?q={mark}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var groups = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("groups").EnumerateArray().ToList();
        Assert.Equal(["constructions", "datasets"], groups.Select(g => g.GetProperty("code").GetString()));
        Assert.Single(groups[0].GetProperty("items").EnumerateArray());
        Assert.Empty(groups[1].GetProperty("items").EnumerateArray());

        // Группу выключенного модуля не попросить и прямо: её код этому экземпляру неизвестен.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/search?q={mark}&group=invoices")).StatusCode);
    }
}
