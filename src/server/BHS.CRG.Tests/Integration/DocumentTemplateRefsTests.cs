using System.Text.Json;
using System.Text.Json.Nodes;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Templates;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Documents;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Ссылки документа на шаблоны (issue #1960): выбор для генерации, одиночный пин и ключи параметров
/// принимают только шаблоны ТИПА документа, чужой id — отказ 400 (а не «сохранено» без него), удаление
/// шаблона уносит и его параметры.
/// </summary>
[Collection("Integration")]
public class DocumentTemplateRefsTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private readonly Guid _userId = Guid.NewGuid();

    private async Task<T> SendAsync<T>(IRequest<T> request)
    {
        using var scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(request);
    }

    private async Task SendAsync(IRequest request)
    {
        using var scope = fixture.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IMediator>().Send(request);
    }

    private async Task<(Guid DocId, Guid A, Guid B, Guid Foreign)> ArrangeAsync()
    {
        var dt = await SendAsync(new CreateDocumentTypeCommand(
            "DT_REFS", "DT_REFS", DocumentTypeKind.Document, null, JsonDocument.Parse(@"{""fields"":[]}")));
        var other = await SendAsync(new CreateDocumentTypeCommand(
            "DT_OTHER", "DT_OTHER", DocumentTypeKind.Document, null, JsonDocument.Parse(@"{""fields"":[]}")));
        var a = await SendAsync(new CreateTemplateCommand(dt.Id, "А", "content"));
        var b = await SendAsync(new CreateTemplateCommand(dt.Id, "Б", "content"));
        var foreign = await SendAsync(new CreateTemplateCommand(other.Id, "Чужой", "content"));

        var c = await SendAsync(new CreateConstructionCommand("Объект", _userId));
        var s = await SendAsync(new CreateSectionCommand(c.Id, "Раздел"));
        var set = await SendAsync(new CreateDocumentSetCommand(s.Id, "Комплект"));
        var doc = await SendAsync(new AddDocumentToSetCommand(set.Id, dt.Id));
        return (doc.Id, a.Id, b.Id, foreign.Id);
    }

    private async Task<(Guid? Id, string? Ids, string? Params)> RefsAsync(Guid docId)
    {
        using var scope = fixture.Services.CreateScope();
        var o = (await scope.ServiceProvider.GetRequiredService<IDomainObjectRepository>().GetByIdAsync(docId))!;
        return (o.TemplateId, o.TemplateIds, o.TemplateParams);
    }

    private static string Ids(params Guid[] ids) => JsonSerializer.Serialize(ids);

    // jsonb хранит JSON по-своему (пробелы, порядок ключей) — сравниваем по смыслу, а не по строке.
    private static Guid[]? Stored(string? json) => json is null ? null : JsonSerializer.Deserialize<Guid[]>(json);
    private static void AssertJson(string expected, string? actual)
        => Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), actual is null ? null : JsonNode.Parse(actual)),
            $"ожидалось {expected}, записано {actual}");

    [Fact]
    public async Task Selection_OfTypeTemplates_IsStored_InOrderWithoutRepeats()
    {
        var (doc, a, b, _) = await ArrangeAsync();

        await SendAsync(new SetDocumentTemplatesCommand(doc, Ids(b, a, b)));

        Assert.Equal([b, a], Stored((await RefsAsync(doc)).Ids));
    }

    [Fact]
    public async Task Selection_EmptyArray_IsStoredAsNull()
    {
        var (doc, a, _, _) = await ArrangeAsync();
        await SendAsync(new SetDocumentTemplatesCommand(doc, Ids(a)));

        await SendAsync(new SetDocumentTemplatesCommand(doc, "[]"));

        Assert.Null((await RefsAsync(doc)).Ids);
    }

    [Theory]
    [InlineData("foreign")]  // шаблон другого типа
    [InlineData("missing")]  // шаблона нет вовсе — так и жили висячие ссылки
    public async Task Selection_WithForeignId_IsRefused_AndKeepsPrevious(string kind)
    {
        var (doc, a, _, foreign) = await ArrangeAsync();
        await SendAsync(new SetDocumentTemplatesCommand(doc, Ids(a)));
        var bad = kind == "foreign" ? foreign : Guid.NewGuid();

        var ex = await Assert.ThrowsAsync<InvalidRequestException>(
            () => SendAsync(new SetDocumentTemplatesCommand(doc, Ids(a, bad))));

        Assert.Contains(bad.ToString(), ex.Message);
        Assert.Equal([a], Stored((await RefsAsync(doc)).Ids)); // прежний выбор не тронут
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[\"не-id\"]")]
    public async Task Selection_NotAnArrayOfIds_IsRefused(string body)
    {
        var (doc, _, _, _) = await ArrangeAsync();

        await Assert.ThrowsAsync<InvalidRequestException>(() => SendAsync(new SetDocumentTemplatesCommand(doc, body)));
    }

    [Fact]
    public async Task SinglePin_ForeignTemplate_IsRefused()
    {
        var (doc, a, _, foreign) = await ArrangeAsync();

        await SendAsync(new SetDocumentTemplateCommand(doc, a));
        await Assert.ThrowsAsync<InvalidRequestException>(() => SendAsync(new SetDocumentTemplateCommand(doc, foreign)));

        Assert.Equal(a, (await RefsAsync(doc)).Id);
    }

    [Fact]
    public async Task Params_KeyedByTypeTemplate_AreStored()
    {
        var (doc, a, _, _) = await ArrangeAsync();
        var json = $"{{\"{a}\":{{\"x\":1}}}}";

        await SendAsync(new SetDocumentTemplateParamsCommand(doc, json));

        AssertJson(json, (await RefsAsync(doc)).Params);
    }

    [Theory]
    [InlineData("missing")] // ключ — шаблон, которого нет
    [InlineData("flat")]    // старый плоский вид {имя:значение}: генерация его не читает
    public async Task Params_WithForeignKey_AreRefused(string kind)
    {
        var (doc, a, _, _) = await ArrangeAsync();
        var json = kind == "flat"
            ? "{\"x\":1}"
            : $"{{\"{a}\":{{\"x\":1}},\"{Guid.NewGuid()}\":{{\"y\":2}}}}";

        await Assert.ThrowsAsync<InvalidRequestException>(() => SendAsync(new SetDocumentTemplateParamsCommand(doc, json)));

        Assert.Null((await RefsAsync(doc)).Params);
    }

    [Fact]
    public async Task DeleteTemplate_DropsItsParams_EvenWithoutPin()
    {
        var (doc, a, b, _) = await ArrangeAsync();
        await SendAsync(new SetDocumentTemplateParamsCommand(doc, $"{{\"{a}\":{{\"x\":1}},\"{b}\":{{\"y\":2}}}}"));

        await SendAsync(new DeleteTemplateCommand(a)); // документ на «А» не запиннут — удаление не спрашивает

        var refs = await RefsAsync(doc);
        AssertJson($"{{\"{b}\":{{\"y\":2}}}}", refs.Params);
    }

    [Fact]
    public async Task DeleteTemplate_LastParams_LeaveNull()
    {
        var (doc, a, _, _) = await ArrangeAsync();
        await SendAsync(new SetDocumentTemplateParamsCommand(doc, $"{{\"{a}\":{{\"x\":1}}}}"));

        await SendAsync(new DeleteTemplateCommand(a));

        Assert.Null((await RefsAsync(doc)).Params);
    }

    // ── Погашенная версия (ревью PR #1963) ─────────────────────────────────────

    private async Task<Guid> RetireAsync(Guid templateId)
        => (await SendAsync(new UpdateTemplateCommand(templateId, "content v2"))).Template.Id;

    private async Task StoreSelectionAsync(Guid docId, string json)
    {
        using var scope = fixture.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDomainObjectRepository>();
        var o = (await repo.GetByIdAsync(docId))!;
        o.SetTemplateIds(json);
        await repo.SaveChangesAsync();
    }

    [Fact]
    public async Task Selection_NewRetiredVersion_IsRefused()
    {
        // Страница со старым списком показывает v1 действующей; щелчок по ней — отказ, а не «сохранено»,
        // которое генерация потом пропустит.
        var (doc, a, b, _) = await ArrangeAsync();
        await RetireAsync(a);

        var ex = await Assert.ThrowsAsync<InvalidRequestException>(
            () => SendAsync(new SetDocumentTemplatesCommand(doc, Ids(b, a))));

        Assert.Contains("не действует", ex.Message);
        Assert.Contains(a.ToString(), ex.Message);
        Assert.Null((await RefsAsync(doc)).Ids);
    }

    [Fact]
    public async Task Selection_AlreadyStoredRetiredVersion_DoesNotLockTheDocument()
    {
        // Давняя погашенная в выборе остаётся, пока существует: иначе любая правка выбора упиралась бы в 400.
        var (doc, a, b, _) = await ArrangeAsync();
        await RetireAsync(a);
        await StoreSelectionAsync(doc, Ids(a));

        await SendAsync(new SetDocumentTemplatesCommand(doc, Ids(a, b)));

        Assert.Equal([a, b], Stored((await RefsAsync(doc)).Ids));
    }

    [Fact]
    public async Task Params_NewKeyOfRetiredVersion_IsRefused()
    {
        var (doc, a, _, _) = await ArrangeAsync();
        await RetireAsync(a);

        await Assert.ThrowsAsync<InvalidRequestException>(
            () => SendAsync(new SetDocumentTemplateParamsCommand(doc, $"{{\"{a}\":{{\"x\":1}}}}")));
    }

    // ── Вид ключа и повтор (ревью PR #1963) ────────────────────────────────────

    [Fact]
    public async Task Params_UpperCaseKey_IsStoredCanonical()
    {
        // Генерация ищет ключ строчными: «AAAA…» был бы «сохранён» и не действовал.
        var (doc, a, _, _) = await ArrangeAsync();

        await SendAsync(new SetDocumentTemplateParamsCommand(doc, $"{{\"{a.ToString().ToUpperInvariant()}\":{{\"x\":1}}}}"));

        AssertJson($"{{\"{a}\":{{\"x\":1}}}}", (await RefsAsync(doc)).Params);
    }

    [Theory]
    [InlineData("N")] // 32 цифры без дефисов
    [InlineData("B")] // в фигурных скобках
    public async Task Params_KeyInOtherGuidFormat_IsRefused(string format)
    {
        var (doc, a, _, _) = await ArrangeAsync();

        await Assert.ThrowsAsync<InvalidRequestException>(
            () => SendAsync(new SetDocumentTemplateParamsCommand(doc, $"{{\"{a.ToString(format)}\":{{\"x\":1}}}}")));
    }

    [Fact]
    public async Task Params_DuplicateKey_IsRefusedAs400_NotAs500()
    {
        var (doc, a, _, _) = await ArrangeAsync();

        await Assert.ThrowsAsync<InvalidRequestException>(
            () => SendAsync(new SetDocumentTemplateParamsCommand(doc, $"{{\"{a}\":{{}},\"{a}\":{{\"x\":1}}}}")));
    }

    [Fact]
    public async Task SinglePin_HistoricVersion_IsAccepted()
    {
        // Одиночный пин генерация берёт и на исторической версии — для него правило «новая — действующая» не стоит.
        var (doc, a, _, _) = await ArrangeAsync();
        await RetireAsync(a);

        await SendAsync(new SetDocumentTemplateCommand(doc, a));

        Assert.Equal(a, (await RefsAsync(doc)).Id);
    }

    [Fact]
    public async Task Params_EmptyObject_IsStoredAsNull()
    {
        // Клиент шлёт «нет переопределений» пустым объектом: без тела адрес отвечал 415.
        var (doc, a, _, _) = await ArrangeAsync();
        await SendAsync(new SetDocumentTemplateParamsCommand(doc, $"{{\"{a}\":{{\"x\":1}}}}"));

        await SendAsync(new SetDocumentTemplateParamsCommand(doc, "{}"));

        Assert.Null((await RefsAsync(doc)).Params);
    }
}
