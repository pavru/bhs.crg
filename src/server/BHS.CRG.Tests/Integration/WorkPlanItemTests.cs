using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Objects;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Перечень работ стройки: таблица, ключ и правила CORE-11 (ТЗ CORE-10, CORE-11, CORE-13, issue #964).
///
/// <para>Позиция — якорь всех модулей: план держит смета, факт — учёт работ, акт — исполнительная
/// документация, и каждый ссылается на позицию. Отсюда все три правила, которые здесь проверяются:
/// в позицию нельзя добавить чужое поле, ключ уникален целиком, удаляется позиция только когда на
/// неё никто не ссылается.</para>
///
/// <para>⚠️ Держателей ссылок сегодня НЕТ: модули, которые будут ссылаться, приходят в этапе 2.
/// Поэтому правило проверяется ПОДСТАВНЫМ держателем — иначе сторож был бы зелёным всегда, как это
/// уже случилось в #962, где «завести внешний ключ с каскадом» проверяло ключ, которого в этом
/// хранении не существует.</para>
/// </summary>
[Collection("Integration")]
public class WorkPlanItemTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Подставной держатель ссылок: столько ссылок, сколько велел тест.</summary>
    private sealed class FakeReferrer(string what, int count) : IWorkPlanItemReferrer
    {
        public string What => what;
        public IReadOnlyCollection<Guid>? Asked { get; private set; }

        public Task<int> CountAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken ct)
        {
            Asked = itemIds;
            return Task.FromResult(count);
        }
    }

    // ── Сторож задачи: в позицию не добавить чужого поля ───────────────────────

    /// <summary>
    /// СТОРОЖ ЗАДАЧИ. У позиции ровно ключ и ничего больше (ТЗ CORE-11): ни объёма, ни цены, ни
    /// сроков, ни факта, ни статуса.
    ///
    /// <para>Список белый, а не правило: новое поле в позиции — это решение, и оно обязано пройти
    /// через человека. Молчаливое «ещё одна колонка» — это и есть свалка, от которой CORE-11
    /// защищает: сегодня статус, завтра объём, и позиция перестаёт быть общей точкой, потому что у
    /// каждого модуля появляется свой повод в неё писать.</para>
    ///
    /// <para>Спрашиваем МОДЕЛЬ EF, а не класс: колонку можно завести и конфигурацией (теневым
    /// свойством), и такую проверка по свойствам класса не увидела бы вовсе.</para>
    /// </summary>
    [Fact]
    public void В_позиции_ничего_кроме_ключа()
    {
        _ = fixture.CreateClient();

        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Infrastructure.Persistence.AppDbContext>();
        var entity = db.Model.FindEntityType(typeof(WorkPlanItem))!;

        var columns = entity.GetProperties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal);

        Assert.Equal(
            ["ConstructionId", "CreatedAt", "Id", "SectionId", "UnitId", "UpdatedAt", "WorkTypeId"],
            columns);

        // Навигаций тоже нет: позиция — якорь, а не агрегат, и «позиция со списком чего-нибудь»
        // означала бы, что у неё появилось содержимое.
        Assert.Empty(entity.GetNavigations());
    }

    // ── Ключ ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Ключ уникален ЦЕЛИКОМ, и пустой раздел в нём — значение, а не «неизвестно».
    ///
    /// <para>Проверка не педантичная: по умолчанию PostgreSQL считает NULL в уникальном индексе
    /// разными значениями, и две позиции «стройка в целом» легли бы обе. А «стройка в целом» — это
    /// штатный случай (ТЗ CORE-Q2): смета у заказчика бывает на стройку без разделов. Дальше «найди
    /// или создай» находил бы то одну, то другую, и мнения модулей разошлись бы по двум позициям с
    /// одинаковым смыслом.</para>
    /// </summary>
    [Fact]
    public async Task Ключ_уникален_вместе_с_пустым_разделом()
    {
        var (construction, _, workType, unit) = await SeedAsync();

        await AddAsync(WorkPlanItem.Create(workType, construction, null, unit));

        var again = await Assert.ThrowsAsync<DbUpdateException>(
            () => AddAsync(WorkPlanItem.Create(workType, construction, null, unit)));
        Assert.Contains("IX_work_plan_items", again.InnerException?.Message ?? "");
    }

    /// <summary>
    /// А позиции, различающиеся хоть одной составляющей, — разные. Проверяется вместе с предыдущим:
    /// уникальность, запрещающая лишнее, так же плоха, как уникальность, не запрещающая нужного.
    /// </summary>
    [Fact]
    public async Task Различие_в_любой_составляющей_даёт_другую_позицию()
    {
        var (construction, section, workType, unit) = await SeedAsync();
        var otherUnit = await AddObjectAsync(await UnitTypeAsync(), "компл");

        await AddAsync(WorkPlanItem.Create(workType, construction, null, unit));
        await AddAsync(WorkPlanItem.Create(workType, construction, section, unit));       // раздел
        await AddAsync(WorkPlanItem.Create(workType, construction, null, otherUnit));     // единица

        using var scope = fixture.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRepository<WorkPlanItem>>();
        Assert.Equal(3, (await repo.GetAllAsync()).Count);
    }

    // ── Удаление ──────────────────────────────────────────────────────────────

    /// <summary>
    /// СТОРОЖ ЗАДАЧИ. Удаление позиции, на которую ссылаются, отказывает — И НАЗЫВАЕТ ЧИСЛО ссылок
    /// (ТЗ CORE-11). Числа мало для красоты: «на позицию ссылаются» не говорит человеку, сколько
    /// работы его ждёт, а «позиции смет: 3» — говорит.
    /// </summary>
    [Fact]
    public async Task Удаление_позиции_со_ссылками_отказывает_с_числом()
    {
        var (construction, _, workType, unit) = await SeedAsync();
        var item = await AddAsync(WorkPlanItem.Create(workType, construction, null, unit));

        var referrer = new FakeReferrer("позиции смет", 3);

        using var scope = fixture.Services.CreateScope();
        var refusal = await Assert.ThrowsAsync<ConflictException>(() => new WorkPlanItemHandlers(
                scope.ServiceProvider.GetRequiredService<IRepository<WorkPlanItem>>(), [referrer])
            .Handle(new DeleteWorkPlanItemCommand(item.Id), default));

        Assert.Contains("позиции смет: 3", refusal.Message);
        // Спросили ровно про эту позицию, а не «про всё вообще».
        Assert.Equal([item.Id], referrer.Asked!);

        // И позиция на месте: отказ не оставляет половины дела.
        Assert.NotNull(await scope.ServiceProvider
            .GetRequiredService<IRepository<WorkPlanItem>>().GetByIdAsync(item.Id));
    }

    /// <summary>Без ссылок позиция удаляется — иначе правило означало бы «не удаляется никогда».</summary>
    [Fact]
    public async Task Без_ссылок_позиция_удаляется()
    {
        var (construction, _, workType, unit) = await SeedAsync();
        var item = await AddAsync(WorkPlanItem.Create(workType, construction, null, unit));

        using var scope = fixture.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRepository<WorkPlanItem>>();
        await new WorkPlanItemHandlers(repo, [new FakeReferrer("позиции смет", 0)])
            .Handle(new DeleteWorkPlanItemCommand(item.Id), default);

        using var after = fixture.Services.CreateScope();
        Assert.Null(await after.ServiceProvider
            .GetRequiredService<IRepository<WorkPlanItem>>().GetByIdAsync(item.Id));
    }

    /// <summary>
    /// Каскад уровня обходил бы правило с фланга: удаление стройки уносит её перечень внешним
    /// ключом, и ссылки модулей повисли бы молча. Поэтому прикладной каскад спрашивает держателей —
    /// тем же приёмом, каким это уже сделано для объектов уровня (issue #739).
    /// </summary>
    [Fact]
    public async Task Удаление_стройки_с_занятым_перечнем_отказывает()
    {
        var (construction, section, workType, unit) = await SeedAsync();
        await AddAsync(WorkPlanItem.Create(workType, construction, section, unit));

        using var scope = fixture.Services.CreateScope();
        var cascade = CascadeWith(scope, new FakeReferrer("строки отчётов монтажников", 7));

        var plan = await cascade.PlanAsync(CatalogScope.Construction, construction);
        var refusal = Assert.Throws<ConflictException>(() => cascade.EnsureDeletable(plan, "стройку"));
        Assert.Contains("строки отчётов монтажников: 7", refusal.Message);

        // Раздел — тот же вопрос: позиция раздела уйдёт с ним, и держатель об этом не узнает.
        var forSection = await cascade.PlanAsync(CatalogScope.Section, section);
        Assert.Throws<ConflictException>(() => cascade.EnsureDeletable(forSection, "раздел"));
    }

    /// <summary>
    /// А без держателей ссылок уровень удаляется, и перечень уходит с ним каскадом базы: правило
    /// «только без ссылок» не должно превращаться в «стройку теперь не удалить».
    /// </summary>
    [Fact]
    public async Task Без_держателей_стройка_удаляется_вместе_с_перечнем()
    {
        var (construction, section, workType, unit) = await SeedAsync();
        await AddAsync(WorkPlanItem.Create(workType, construction, section, unit));

        using var scope = fixture.Services.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        await mediator.Send(new DeleteConstructionCommand(construction));

        using var after = fixture.Services.CreateScope();
        Assert.Empty(await after.ServiceProvider
            .GetRequiredService<IRepository<WorkPlanItem>>().GetAllAsync());
    }

    /// <summary>
    /// Запись классификатора, на которую ссылается перечень, не удаляется — и это ОТКАЗ, а не
    /// внутренняя ошибка сервера. Внешний ключ здесь последний рубеж, а человеку нужен текст:
    /// «поломка» отправила бы его писать в поддержку вместо того, чтобы убрать позиции.
    /// </summary>
    [Fact]
    public async Task Запись_из_перечня_не_удаляется_отказом_а_не_поломкой()
    {
        var (construction, _, workType, unit) = await SeedAsync();
        await AddAsync(WorkPlanItem.Create(workType, construction, null, unit));

        using var scope = fixture.Services.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var refusal = await Assert.ThrowsAsync<ConflictException>(
            () => mediator.Send(new DeleteCommonDataEntryCommand(workType)));
        Assert.Contains("перечень работ, позиций: 1", refusal.Message);

        // Единица измерения — тот же случай: без неё позиция означала бы «сколько-то чего-то».
        Assert.Contains("перечень работ",
            (await Assert.ThrowsAsync<ConflictException>(
                () => mediator.Send(new DeleteCommonDataEntryCommand(unit)))).Message);
    }

    // ── Помощники ─────────────────────────────────────────────────────────────

    /// <summary>Каскад уровня с подставным держателем ссылок на позиции.</summary>
    private static ScopeCascade CascadeWith(IServiceScope scope, IWorkPlanItemReferrer referrer) =>
        new(scope.ServiceProvider.GetRequiredService<IRepository<DomainObject>>(),
            scope.ServiceProvider.GetRequiredService<IRepository<QualityDocument>>(),
            scope.ServiceProvider.GetRequiredService<IRepository<MaterialQualityLink>>(),
            scope.ServiceProvider.GetRequiredService<IRepository<Section>>(),
            scope.ServiceProvider.GetRequiredService<IRepository<WorkPlanItem>>(),
            [referrer],
            scope.ServiceProvider.GetRequiredService<IReferenceIndex>(),
            scope.ServiceProvider.GetRequiredService<IScopeSubtree>());

    /// <summary>Стройка с разделом, запись классификатора и единица измерения — минимум для позиции.</summary>
    private async Task<(Guid Construction, Guid Section, Guid WorkType, Guid Unit)> SeedAsync()
    {
        _ = fixture.CreateClient();

        using var scope = fixture.Services.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var construction = await mediator.Send(new CreateConstructionCommand("Стройка", Guid.NewGuid()));
        var section = await mediator.Send(new CreateSectionCommand(construction.Id, "ЭОМ-1"));

        var workType = await AddObjectAsync(
            await TypeAsync(CoreRecordTypes.WorkTypeCode, "Вид работы"), "Прокладка кабеля");
        var unit = await AddObjectAsync(await UnitTypeAsync(), "м");

        return (construction.Id, section.Id, workType, unit);
    }

    private Task<Guid> UnitTypeAsync() => TypeAsync(CoreRecordTypes.UnitCode, "Единица измерения");

    private async Task<Guid> TypeAsync(string code, string name)
    {
        using var scope = fixture.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRepository<DocumentType>>();
        var found = await repo.FindAsync(t => t.Code == code);
        if (found.Count > 0) return found[0].Id;

        var type = DocumentType.Create(name, code, DocumentTypeKind.Composite, null,
            JsonDocument.Parse("""{"fields":[]}"""), TypeOwner.Core, TypeVisibility.Shared);
        await repo.AddAsync(type);
        await repo.SaveChangesAsync();
        return type.Id;
    }

    private async Task<Guid> AddObjectAsync(Guid typeId, string name)
    {
        using var scope = fixture.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRepository<DomainObject>>();
        var obj = DomainObject.Create(typeId, name, JsonDocument.Parse("{}"), CatalogScope.System, null);
        await repo.AddAsync(obj);
        await repo.SaveChangesAsync();
        return obj.Id;
    }

    private async Task<WorkPlanItem> AddAsync(WorkPlanItem item)
    {
        using var scope = fixture.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRepository<WorkPlanItem>>();
        await repo.AddAsync(item);
        await repo.SaveChangesAsync();
        return item;
    }
}
