using System.Text.Json;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Modules.Ports;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using RecordsFor = BHS.CRG.Modules.Ports.RecordsFor;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Порт называет запись справочника для показа названием из поля с тэгом названия — организацию
/// сокращённым, если оно заполнено (ТЗ CORE-6, CORE-20.4, issue #1473), — во всех чтениях, а полное
/// кладёт рядом.
///
/// <para>Роль у каждого теста своя (<see cref="TestDirectory" />): её тэг названия устроен как
/// <c>org.shortName</c> — на части составного поля названия или на своём поле. Носитель
/// <c>role.organization</c> в общей тестовой базе один и чужой, на нём такие проверки шли бы вслепую.</para>
/// </summary>
[Collection("Integration")]
public class OrganizationShortNamePortTests(IntegrationTestFixture host)
{
    private const string Full = "Общество с ограниченной ответственностью «Ромашка»";
    private const string Short = "ООО «Ромашка»";

    /// <summary>Как у заказчика: «Наименование» — составное, сокращённое — его часть.</summary>
    private async Task<(TestDirectory Directory, DocumentType Type)> CustomerLikeAsync(bool tagged = true)
    {
        var directory = new TestDirectory();
        var name = await TypeAsync($$"""
            {"fields":[{"key":"Полное","type":"string","required":true},
                       {"key":"Сокращённое","type":"string"{{(tagged ? $$""","tags":["{{directory.ShortTag}}"]""" : "")}}}]}
            """);
        var type = await TypeAsync($$"""
            {"tags":["{{directory.Role}}"],"fields":[
              {"key":"Наименование","type":"complex","typeId":"{{name.Id}}","tags":["{{directory.NameTag}}"]},
              {"key":"ИНН","type":"string","tags":["{{directory.FieldTag}}"]}]}
            """);
        return (directory, type);
    }

    [Fact]
    public async Task Сокращённое_пустое_и_унаследованное_во_всех_чтениях_порта()
    {
        var (directory, type) = await CustomerLikeAsync();
        var named = await EntryAsync(type, Full, $$"""{"Наименование":{"Полное":"{{Full}}","Сокращённое":"{{Short}}" },"ИНН":"7701234560"}""");
        var plain = await EntryAsync(type, "Индивидуальный предприниматель Петров", """{"Наименование":{"Полное":"ИП Петров","Сокращённое":"  "}}""");
        // Хранит только ссылку на основу — как четыре записи стройки на копии заказчика.
        var role = await EntryAsync(type, "Ромашка на стройке", $$"""{"_baseRef":"{{named}}"}""");
        // Своё составное перекрывает основу целиком: сокращённого в нём нет — значит, нет.
        var own = await EntryAsync(type, "Своё название", $$"""{"_baseRef":"{{named}}","Наименование":{"Полное":"Своё название"} }""");

        using var scope = host.Services.CreateScope();
        var port = directory.Port(scope.ServiceProvider);

        var list = (await port.ListAsync(directory.Role, RecordsFor.Display))!.ToDictionary(e => e.Id);
        Assert.Equal((Short, Full), (list[named].DisplayName, list[named].FullName));
        Assert.Equal(("Индивидуальный предприниматель Петров", null), (list[plain].DisplayName, list[plain].FullName));
        Assert.Equal((Short, "Ромашка на стройке"), (list[role].DisplayName, list[role].FullName));
        Assert.Equal(("Своё название", null), (list[own].DisplayName, list[own].FullName));

        var refs = (await port.RefsAsync(directory.Role, [named, plain, role]))!.ToDictionary(r => r.Id);
        Assert.Equal((Short, Full), (refs[named].DisplayName, refs[named].FullName));
        Assert.Equal(Short, refs[role].DisplayName);
        Assert.Null(refs[plain].FullName);

        var values = (await port.FieldValuesAsync(directory.Role, directory.FieldTag, RecordsFor.Display))!;
        Assert.Equal(Short, Assert.Single(values.Records, v => v.Record.Id == named).Record.DisplayName);

        var found = await port.GetAsync(named);
        Assert.Equal((Short, Full), (found!.DisplayName, found.FullName));

        var choice = (await port.SearchAsync(directory.Role, "Ромашка", 10))!;
        Assert.Equal(Short, Assert.Single(choice.Items, r => r.Id == named).DisplayName);
    }

    /// <summary>
    /// Поиск — и по видимому сокращённому названию (ревью PR #1898): поиск ядра знает только название
    /// записи и альтернативные имена. Найденное по полному, а показанное сокращённым, называет полное в
    /// <see cref="ModuleCatalogRef.MatchedAlias" />; архивное, найденное по сокращённому, — числом.
    /// </summary>
    [Fact]
    public async Task Поиск_находит_по_сокращённому_и_называет_полное_найденным_по_нему()
    {
        var (directory, type) = await CustomerLikeAsync();
        var named = await EntryAsync(type, Full, $$"""{"Наименование":{"Полное":"{{Full}}","Сокращённое":"{{Short}}" } }""");
        var gone = await EntryAsync(type, "Закрытое акционерное общество «Лютик»",
            """{"Наименование":{"Полное":"Закрытое акционерное общество «Лютик»","Сокращённое":"ЗАО «Лютик»"} }""");
        await SendAsync(new SetRecordArchiveCommand(gone, true));

        using var scope = host.Services.CreateScope();
        var port = directory.Port(scope.ServiceProvider);

        var byShort = (await port.SearchAsync(directory.Role, "ООО «Ром", 10))!;
        var hit = Assert.Single(byShort.Items);
        Assert.Equal((named, Short, null), (hit.Id, hit.DisplayName, hit.MatchedAlias));

        var byFull = Assert.Single((await port.SearchAsync(directory.Role, "Общество с огр", 10))!.Items);
        Assert.Equal((named, Short, Full), (byFull.Id, byFull.DisplayName, byFull.MatchedAlias));

        var archived = (await port.SearchAsync(directory.Role, "ЗАО «Лют", 10))!;
        Assert.Empty(archived.Items);
        Assert.Equal(1, archived.InArchive);
    }

    /// <summary>Тэга нет — названия записей, ничего не угадывается по ключу «Сокращённое».</summary>
    [Fact]
    public async Task Без_тэга_организация_называется_названием_записи()
    {
        var (directory, type) = await CustomerLikeAsync(tagged: false);
        var named = await EntryAsync(type, Full, $$"""{"Наименование":{"Полное":"{{Full}}","Сокращённое":"{{Short}}"} }""");

        using var scope = host.Services.CreateScope();
        var entry = Assert.Single((await directory.Port(scope.ServiceProvider).ListAsync(directory.Role, RecordsFor.Display))!);

        Assert.Equal(named, entry.Id);
        Assert.Equal((Full, null), (entry.DisplayName, entry.FullName));
    }

    /// <summary>Сокращённое — своим строковым полем организации: тэг на нём, часть не нужна.</summary>
    [Fact]
    public async Task Сокращённое_своим_полем_читается_так_же()
    {
        var directory = new TestDirectory();
        var type = await TypeAsync($$"""
            {"tags":["{{directory.Role}}"],"fields":[
              {"key":"Название","type":"string","tags":["{{directory.NameTag}}"]},
              {"key":"КраткоеНазвание","type":"string","tags":["{{directory.ShortTag}}"]},
              {"key":"ИНН","type":"string","tags":["{{directory.FieldTag}}"]}]}
            """);
        var named = await EntryAsync(type, Full, $$"""{"Название":"{{Full}}","КраткоеНазвание":"{{Short}}"}""");

        using var scope = host.Services.CreateScope();
        var entry = Assert.Single((await directory.Port(scope.ServiceProvider).ListAsync(directory.Role, RecordsFor.Display))!);

        Assert.Equal((named, Short, Full), (entry.Id, entry.DisplayName, entry.FullName));
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
