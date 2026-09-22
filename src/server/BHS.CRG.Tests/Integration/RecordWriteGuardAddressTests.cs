using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Infrastructure.Persistence;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Охрана записи ДОЕЗЖАЕТ до каждого адреса сохранения (issue #957, ТЗ CORE-20).
///
/// Сами правила проверены отдельно и целиком (<c>RecordWriteGuardTests</c>), перечень адресов — в
/// <c>RecordWriteGuardCoverageTests</c>. Здесь третье: что у адреса охрану СПРАШИВАЮТ, что отказ
/// доезжает до того, кто сохранял, и что отказ ничего не записал. Правило, не подключённое к
/// адресу, зелено в своих тестах и не останавливает ничего.
/// </summary>
[Collection("Integration")]
public class RecordWriteGuardAddressTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private readonly Guid _userId = Guid.NewGuid();

    /// <summary>Тип модуля: число, поле модуля без замка и запертое поле.</summary>
    private const string ModuleSchema = """
        {"fields":[
          {"key":"Кол","type":"number","title":"Количество"},
          {"key":"Табельный","type":"string","title":"Табельный номер","origin":"module"},
          {"key":"Сумма","type":"number","title":"Сумма","origin":"module","locked":true}]}
        """;

    // ── Реквизиты документа ───────────────────────────────────────────────────

    [Fact]
    public async Task Реквизиты_с_неверным_видом_значения_отказывают_и_не_сохраняются()
    {
        var (typeId, instanceId) = await SeedDocumentAsync("DOC_GUARD", SchemaEditLevel.Extendable);

        var refusal = await Assert.ThrowsAsync<RecordWriteRefusedException>(() => SendAsync(
            new UpdateRequisitesCommand(instanceId, JsonDocument.Parse("""{"Кол":"12,5"}"""))));

        Assert.Contains("Количество", refusal.Message);
        Assert.Equal("Кол", Assert.Single(refusal.Details).Path);

        // Отказ, который «почти сохранил», хуже отсутствия отказа.
        var saved = await SendAsync(new GetDocumentInstanceQuery(instanceId));
        Assert.DoesNotContain("12,5", saved!.Data.RootElement.GetRawText());
        Assert.NotEqual(Guid.Empty, typeId);
    }

    /// <summary>
    /// Тот же адрес и то же значение, но тип открытый — сохранение проходит. Без этой половины
    /// тест доказывал бы лишь то, что реквизиты вообще отказывают, и остался бы зелёным, запрети мы
    /// их всем.
    /// </summary>
    [Fact]
    public async Task В_открытом_типе_те_же_реквизиты_сохраняются()
    {
        var (_, instanceId) = await SeedDocumentAsync("DOC_OPEN", SchemaEditLevel.Open);

        var saved = await SendAsync(
            new UpdateRequisitesCommand(instanceId, JsonDocument.Parse("""{"Кол":"12,5"}""")));

        Assert.Contains("12,5", saved.Data.RootElement.GetRawText());
    }

    [Fact]
    public async Task Правка_запертого_поля_в_реквизитах_отказывает()
    {
        var (_, instanceId) = await SeedDocumentAsync("DOC_LOCK", SchemaEditLevel.Extendable,
            """{"Сумма":100}""");

        var refusal = await Assert.ThrowsAsync<RecordWriteRefusedException>(() => SendAsync(
            new UpdateRequisitesCommand(instanceId, JsonDocument.Parse("""{"Сумма":200}"""))));

        Assert.Equal(RecordWriteGuard.LockedField, Assert.Single(refusal.Details).Code);
    }

    // ── Запись общих данных ───────────────────────────────────────────────────

    [Fact]
    public async Task Создание_записи_общих_данных_с_запертым_полем_отказывает()
    {
        var typeId = await SeedTypeAsync("CD_CREATE", SchemaEditLevel.Extendable);

        var refusal = await Assert.ThrowsAsync<RecordWriteRefusedException>(() => SendAsync(
            new CreateCommonDataEntryCommand("Запись", typeId,
                JsonDocument.Parse("""{"Сумма":100}"""), CatalogScope.System, null)));

        Assert.Contains("впервые", refusal.Message);
    }

    [Fact]
    public async Task Правка_записи_общих_данных_с_кривым_значением_отказывает()
    {
        var typeId = await SeedTypeAsync("CD_UPDATE", SchemaEditLevel.Extendable);
        var entry = await SendAsync(new CreateCommonDataEntryCommand(
            "Запись", typeId, JsonDocument.Parse("""{"Кол":1}"""), CatalogScope.System, null));

        var refusal = await Assert.ThrowsAsync<RecordWriteRefusedException>(() => SendAsync(
            new UpdateCommonDataEntryCommand(entry.Id, "Запись", JsonDocument.Parse("""{"Кол":"12,5"}"""))));

        Assert.Equal("Кол", Assert.Single(refusal.Details).Path);
    }

    // ── Документ качества ─────────────────────────────────────────────────────

    [Fact]
    public async Task Правка_документа_качества_с_кривым_значением_отказывает()
    {
        var typeId = await SeedTypeAsync("QD_UPDATE", SchemaEditLevel.Extendable);
        var doc = await SendAsync(new CreateQualityDocumentCommand(
            typeId, "Сертификат", JsonDocument.Parse("""{"Кол":1}"""),
            CatalogScope.System, null, QualityDocSource.Manual, null, null, null));

        var refusal = await Assert.ThrowsAsync<RecordWriteRefusedException>(() => SendAsync(
            new UpdateQualityDocumentCommand(doc.Id, typeId, "Сертификат",
                JsonDocument.Parse("""{"Кол":"12,5"}"""))));

        Assert.Equal("Кол", Assert.Single(refusal.Details).Path);
    }

    [Fact]
    public async Task Документ_качества_с_верным_значением_сохраняется()
    {
        var typeId = await SeedTypeAsync("QD_OK", SchemaEditLevel.Extendable);
        var doc = await SendAsync(new CreateQualityDocumentCommand(
            typeId, "Сертификат ОК", JsonDocument.Parse("""{"Кол":1}"""),
            CatalogScope.System, null, QualityDocSource.Manual, null, null, null));

        var saved = await SendAsync(new UpdateQualityDocumentCommand(
            doc.Id, typeId, "Сертификат ОК", JsonDocument.Parse("""{"Кол":2}""")));

        Assert.Contains("\"Кол\":2", saved.Requisites.RootElement.GetRawText());
    }

    // ── Посев ─────────────────────────────────────────────────────────────────

    private async Task<Guid> SeedTypeAsync(string code, SchemaEditLevel level)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Уровень назначается прямо в базе: адреса «сменить уровень» нет и не будет — его объявляет
        // модуль, а модулей, заводящих типы, пока нет.
        var type = DocumentType.Create(code, code, DocumentTypeKind.Document, null,
            JsonDocument.Parse(ModuleSchema), TypeOwner.Core, TypeVisibility.Shared, editLevel: level);
        db.DocumentTypes.Add(type);
        await db.SaveChangesAsync();
        return type.Id;
    }

    private async Task<(Guid TypeId, Guid InstanceId)> SeedDocumentAsync(
        string code, SchemaEditLevel level, string? stored = null)
    {
        var typeId = await SeedTypeAsync(code, level);
        using var scope = fixture.Services.CreateScope();
        var m = scope.ServiceProvider.GetRequiredService<IMediator>();
        var construction = await m.Send(new CreateConstructionCommand($"Объект {code}", _userId));
        var section = await m.Send(new CreateSectionCommand(construction.Id, "Раздел"));
        var set = await m.Send(new CreateDocumentSetCommand(section.Id, "Комплект"));
        var instance = await m.Send(new AddDocumentToSetCommand(set.Id, typeId));

        if (stored is not null)
        {
            // Прямо в базу: положить запертое значение через охраняемый адрес нельзя — она же и
            // откажет. Это и есть «значение кладёт код модуля своей командой».
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var obj = await db.DomainObjects.FindAsync(instance.Id);
            obj!.SetData(JsonDocument.Parse(stored));
            await db.SaveChangesAsync();
        }
        return (typeId, instance.Id);
    }

    private async Task<T> SendAsync<T>(IRequest<T> request)
    {
        using var scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(request);
    }
}
