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
}
