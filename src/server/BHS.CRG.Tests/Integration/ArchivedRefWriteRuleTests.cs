using System.Text.Json;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Objects;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Modules.Ports;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Правило архива при записи (ТЗ CORE-34.4, issue #1185, шаг 5): сервер не принимает НОВУЮ ссылку на
/// архивную запись, а стоявшую оставляет.
///
/// <para>Здесь — что правило доезжает до каждого адреса, который зовёт охрану записи, и что машинный
/// путь (копия документа) стоявших ссылок не теряет. Типы в тестах — ОТКРЫТЫЕ: охрана схемы на них
/// выходит сразу, и правило, спрятанное за её дешёвый выход, не сработало бы ни разу — открытых
/// типов в живой системе все.</para>
/// </summary>
[Collection("Integration")]
public class ArchivedRefWriteRuleTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private readonly Guid _userId = Guid.NewGuid();

    private const string OrgSchema = """{"fields":[{"key":"ИНН","type":"string"}]}""";
    private const string HolderSchema = """
        {"fields":[
          {"key":"Подрядчик","type":"complex","title":"Подрядная организация"},
          {"key":"Подписанты","type":"table","title":"Подписанты"},
          {"key":"Примечание","type":"string"}]}
        """;

    private static string RefJson(Guid entryId, string name) =>
        $$"""{"$ref":"catalog","entryId":"{{entryId}}","displayName":"{{name}}"}""";

    private static JsonDocument Ref(string field, Guid entryId, string name, string rest = "") =>
        JsonDocument.Parse("{\"" + field + "\":" + RefJson(entryId, name) + rest + "}");

    /// <summary>Таблица подписантов: в каждой строке — ссылка на организацию.</summary>
    private static JsonDocument Signers(params string[] refs) => JsonDocument.Parse(
        "{\"Подписанты\":[" + string.Join(",", refs.Select(r => "{\"Организация\":" + r + "}")) + "]}");

    // ── Реквизиты документа ───────────────────────────────────────────────────

    [Fact]
    public async Task Новая_ссылка_на_архивную_запись_в_реквизитах_отвергнута_и_называет_поле_и_запись()
    {
        var (archived, _) = await SeedOrgsAsync();
        var (_, instanceId) = await SeedDocumentAsync("DOC_A");

        var refusal = await Assert.ThrowsAsync<RecordWriteRefusedException>(() => SendAsync(
            new UpdateRequisitesCommand(instanceId, Ref("Подрядчик", archived, "Ромашка"))));

        var detail = Assert.Single(refusal.Details);
        Assert.Equal(ArchivedRefRule.ArchivedRef, detail.Code);
        Assert.Equal("Подрядчик", detail.Path);
        Assert.Contains("Подрядная организация", refusal.Message);
        Assert.Contains("Ромашка", refusal.Message);

        var saved = await SendAsync(new GetDocumentInstanceQuery(instanceId));
        Assert.DoesNotContain(archived.ToString(), saved!.Data.RootElement.GetRawText());
    }

    [Fact]
    public async Task Ссылка_на_действующую_запись_сохраняется()
    {
        var (_, active) = await SeedOrgsAsync();
        var (_, instanceId) = await SeedDocumentAsync("DOC_B");

        var saved = await SendAsync(new UpdateRequisitesCommand(instanceId, Ref("Подрядчик", active, "Лютик")));

        Assert.Contains(active.ToString(), saved.Data.RootElement.GetRawText());
    }

    /// <summary>
    /// Главное обещание архива: документ со стоявшей ссылкой правится как прежде. И переезд ссылки
    /// внутри документа — из поля в строку таблицы — новой её не делает: сравнение по записи, а не
    /// по месту.
    /// </summary>
    [Fact]
    public async Task Стоявшая_ссылка_переживает_правку_и_переезд_внутри_документа()
    {
        var (orgType, active) = await SeedOrgTypeAndEntryAsync("Ромашка");
        var (_, instanceId) = await SeedDocumentAsync("DOC_C");
        await SendAsync(new UpdateRequisitesCommand(instanceId, Ref("Подрядчик", active, "Ромашка")));
        await ArchiveAsync(active);

        var edited = await SendAsync(new UpdateRequisitesCommand(
            instanceId, Ref("Подрядчик", active, "Ромашка", ",\"Примечание\":\"правка\"")));
        Assert.Contains("правка", edited.Data.RootElement.GetRawText());

        var moved = await SendAsync(new UpdateRequisitesCommand(instanceId, Signers(RefJson(active, "Ромашка"))));
        Assert.Contains(active.ToString(), moved.Data.RootElement.GetRawText());
        Assert.NotEqual(Guid.Empty, orgType);
    }

    /// <summary>Сняли ссылку и сохранили — «стоявшей» она быть перестала: вернуть её можно только из архива.</summary>
    [Fact]
    public async Task Снятая_и_сохранённая_ссылка_обратно_не_ставится()
    {
        var (_, active) = await SeedOrgTypeAndEntryAsync("Ромашка");
        var (_, instanceId) = await SeedDocumentAsync("DOC_D");
        await SendAsync(new UpdateRequisitesCommand(instanceId, Ref("Подрядчик", active, "Ромашка")));
        await ArchiveAsync(active);
        await SendAsync(new UpdateRequisitesCommand(instanceId, JsonDocument.Parse("{}")));

        await Assert.ThrowsAsync<RecordWriteRefusedException>(() => SendAsync(
            new UpdateRequisitesCommand(instanceId, Ref("Подрядчик", active, "Ромашка"))));
    }

    [Fact]
    public async Task Ссылка_в_строке_таблицы_тоже_проверяется_и_адрес_называет_строку()
    {
        var (archived, active) = await SeedOrgsAsync();
        var (_, instanceId) = await SeedDocumentAsync("DOC_E");

        var refusal = await Assert.ThrowsAsync<RecordWriteRefusedException>(() => SendAsync(
            new UpdateRequisitesCommand(instanceId, Signers(RefJson(active, "Лютик"), RefJson(archived, "Ромашка")))));

        Assert.Equal("Подписанты[1].Организация", Assert.Single(refusal.Details).Path);
        Assert.Contains("«Подписанты»", refusal.Message);
    }

    // ── Общие данные ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Создание_записи_общих_данных_со_ссылкой_на_архивную_отвергнуто()
    {
        var (archived, _) = await SeedOrgsAsync();
        var holder = await SeedTypeAsync("CD_HOLDER", DocumentTypeKind.Composite, HolderSchema);

        var refusal = await Assert.ThrowsAsync<RecordWriteRefusedException>(() => SendAsync(
            new CreateCommonDataEntryCommand("Договор", holder, Ref("Подрядчик", archived, "Ромашка"),
                CatalogScope.System, null)));

        Assert.Equal(ArchivedRefRule.ArchivedRef, Assert.Single(refusal.Details).Code);
    }

    [Fact]
    public async Task Правка_записи_общих_данных_новую_архивную_ссылку_отвергает_а_стоявшую_оставляет()
    {
        var (archived, active) = await SeedOrgsAsync();
        var holder = await SeedTypeAsync("CD_HOLDER2", DocumentTypeKind.Composite, HolderSchema);
        var entry = await SendAsync(new CreateCommonDataEntryCommand(
            "Договор", holder, Ref("Подрядчик", active, "Лютик"), CatalogScope.System, null));

        await Assert.ThrowsAsync<RecordWriteRefusedException>(() => SendAsync(
            new UpdateCommonDataEntryCommand(entry.Id, "Договор", Ref("Подрядчик", archived, "Ромашка"), TestAccess.All)));

        await ArchiveAsync(active);
        var saved = await SendAsync(new UpdateCommonDataEntryCommand(
            entry.Id, "Договор (правка)", Ref("Подрядчик", active, "Лютик"), TestAccess.All));
        Assert.Equal("Договор (правка)", saved.DisplayName);
    }

    // ── Документ качества и порт модуля ───────────────────────────────────────

    [Fact]
    public async Task Документ_качества_со_ссылкой_на_архивную_запись_отвергнут()
    {
        var (archived, _) = await SeedOrgsAsync();
        var typeId = await SeedTypeAsync("QD_HOLDER", DocumentTypeKind.Document, HolderSchema);

        await Assert.ThrowsAsync<RecordWriteRefusedException>(() => SendAsync(new CreateQualityDocumentCommand(
            typeId, "Сертификат", Ref("Подрядчик", archived, "Ромашка"),
            CatalogScope.System, null, QualityDocSource.Manual, null, null, null)));
    }

    /// <summary>Модуль получает находку списком, как и находки охраны схемы: решение — его.</summary>
    [Fact]
    public async Task Порт_модуля_возвращает_находку_а_стоявшую_ссылку_пропускает()
    {
        var (archived, _) = await SeedOrgsAsync();
        var typeId = await SeedTypeAsync("MOD_HOLDER", DocumentTypeKind.Composite, HolderSchema);
        var json = Ref("Подрядчик", archived, "Ромашка").RootElement.GetRawText();

        using var scope = fixture.Services.CreateScope();
        var guard = scope.ServiceProvider.GetRequiredService<IModuleWriteGuard>();

        var found = Assert.Single(await guard.RefusalsAsync(typeId, null, json));
        Assert.Equal(ArchivedRefRule.ArchivedRef, found.Code);
        Assert.Empty(await guard.RefusalsAsync(typeId, json, json));
    }

    // ── Машинный путь ─────────────────────────────────────────────────────────

    /// <summary>
    /// Копия документа переносит СТОЯВШИЕ ссылки: человек их не выбирал, и охрану этот путь не зовёт.
    /// Позови он её как создание — «как лежит» было бы пусто, и документ со ссылкой на архивную
    /// запись стал бы некопируемым.
    /// </summary>
    [Fact]
    public async Task Копия_документа_сохраняет_ссылку_на_архивную_запись()
    {
        var (_, active) = await SeedOrgTypeAndEntryAsync("Ромашка");
        var (_, instanceId, setId) = await SeedDocumentWithSetAsync("DOC_COPY");
        await SendAsync(new UpdateRequisitesCommand(instanceId, Ref("Подрядчик", active, "Ромашка")));
        await ArchiveAsync(active);
        var target = await SendAsync(new CreateDocumentSetCommand(
            (await SendAsync(new GetDocumentSetQuery(setId)))!.SectionId, "Второй комплект"));

        var copy = await SendAsync(new CopyDocumentToSetCommand(instanceId, target.Id, CopyStrategy.SmartCleanup));

        Assert.Contains(active.ToString(), copy.Instance.Data.RootElement.GetRawText());
    }

    // ── Посев ─────────────────────────────────────────────────────────────────

    private async Task<Guid> SeedTypeAsync(string code, DocumentTypeKind kind, string schema) =>
        (await SendAsync(new CreateDocumentTypeCommand(code, code, kind, null, JsonDocument.Parse(schema)))).Id;

    private async Task<(Guid TypeId, Guid EntryId)> SeedOrgTypeAndEntryAsync(string name)
    {
        var typeId = await SeedTypeAsync("ORG_" + Guid.NewGuid().ToString("N")[..8], DocumentTypeKind.Composite, OrgSchema);
        var entry = await SendAsync(new CreateCommonDataEntryCommand(
            name, typeId, JsonDocument.Parse("""{"ИНН":"7701"}"""), CatalogScope.System, null));
        return (typeId, entry.Id);
    }

    /// <summary>Две организации: «Ромашка» в архиве и действующий «Лютик».</summary>
    private async Task<(Guid Archived, Guid Active)> SeedOrgsAsync()
    {
        var (typeId, archived) = await SeedOrgTypeAndEntryAsync("Ромашка");
        var active = await SendAsync(new CreateCommonDataEntryCommand(
            "Лютик", typeId, JsonDocument.Parse("""{"ИНН":"7702"}"""), CatalogScope.System, null));
        await ArchiveAsync(archived);
        return (archived, active.Id);
    }

    private async Task ArchiveAsync(Guid id)
    {
        using var scope = fixture.Services.CreateScope();
        Assert.Equal(ArchiveOutcome.Changed,
            await scope.ServiceProvider.GetRequiredService<IRecordArchive>().SetAsync(id, true));
    }

    private async Task<(Guid TypeId, Guid InstanceId)> SeedDocumentAsync(string code)
    {
        var (typeId, instanceId, _) = await SeedDocumentWithSetAsync(code);
        return (typeId, instanceId);
    }

    private async Task<(Guid TypeId, Guid InstanceId, Guid SetId)> SeedDocumentWithSetAsync(string code)
    {
        var typeId = await SeedTypeAsync(code, DocumentTypeKind.Document, HolderSchema);
        var construction = await SendAsync(new CreateConstructionCommand($"Объект {code}", _userId));
        var section = await SendAsync(new CreateSectionCommand(construction.Id, "Раздел"));
        var set = await SendAsync(new CreateDocumentSetCommand(section.Id, "Комплект"));
        var instance = await SendAsync(new AddDocumentToSetCommand(set.Id, typeId));
        return (typeId, instance.Id, set.Id);
    }

    private async Task<T> SendAsync<T>(IRequest<T> request)
    {
        using var scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(request);
    }
}
