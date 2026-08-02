using System.Text.Json;
using BHS.CRG.Application.Catalog;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Documents;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Приведение значения к типу как исправление аудита (issue #643) — от находки до чистого повтора.
///
/// Проверяется именно связка: находит расхождение аудит (#642), а чинит команда исправлений, и обе
/// должны считать одно и то же одним и тем же. Разойдись они — кнопка «Привести» либо не появилась
/// бы там, где нужно, либо не убирала бы находку, ради которой её нажали.
/// </summary>
[Collection("Integration")]
public class AuditCoerceTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private IMediator Mediator(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<IMediator>();

    /// <summary>Тип с полем «Количество листов» примитива «Цело число» — живой случай из #642.</summary>
    private async Task<(Guid TypeId, Guid InstanceId)> SeedAsync(IServiceScope scope, string storedJson)
    {
        var m = Mediator(scope);
        var prim = await m.Send(new CreatePrimitiveTypeCommand(
            "Цело число", "int", "number", null, JsonDocument.Parse("{\"integer\":true}")));
        var type = await m.Send(new CreateDocumentTypeCommand("Внешний документ", "EXT", DocumentTypeKind.Document, null,
            JsonDocument.Parse($"{{\"fields\":[{{\"key\":\"КоличествоЛистов\",\"title\":\"Количество листов\"," +
                               $"\"type\":\"primitive\",\"typeId\":\"{prim.Id}\"}}]}}")));

        var c = await m.Send(new CreateConstructionCommand("Объект", Guid.NewGuid()));
        var s = await m.Send(new CreateSectionCommand(c.Id, "Раздел"));
        var set = await m.Send(new CreateDocumentSetCommand(s.Id, "Комплект"));
        var inst = await m.Send(new AddDocumentToSetCommand(set.Id, type.Id));
        await m.Send(new UpdateRequisitesCommand(inst.Id, JsonDocument.Parse(storedJson)));
        return (type.Id, inst.Id);
    }

    [Fact]
    public async Task Coerce_TurnsStoredStringIntoNumber_AndAuditGoesClean()
    {
        using var scope = fixture.Services.CreateScope();
        var (typeId, instanceId) = await SeedAsync(scope, "{\"КоличествоЛистов\":\"3\"}");

        var before = await Mediator(scope).Send(new AuditDocumentTypeQuery(typeId));
        var finding = Assert.Single(before.Findings, f => f.Code == SchemaDataAuditor.ValueType);

        var result = await Mediator(scope).Send(new ApplyAuditFixesCommand(
            [new AuditFix(instanceId, "coerce", finding.Path)]));

        Assert.Equal(1, result.Applied);
        // Прежнее значение возвращается в журнале — единственная возможность откатить руками.
        Assert.Equal("\"3\"", Assert.Single(result.Outcomes).OldValue);

        using var after = fixture.Services.CreateScope();
        var report = await Mediator(after).Send(new AuditDocumentTypeQuery(typeId));
        Assert.Empty(report.Findings);

        var inst = await Mediator(after).Send(new GetDocumentInstanceQuery(instanceId));
        Assert.Equal(JsonValueKind.Number, inst!.Data.RootElement.GetProperty("КоличествоЛистов").ValueKind);
        Assert.Equal(3, inst.Data.RootElement.GetProperty("КоличествоЛистов").GetInt32());
    }

    [Fact]
    public async Task Coerce_RefusesFraction_AndLeavesValueAlone()
    {
        using var scope = fixture.Services.CreateScope();
        var (typeId, instanceId) = await SeedAsync(scope, "{\"КоличествоЛистов\":\"2.1\"}");

        var result = await Mediator(scope).Send(new ApplyAuditFixesCommand(
            [new AuditFix(instanceId, "coerce", "КоличествоЛистов")]));

        Assert.Equal(0, result.Applied);
        Assert.Contains("округление придумало бы данные", Assert.Single(result.Outcomes).Reason);

        using var after = fixture.Services.CreateScope();
        var inst = await Mediator(after).Send(new GetDocumentInstanceQuery(instanceId));
        Assert.Equal("2.1", inst!.Data.RootElement.GetProperty("КоличествоЛистов").GetString());
        // Находка остаётся: расхождение никуда не делось, и молчать о нём было бы хуже всего.
        var report = await Mediator(after).Send(new AuditDocumentTypeQuery(typeId));
        Assert.Contains(report.Findings, f => f.Code == SchemaDataAuditor.ValueType);
    }

    [Fact]
    public async Task Coerce_UnknownPath_IsSkippedWithReason()
    {
        using var scope = fixture.Services.CreateScope();
        var (_, instanceId) = await SeedAsync(scope, "{\"КоличествоЛистов\":\"3\"}");

        var result = await Mediator(scope).Send(new ApplyAuditFixesCommand(
            [new AuditFix(instanceId, "coerce", "НетТакогоПоля")]));

        Assert.Equal(0, result.Applied);
        Assert.Contains("не найдено", Assert.Single(result.Outcomes).Reason);
    }
}
