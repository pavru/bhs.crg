using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Common;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Modules.Ports;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Заведение записи справочника по названию и одному значению (issue #1077): так модуль счетов
/// заводит организацию, прочитанную в скане.
///
/// <para>Сторожей два. Запись не заводится дважды — ни повторным нажатием, ни одновременным. И запись
/// не заводится такой, какой её не дала бы сохранить её же форма: обязательное поле, заполнить
/// которое нечем, — отказ с его названием, а не пустое место в справочнике.</para>
///
/// <para>На общем хосте, базу не чистим: вид у каждого теста свой (см. <c>ModuleCatalogFieldValuesTests</c>).</para>
/// </summary>
[Collection("Integration")]
public class CatalogIntakeTests(IntegrationTestFixture host)
{
    private const string Name = "Наименование";
    private const string TaxId = "ИНН";

    /// <summary>Схема как у заказчика: название — составное, «Полное» обязательно, «Сокращённое» нет.</summary>
    [Fact]
    public async Task Название_ложится_в_обязательные_строки_составного_поля_и_только_в_них()
    {
        var type = await CustomerLikeAsync();

        var outcome = await IntakeAsync(type, "ООО «Ромашка»", "7701234560");

        var created = Assert.IsType<DomainObject>(outcome.Created);
        Assert.Empty(outcome.Existing);
        Assert.Empty(outcome.Refusals);
        Assert.Equal("ООО «Ромашка»", created.DisplayName);
        Assert.Equal(CatalogScope.System, created.ScopeLevel);

        var data = (await StoredAsync(created.Id)).Data.RootElement;
        Assert.Equal("7701234560", data.GetProperty(TaxId).GetString());
        var name = data.GetProperty(Name);
        Assert.Equal("ООО «Ромашка»", name.GetProperty("Полное").GetString());
        // Сокращённого в скане не было — и в записи его нет: полное на его месте было бы выдумкой.
        Assert.False(name.TryGetProperty("Сокращённое", out _));
        // Ничего сверх названия и значения.
        Assert.Equal(new[] { Name, TaxId }.Order(), data.EnumerateObject().Select(p => p.Name).Order());
    }

    [Fact]
    public async Task Строковое_поле_названия_получает_название_как_есть()
    {
        var type = await TypeAsync($$"""
            {"fields":[{"key":"{{Name}}","type":"string","required":true},{"key":"{{TaxId}}","type":"string","required":true}]}
            """);

        var created = Assert.IsType<DomainObject>((await IntakeAsync(type, "ИП Иванов", "770123456703")).Created);

        Assert.Equal("ИП Иванов", (await StoredAsync(created.Id)).Data.RootElement.GetProperty(Name).GetString());
    }

    /// <summary>
    /// Схему ведёт человек: добавил обязательный «ОГРН» — и заводить из скана стало нечем. Ответ —
    /// название поля, а не запись с дырой и не молчаливый пропуск.
    /// </summary>
    [Fact]
    public async Task Обязательное_поле_которое_заполнить_нечем_отказ_с_его_названием()
    {
        var type = await TypeAsync($$"""
            {"fields":[{"key":"{{Name}}","type":"string","required":true},{"key":"{{TaxId}}","type":"string","required":true},
                       {"key":"ОГРН","type":"string","title":"ОГРН организации","required":true}]}
            """);

        var outcome = await IntakeAsync(type, "ООО «Ромашка»", "7701234560");

        Assert.Null(outcome.Created);
        Assert.Contains("«ОГРН организации»", Assert.Single(outcome.Refusals));
        Assert.Empty(await RecordsAsync(type));
    }

    [Theory]
    // Обязательная часть названия — не строка: что в неё класть, неизвестно.
    [InlineData("""[{"key":"Полное","type":"string","required":true},{"key":"Вид","type":"number","title":"Вид","required":true}]""", "«Вид»")]
    // Обязательной строки нет вовсе: в какую из частей класть название, по схеме не понять.
    [InlineData("""[{"key":"Полное","type":"string"},{"key":"Сокращённое","type":"string"}]""", "нет обязательной строки")]
    public async Task Составное_название_которое_не_разложить_отказ(string inner, string expected)
    {
        var nameType = await TypeAsync($$"""{"fields":{{inner}}}""");
        var type = await TypeAsync($$"""
            {"fields":[{"key":"{{Name}}","type":"complex","typeId":"{{nameType.Id}}","required":true},{"key":"{{TaxId}}","type":"string"}]}
            """);

        var outcome = await IntakeAsync(type, "ООО «Ромашка»", "7701234560");

        Assert.Null(outcome.Created);
        Assert.Contains(expected, Assert.Single(outcome.Refusals));
        Assert.Empty(await RecordsAsync(type));
    }

    /// <summary>Поле переименовали — отказ называет его, а запись без ИНН не заводится.</summary>
    [Fact]
    public async Task Нет_поля_единственности_или_названия_отказ()
    {
        var type = await TypeAsync("""{"fields":[{"key":"Название","type":"string"},{"key":"ИННОрганизации","type":"string"}]}""");

        var outcome = await IntakeAsync(type, "ООО «Ромашка»", "7701234560");

        Assert.Null(outcome.Created);
        Assert.Contains(outcome.Refusals, r => r.Contains($"«{TaxId}»"));
        Assert.Contains(outcome.Refusals, r => r.Contains($"«{Name}»"));
        Assert.Empty(await RecordsAsync(type));
    }

    /// <summary>
    /// Повторное нажатие получает запись первого. «Уже есть» — это и архивная запись, и роль,
    /// у которой значение своё не хранится, а наследуется от основы.
    /// </summary>
    [Fact]
    public async Task Запись_с_таким_значением_уже_есть_вторая_не_заводится()
    {
        var type = await CustomerLikeAsync();
        var first = Assert.IsType<DomainObject>((await IntakeAsync(type, "ООО «Ромашка»", "7701234560")).Created);
        var role = await EntryAsync(type, "Ромашка (подрядчик)", $$"""{"_baseRef":"{{first.Id}}"}""");

        var again = await IntakeAsync(type, "Ромашка, ООО", "7701234560");

        Assert.Null(again.Created);
        Assert.Equal(new[] { first.Id, role }.Order(), again.Existing.Select(r => r.Id).Order());
        Assert.Equal(2, (await RecordsAsync(type)).Count);

        // Архивная — тоже «есть»: вернуть её из архива или завести новую решает человек, в каталоге.
        await SendAsync(new SetRecordArchiveCommand(role, true));
        await SendAsync(new SetRecordArchiveCommand(first.Id, true));
        var archived = await IntakeAsync(type, "ООО «Ромашка»", "7701234560");
        Assert.Null(archived.Created);
        Assert.All(archived.Existing, r => Assert.True(r.Archived));
    }

    /// <summary>
    /// Два нажатия в одну секунду. Без замка оба прошли бы проверку «такой ещё нет» раньше, чем
    /// хоть одно записало, — и в справочнике стояли бы две организации с одним ИНН.
    /// </summary>
    [Fact]
    public async Task Одновременные_заведения_дают_одну_запись()
    {
        var type = await CustomerLikeAsync();

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => IntakeAsync(type, "ООО «Гонка»", "7802345676"))));

        var created = Assert.Single(outcomes, o => o.Created is not null).Created!;
        Assert.All(outcomes.Where(o => o.Created is null), o => Assert.Equal(created.Id, Assert.Single(o.Existing).Id));
        Assert.Equal(created.Id, Assert.Single(await RecordsAsync(type)).Id);
    }

    /// <summary>
    /// То же — без надежды на случай: параллельные заведения выше на быстрой машине проходят и без
    /// замка через раз (проверено поломкой). Здесь тест сам держит замок, пока в справочнике появляется запись с
    /// этим ИНН: заведение обязано ДОЖДАТЬСЯ замка и только потом смотреть, есть ли такая. Проверь оно
    /// раньше — записи ещё не было бы, и оно завело бы вторую.
    /// </summary>
    [Fact]
    public async Task Проверка_такой_ещё_нет_идёт_под_замком()
    {
        var type = await CustomerLikeAsync();

        using var holder = host.Services.CreateScope();
        var db = holder.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_lock({AdvisoryLockKeys.CatalogIntake})");
        Task<CatalogIntakeOutcome> waiting;
        Guid other;
        try
        {
            waiting = Task.Run(() => IntakeAsync(type, "ООО «Под замком»", "7701234560"));
            Assert.NotSame(waiting, await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromMilliseconds(700))));
            other = await EntryAsync(type, "Под замком, ООО", """{"ИНН":"7701234560"}""");
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_unlock({AdvisoryLockKeys.CatalogIntake})");
        }

        var outcome = await waiting;
        Assert.Null(outcome.Created);
        Assert.Equal(other, Assert.Single(outcome.Existing).Id);
    }

    /// <summary>Тип, которого модуль не объявил, порт не заводит — что бы ни стояло в базе.</summary>
    [Fact]
    public async Task Необъявленный_тип_порт_не_заводит()
    {
        var type = await CustomerLikeAsync();
        using var scope = host.Services.CreateScope();
        var port = scope.ServiceProvider.GetRequiredService<IModuleCatalogIntake>();

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => port.CreateAsync(type.Code, "ООО «Ромашка»", "7701234560"));

        Assert.Contains("IntakeTypes", refusal.Message);
        Assert.Empty(await RecordsAsync(type));
    }

    private async Task<DocumentType> CustomerLikeAsync()
    {
        var nameType = await TypeAsync(
            """{"fields":[{"key":"Полное","type":"string","required":true},{"key":"Сокращённое","type":"string","tags":["identity"]}]}""");
        return await TypeAsync($$"""
            {"fields":[{"key":"Логотип","type":"image"},
                       {"key":"{{Name}}","type":"complex","typeId":"{{nameType.Id}}","required":true},
                       {"key":"{{TaxId}}","type":"string","required":true},
                       {"key":"КПП","type":"string"}]}
            """);
    }

    private async Task<CatalogIntakeOutcome> IntakeAsync(DocumentType type, string name, string taxId)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ICatalogIntake>()
            .CreateAsync(new CatalogIntakeRequest(type.Id, Name, TaxId, name, taxId));
    }

    private async Task<IReadOnlyList<DomainObject>> RecordsAsync(DocumentType type)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IRepository<DomainObject>>()
            .FindAsync(o => o.CompositeTypeId == type.Id);
    }

    private async Task<DomainObject> StoredAsync(Guid id)
    {
        using var scope = host.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IRepository<DomainObject>>().GetByIdAsync(id))!;
    }

    private async Task<DocumentType> TypeAsync(string schema) =>
        await SendAsync(new CreateDocumentTypeCommand(
            $"Контрагент {Guid.NewGuid():N}", $"Контрагент{Guid.NewGuid():N}", DocumentTypeKind.Composite, null,
            JsonDocument.Parse(schema)));

    private async Task<Guid> EntryAsync(DocumentType type, string name, string data) =>
        (await SendAsync(new CreateCommonDataEntryCommand(name, type.Id, JsonDocument.Parse(data), CatalogScope.System, null))).Id;

    private async Task<T> SendAsync<T>(IRequest<T> request)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(request);
    }
}
