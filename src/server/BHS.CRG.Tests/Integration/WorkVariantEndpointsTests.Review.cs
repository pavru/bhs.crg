using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Api.Endpoints.Core;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Objects;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Правила двери вариантов, найденные ревью PR #1966: возврат к архивному виду, гонка двух заведений,
/// порядок «охрана записи — есть в архиве» и вариант без ссылки на вид.
/// </summary>
public partial class WorkVariantEndpointsTests
{
    /// <summary>
    /// К архивному виду вариант не заводится — и не возвращается: иначе архив вида обходился бы
    /// возвратом его варианта. Отказ называет поле вида, вариант остаётся в архиве.
    /// </summary>
    [Fact]
    public async Task Возврат_варианта_к_архивному_виду_отказ()
    {
        var (variantType, cable, _) = await seed.SetUpAsync();
        var variant = await seed.VariantAsync(variantType, cable, "в лотке");
        await seed.Types.ArchiveAsync(variant.Id);
        await seed.Types.ArchiveAsync(cable.Id);
        var version = (await seed.Types.SendAsync(new GetCommonDataEntryQuery(variant.Id)))!.Version;
        var estimator = await AccessProbe.SignInAsync(fixture, "Estimator", "wv");

        var refused = await SendWithVersionAsync(estimator, HttpMethod.Post, $"{WorkVariantEndpoints.Door}/{variant.Id}/unarchive", version);

        await AssertRefusedAsync(refused, HttpStatusCode.Conflict, CoreRecordTypes.WorkVariantWorkTypeKey, "в архиве");
        Assert.True((await seed.Types.SendAsync(new GetCommonDataEntryQuery(variant.Id)))!.IsArchived);
    }

    /// <summary>
    /// Одновременные «Создать» с одним наименованием у одного вида дают ОДИН вариант: проверка тёзки и
    /// запись идут под замком, остальные получают «уже есть».
    /// </summary>
    [Fact]
    public async Task Одновременные_заведения_одного_наименования_дают_один_вариант()
    {
        var (variantType, cable, _) = await seed.SetUpAsync();
        var estimator = await AccessProbe.SignInAsync(fixture, "Estimator", "wv");

        var answers = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => PostAsync(estimator, WorkVariantSeed.Data(cable, "в лотке"))));

        Assert.Single(answers, a => a.StatusCode == HttpStatusCode.OK);
        Assert.All(answers.Where(a => a.StatusCode != HttpStatusCode.OK), a => Assert.Equal(HttpStatusCode.Conflict, a.StatusCode));
        Assert.Single(await seed.Types.SendAsync(new ListCommonDataEntriesQuery(RecordsFor.Display, CompositeTypeId: variantType.Id)));
    }

    /// <summary>
    /// «Есть в архиве» — после охраны записи, как у общего адреса (ревью PR #1229): запись, которую охрана
    /// не пропустит, отказом охраны и отвечает, а не предложением «вернуть или завести всё равно».
    /// </summary>
    [Fact]
    public async Task Отказ_охраны_записи_раньше_предложения_вернуть_из_архива()
    {
        var (variantType, cable, _) = await seed.SetUpAsync();
        var lengthy = await seed.Types.TypeAsync($"ВариантСДлиной{Guid.NewGuid():N}", "Вариант с длиной",
            """{"fields":[{"key":"Длина","type":"number","title":"Длина"}]}""", variantType.Id);
        var gone = await seed.VariantAsync(variantType, cable, "в лотке");
        await seed.Types.ArchiveAsync(gone.Id);
        var estimator = await AccessProbe.SignInAsync(fixture, "Estimator", "wv");
        var data = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [CoreRecordTypes.WorkVariantWorkTypeKey] = WorkTypeSeed.Ref(cable),
            [CoreRecordTypes.WorkVariantNameKey] = "в лотке",
            ["Длина"] = "12,5",
        });

        var refused = await estimator.PostAsJsonAsync(WorkVariantEndpoints.Door,
            new { displayName = "в лотке", data, compositeTypeId = lengthy.Id });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.DoesNotContain("archived-twin", await refused.Content.ReadAsStringAsync());
        Assert.Empty(await seed.Types.SendAsync(new ListCommonDataEntriesQuery(RecordsFor.Display, CompositeTypeId: lengthy.Id)));
    }

    /// <summary>
    /// Ответ «есть в архиве» ничего не оставляет: запись, сделанная ради охраны, откатывается.
    /// </summary>
    [Fact]
    public async Task Предложение_вернуть_из_архива_не_оставляет_записи()
    {
        var (variantType, cable, _) = await seed.SetUpAsync();
        var gone = await seed.VariantAsync(variantType, cable, "в лотке");
        await seed.Types.ArchiveAsync(gone.Id);
        var estimator = await AccessProbe.SignInAsync(fixture, "Estimator", "wv");

        var twin = await PostAsync(estimator, WorkVariantSeed.Data(cable, "в лотке"));

        Assert.Equal(HttpStatusCode.Conflict, twin.StatusCode);
        Assert.Equal(gone.Id, Assert.Single(await seed.Types.SendAsync(
            new ListCommonDataEntriesQuery(RecordsFor.Display, CompositeTypeId: variantType.Id))).Id);
    }

    /// <summary>
    /// Общий адрес возврата (им экран возвращает запись из «есть в архиве») идёт по правилам двери:
    /// рядом с живым тёзкой вариант не встаёт, к архивному виду не возвращается, — иначе дверь
    /// обходилась бы соседним адресом и у вида оказывались бы два живых «в лотке».
    /// </summary>
    [Fact]
    public async Task Общий_возврат_при_живом_тёзке_отказ()
    {
        var (variantType, cable, _) = await seed.SetUpAsync();
        var gone = await seed.VariantAsync(variantType, cable, "в лотке");
        await seed.Types.ArchiveAsync(gone.Id);
        var live = await seed.VariantAsync(variantType, cable, "в лотке", createAnyway: true);
        var engineer = await AccessProbe.SignInAsync(fixture, SystemRoles.IdEngineer, "wv");

        var refused = await engineer.PostAsync($"/api/common-data/{gone.Id}/unarchive", null);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(live.Id, (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("existingId").GetGuid());
        Assert.True((await seed.Types.SendAsync(new GetCommonDataEntryQuery(gone.Id)))!.IsArchived);
    }

    [Fact]
    public async Task Общий_возврат_к_архивному_виду_отказ()
    {
        var (variantType, cable, _) = await seed.SetUpAsync();
        var variant = await seed.VariantAsync(variantType, cable, "в лотке");
        await seed.Types.ArchiveAsync(variant.Id);
        await seed.Types.ArchiveAsync(cable.Id);
        var engineer = await AccessProbe.SignInAsync(fixture, SystemRoles.IdEngineer, "wv");

        var refused = await engineer.PostAsync($"/api/common-data/{variant.Id}/unarchive", null);

        await AssertRefusedAsync(refused, HttpStatusCode.Conflict, CoreRecordTypes.WorkVariantWorkTypeKey, "в архиве");
        Assert.True((await seed.Types.SendAsync(new GetCommonDataEntryQuery(variant.Id)))!.IsArchived);
    }

    /// <summary>Без тёзки и с живым видом общий возврат проходит — как у любой записи, без версии.</summary>
    [Fact]
    public async Task Общий_возврат_без_помех_проходит()
    {
        var (variantType, cable, _) = await seed.SetUpAsync();
        var variant = await seed.VariantAsync(variantType, cable, "в лотке");
        await seed.Types.ArchiveAsync(variant.Id);
        var engineer = await AccessProbe.SignInAsync(fixture, SystemRoles.IdEngineer, "wv");

        var returned = await engineer.PostAsync($"/api/common-data/{variant.Id}/unarchive", null);

        Assert.Equal(HttpStatusCode.OK, returned.StatusCode);
        Assert.False((await seed.Types.SendAsync(new GetCommonDataEntryQuery(variant.Id)))!.IsArchived);
    }

    /// <summary>
    /// Вариант без ссылки на вид (положенный мимо двери) получает вид правкой — по правилам заведения;
    /// иначе любая ссылка считалась бы переносом, и починить запись было бы нечем, кроме удаления.
    /// </summary>
    [Fact]
    public async Task Вариант_без_вида_получает_вид_правкой()
    {
        var (variantType, cable, _) = await seed.SetUpAsync();
        Guid orphanId;
        using (var scope = fixture.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IRepository<DomainObject>>();
            var orphan = DomainObject.Create(variantType.Id, "в лотке",
                JsonDocument.Parse($$"""{"{{CoreRecordTypes.WorkVariantNameKey}}":"в лотке"}"""), CatalogScope.System, null);
            await repo.AddAsync(orphan);
            await repo.SaveChangesAsync();
            orphanId = orphan.Id;
        }
        var version = (await seed.Types.SendAsync(new GetCommonDataEntryQuery(orphanId)))!.Version;
        var estimator = await AccessProbe.SignInAsync(fixture, "Estimator", "wv");
        var url = $"{WorkVariantEndpoints.Door}/{orphanId}";

        var request = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = JsonContent.Create(new { displayName = "в лотке", data = WorkVariantSeed.Data(cable, "в лотке") }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", version);
        var fixedUp = await estimator.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, fixedUp.StatusCode);
        Assert.Equal(cable.Id, (await fixedUp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("workTypeId").GetGuid());
    }
}
