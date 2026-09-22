using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Schema;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Тип, объявленный модулем, живёт по общим правилам (issue #958, ТЗ CORE-20.1, CORE-20.2).
///
/// <para>Главный тест здесь — <see cref="Тэг_на_системном_поле_работает_как_на_поле_схемы"/>: это
/// сторож задачи. Он проверяет не «поле создалось», а то, ради чего всё и делалось — что реестр
/// тэгов НАХОДИТ системное поле. Реестр читает схему напрямую, мимо расчёта эффективных полей, и
/// на нём стоят печать, метаданные генерации и ключ идентичности материала: подмешай мы системные
/// поля веткой резолвера, тэг бы не нашёлся, и печать потеряла бы поле.</para>
/// </summary>
[Collection("Integration")]
public class ModuleTypeProjectionTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static ModuleTypeSpec Spec(string code, params ModuleFieldSpec[] fields) =>
        new("work", code, $"Тип {code}", SchemaEditLevel.Extendable, fields);

    private static readonly ModuleFieldSpec Numbered =
        new("Номер", "Номер записи", "string", Tags: [FunctionalTag.DocNumber]);

    private static readonly ModuleFieldSpec Volume = new("Объём", "Объём работ", "number");

    // ── Сторож задачи ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Тэг_на_системном_поле_работает_как_на_поле_схемы()
    {
        var type = await SendAsync(new ProjectModuleTypeCommand(Spec("WORK_TAGGED", Numbered, Volume)));
        var all = await SendAsync(new ListDocumentTypesQuery());

        var tagged = SchemaTags.TaggedFields(type, all);

        // Ровно то, что делает печать: находит поле ПО ТЭГУ, а не по имени.
        Assert.Contains(tagged, t => t.Key == "Номер" && t.Tag == FunctionalTag.DocNumber);
    }

    /// <summary>
    /// Вторая половина сторожа: то же поле, объявленное администратором вручную, находится так же.
    /// Без неё тест доказывал бы только то, что реестр тэгов вообще работает, — и остался бы
    /// зелёным, начни системные поля теряться где-то дальше.
    /// </summary>
    [Fact]
    public async Task Такое_же_поле_заказчика_находится_тем_же_способом()
    {
        var type = await SeedCustomerTypeAsync("CUSTOMER_TAGGED",
            $$"""{"fields":[{"key":"Номер","title":"Номер","type":"string","tags":["{{FunctionalTag.DocNumber}}"]}]}""");
        var all = await SendAsync(new ListDocumentTypesQuery());

        var tagged = SchemaTags.TaggedFields(type, all);

        Assert.Contains(tagged, t => t.Key == "Номер" && t.Tag == FunctionalTag.DocNumber);
    }

    /// <summary>Порядок: системные поля идут первыми — их и ждёт человек сверху в редакторе.</summary>
    [Fact]
    public async Task Системные_поля_идут_первыми()
    {
        await SendAsync(new ProjectModuleTypeCommand(Spec("WORK_ORDER", Numbered, Volume)));
        var type = await ByCodeAsync("WORK_ORDER");
        await AddCustomerFieldAsync(type, "Своё");

        var again = await SendAsync(new ProjectModuleTypeCommand(Spec("WORK_ORDER", Numbered, Volume)));
        var keys = DocumentTypeSchemaReader.EffectiveFields(again.Id, await ByIdAsync()).Select(f => f.Key);

        Assert.Equal(["Номер", "Объём", "Своё"], keys);
    }

    // ── Метки, на которых держатся F2 и F3 ────────────────────────────────────

    [Fact]
    public async Task Системное_поле_помечено_модулем_и_заперто()
    {
        var type = await SendAsync(new ProjectModuleTypeCommand(Spec("WORK_MARKS", Numbered)));

        var field = DocumentTypeSchemaReader.EffectiveFields(type.Id, await ByIdAsync()).Single();

        Assert.True(field.Locked);
        Assert.Equal(SchemaEditLevel.Extendable, type.EditLevel);
        Assert.Equal("work", type.Module);
    }

    /// <summary>
    /// Охрана записи (issue #957) видит системное поле как запертое — то есть «проверяется тем же
    /// механизмом» не на словах.
    /// </summary>
    [Fact]
    public async Task Охрана_записи_не_даёт_тронуть_системное_поле()
    {
        var type = await SendAsync(new ProjectModuleTypeCommand(Spec("WORK_GUARD", Volume)));
        var entry = await SendAsync(new CreateCommonDataEntryCommand(
            "Запись", type.Id, JsonDocument.Parse("{}"), Domain.Catalog.CatalogScope.System, null));

        var refusal = await Assert.ThrowsAsync<RecordWriteRefusedException>(() => SendAsync(
            new UpdateCommonDataEntryCommand(entry.Id, "Запись", JsonDocument.Parse("""{"Объём":5}"""))));

        Assert.Equal(RecordWriteGuard.LockedField, Assert.Single(refusal.Details).Code);
    }

    // ── Идемпотентность ───────────────────────────────────────────────────────

    [Fact]
    public async Task Повторный_старт_не_плодит_полей_и_не_трогает_поле_заказчика()
    {
        await SendAsync(new ProjectModuleTypeCommand(Spec("WORK_TWICE", Numbered)));
        var type = await ByCodeAsync("WORK_TWICE");
        await AddCustomerFieldAsync(type, "Своё");

        await SendAsync(new ProjectModuleTypeCommand(Spec("WORK_TWICE", Numbered)));

        var keys = DocumentTypeSchemaReader.EffectiveFields((await ByCodeAsync("WORK_TWICE")).Id, await ByIdAsync())
            .Select(f => f.Key).ToList();
        Assert.Equal(["Номер", "Своё"], keys);
    }

    /// <summary>
    /// Подпись системного поля — единственное, что остаётся администратору даже в закрытом типе.
    /// Затирай её проекция, его правка молча отменялась бы при первом же перезапуске.
    /// </summary>
    [Fact]
    public async Task Подпись_правленная_администратором_переживает_проекцию()
    {
        await SendAsync(new ProjectModuleTypeCommand(Spec("WORK_TITLE", Numbered)));
        var type = await ByCodeAsync("WORK_TITLE");
        await RenameFieldTitleAsync(type, "Номер", "Номер по журналу");

        await SendAsync(new ProjectModuleTypeCommand(Spec("WORK_TITLE", Numbered)));

        var field = DocumentTypeSchemaReader.EffectiveFields((await ByCodeAsync("WORK_TITLE")).Id, await ByIdAsync())
            .Single(f => f.Key == "Номер");
        Assert.Equal("Номер по журналу", field.Title);
    }

    // ── Отказы, останавливающие старт ─────────────────────────────────────────

    [Fact]
    public async Task Поле_заказчика_с_тем_же_ключом_останавливает_старт()
    {
        var type = await SeedCustomerTypeAsync("WORK_CLASH",
            """{"fields":[{"key":"Номер","title":"Свой номер","type":"string"}]}""");
        Assert.NotEqual(Guid.Empty, type.Id);

        var refusal = await Assert.ThrowsAsync<ConflictException>(() => SendAsync(
            new ProjectModuleTypeCommand(Spec("WORK_CLASH", Numbered))));

        Assert.Contains("Номер", refusal.Message);
    }

    [Fact]
    public async Task Неизвестный_тэг_останавливает_старт()
    {
        var spec = Spec("WORK_BADTAG", new ModuleFieldSpec("Поле", "Поле", "string", Tags: ["doc.нетТакого"]));

        var refusal = await Assert.ThrowsAsync<ConflictException>(
            () => SendAsync(new ProjectModuleTypeCommand(spec)));

        Assert.Contains("неизвестный", refusal.Message);
    }

    [Fact]
    public async Task Повтор_ключа_в_объявлении_останавливает_старт()
    {
        var spec = Spec("WORK_DUP", Numbered, Numbered);

        var refusal = await Assert.ThrowsAsync<ConflictException>(
            () => SendAsync(new ProjectModuleTypeCommand(spec)));

        Assert.Contains("повторяющимися", refusal.Message);
    }

    // ── Посев и помощники ─────────────────────────────────────────────────────

    private async Task<DocumentType> SeedCustomerTypeAsync(string code, string schema)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Infrastructure.Persistence.AppDbContext>();
        var type = DocumentType.Create(code, code, DocumentTypeKind.Document, null,
            JsonDocument.Parse(schema), TypeOwner.Core, TypeVisibility.Shared);
        db.DocumentTypes.Add(type);
        await db.SaveChangesAsync();
        return type;
    }

    /// <summary>Правка администратора — прямо в базе: через свой адрес он бы уткнулся в политику F2.</summary>
    private async Task AddCustomerFieldAsync(DocumentType type, string key)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Infrastructure.Persistence.AppDbContext>();
        var live = await db.DocumentTypes.FindAsync(type.Id);
        var root = System.Text.Json.Nodes.JsonNode.Parse(live!.Schema.RootElement.GetRawText())!.AsObject();
        (root["fields"]!.AsArray()).Add(new System.Text.Json.Nodes.JsonObject
        {
            ["key"] = key, ["title"] = key, ["type"] = "string",
        });
        live.UpdateSchema(JsonDocument.Parse(root.ToJsonString()));
        await db.SaveChangesAsync();
    }

    private async Task RenameFieldTitleAsync(DocumentType type, string key, string title)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Infrastructure.Persistence.AppDbContext>();
        var live = await db.DocumentTypes.FindAsync(type.Id);
        var root = System.Text.Json.Nodes.JsonNode.Parse(live!.Schema.RootElement.GetRawText())!.AsObject();
        foreach (var f in root["fields"]!.AsArray())
            if (f!["key"]!.GetValue<string>() == key) f["title"] = title;
        live.UpdateSchema(JsonDocument.Parse(root.ToJsonString()));
        await db.SaveChangesAsync();
    }

    private async Task<DocumentType> ByCodeAsync(string code) =>
        (await SendAsync(new ListDocumentTypesQuery())).Single(t => t.Code == code);

    private async Task<IReadOnlyDictionary<Guid, DocumentType>> ByIdAsync() =>
        (await SendAsync(new ListDocumentTypesQuery())).ToDictionary(t => t.Id);

    private async Task<T> SendAsync<T>(IRequest<T> request)
    {
        using var scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(request);
    }
}
