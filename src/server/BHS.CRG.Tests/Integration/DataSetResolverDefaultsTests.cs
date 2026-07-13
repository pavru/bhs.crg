using System.Text.Json;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Generation;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// DataSetResolver.InjectAsync + defaultValue незамапленных полей типа материализации (issue #53,
/// часть 2): для табличного (TargetFieldKey) биндинга, замапленного через материализацию источника
/// (issue #19), поля целевого типа без ключа в MaterializeMapping, но с defaultValue схемы, должны
/// попадать в каждый построчный объект — воспроизводит сценарий «Реестр исполнительных схем» / «Схемы».
/// </summary>
[Collection("Integration")]
public class DataSetResolverDefaultsTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly byte[] CsvBytes = System.Text.Encoding.UTF8.GetBytes("A,B\n1,2\n3,4\n");

    private IMediator M(IServiceScope s) => s.ServiceProvider.GetRequiredService<IMediator>();
    private IDataSetService Svc(IServiceScope s) => s.ServiceProvider.GetRequiredService<IDataSetService>();
    private static JsonDocument J(string singleQuoted) => JsonDocument.Parse(singleQuoted.Replace('\'', '"'));

    [Fact]
    public async Task TableBinding_MaterializedRow_UnmappedFieldWithDefault_IsFilled()
    {
        using var scope = fixture.Services.CreateScope();
        var svc = Svc(scope);
        var m = M(scope);

        // Тип строки материализации: «Поле» — из колонки CSV; «ВидДокумента» — НЕ в маппинге, но с
        // defaultValue (воспроизводит fieldOverrides «ВидДокумента» в реальном сценарии).
        var rowType = await m.Send(new CreateDocumentTypeCommand("ROW", "ROW", DocumentTypeKind.Composite, null,
            J("{'fields':[{'key':'Поле','type':'string','required':false},{'key':'ВидДокумента','type':'string','required':false,'defaultValue':'исполнительная схема'}]}")));

        var docType = await m.Send(new CreateDocumentTypeCommand("REESTR", "REESTR", DocumentTypeKind.Document, null, J("{'fields':[]}")));

        var construction = await m.Send(new CreateConstructionCommand("Объект", Guid.NewGuid()));
        var section = await m.Send(new CreateSectionCommand(construction.Id, "Раздел"));
        var set = await m.Send(new CreateDocumentSetCommand(section.Id, "Комплект"));
        var instance = await m.Send(new AddDocumentToSetCommand(set.Id, docType.Id));

        var file = await svc.UploadFileAsync(new UploadFileInput(CsvBytes, "test.csv", "text/csv", "Тест", "System", null), default);
        var candidate = (await svc.DetectSourceCandidatesAsync(file.Id, default)).Single();
        var source = await svc.CreateSourceAsync(file.Id, new CreateSourceInput("Данные", candidate.SheetOrPath, null), default);

        // Материализация источника: маппинг покрывает только «Поле» — «ВидДокумента» намеренно не замаплено.
        await svc.SetMaterializationAsync(source.Id, rowType.Id, new() { ["Поле"] = "A" }, default);

        // Биндинг табличный (TargetFieldKey задан), СВОЙ маппинг пуст — эффективный маппинг берётся
        // с материализации источника (см. EffectiveMappingJson).
        await svc.CreateBindingAsync(new CreateBindingInput(instance.Id, source.Id, "Строки", null), default);

        var inst = await m.Send(new GetDocumentInstanceQuery(instance.Id));
        var resolver = scope.ServiceProvider.GetRequiredService<IEntityResolver>();
        var dataSetResolver = scope.ServiceProvider.GetRequiredService<IDataSetResolver>();

        var view = DocumentView.From(inst!);
        var ctx = await resolver.ResolveAsync(view, default);
        await dataSetResolver.InjectAsync(ctx, view, null, default);

        var rows = (JsonElement)ctx.Data["Строки"]!;
        Assert.Equal(JsonValueKind.Array, rows.ValueKind);
        Assert.Equal(2, rows.GetArrayLength());
        foreach (var row in rows.EnumerateArray())
        {
            Assert.Equal("исполнительная схема", row.GetProperty("ВидДокумента").GetString());
        }
        // Замапленное поле по-прежнему на месте.
        Assert.Equal("1", rows[0].GetProperty("Поле").GetString());
        Assert.Equal("3", rows[1].GetProperty("Поле").GetString());
    }

    [Fact]
    public async Task TableBinding_MaterializedRow_MappedValueWinsOverDefault()
    {
        using var scope = fixture.Services.CreateScope();
        var svc = Svc(scope);
        var m = M(scope);

        // «ВидДокумента» ИМЕЕТ и defaultValue, И явный маппинг (на колонку B) — маппинг должен победить.
        var rowType = await m.Send(new CreateDocumentTypeCommand("ROW2", "ROW2", DocumentTypeKind.Composite, null,
            J("{'fields':[{'key':'ВидДокумента','type':'string','required':false,'defaultValue':'дефолт'}]}")));
        var docType = await m.Send(new CreateDocumentTypeCommand("REESTR2", "REESTR2", DocumentTypeKind.Document, null, J("{'fields':[]}")));

        var construction = await m.Send(new CreateConstructionCommand("Объект", Guid.NewGuid()));
        var section = await m.Send(new CreateSectionCommand(construction.Id, "Раздел"));
        var set = await m.Send(new CreateDocumentSetCommand(section.Id, "Комплект"));
        var instance = await m.Send(new AddDocumentToSetCommand(set.Id, docType.Id));

        var file = await svc.UploadFileAsync(new UploadFileInput(CsvBytes, "test.csv", "text/csv", "Тест", "System", null), default);
        var candidate = (await svc.DetectSourceCandidatesAsync(file.Id, default)).Single();
        var source = await svc.CreateSourceAsync(file.Id, new CreateSourceInput("Данные", candidate.SheetOrPath, null), default);

        await svc.SetMaterializationAsync(source.Id, rowType.Id, new() { ["ВидДокумента"] = "B" }, default);
        await svc.CreateBindingAsync(new CreateBindingInput(instance.Id, source.Id, "Строки", null), default);

        var inst = await m.Send(new GetDocumentInstanceQuery(instance.Id));
        var resolver = scope.ServiceProvider.GetRequiredService<IEntityResolver>();
        var dataSetResolver = scope.ServiceProvider.GetRequiredService<IDataSetResolver>();

        var view = DocumentView.From(inst!);
        var ctx = await resolver.ResolveAsync(view, default);
        await dataSetResolver.InjectAsync(ctx, view, null, default);

        var rows = (JsonElement)ctx.Data["Строки"]!;
        Assert.Equal("2", rows[0].GetProperty("ВидДокумента").GetString()); // из колонки B, не "дефолт"
        Assert.Equal("4", rows[1].GetProperty("ВидДокумента").GetString());
    }
}
