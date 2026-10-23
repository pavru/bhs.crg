using System.Data.Common;
using System.Text.Json;
using BHS.CRG.Api.Modules.Ports;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Ports;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Порт «найди или создай» позиции перечня (ТЗ CORE-12, STG-6.1; change <c>plan-04-plan-item-port</c>).
///
/// <para>Сторож задачи — гонка: «прочитал — не нашёл — вставил» проходит каждый тест по одному вызову и
/// даёт дубль, как только два импорта одной стройки идут одновременно. Тогда план двух смет висит на
/// разных позициях с одним смыслом, и свод по стройке их не сложит.</para>
/// </summary>
[Collection("Integration")]
public class ModulePlanItemsPortTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    private readonly WorkTypeSeed seed = new(fixture);
    private readonly List<IServiceScope> scopes = [];

    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();

    public Task DisposeAsync()
    {
        foreach (var scope in scopes) scope.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// СТОРОЖ ЗАДАЧИ. Одновременные вызовы с одним ключом — одна позиция, и все получают её
    /// идентификатор. Вызовов много, а не два: с двумя гонка могла бы и не случиться.
    /// </summary>
    [Fact]
    public async Task Одновременные_вызовы_с_одним_ключом_дают_одну_позицию()
    {
        var s = await SeedAsync();
        var key = new PlanItemKey(s.WorkType, s.Construction, s.Section, s.Unit);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => Port().FindOrCreateAsync(key))));

        Assert.Single(outcomes.Select(o => o.Item!.Id).Distinct());
        Assert.Single(outcomes, o => o.Item!.Created);
        Assert.Equal(1, await CountAsync());
    }

    /// <summary>Повтор после сбоя модуля находит ту же позицию и не заводит новой.</summary>
    [Fact]
    public async Task Повтор_возвращает_ту_же_позицию_и_не_заводит_новой()
    {
        var s = await SeedAsync();
        var key = new PlanItemKey(s.WorkType, s.Construction, s.Section, s.Unit);

        var first = (await Port().FindOrCreateAsync(key)).Item!;
        var again = (await Port().FindOrCreateAsync(key)).Item!;

        Assert.True(first.Created);
        Assert.Equal(new PlanItemResult(first.Id, Created: false), again);
        Assert.Equal(1, await CountAsync());

        var read = Assert.Single(await Port().GetAsync([first.Id, Guid.NewGuid()]));
        Assert.Equal(key, read.Key);
    }

    /// <summary>
    /// Пустой раздел — значение «стройка в целом» (ТЗ CORE-Q2): два вызова без раздела — одна позиция, а
    /// позиция с разделом — другая.
    /// </summary>
    [Fact]
    public async Task Без_раздела_одна_позиция_на_стройку_в_целом()
    {
        var s = await SeedAsync();
        var whole = new PlanItemKey(s.WorkType, s.Construction, null, s.Unit);

        var first = (await Port().FindOrCreateAsync(whole)).Item!;
        var second = (await Port().FindOrCreateAsync(whole)).Item!;
        var bySection = (await Port().FindOrCreateAsync(whole with { SectionId = s.Section })).Item!;

        Assert.Equal(first.Id, second.Id);
        Assert.False(second.Created);
        Assert.NotEqual(first.Id, bySection.Id);
        Assert.Equal(2, await CountAsync());
    }

    /// <summary>Архивный вид работы — отказ с составляющей, позиции нет (ТЗ CORE-34.4).</summary>
    [Fact]
    public async Task Архивный_вид_работы_даёт_отказ_с_составляющей()
    {
        var s = await SeedAsync();
        await seed.ArchiveAsync(s.WorkType);

        var outcome = await Port().FindOrCreateAsync(new PlanItemKey(s.WorkType, s.Construction, null, s.Unit));

        Assert.Null(outcome.Item);
        Assert.Equal(PlanItemKeyPart.WorkType, outcome.Refusal!.Part);
        Assert.Contains("архив", outcome.Refusal.Reason);
        Assert.Equal(0, await CountAsync());
    }

    /// <summary>
    /// Раздел чужой стройки — отказ «раздел»: иначе позиция одной стройки легла бы в перечень другой.
    /// Как и запись чужого справочника на месте единицы, и стройка, которой нет.
    /// </summary>
    [Fact]
    public async Task Раздел_чужой_стройки_и_чужая_запись_дают_отказ()
    {
        var s = await SeedAsync();
        var other = await SendAsync(new CreateConstructionCommand("Другая стройка", Guid.NewGuid()));
        var foreign = await SendAsync(new CreateSectionCommand(other.Id, "ЭОМ-2"));

        var outcome = await Port().FindOrCreateAsync(new PlanItemKey(s.WorkType, s.Construction, foreign.Id, s.Unit));
        Assert.Equal(PlanItemKeyPart.Section, outcome.Refusal!.Part);

        // Вид работы на месте единицы: запись есть и не в архиве, но она не из справочника единиц.
        var wrong = await Port().FindOrCreateAsync(new PlanItemKey(s.WorkType, s.Construction, null, s.WorkType));
        Assert.Equal(PlanItemKeyPart.Unit, wrong.Refusal!.Part);

        var gone = await Port().FindOrCreateAsync(new PlanItemKey(s.WorkType, Guid.NewGuid(), null, s.Unit));
        Assert.Equal(PlanItemKeyPart.Construction, gone.Refusal!.Part);

        Assert.Equal(0, await CountAsync());
    }

    /// <summary>
    /// Пакет из 300 ключей с одной архивной единицей: 299 позиций, один отказ поимённо — пакет целиком
    /// не отвергнут.
    /// </summary>
    [Fact]
    public async Task Пакет_с_одной_плохой_строкой_не_отвергается_целиком()
    {
        var s = await SeedAsync();
        var archived = await seed.RecordAsync(s.UnitType, "м2");
        await seed.ArchiveAsync(archived.Id);
        var keys = await KeysAsync(s, 300);
        var bad = keys[137] with { UnitId = archived.Id };
        keys[137] = bad;

        var batch = await Port().FindOrCreateManyAsync(keys);

        Assert.Equal(299, batch.Items.Count);
        var refusal = Assert.Single(batch.Refusals);
        Assert.Equal((bad, PlanItemKeyPart.Unit), (refusal.Key, refusal.Part));
        Assert.Equal(299, await CountAsync());
    }

    /// <summary>
    /// Пакет больше части, с повторами ключа внутри: каждая часть заведена, повтор слит, а повторный
    /// пакет находит всё заведённое и не заводит ничего.
    /// </summary>
    [Fact]
    public async Task Пакет_больше_части_с_повторами_заводит_каждую_позицию_один_раз()
    {
        var s = await SeedAsync();
        var keys = await KeysAsync(s, ModulePlanItemsPort.ChunkSize + 5);

        var batch = await Port().FindOrCreateManyAsync([.. keys, .. keys.Take(10)]);
        var again = await Port().FindOrCreateManyAsync(keys);

        Assert.Equal(keys.Count, batch.Items.Count);
        Assert.All(batch.Items.Values, i => Assert.True(i.Created));
        Assert.Equal(batch.Items.ToDictionary(i => i.Key, i => i.Value.Id), again.Items.ToDictionary(i => i.Key, i => i.Value.Id));
        Assert.All(again.Items.Values, i => Assert.False(i.Created));
        Assert.Equal(keys.Count, await CountAsync());
    }

    /// <summary>
    /// Два импорта одной стройки с общими ключами в РАЗНОМ порядке не обрывают друг друга взаимной
    /// блокировкой (ревью PR #1596): вставка берёт блокировки ключей по порядку строк, и встречный порядок
    /// без сортировки — это A держит k1 и ждёт k2, B держит k2 и ждёт k1. Раундов несколько, на свежих
    /// ключах: в одном раунде встречные вставки могли бы и разминуться.
    /// </summary>
    [Fact]
    public async Task Встречные_пакеты_с_общими_ключами_не_обрывают_друг_друга()
    {
        var s = await SeedAsync();
        var works = (await KeysAsync(s, 300)).Select(k => k.WorkTypeId).Distinct().ToList();

        for (var round = 0; round < 5; round++)
        {
            var site = await SendAsync(new CreateConstructionCommand($"Стройка {round}", Guid.NewGuid()));
            var keys = works.Select(w => new PlanItemKey(w, site.Id, null, s.Unit)).ToList();
            var reversed = Enumerable.Reverse(keys).ToList();

            var batches = await Task.WhenAll(
                Task.Run(() => Port().FindOrCreateManyAsync(keys)),
                Task.Run(() => Port().FindOrCreateManyAsync(reversed)));

            Assert.All(batches, b => Assert.Equal(keys.Count, b.Items.Count));
            Assert.Equal(batches[0].Items[keys[0]].Id, batches[1].Items[keys[0]].Id);
        }
    }

    /// <summary>
    /// Раздел удалили между проверкой ключа и вставкой — отказ внешнего ключа не роняет пакет, а
    /// становится отказом «раздел» у своего ключа; остальные ключи части заведены (ревью PR #1596).
    /// Удаление ставится ровно перед вставкой — перехватчиком команд: иначе окно в миллисекунды не поймать.
    /// </summary>
    [Fact]
    public async Task Раздел_удалён_перед_вставкой_даёт_отказ_а_не_сбой_пакета()
    {
        var s = await SeedAsync();
        var doomed = await SendAsync(new CreateSectionCommand(s.Construction, "Снос"));
        PlanItemKey keep = new(s.WorkType, s.Construction, s.Section, s.Unit), lose = keep with { SectionId = doomed.Id };

        var batch = await PortDeletingSectionBefore("INSERT INTO work_plan_items", doomed.Id)
            .FindOrCreateManyAsync([keep, lose]);

        Assert.True(batch.Items[keep].Created);
        var refusal = Assert.Single(batch.Refusals);
        Assert.Equal((lose, PlanItemKeyPart.Section), (refusal.Key, refusal.Part));
        Assert.Equal(1, await CountAsync());
    }

    /// <summary>Раздел удалили между вставкой и чтением — каскад унёс позицию, и ключ становится отказом.</summary>
    [Fact]
    public async Task Позиция_унесённая_каскадом_до_чтения_даёт_отказ()
    {
        var s = await SeedAsync();
        var doomed = await SendAsync(new CreateSectionCommand(s.Construction, "Снос"));
        PlanItemKey keep = new(s.WorkType, s.Construction, s.Section, s.Unit), lose = keep with { SectionId = doomed.Id };

        var batch = await PortDeletingSectionBefore("SELECT p.* FROM work_plan_items", doomed.Id)
            .FindOrCreateManyAsync([keep, lose]);

        Assert.Equal([keep], batch.Items.Keys);
        Assert.Equal(PlanItemKeyPart.Section, Assert.Single(batch.Refusals).Part);
    }

    /// <summary>Порт на контексте, который удаляет раздел перед первой командой с этим текстом.</summary>
    private ModulePlanItemsPort PortDeletingSectionBefore(string command, Guid sectionId)
    {
        var scope = fixture.Services.CreateScope();
        scopes.Add(scope);
        var options = new DbContextOptionsBuilder<AppDbContext>(
                scope.ServiceProvider.GetRequiredService<DbContextOptions<AppDbContext>>())
            .AddInterceptors(new DeleteSectionBefore(fixture, command, sectionId)).Options;
        return new ModulePlanItemsPort(new AppDbContext(options), scope.ServiceProvider.GetRequiredService<IMediator>());
    }

    private sealed class DeleteSectionBefore(IntegrationTestFixture fixture, string command, Guid sectionId)
        : DbCommandInterceptor
    {
        private bool done;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand cmd,
            CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (!done && cmd.CommandText.Contains(command, StringComparison.Ordinal))
            {
                done = true;
                using var scope = fixture.Services.CreateScope();
                await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database
                    .ExecuteSqlInterpolatedAsync($"""DELETE FROM sections WHERE "Id" = {sectionId}""", ct);
            }
            return result;
        }
    }

    private sealed record Seeded(Guid Construction, Guid Section, Guid WorkType, Guid WorkTypeClass, Guid Unit, Guid UnitType);

    /// <summary>Стройка с разделом, классификатор с одним видом работы и справочник единиц с одной единицей.</summary>
    private async Task<Seeded> SeedAsync()
    {
        var construction = await SendAsync(new CreateConstructionCommand("Стройка", Guid.NewGuid()));
        var section = await SendAsync(new CreateSectionCommand(construction.Id, "ЭОМ-1"));
        var (unitType, workType) = await seed.ClassifierAsync();
        var unit = await seed.RecordAsync(unitType.Id, "м");
        var work = await seed.RecordAsync(workType.Id, "Прокладка кабеля", WorkTypeSeed.WorkTypeData("01.02", "Прокладка кабеля", unit));
        return new Seeded(construction.Id, section.Id, work.Id, workType.Id, unit.Id, unitType.Id);
    }

    /// <summary>
    /// Столько разных ключей одной стройки: виды работ по пять разделов на вид. Записи — хранилищем, а не
    /// командой по одной: сотни команд создания тест не проверяет, а время на них тратит.
    /// </summary>
    private async Task<List<PlanItemKey>> KeysAsync(Seeded s, int count)
    {
        var sections = new List<Guid> { s.Section };
        for (var i = 2; i <= 5; i++)
            sections.Add((await SendAsync(new CreateSectionCommand(s.Construction, $"ЭОМ-{i}"))).Id);

        using var scope = fixture.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRepository<DomainObject>>();
        var works = new List<Guid>();
        for (var i = 0; i < (count + sections.Count - 1) / sections.Count; i++)
        {
            var work = DomainObject.Create(s.WorkTypeClass, $"Вид {i}", JsonDocument.Parse("{}"), CatalogScope.System, null);
            await repo.AddAsync(work);
            works.Add(work.Id);
        }
        await repo.SaveChangesAsync();

        return [.. works.SelectMany(w => sections.Select(sec => new PlanItemKey(w, s.Construction, sec, s.Unit))).Take(count)];
    }

    private async Task<int> CountAsync()
    {
        using var scope = fixture.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IRepository<WorkPlanItem>>().GetAllAsync()).Count;
    }

    private async Task<T> SendAsync<T>(IRequest<T> request)
    {
        using var scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(request);
    }

    /// <summary>Порт из своей области — как его получает модуль на запросе.</summary>
    private IModulePlanItems Port()
    {
        var scope = fixture.Services.CreateScope();
        lock (scopes) scopes.Add(scope);
        return scope.ServiceProvider.GetRequiredService<IModulePlanItems>();
    }
}
