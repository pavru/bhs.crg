using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Objects;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Modules.Costs;
using BHS.CRG.Modules.Data;
using BHS.CRG.Modules.Ports;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using PortRecordsFor = BHS.CRG.Modules.Ports.RecordsFor;
using RecordsFor = BHS.CRG.Application.Documents.RecordsFor;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Архивная запись на КАЖДОМ пути чтения (issue #1185, ТЗ CORE-34.4): в списке «на выбор» её нет,
/// в показе она есть и названа архивной.
///
/// <para>Это второй слой сторожа. Перепись <c>ArchiveReadInventoryTests</c> доказывает, что о каждом
/// месте чтения решение ПРИНЯТО; что оно исполнено, доказывают только эти прогоны — и перепись
/// требует, чтобы у каждой строки «выбор» здесь стоял свой тест, называя его по имени.</para>
///
/// <para>Признак ставит <see cref="IRecordArchive" /> напрямую: действия «в архив» у человека ещё нет,
/// а отбор обязан работать до того, как оно появится.</para>
/// </summary>
[Collection("Integration")]
public class ArchiveReadPurposeTests(InvoiceLineHost host) : InvoiceLineTestBase(host)
{
    private readonly InvoiceLineHost host = host;

    [Fact]
    public async Task Список_уровня_скрывает_архив_в_выборе_и_показывает_в_показе()
    {
        var (type, live, archived) = await PairAsync();

        var choice = await SendAsync(new ListCommonDataEntriesQuery(RecordsFor.Choice, CompositeTypeId: type));
        var display = await SendAsync(new ListCommonDataEntriesQuery(RecordsFor.Display, CompositeTypeId: type));

        Assert.Equal([live], choice.Select(e => e.Id));
        Assert.Equal(new[] { live, archived }.Order(), display.Select(e => e.Id).Order());
        Assert.True(display.Single(e => e.Id == archived).IsArchived);
        Assert.False(display.Single(e => e.Id == live).IsArchived);
    }

    [Fact]
    public async Task Список_комплекта_скрывает_архив_в_выборе_и_показывает_в_показе()
    {
        var (type, live, archived) = await PairAsync();
        var set = await SetAsync();

        var choice = await SendAsync(new ResolveCommonDataForSetQuery(set, RecordsFor.Choice, type));
        var display = await SendAsync(new ResolveCommonDataForSetQuery(set, RecordsFor.Display, type));

        Assert.Equal([live], choice.Select(e => e.Id));
        Assert.True(display.Single(e => e.Id == archived).Archived);
        Assert.False(display.Single(e => e.Id == live).Archived);
    }

    [Fact]
    public async Task Список_цепочки_уровней_скрывает_архив_в_выборе_и_показывает_в_показе()
    {
        var (type, live, archived) = await PairAsync();

        var choice = await SendAsync(new ResolveCommonDataForScopeQuery(CatalogScope.System, null, RecordsFor.Choice, type));
        var display = await SendAsync(new ResolveCommonDataForScopeQuery(CatalogScope.System, null, RecordsFor.Display, type));

        Assert.Equal([live], choice.Select(e => e.Id));
        Assert.True(display.Single(e => e.Id == archived).Archived);
    }

    /// <summary>
    /// Число «в архиве» считается ТЕМ ЖЕ отбором, что и выбор: под другой запрос архивная запись
    /// не подходит — и числа нет. Иначе подсказка «есть в архиве» звала бы искать то, чего там нет.
    /// </summary>
    [Fact]
    public async Task Поиск_на_выбор_архив_не_отдаёт_но_называет_числом_а_по_идентификаторам_находит()
    {
        var (type, live, archived) = await PairAsync();

        var all = await SendAsync(new SearchCommonDataForChoiceQuery([type]));
        var byText = await SendAsync(new SearchCommonDataForChoiceQuery([type], "в архиве"));
        var other = await SendAsync(new SearchCommonDataForChoiceQuery([type], "живая"));
        var refs = await SendAsync(new CommonDataRefsByIdsQuery([type], [live, archived]));

        Assert.Equal([live], all.Items.Select(r => r.Id));
        Assert.Equal(1, all.InArchive);
        Assert.Empty(byText.Items);
        Assert.Equal(1, byText.InArchive);
        Assert.Equal([live], other.Items.Select(r => r.Id));
        Assert.Equal(0, other.InArchive);
        Assert.True(refs.Single(r => r.Id == archived).Archived);
        Assert.False(refs.Single(r => r.Id == live).Archived);
    }

    /// <summary>
    /// Адреса: назначение обязательно — без него отказ, а не «как раньше». Сервер, выбирающий за
    /// клиента, либо вернул бы архив в выбор, либо оставил бы сохранённую ссылку без названия.
    /// </summary>
    [Theory]
    [InlineData("/api/common-data?typeId={0}")]
    [InlineData("/api/common-data/for-scope?scope=System&typeId={0}")]
    [InlineData("/api/common-data/for-set/{1}?typeId={0}")]
    public async Task Адрес_списка_требует_назначение_и_отвечает_по_нему(string template)
    {
        var (client, _) = await SignInAsync("Admin");
        var (type, live, archived) = await PairAsync();
        var url = string.Format(template, type, await SetAsync());

        var refusal = await client.GetAsync(url);
        var unknown = await client.GetAsync(url + "&purpose=all");
        var choice = await client.GetFromJsonAsync<JsonElement>(url + "&purpose=choice");
        var display = await client.GetFromJsonAsync<JsonElement>(url + "&purpose=display");

        Assert.Equal(HttpStatusCode.BadRequest, refusal.StatusCode);
        Assert.Contains("purpose", (await refusal.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal([live], choice.EnumerateArray().Select(e => e.GetProperty("id").GetGuid()));
        var shown = display.EnumerateArray().ToDictionary(e => e.GetProperty("id").GetGuid(), e => e.GetProperty("archived").GetBoolean());
        Assert.True(shown[archived]);
        Assert.False(shown[live]);
    }

    [Fact]
    public async Task Порт_модулей_список_по_назначению_поиск_всегда_выбор_остальное_с_признаком()
    {
        var (type, live, archived) = await PairAsync();
        var code = await CodeAsync(type);
        using var scope = host.Services.CreateScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IModuleCatalog>();

        var choice = await catalog.ListAsync(code, PortRecordsFor.Choice);
        var display = await catalog.ListAsync(code, PortRecordsFor.Display);
        var found = await catalog.SearchAsync(code, null, 10);
        var refs = await catalog.RefsAsync(code, [live, archived]);

        Assert.Equal([live], choice!.Select(e => e.Id));
        Assert.True(display!.Single(e => e.Id == archived).Archived);
        Assert.False(display!.Single(e => e.Id == live).Archived);
        Assert.Equal([live], found!.Items.Select(r => r.Id));
        Assert.Equal(1, found.InArchive);
        Assert.True(refs!.Single(r => r.Id == archived).Archived);
        Assert.False(refs!.Single(r => r.Id == live).Archived);
        Assert.True((await catalog.GetAsync(archived))!.Archived);
        Assert.False((await catalog.GetAsync(live))!.Archived);
        // Пустой перечень — пустой ответ, даже когда вида нет: спрашивать нечего, и «вида нет» здесь
        // звавший принял бы за отказ.
        Assert.Empty((await catalog.RefsAsync("ТипаТакогоНет", []))!);
    }

    /// <summary>Архивная запись — не потеря: у ссылки на неё своё состояние, третье.</summary>
    [Fact]
    public async Task Состояние_ссылки_различает_на_месте_в_архиве_и_потеряна()
    {
        var (_, live, archived) = await PairAsync();
        var lost = Guid.NewGuid();
        using var scope = host.Services.CreateScope();
        var targets = scope.ServiceProvider.GetRequiredService<IModuleReferenceTargets>();

        var states = await targets.StatesAsync(ReferenceTarget.Record, [live, archived, lost]);

        Assert.Equal(ReferenceState.Present, states[live]);
        Assert.Equal(ReferenceState.Archived, states[archived]);
        Assert.Equal(ReferenceState.Lost, states[lost]);
    }

    [Fact]
    public async Task Выбор_позиции_номенклатуры_в_счёте_архивную_не_предлагает()
    {
        var (client, _) = await SignInAsync("Admin");
        Guid nomenclature;
        using (var scope = host.Services.CreateScope())
            nomenclature = (await scope.ServiceProvider.GetRequiredService<IRepository<DocumentType>>()
                .FindAsync(t => t.Code == CostsRecordTypes.NomenclatureCode)).Single().Id;
        var mark = Guid.NewGuid().ToString("N")[..8];
        var live = await EntryAsync(nomenclature, $"Автомат {mark} живой");
        var archived = await EntryAsync(nomenclature, $"Автомат {mark} в архиве");
        await ArchiveAsync(archived);

        var found = await client.GetFromJsonAsync<JsonElement>($"/api/costs/nomenclature?query=Автомат {mark}");

        Assert.Equal([live], found.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
        // Число доезжает до формы: без него ненайденная архивная позиция читается как отсутствующая.
        Assert.Equal(1, found.GetProperty("inArchive").GetInt32());
    }

    /// <summary>
    /// «Умолчания нет» держит не только сигнатура (ревью PR #1224): запрос, собранный без назначения,
    /// несёт нулевое значение — и оно не совпадает ни с выбором, ни с показом. Отказ, а не тихий
    /// ответ по одному из них.
    /// </summary>
    [Fact]
    public async Task Неназванное_назначение_отвергается_а_не_трактуется_как_одно_из_двух()
    {
        var (type, _, _) = await PairAsync();
        var set = await SetAsync();
        var code = await CodeAsync(type);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            SendAsync(new ListCommonDataEntriesQuery(default, CompositeTypeId: type)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            SendAsync(new ResolveCommonDataForSetQuery(set, default, type)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            SendAsync(new ResolveCommonDataForScopeQuery(CatalogScope.System, null, (RecordsFor)7, type)));

        using var scope = host.Services.CreateScope();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            scope.ServiceProvider.GetRequiredService<IModuleCatalog>().ListAsync(code, default));
    }

    // ── Подготовка ─────────────────────────────────────────────────────────────

    /// <summary>Свой тип и две записи уровня «Система»: живая и архивная.</summary>
    private async Task<(Guid Type, Guid Live, Guid Archived)> PairAsync()
    {
        var mark = Guid.NewGuid().ToString("N")[..8];
        var type = await TypeAsync($"Архив{mark}", $"Архив {mark}");
        var live = await EntryAsync(type, $"Запись {mark} живая");
        var archived = await EntryAsync(type, $"Запись {mark} в архиве");
        await ArchiveAsync(archived);
        return (type, live, archived);
    }

    private async Task ArchiveAsync(Guid id)
    {
        using var scope = host.Services.CreateScope();
        Assert.Equal(ArchiveOutcome.Changed,
            await scope.ServiceProvider.GetRequiredService<IRecordArchive>().SetAsync(id, archived: true));
    }

    private async Task<Guid> SetAsync()
    {
        var (_, sections) = await SiteAsync("Архив", "Раздел");
        return (await SendAsync(new CreateDocumentSetCommand(sections[0], "Комплект"))).Id;
    }

    private async Task<string> CodeAsync(Guid type)
    {
        using var scope = host.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IRepository<DocumentType>>().GetByIdAsync(type))!.Code;
    }

    private async Task<T> SendAsync<T>(IRequest<T> request)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(request);
    }
}
