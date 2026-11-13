using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Api.Endpoints.Core;
using BHS.CRG.Application.Documents;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Удаление, архив и общий адрес у вариантов ([CORE-34.1] [CORE-34.4] [WORK-49]): занятый вариант не
/// удаляется, а уходит в архив; общий адрес справочников варианты не пишет.
/// </summary>
public partial class WorkVariantEndpointsTests
{
    /// <summary>
    /// Занятый вариант не удаляется: отказ называет число держателей и предлагает архив. Держатель —
    /// подставная схема с колонкой, где стоит идентификатор варианта: держателей находит скан базы, и о
    /// модуле учёта работ по имени ядру знать не нужно (строку отчёта с вариантом заведёт work-13).
    /// </summary>
    [Fact]
    public async Task Занятый_вариант_не_удаляется_отказ_с_числом_держателей()
    {
        var (variantType, cable, _) = await seed.SetUpAsync();
        var variant = await seed.VariantAsync(variantType, cable, "в лотке");
        var schema = $"probe_{Guid.NewGuid():N}";
        await SqlAsync($"CREATE SCHEMA {schema}");
        try
        {
            await SqlAsync($"CREATE TABLE {schema}.report_lines (id uuid PRIMARY KEY, variant uuid)");
            await SqlAsync($"INSERT INTO {schema}.report_lines VALUES (gen_random_uuid(), @p0)", variant.Id);
            var estimator = await AccessProbe.SignInAsync(fixture, "Estimator", "wv");

            var refused = await SendWithVersionAsync(estimator, HttpMethod.Delete, $"{WorkVariantEndpoints.Door}/{variant.Id}", variant.Version);

            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
            // Схему не называет ни один модуль — держатель назван владельцем и числом строк.
            Assert.Contains(": 1", body.GetProperty("error").GetString());
            Assert.True(body.GetProperty("canArchive").GetBoolean());
            Assert.NotNull(await seed.Types.SendAsync(new GetCommonDataEntryQuery(variant.Id)));
        }
        finally { await SqlAsync($"DROP SCHEMA {schema} CASCADE"); }
    }

    /// <summary>Свободный вариант удаляется с действующей версией — 204, повтор — 404.</summary>
    [Fact]
    public async Task Свободный_вариант_удаляется()
    {
        var (variantType, cable, _) = await seed.SetUpAsync();
        var variant = await seed.VariantAsync(variantType, cable, "в лотке");
        var estimator = await AccessProbe.SignInAsync(fixture, "Estimator", "wv");
        var url = $"{WorkVariantEndpoints.Door}/{variant.Id}";

        Assert.Equal(HttpStatusCode.NoContent, (await SendWithVersionAsync(estimator, HttpMethod.Delete, url, variant.Version)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendWithVersionAsync(estimator, HttpMethod.Delete, url, variant.Version)).StatusCode);
    }

    /// <summary>
    /// Архив — с версией, ответ несёт новую: в выборе варианта нет, в показе он с признаком, и «Вернуть»
    /// сразу следом не упирается в отказ.
    /// </summary>
    [Fact]
    public async Task Архив_убирает_из_выбора_и_оставляет_в_показе()
    {
        var (variantType, cable, _) = await seed.SetUpAsync();
        var variant = await seed.VariantAsync(variantType, cable, "в лотке");
        var estimator = await AccessProbe.SignInAsync(fixture, "Estimator", "wv");
        var url = $"{WorkVariantEndpoints.Door}/{variant.Id}";

        var archived = await SendWithVersionAsync(estimator, HttpMethod.Post, $"{url}/archive", variant.Version);
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        var version = (await archived.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetString()!;
        Assert.NotEqual(variant.Version, version);

        Assert.Empty(await IdsAsync(estimator, $"{WorkVariantEndpoints.Door}?purpose=choice&workTypeId={cable.Id}"));
        var shown = await estimator.GetFromJsonAsync<JsonElement>($"{WorkVariantEndpoints.Door}?purpose=display&workTypeId={cable.Id}");
        Assert.True(Assert.Single(shown.EnumerateArray()).GetProperty("archived").GetBoolean());

        Assert.Equal(HttpStatusCode.OK, (await SendWithVersionAsync(estimator, HttpMethod.Post, $"{url}/unarchive", version)).StatusCode);
        Assert.False((await seed.Types.SendAsync(new GetCommonDataEntryQuery(variant.Id)))!.IsArchived);
    }

    /// <summary>
    /// Вернуть из архива «в лотке», когда у вида уже живой «в лотке», — дубль, которого заведение не
    /// допускает: отказ с названием живого, вариант остаётся в архиве.
    /// </summary>
    [Fact]
    public async Task Возврат_при_живом_тёзке_отказ()
    {
        var (variantType, cable, _) = await seed.SetUpAsync();
        var gone = await seed.VariantAsync(variantType, cable, "в лотке");
        await seed.Types.ArchiveAsync(gone.Id);
        var live = await seed.VariantAsync(variantType, cable, "в лотке", createAnyway: true);
        var archivedVersion = (await seed.Types.SendAsync(new GetCommonDataEntryQuery(gone.Id)))!.Version;
        var estimator = await AccessProbe.SignInAsync(fixture, "Estimator", "wv");

        var refused = await SendWithVersionAsync(estimator, HttpMethod.Post, $"{WorkVariantEndpoints.Door}/{gone.Id}/unarchive", archivedVersion);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(live.Id, (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("existingId").GetGuid());
        Assert.True((await seed.Types.SendAsync(new GetCommonDataEntryQuery(gone.Id)))!.IsArchived);
    }

    /// <summary>
    /// Общий адрес вариант не заводит, не правит, не удаляет и не архивирует — 409 с адресом двери, запись
    /// не меняется: иначе правило «вид не меняется» обходилось бы общей правкой.
    /// </summary>
    [Fact]
    public async Task Общий_адрес_вариант_не_пишет_и_называет_дверь()
    {
        var (variantType, cable, wire) = await seed.SetUpAsync();
        var variant = await seed.VariantAsync(variantType, cable, "в лотке");
        var engineer = await AccessProbe.SignInAsync(fixture, SystemRoles.IdEngineer, "wv");
        Assert.Contains(CorePermissions.CatalogEdit, (await AccessProbe.AccessAsync(engineer)).Permissions);
        engineer.DefaultRequestHeaders.TryAddWithoutValidation("If-Match", variant.Version);

        foreach (var refused in new[]
        {
            await engineer.PostAsJsonAsync("/api/common-data", new
            {
                displayName = "в трубе", compositeTypeId = variantType.Id, data = WorkVariantSeed.Data(cable, "в трубе"), scope = "System",
            }),
            await engineer.PutAsJsonAsync($"/api/common-data/{variant.Id}", new
            {
                displayName = "в лотке", data = WorkVariantSeed.Data(wire, "в лотке"),
            }),
            await engineer.DeleteAsync($"/api/common-data/{variant.Id}"),
            await engineer.PostAsync($"/api/common-data/{variant.Id}/archive", null),
        })
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal(WorkVariantEndpoints.Door,
                (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("door").GetString());
        }

        var stored = await seed.Types.SendAsync(new GetCommonDataEntryQuery(variant.Id));
        Assert.Equal(variant.Version, stored!.Version);
        Assert.Single(await seed.Types.SendAsync(new ListCommonDataEntriesQuery(RecordsFor.Display, CompositeTypeId: variantType.Id)));
    }

    private async Task SqlAsync(string sql, params object[] parameters)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
#pragma warning disable EF1002 // Имя схемы — из теста, значения — параметрами.
        await db.Database.ExecuteSqlRawAsync(sql, parameters);
#pragma warning restore EF1002
    }
}
