using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Templates;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Tests.Support;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Поведение генерации, предпросмотра и проверки документа, которое обязано пережить их переход на
/// общую сборку контекста (change core-generation-input-one-path, D1): у каждого из трёх свои правила
/// остановки, и общий код не должен их уравнять.
/// </summary>
[Collection("Integration")]
public class GenerationBehaviourRegressionTests(IntegrationTestFixture fixture)
{
    /// <summary>Документ с незаполненным обязательным полем и числом, записанным словом.</summary>
    private async Task<Guid> SeedAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var scope = fixture.Services.CreateScope();
        var m = scope.ServiceProvider.GetRequiredService<IMediator>();

        var docType = await m.Send(new CreateDocumentTypeCommand(
            $"Протокол {suffix}", $"RGR{suffix}"[..11], DocumentTypeKind.Document, null,
            JsonDocument.Parse("""
                { "fields": [
                    { "key": "Номер", "type": "string", "required": true },
                    { "key": "Количество", "type": "number" } ] }
                """)));
        await m.Send(new CreateTemplateCommand(docType.Id, $"Шаблон {suffix}", """
            #import "systemlib.typ": *
            #let data = json("data.json")
            = Протокол № #data.at("Номер", default: "—")
            Количество: #data.at("Количество", default: "—")
            """));

        var construction = await m.Send(new CreateConstructionCommand($"Стройка {suffix}", Guid.NewGuid()));
        var section = await m.Send(new CreateSectionCommand(construction.Id, $"Раздел {suffix}"));
        var set = await m.Send(new CreateDocumentSetCommand(section.Id, $"Комплект {suffix}"));
        var instance = await m.Send(new AddDocumentToSetCommand(set.Id, docType.Id));
        await m.Send(new UpdateRequisitesCommand(instance.Id, JsonDocument.Parse("""{ "Количество": "много" }""")));
        return instance.Id;
    }

    private Task<HttpClient> ClientAsync() => GrantedSignIn.WithPermissionsAsync(fixture, "id.document.generate");

    /// <summary>
    /// Предпросмотр незаконченного документа рисуется: полноту и типы значений он не проверяет —
    /// иначе панель предпросмотра гасла бы на каждом незаполненном поле.
    /// </summary>
    [Fact]
    public async Task Preview_OfUnfinishedDocument_IsRendered()
    {
        TypstRequired.Ensure();
        var id = await SeedAsync();
        var client = await ClientAsync();

        var response = await client.PostAsJsonAsync($"/api/generate/preview/{id}", new { Количество = "много" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>Проверка документа называет тот же набор кодов и путей, что до перехода.</summary>
    [Fact]
    public async Task Validate_ReturnsTheSameDiagnostics()
    {
        var id = await SeedAsync();
        var client = await ClientAsync();

        var diagnostics = await client.GetFromJsonAsync<JsonElement>($"/api/generate/validate/{id}");

        var found = diagnostics.EnumerateArray()
            .Select(d => $"{d.GetProperty("severity").GetString()} {d.GetProperty("code").GetString()} {d.GetProperty("path").GetString()}")
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(Snapshot, found);
    }

    /// <summary>Снимок на сиде, снятый до перехода (master 422d94bd): полнота — ошибкой, тип значения —
    /// предупреждением (issue #461).</summary>
    private static readonly string[] Snapshot = ["error missing-required Номер", "warning value-type Количество"];

    /// <summary>Генерация с незаполненным обязательным полем — отказ 422 с кодом и путём поля.</summary>
    [Fact]
    public async Task Generate_WithMissingRequired_Is422()
    {
        var id = await SeedAsync();
        var client = await ClientAsync();

        var response = await client.PostAsJsonAsync($"/api/generate/{id}", new { format = "pdf" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(body.GetProperty("diagnostics").EnumerateArray(),
            d => d.GetProperty("code").GetString() == "missing-required" && d.GetProperty("path").GetString() == "Номер");
    }
}
