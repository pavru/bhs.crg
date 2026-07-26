using System.Text.Json;
using BHS.CRG.Api.Mcp;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Documents;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Диагностика документа для внешнего агента (issue #425). Проверка в системе уже была — важно, что
/// агент получает ЕЁ, а не собственные догадки о том, что с документом не так.
/// </summary>
[Collection("Integration")]
public class DocumentValidationMcpTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static DocumentActionTools Tools(IServiceScope s) => new(
        s.ServiceProvider.GetRequiredService<IMediator>(),
        s.ServiceProvider.GetRequiredService<IHttpContextAccessor>());

    private static async Task<Guid> SeedDocumentAsync(IMediator m, string schema)
    {
        var type = await m.Send(new CreateDocumentTypeCommand(
            "Акт", $"ACT_{Guid.NewGuid():N}"[..12], DocumentTypeKind.Document, null,
            JsonDocument.Parse(schema)));
        var construction = await m.Send(new CreateConstructionCommand("ДНС Сити", Guid.NewGuid()));
        var section = await m.Send(new CreateSectionCommand(construction.Id, "ЭОМ"));
        var set = await m.Send(new CreateDocumentSetCommand(section.Id, "ЭОМ-1"));
        return (await m.Send(new AddDocumentToSetCommand(set.Id, type.Id))).Id;
    }

    [Fact]
    public async Task Validate_ReportsMissingRequiredField_WithPathAndCode()
    {
        using var scope = fixture.Services.CreateScope();
        var m = scope.ServiceProvider.GetRequiredService<IMediator>();

        var docId = await SeedDocumentAsync(m, """
            {"fields":[{"key":"НомерАкта","title":"Номер акта","type":"string","required":true}]}
            """);

        var result = await Tools(scope).ValidateDocumentAsync(docId, CancellationToken.None);

        Assert.Equal(docId, result.DocumentId);
        Assert.Equal(1, result.ErrorCount);
        var d = Assert.Single(result.Diagnostics);
        Assert.Equal("Error", d.Severity);
        Assert.Equal("НомерАкта", d.Path);
        // Код различает вид проблемы — по нему агент отличает «не заполнено» от «ссылка битая».
        Assert.Equal("missing-required", d.Code);
    }

    /// <summary>
    /// Проверка и выпуск обязаны отвечать на один вопрос одинаково: раньше ValueTypeScanner был
    /// подключён только к проверке, и человек выпускал документ, о расхождениях в котором его
    /// предупреждали в другом месте (#464).
    /// </summary>
    [Fact]
    public async Task Generation_SeesSameValueTypeWarnings_AsValidation()
    {
        using var scope = fixture.Services.CreateScope();
        var m = scope.ServiceProvider.GetRequiredService<IMediator>();

        var primitives = scope.ServiceProvider
            .GetRequiredService<IRepository<BHS.CRG.Domain.Catalog.PrimitiveType>>();
        var integer = BHS.CRG.Domain.Catalog.PrimitiveType.Create(
            "Цело число", "Integer_" + Guid.NewGuid().ToString("N")[..6], "number", null,
            JsonDocument.Parse("""{"integer":true}"""));
        await primitives.AddAsync(integer);
        await primitives.SaveChangesAsync();

        var docId = await SeedDocumentAsync(m, $$"""
            {"fields":[{"key":"Номер","title":"Номер","type":"primitive","typeId":"{{integer.Id}}"}]}
            """);
        await m.Send(new UpdateRequisitesCommand(docId, JsonDocument.Parse("""{"Номер":"2.1"}""")));

        var validation = await Tools(scope).ValidateDocumentAsync(docId, CancellationToken.None);
        Assert.Equal(0, validation.ErrorCount);
        var warning = Assert.Single(validation.Diagnostics);
        Assert.Equal("value-type", warning.Code);

        // Предупреждение выпуск НЕ блокирует — обязательность и типы разные вещи, данные накоплены.
        Assert.Equal(0, validation.ErrorCount);
    }

    [Fact]
    public async Task Validate_CleanDocument_HasNoDiagnostics()
    {
        using var scope = fixture.Services.CreateScope();
        var m = scope.ServiceProvider.GetRequiredService<IMediator>();

        var docId = await SeedDocumentAsync(m, """
            {"fields":[{"key":"НомерАкта","title":"Номер акта","type":"string","required":true}]}
            """);
        await m.Send(new UpdateRequisitesCommand(docId, JsonDocument.Parse("""{"НомерАкта":"12"}""")));

        var result = await Tools(scope).ValidateDocumentAsync(docId, CancellationToken.None);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(0, result.ErrorCount);
        Assert.Equal(0, result.WarningCount);
    }
}
