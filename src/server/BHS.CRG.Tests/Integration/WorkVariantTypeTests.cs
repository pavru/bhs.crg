using System.Text.Json;
using System.Text.Json.Nodes;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Schema;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Тип ядра «ВариантРаботы» ([WORK-49] [TYPE-9] [CORE-30], change <c>work-06-work-type-variants</c>):
/// объявление ядра, ссылка на вид работы по коду, чистая установка и защита поля ядра от редактора
/// типов. Проверяется настоящее объявление из <see cref="CoreRecordTypes" />, а не копия.
/// </summary>
[Collection("Integration")]
public class WorkVariantTypeTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    private readonly WorkTypeSeed seed = new(fixture);

    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Вариант_ссылается_на_вид_работы_по_коду()
    {
        var (_, workType) = await seed.ClassifierAsync();

        var variant = await seed.SendAsync(new ProjectModuleTypeCommand(CoreRecordTypes.WorkVariant));

        Assert.NotNull(variant);
        Assert.Equal(TypeOwner.Core, variant.Module);
        Assert.Equal(SchemaEditLevel.Extendable, variant.EditLevel);
        Assert.Null(variant.ParentId);
        var byKey = Fields(variant);
        var link = byKey[CoreRecordTypes.WorkVariantWorkTypeKey];
        Assert.Equal("complex", link.GetProperty("type").GetString());
        Assert.Equal(workType.Id.ToString(), link.GetProperty("typeId").GetString());
        Assert.True(link.GetProperty("required").GetBoolean());
        // Не запертая: заводит вариант человек, и замок значения не дал бы заполнить ссылку вовсе.
        Assert.False(link.GetProperty("locked").GetBoolean());
        Assert.Contains(FunctionalTag.Identity + ":1",
            byKey[CoreRecordTypes.WorkVariantNameKey].GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
    }

    /// <summary>
    /// Сторож решения «не Locked»: ссылка на вид заполняется той же командой общих данных, которую
    /// зовёт дверь. Будь поле запертым, охрана записи отвергла бы первое же заведение.
    /// </summary>
    [Fact]
    public async Task Ссылка_на_вид_заполняется_при_заведении()
    {
        var (unit, workType) = await seed.ClassifierAsync();
        var variantType = (await seed.SendAsync(new ProjectModuleTypeCommand(CoreRecordTypes.WorkVariant)))!;
        var work = await seed.RecordAsync(workType.Id, "Прокладка кабеля",
            WorkTypeSeed.WorkTypeData("01", "Прокладка кабеля", await seed.RecordAsync(unit.Id, "м")));

        var created = await seed.RecordAsync(variantType.Id, "в лотке", WorkVariantSeed.Data(work, "в лотке"));

        Assert.Equal(work.Id.ToString(), created.Data.RootElement
            .GetProperty(CoreRecordTypes.WorkVariantWorkTypeKey).GetProperty("entryId").GetString());
    }

    /// <summary>Чистая установка: нет единиц — нет видов работ — нет и вариантов, а старт идёт дальше.</summary>
    [Fact]
    public async Task Без_вида_работы_вариант_не_заводится_и_старт_не_падает()
    {
        Assert.Null(await seed.SendAsync(new ProjectModuleTypeCommand(CoreRecordTypes.WorkType)));
        Assert.Null(await seed.SendAsync(new ProjectModuleTypeCommand(CoreRecordTypes.WorkVariant)));

        var all = await seed.SendAsync(new ListDocumentTypesQuery());
        Assert.DoesNotContain(all, t => t.Code == CoreRecordTypes.WorkVariantCode);
    }

    [Fact]
    public async Task Повторный_старт_тип_не_дублирует()
    {
        await seed.ClassifierAsync();
        var first = await seed.SendAsync(new ProjectModuleTypeCommand(CoreRecordTypes.WorkVariant));
        var again = await seed.SendAsync(new ProjectModuleTypeCommand(CoreRecordTypes.WorkVariant));

        Assert.Equal(first!.Id, again!.Id);
        Assert.Single(await seed.SendAsync(new ListDocumentTypesQuery()), t => t.Code == CoreRecordTypes.WorkVariantCode);
    }

    /// <summary>Уровень «расширяемый»: заказчик дописывает своё необязательное поле.</summary>
    [Fact]
    public async Task Заказчик_дописывает_своё_поле()
    {
        await seed.ClassifierAsync();
        var variant = (await seed.SendAsync(new ProjectModuleTypeCommand(CoreRecordTypes.WorkVariant)))!;
        var schema = JsonNode.Parse(variant.Schema!.RootElement.GetRawText())!;
        schema["fields"]!.AsArray().Add(new JsonObject
        {
            ["key"] = "СпособКрепления", ["title"] = "Способ крепления", ["type"] = "string",
        });

        var saved = await seed.SendAsync(new UpdateDocumentTypeSchemaCommand(variant.Id, JsonDocument.Parse(schema.ToJsonString())));

        Assert.Contains("СпособКрепления", Fields(saved).Keys);
    }

    /// <summary>
    /// Ссылку на вид не снять ни удалением, ни переименованием: на поле опирается код ядра. Переименование
    /// неотличимо от «удалил одно, добавил другое», поэтому оба случая — одна проверка.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ссылку_на_вид_не_снять_ни_удалением_ни_переименованием(bool rename)
    {
        await seed.ClassifierAsync();
        var variant = (await seed.SendAsync(new ProjectModuleTypeCommand(CoreRecordTypes.WorkVariant)))!;
        var schema = JsonNode.Parse(variant.Schema!.RootElement.GetRawText())!;
        var fields = schema["fields"]!.AsArray();
        var link = fields.First(f => f!["key"]!.GetValue<string>() == CoreRecordTypes.WorkVariantWorkTypeKey)!;
        if (rename) link["key"] = "Вид";
        else fields.Remove(link);

        var refusal = await Assert.ThrowsAsync<ConflictException>(() => seed.SendAsync(
            new UpdateDocumentTypeSchemaCommand(variant.Id, JsonDocument.Parse(schema.ToJsonString()))));

        Assert.Contains("удалено или переименовано", refusal.Message);
        var stored = await seed.SendAsync(new GetDocumentTypeQuery(variant.Id));
        Assert.Contains(CoreRecordTypes.WorkVariantWorkTypeKey, Fields(stored!).Keys);
    }

    private static Dictionary<string, JsonElement> Fields(DocumentType type) =>
        type.Schema!.RootElement.GetProperty("fields").EnumerateArray()
            .ToDictionary(f => f.GetProperty("key").GetString()!);
}
