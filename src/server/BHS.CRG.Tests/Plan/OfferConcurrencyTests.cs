using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Modules.Plan;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Tests.Integration;
using BHS.CRG.Tests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BHS.CRG.Tests.Plan;

/// <summary>Гонки КП из ревью #1999 (issue #2004): снимок чтения и конфликт выдачи номера.</summary>
[Collection(PlanOnlyCollection.Name)]
public class OfferConcurrencyTests(PlanOnlyHost host)
{
    [Fact]
    public async Task Данные_и_версия_одного_снимка_чужая_правка_не_затирается()
    {
        var (site, revision, _) = await RevisionsAsync();
        var manager = await ManagerAsync(host);
        var created = await manager.PostAsJsonAsync("/api/plan/offers", new { revisionId = revision, date = "2026-11-18" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var race = new ChangeAfterRead(host.ConnectionString, id);
        await using var probed = WithInterceptor(race);
        var client = probed.CreateClient();
        client.DefaultRequestHeaders.Authorization = manager.DefaultRequestHeaders.Authorization;

        var snapshot = await client.GetFromJsonAsync<JsonElement>($"/api/plan/offers/{id}");

        Assert.True(race.Changed, "Конкурентная правка не попала между чтением снимка и сборкой ответа.");
        Assert.Equal("2026-11-18", snapshot.GetProperty("date").GetString());
        var write = new HttpRequestMessage(HttpMethod.Put, $"/api/plan/offers/{id}")
        {
            Content = JsonContent.Create(new { date = snapshot.GetProperty("date").GetString(), vatMode = "none" }),
        };
        write.Headers.TryAddWithoutValidation("If-Match", snapshot.GetProperty("version").GetString());
        var refused = await manager.SendAsync(write);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var current = await manager.GetFromJsonAsync<JsonElement>($"/api/plan/offers/{id}");
        Assert.Equal("2026-11-19", current.GetProperty("date").GetString());
        Assert.NotEqual(snapshot.GetProperty("version").GetString(), current.GetProperty("version").GetString());
        Assert.Equal(site, current.GetProperty("constructionId").GetGuid());
    }

    [Fact]
    public async Task Два_КП_получили_один_номер_второе_409_а_не_500()
    {
        var (site, first, second) = await RevisionsAsync();
        var race = new SameNumber();
        await using var probed = WithInterceptor(race);
        var manager = await ManagerAsync(probed);
        race.Armed = true;

        var responses = await Task.WhenAll(
            manager.PostAsJsonAsync("/api/plan/offers", new { revisionId = first }),
            manager.PostAsJsonAsync("/api/plan/offers", new { revisionId = second }));

        Assert.Equal(2, race.Arrived);
        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], responses.Select(r => r.StatusCode).Order());
        var refused = await responses.Single(r => r.StatusCode == HttpStatusCode.Conflict).Content.ReadAsStringAsync();
        Assert.Contains("номер", refused);
        Assert.Contains("Повторите", refused);
        using var scope = host.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<PlanDbContext>().Offers
            .CountAsync(o => o.ConstructionId == site));
    }

    private WebApplicationFactory<Program> WithInterceptor(DbCommandInterceptor interceptor) =>
        host.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddDbContext<PlanDbContext>(options => options.AddInterceptors(interceptor))));

    private static Task<HttpClient> ManagerAsync(WebApplicationFactory<Program> factory) =>
        GrantedSignIn.WithPermissionsAsync(factory, PlanModule.OfferRead, PlanModule.OfferEdit, PlanModule.EstimateRead);

    private async Task<(Guid Site, Guid First, Guid Second)> RevisionsAsync()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        var row = new EstimateRowDraft("Работа", SourceVolume: 1m, SourceUnit: "шт", Total: 100m);
        var first = await EstimateSeed.RevisionAsync(host, estimate, row);
        var second = await EstimateSeed.RevisionAsync(host, estimate, row);
        return (site.Construction, first.Id, second.Id);
    }

    /// <summary>SQL уже прочёл снимок: другое соединение меняет шапку до сборки ответа HTTP.</summary>
    private sealed class ChangeAfterRead(string connection, Guid id) : DbCommandInterceptor
    {
        public bool Changed { get; private set; }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken ct = default)
        {
            if (!Changed && command.CommandText.Contains("FROM plan.offers", StringComparison.Ordinal)
                && command.Parameters.Cast<DbParameter>().Any(p => p.Value is Guid value && value == id))
            {
                Changed = true;
                await using var other = new NpgsqlConnection(connection);
                await other.OpenAsync(ct);
                await using var update = new NpgsqlCommand("UPDATE plan.offers SET date = @date WHERE id = @id", other);
                update.Parameters.AddWithValue("date", new DateOnly(2026, 11, 19));
                update.Parameters.AddWithValue("id", id);
                Assert.Equal(1, await update.ExecuteNonQueryAsync(ct));
            }
            return result;
        }
    }

    /// <summary>Оба SELECT max(sequence) закончены до первой вставки: номер совпадёт без случайных задержек.</summary>
    private sealed class SameNumber : DbCommandInterceptor
    {
        private readonly TaskCompletionSource both = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int arrived;
        public bool Armed { get; set; }
        public int Arrived => Volatile.Read(ref arrived);

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken ct = default)
        {
            if (Armed && command.CommandText.Contains("max(o.sequence)", StringComparison.OrdinalIgnoreCase))
            {
                if (Interlocked.Increment(ref arrived) == 2) both.TrySetResult();
                await both.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            }
            return result;
        }
    }
}
