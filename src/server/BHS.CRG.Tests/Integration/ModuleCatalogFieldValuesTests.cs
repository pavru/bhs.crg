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
/// Порт справочника отдаёт значение поля с РАЗРЕШЁННЫМ наследованием (issue #1077): по нему модуль
/// счетов ищет поставщика распознанного счёта по ИНН.
///
/// <para>Сторож здесь один на все проверки: запись, про которую ИНН узнать не удалось, не имеет права
/// выглядеть записью без ИНН. Иначе «такой организации нет» говорилось бы про организацию, которая в
/// справочнике стоит, — и человек завёл бы вторую.</para>
///
/// <para>На общем хосте, а не на хосте портов: тот чистят перед каждым тестом сразу несколько
/// классов, и параллельная очистка стирала бы записи из-под этих проверок. Базу здесь не чистим —
/// вид у каждого теста свой.</para>
/// </summary>
[Collection("Integration")]
public class ModuleCatalogFieldValuesTests(IntegrationTestFixture host)
{
    private const string Field = "ИНН";

    [Fact]
    public async Task Своё_значение_читается_строкой_и_числом_а_записи_без_поля_оно_пусто()
    {
        var type = await TypeAsync();
        var text = await EntryAsync(type, "Строкой", """{"ИНН":"7701234567"}""");
        // Число в JSON — тоже ИНН: так его кладёт импорт из таблицы.
        var number = await EntryAsync(type, "Числом", """{"ИНН":7701234567}""");
        var none = await EntryAsync(type, "Без поля", "{}");
        var blank = await EntryAsync(type, "Пустое", """{"ИНН":"  "}""");

        var values = await ValuesAsync(type);

        Assert.Equal(("7701234567", null, false), Of(values, text));
        Assert.Equal(("7701234567", null, false), Of(values, number));
        Assert.Equal((null, null, false), Of(values, none));
        Assert.Equal((null, null, false), Of(values, blank));
    }

    /// <summary>
    /// Главный случай: роль своих реквизитов не хранит. Ссылка на основу бывает строкой и объектом
    /// <c>{kind,id}</c>, цепочка — длиннее одного звена.
    /// </summary>
    [Fact]
    public async Task Наследник_получает_значение_основы_и_называет_её()
    {
        var type = await TypeAsync();
        var org = await EntryAsync(type, "Основа", """{"ИНН":"7701234567"}""");
        var role = await EntryAsync(type, "Роль строкой", $$"""{"_baseRef":"{{org}}"}""");
        var typed = await EntryAsync(type, "Роль объектом", $$"""{"_baseRef":{"kind":"catalog","id":"{{org}}"} }""");
        var second = await EntryAsync(type, "Роль роли", $$"""{"_baseRef":"{{role}}"}""");

        var values = await ValuesAsync(type);

        Assert.Equal(("7701234567", org, false), Of(values, role));
        Assert.Equal(("7701234567", org, false), Of(values, typed));
        Assert.Equal(("7701234567", org, false), Of(values, second));
    }

    /// <summary>Своё поле перекрывает основу — и пустое тоже: так же считает печать документа.</summary>
    [Fact]
    public async Task Своё_значение_перекрывает_унаследованное()
    {
        var type = await TypeAsync();
        var org = await EntryAsync(type, "Основа", """{"ИНН":"7701234567"}""");
        var own = await EntryAsync(type, "Свой ИНН", $$"""{"_baseRef":"{{org}}","ИНН":"7802345678"}""");
        var erased = await EntryAsync(type, "Стёрт", $$"""{"_baseRef":"{{org}}","ИНН":null}""");

        var values = await ValuesAsync(type);

        Assert.Equal(("7802345678", null, false), Of(values, own));
        Assert.Equal((null, null, false), Of(values, erased));
    }

    /// <summary>
    /// Сторож: основа названа, а дойти до неё нельзя — это «прочитать не удалось», а не «ИНН нет».
    /// </summary>
    [Fact]
    public async Task Негодная_основа_делает_запись_нечитаемой_а_не_пустой()
    {
        var type = await TypeAsync();
        var here = Guid.NewGuid();
        var there = Guid.NewGuid();
        var foreign = await EntryAsync(type, "Чужая стройка", """{"ИНН":"7701234567"}""", CatalogScope.Construction, there);
        var local = await EntryAsync(type, "Своя стройка", """{"ИНН":"7802345678"}""", CatalogScope.Construction, here);

        var lost = await EntryAsync(type, "Основа удалена", $$"""{"_baseRef":"{{Guid.NewGuid()}}"}""");
        var garbage = await EntryAsync(type, "Ссылка не читается", """{"_baseRef":"не идентификатор"}""");
        var sideways = await EntryAsync(type, "Основа вбок", $$"""{"_baseRef":"{{foreign}}"}""", CatalogScope.Construction, here);
        var upwards = await EntryAsync(type, "Основа в системе снизу", $$"""{"_baseRef":"{{foreign}}"}""");
        var near = await EntryAsync(type, "Основа рядом", $$"""{"_baseRef":"{{local}}"}""", CatalogScope.Construction, here);
        var shaped = await EntryAsync(type, "Не скаляр", """{"ИНН":{"значение":"7701234567"}}""");
        // «id» числом: ссылка негодна, но чтение всего списка она ронять не вправе.
        var numeric = await EntryAsync(type, "Ссылка с числом", """{"_baseRef":{"kind":"catalog","id":5}}""");

        var values = await ValuesAsync(type);

        Assert.Equal((null, null, true), Of(values, lost));
        Assert.Equal((null, null, true), Of(values, garbage));
        Assert.Equal((null, null, true), Of(values, sideways));
        Assert.Equal((null, null, true), Of(values, numeric));
        // Основа ниже наследника, в его же ветке: документ этой стройки печатает значение, и «не
        // прочитано» было бы ложью. Чужая ветка (sideways) по-прежнему недостижима.
        Assert.Equal(("7701234567", foreign, false), Of(values, upwards));
        Assert.Equal((null, null, true), Of(values, shaped));
        Assert.Equal(("7802345678", local, false), Of(values, near));
    }

    /// <summary>
    /// Настоящее дерево: стройка → раздел → комплект. Роль комплекта наследует от записи раздела и
    /// от записи стройки (вверх по своей ветке), запись стройки — от записи комплекта (вниз по своей).
    /// Комплект соседней стройки — чужая ветка. Цепочки областей на всех грузятся разом.
    /// </summary>
    [Fact]
    public async Task Основа_достижима_вверх_и_вниз_по_своей_ветке_и_недостижима_в_чужой()
    {
        var type = await TypeAsync();
        var (construction, section, set) = await TreeAsync();
        var (_, _, otherSet) = await TreeAsync();

        var atSection = await EntryAsync(type, "Раздел", """{"ИНН":"7701234567"}""", CatalogScope.Section, section);
        var atConstruction = await EntryAsync(type, "Стройка", """{"ИНН":"7802345678"}""", CatalogScope.Construction, construction);
        var atSet = await EntryAsync(type, "Комплект", """{"ИНН":"7705000001"}""", CatalogScope.Set, set);

        var up = await EntryAsync(type, "Вверх на раздел", $$"""{"_baseRef":"{{atSection}}"}""", CatalogScope.Set, set);
        var upTwice = await EntryAsync(type, "Вверх на стройку", $$"""{"_baseRef":"{{atConstruction}}"}""", CatalogScope.Set, set);
        var down = await EntryAsync(type, "Вниз на комплект", $$"""{"_baseRef":"{{atSet}}"}""", CatalogScope.Construction, construction);
        var across = await EntryAsync(type, "В чужой комплект", $$"""{"_baseRef":"{{atSet}}"}""", CatalogScope.Set, otherSet);

        var values = await ValuesAsync(type);

        Assert.Equal(("7701234567", atSection, false), Of(values, up));
        Assert.Equal(("7802345678", atConstruction, false), Of(values, upTwice));
        Assert.Equal(("7705000001", atSet, false), Of(values, down));
        Assert.Equal((null, null, true), Of(values, across));
    }

    private async Task<(Guid Construction, Guid Section, Guid Set)> TreeAsync()
    {
        var construction = await SendAsync(new CreateConstructionCommand($"Объект {Guid.NewGuid():N}", Guid.NewGuid()));
        var section = await SendAsync(new CreateSectionCommand(construction.Id, "Раздел"));
        var set = await SendAsync(new CreateDocumentSetCommand(section.Id, "Комплект"));
        return (construction.Id, section.Id, set.Id);
    }

    [Fact]
    public async Task Замкнутая_цепочка_основ_нечитаема_и_запрос_не_зависает()
    {
        var type = await TypeAsync();
        var created = await SendAsync(new CreateCommonDataEntryCommand(
            "Первая", type.Id, JsonDocument.Parse("{}"), CatalogScope.System, null));
        var first = created.Id;
        var second = await EntryAsync(type, "Вторая", $$"""{"_baseRef":"{{first}}"}""");
        await SendAsync(new UpdateCommonDataEntryCommand(
            first, "Первая", JsonDocument.Parse($$"""{"_baseRef":"{{second}}"}"""), TestAccess.All, created.Version));

        var values = await ValuesAsync(type);

        Assert.Equal((null, null, true), Of(values, first));
        Assert.Equal((null, null, true), Of(values, second));
    }

    /// <summary>
    /// Архивная запись сопоставлению видна, с признаком: иначе «организация в архиве» выглядела бы как
    /// «организации нет». Выбору — не видна. И значение архивной ОСНОВЫ у живой роли не пропадает.
    /// </summary>
    [Fact]
    public async Task Архивная_запись_видна_показу_с_признаком_и_скрыта_от_выбора()
    {
        var type = await TypeAsync();
        var org = await EntryAsync(type, "В архиве", """{"ИНН":"7701234567"}""");
        var role = await EntryAsync(type, "Роль архивной", $$"""{"_baseRef":"{{org}}"}""");
        await SendAsync(new SetRecordArchiveCommand(org, true));

        var shown = await ValuesAsync(type);
        Assert.True(shown.Single(v => v.Record.Id == org).Record.Archived);
        Assert.Equal(("7701234567", org, false), Of(shown, role));

        var chosen = await ValuesAsync(type, RecordsFor.Choice);
        Assert.DoesNotContain(chosen, v => v.Record.Id == org);
        Assert.Equal(("7701234567", org, false), Of(chosen, role));
    }

    /// <summary>Подтип вида входит в вид, а код у записи — свой.</summary>
    [Fact]
    public async Task Запись_подтипа_приходит_со_своим_кодом()
    {
        var type = await TypeAsync();
        var subtype = await SendAsync(new CreateDocumentTypeCommand(
            $"Подтип {Guid.NewGuid():N}", $"Подтип{Guid.NewGuid():N}", DocumentTypeKind.Composite,
            type.Id, JsonDocument.Parse("""{"fields":[]}""")));
        var entry = await EntryAsync(subtype, "Запись подтипа", """{"ИНН":"7701234567"}""");

        var found = (await ValuesAsync(type)).Single(v => v.Record.Id == entry);

        Assert.Equal(subtype.Code, found.Record.EntityType);
        Assert.Equal("7701234567", found.Value);
    }

    /// <summary>
    /// Сторож: поля в схеме нет (переименовали, сделали составным) — это отдельный ответ, а не «ни у
    /// кого значения нет». Вида нет — третий.
    /// </summary>
    [Fact]
    public async Task Поле_которого_нет_в_схеме_и_вид_которого_нет_отвечают_по_разному()
    {
        var type = await TypeAsync();
        await EntryAsync(type, "Есть запись", """{"ИНН":"7701234567","Наименование":{"Краткое":"ООО"}}""");

        using var scope = host.Services.CreateScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IModuleCatalog>();

        Assert.Null(await catalog.FieldValuesAsync("НетТакогоВида", Field, RecordsFor.Display));

        var renamed = await catalog.FieldValuesAsync(type.Code, "ИНН организации", RecordsFor.Display);
        Assert.False(renamed!.Declared);
        Assert.Empty(renamed.Records);

        var complex = await catalog.FieldValuesAsync(type.Code, "Наименование", RecordsFor.Display);
        Assert.False(complex!.Declared);

        Assert.True((await catalog.FieldValuesAsync(type.Code, Field, RecordsFor.Display))!.Declared);
    }

    private static (string? Value, Guid? From, bool Unreadable) Of(IReadOnlyList<ModuleCatalogFieldValue> values, Guid id)
    {
        var found = Assert.Single(values, v => v.Record.Id == id);
        return (found.Value, found.InheritedFrom, found.Unreadable);
    }

    private async Task<IReadOnlyList<ModuleCatalogFieldValue>> ValuesAsync(
        DocumentType type, RecordsFor purpose = RecordsFor.Display)
    {
        using var scope = host.Services.CreateScope();
        var answer = await scope.ServiceProvider.GetRequiredService<IModuleCatalog>()
            .FieldValuesAsync(type.Code, Field, purpose);
        Assert.True(answer!.Declared);
        return answer.Records;
    }

    /// <summary>Свой вид на каждый тест: схему «Организации» у заказчика ведёт человек, здесь — мы.</summary>
    private async Task<DocumentType> TypeAsync() =>
        await SendAsync(new CreateDocumentTypeCommand(
            $"Контрагент {Guid.NewGuid():N}", $"Контрагент{Guid.NewGuid():N}", DocumentTypeKind.Composite, null,
            JsonDocument.Parse(
                """{"fields":[{"key":"ИНН","type":"string","title":"ИНН"},{"key":"Наименование","type":"complex","title":"Наименование"}]}""")));

    private async Task<Guid> EntryAsync(
        DocumentType type, string name, string data, CatalogScope scope = CatalogScope.System, Guid? scopeId = null) =>
        (await SendAsync(new CreateCommonDataEntryCommand(name, type.Id, JsonDocument.Parse(data), scope, scopeId))).Id;

    private async Task<T> SendAsync<T>(IRequest<T> request)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(request);
    }
}
