using System.Text;
using System.Text.Json;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Generation;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Infrastructure.DataSets;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Вариант union'а выбирается ПО СТРОКЕ (issue #716).
///
/// Материализация была статична: один вариант на все строки. Но строки источника «Документы
/// комплекта» разнородны — титульный лист, АОСР, реестры, — и каждой нужен свой вариант. Здесь
/// проверяется сквозной путь: правило на источнике → выбор варианта по типу документа строки →
/// корректный union-экземпляр в контексте генерации.
/// </summary>
[Collection("Integration")]
public class DataSetUnionDiscriminatorTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static IMediator M(IServiceScope s) => s.ServiceProvider.GetRequiredService<IMediator>();
    private static IDataSetService Svc(IServiceScope s) => s.ServiceProvider.GetRequiredService<IDataSetService>();
    private static JsonDocument J(string singleQuoted) => JsonDocument.Parse(singleQuoted.Replace('\'', '"'));

    private sealed record Fixtureset(
        Guid SetId, Guid ReestrId, Guid AosrId, Guid WorkRegistryId,
        Guid AosrTypeId, Guid WorkRegistryTypeId, Guid UnionTypeId,
        string AosrCode, string WorkRegistryCode);

    /// <summary>
    /// Комплект с двумя разнородными документами и union-типом «Документ комплекта» из двух
    /// doc-ref-вариантов — уменьшенная копия живого кейса.
    /// </summary>
    private static async Task<Fixtureset> SeedAsync(IServiceScope scope)
    {
        var m = M(scope);
        var aosrCode = $"AOSR{Guid.NewGuid():N}"[..12];
        var registryCode = $"REGW{Guid.NewGuid():N}"[..12];

        var aosrType = await m.Send(new CreateDocumentTypeCommand("АОСР", aosrCode,
            DocumentTypeKind.Document, null, J("{'fields':[{'key':'Номер','type':'string'}]}")));
        var workRegistryType = await m.Send(new CreateDocumentTypeCommand("Реестр работ", registryCode,
            DocumentTypeKind.Document, null, J("{'fields':[{'key':'Номер','type':'string'}]}")));
        var unionType = await m.Send(new CreateDocumentTypeCommand("Документ комплекта", $"UN{Guid.NewGuid():N}"[..12],
            DocumentTypeKind.Composite, null,
            J($"{{'tags':['type.union'],'fields':[" +
              $"{{'key':'АОСР','type':'doc-ref','typeId':'{aosrType.Id}'}}," +
              $"{{'key':'РеестрРабот','type':'doc-ref','typeId':'{workRegistryType.Id}'}}]}}")));
        var reestrType = await m.Send(new CreateDocumentTypeCommand("Сводный реестр", $"SUM{Guid.NewGuid():N}"[..12],
            DocumentTypeKind.Document, null, J("{'fields':[]}")));

        var construction = await m.Send(new CreateConstructionCommand("Объект", Guid.NewGuid()));
        var section = await m.Send(new CreateSectionCommand(construction.Id, "Раздел"));
        var set = await m.Send(new CreateDocumentSetCommand(section.Id, "250701.ЭОМ-1"));

        var aosr = await m.Send(new AddDocumentToSetCommand(set.Id, aosrType.Id));
        await m.Send(new UpdateRequisitesCommand(aosr.Id, J("{'Номер':'17'}")));
        var workRegistry = await m.Send(new AddDocumentToSetCommand(set.Id, workRegistryType.Id));
        await m.Send(new UpdateRequisitesCommand(workRegistry.Id, J("{'Номер':'ЭОМ-1.1'}")));
        var reestr = await m.Send(new AddDocumentToSetCommand(set.Id, reestrType.Id));

        return new Fixtureset(set.Id, reestr.Id, aosr.Id, workRegistry.Id,
            aosrType.Id, workRegistryType.Id, unionType.Id, aosrCode, registryCode);
    }

    private static async Task<Guid> SourceAsync(
        IServiceScope scope, string csv, Guid typeId,
        Dictionary<string, string> mapping, MaterializeDiscriminatorConfig? discriminator)
    {
        var svc = Svc(scope);
        var file = await svc.UploadFileAsync(
            new UploadFileInput(Encoding.UTF8.GetBytes(csv), "docs.csv", "text/csv", "Тест", "System", null), default);
        var candidate = (await svc.DetectSourceCandidatesAsync(file.Id, default)).Single();
        var source = await svc.CreateSourceAsync(file.Id, new CreateSourceInput("Документы комплекта", candidate.SheetOrPath, null), default);
        await svc.SetMaterializationAsync(source.Id, typeId, mapping, discriminator, byIdColumn: null, default);
        return source.Id;
    }

    private static async Task<GenerationContext> ResolveAsync(
        IServiceScope scope, Guid instanceId, List<ResolutionDiagnostic>? diagnostics = null)
    {
        var inst = await M(scope).Send(new GetDocumentInstanceQuery(instanceId));
        var view = DocumentView.From(inst!);
        var entity = scope.ServiceProvider.GetRequiredService<IEntityResolver>();
        var ctx = await entity.ResolveAsync(view);
        await scope.ServiceProvider.GetRequiredService<IDataSetResolver>().InjectAsync(ctx, view, diagnostics, default);
        await entity.ResolveContextRefsAsync(ctx, view.DocumentSetId);
        return ctx;
    }

    /// <summary>
    /// Разнородные строки раскладываются по своим вариантам, и каждый экземпляр несёт РОВНО ОДИН
    /// ключ — иначе это уже не union.
    /// </summary>
    [Fact]
    public async Task RowsAreMaterializedIntoTheirOwnVariants()
    {
        using var scope = fixture.Services.CreateScope();
        var f = await SeedAsync(scope);

        var csv = $"Ид,ТипКод\n{f.AosrId},{f.AosrCode}\n{f.WorkRegistryId},{f.WorkRegistryCode}\n";
        var sourceId = await SourceAsync(scope, csv, f.UnionTypeId,
            new Dictionary<string, string> { ["АОСР"] = "Ид", ["РеестрРабот"] = "Ид" },
            new MaterializeDiscriminatorConfig("ТипКод", MaterializeDiscriminatorConfig.ByTypeCode,
                new Dictionary<string, List<Guid>>
                {
                    ["АОСР"] = [f.AosrTypeId],
                    ["РеестрРабот"] = [f.WorkRegistryTypeId],
                }));
        await Svc(scope).CreateBindingAsync(new CreateBindingInput(f.ReestrId, sourceId, "Состав", null), default);

        var ctx = await ResolveAsync(scope, f.ReestrId);
        var rows = ((JsonElement)ctx.Data["Состав"]!).EnumerateArray().ToList();

        Assert.Equal(2, rows.Count);
        Assert.Equal("АОСР", Assert.Single(rows[0].EnumerateObject()).Name);
        Assert.Equal("РеестрРабот", Assert.Single(rows[1].EnumerateObject()).Name);
        // И ссылки разрешены: в шаблон приходят реквизиты самих документов.
        Assert.Equal("17", rows[0].GetProperty("АОСР").GetProperty("Номер").GetString());
        Assert.Equal("ЭОМ-1.1", rows[1].GetProperty("РеестрРабот").GetProperty("Номер").GetString());
    }

    /// <summary>
    /// Строка, которой не досталось варианта, не превращается в пустой объект: её в результате нет
    /// вовсе, а о пропуске сказано ОДНОЙ сводкой. Сотня одинаковых предупреждений на реестр из сотни
    /// документов означала бы, что не видно ни одного.
    /// </summary>
    [Fact]
    public async Task RowsWithoutVariant_AreSkipped_AndSummarisedOnce()
    {
        using var scope = fixture.Services.CreateScope();
        var f = await SeedAsync(scope);

        // Вариант «РеестрРабот» выключен (правила нет), плюс строка с нераспознанным кодом.
        var csv = $"Ид,ТипКод\n{f.AosrId},{f.AosrCode}\n{f.WorkRegistryId},{f.WorkRegistryCode}\n{f.AosrId},НетТакого\n";
        var sourceId = await SourceAsync(scope, csv, f.UnionTypeId,
            new Dictionary<string, string> { ["АОСР"] = "Ид" },
            new MaterializeDiscriminatorConfig("ТипКод", MaterializeDiscriminatorConfig.ByTypeCode,
                new Dictionary<string, List<Guid>> { ["АОСР"] = [f.AosrTypeId] }));
        await Svc(scope).CreateBindingAsync(new CreateBindingInput(f.ReestrId, sourceId, "Состав", null), default);

        var diagnostics = new List<ResolutionDiagnostic>();
        var ctx = await ResolveAsync(scope, f.ReestrId, diagnostics);

        var rows = ((JsonElement)ctx.Data["Состав"]!).EnumerateArray().ToList();
        Assert.Single(rows);

        var summary = Assert.Single(diagnostics, d => d.Message.Contains("пропущено"));
        Assert.Equal(DiagnosticSeverity.Warning, summary.Severity);
        Assert.Contains("2", summary.Message);
        Assert.Contains("не назначен вариант", summary.Message);
        Assert.Contains("код типа документа не найден", summary.Message);
    }

    /// <summary>Признак может быть и колонкой с Ид документа — тип берётся у него самого.</summary>
    [Fact]
    public async Task DiscriminatorByDocumentId_Works()
    {
        using var scope = fixture.Services.CreateScope();
        var f = await SeedAsync(scope);

        var csv = $"Ид\n{f.AosrId}\n{f.WorkRegistryId}\n";
        var sourceId = await SourceAsync(scope, csv, f.UnionTypeId,
            new Dictionary<string, string> { ["АОСР"] = "Ид", ["РеестрРабот"] = "Ид" },
            new MaterializeDiscriminatorConfig("Ид", MaterializeDiscriminatorConfig.ByDocumentId,
                new Dictionary<string, List<Guid>>
                {
                    ["АОСР"] = [f.AosrTypeId],
                    ["РеестрРабот"] = [f.WorkRegistryTypeId],
                }));
        await Svc(scope).CreateBindingAsync(new CreateBindingInput(f.ReestrId, sourceId, "Состав", null), default);

        var rows = ((JsonElement)(await ResolveAsync(scope, f.ReestrId)).Data["Состав"]!).EnumerateArray().ToList();

        Assert.Equal("АОСР", Assert.Single(rows[0].EnumerateObject()).Name);
        Assert.Equal("РеестрРабот", Assert.Single(rows[1].EnumerateObject()).Name);
    }

    /// <summary>
    /// Собственный маппинг привязки замещает материализацию ЦЕЛИКОМ — вместе с правилом. Плоский
    /// маппинг построчной вариативности не выражает, и оставить правило в силе значило бы применять
    /// его к чужим ключам.
    /// </summary>
    [Fact]
    public async Task OwnBindingMapping_TurnsTheDiscriminatorOff()
    {
        using var scope = fixture.Services.CreateScope();
        var f = await SeedAsync(scope);

        var csv = $"Ид,ТипКод\n{f.AosrId},{f.AosrCode}\n{f.WorkRegistryId},{f.WorkRegistryCode}\n";
        var sourceId = await SourceAsync(scope, csv, f.UnionTypeId,
            new Dictionary<string, string> { ["АОСР"] = "Ид", ["РеестрРабот"] = "Ид" },
            new MaterializeDiscriminatorConfig("ТипКод", MaterializeDiscriminatorConfig.ByTypeCode,
                new Dictionary<string, List<Guid>>
                {
                    ["АОСР"] = [f.AosrTypeId],
                    ["РеестрРабот"] = [f.WorkRegistryTypeId],
                }));
        // Свой маппинг привязки — плоский, на одну колонку.
        await Svc(scope).CreateBindingAsync(new CreateBindingInput(
            f.ReestrId, sourceId, "Состав", new Dictionary<string, string> { ["АОСР"] = "Ид" }), default);

        var rows = ((JsonElement)(await ResolveAsync(scope, f.ReestrId)).Data["Состав"]!).EnumerateArray().ToList();

        // Правило не применялось: обе строки легли в единственный ключ собственного маппинга.
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal("АОСР", Assert.Single(r.EnumerateObject()).Name));
    }

    /// <summary>
    /// Предпросмотр называет вариант каждой строки и перечисляет пропущенные ПОИМЁННО: его открывают,
    /// чтобы понять, какие документы не доехали, а сводка числом на это не отвечает.
    /// </summary>
    [Fact]
    public async Task Preview_ReportsVariantPerRow_AndSkippedRows()
    {
        using var scope = fixture.Services.CreateScope();
        var f = await SeedAsync(scope);

        var csv = $"Ид,ТипКод\n{f.AosrId},{f.AosrCode}\n{f.WorkRegistryId},{f.WorkRegistryCode}\n";
        var sourceId = await SourceAsync(scope, csv, f.UnionTypeId,
            new Dictionary<string, string> { ["АОСР"] = "Ид" },
            new MaterializeDiscriminatorConfig("ТипКод", MaterializeDiscriminatorConfig.ByTypeCode,
                new Dictionary<string, List<Guid>> { ["АОСР"] = [f.AosrTypeId] }));

        var preview = await Svc(scope).MaterializePreviewAsync(sourceId, 50, null, null, null, null, default);

        Assert.NotNull(preview);
        Assert.Null(preview!.Error);
        Assert.Equal(["АОСР"], preview.Variants);
        var skippedRow = Assert.Single(preview.Skipped!);
        Assert.Equal(2, skippedRow.RowNumber);
        Assert.Equal(f.WorkRegistryCode, skippedRow.Value);
        Assert.Equal(MaterializeSkipReason.NoVariant, skippedRow.ReasonCode);
    }

    /// <summary>
    /// Предпросмотр ПРИВЯЗКИ (вкладка «Наборы данных» у документа) обязан показывать то же, что
    /// получится при генерации.
    ///
    /// Экран этот открывают, чтобы понять, почему поле выглядит не так. Показывать в нём все
    /// варианты сразу и полный список строк значило бы отвечать на этот вопрос неправдой: в
    /// документ уедет один ключ на строку, а часть строк не уедет вовсе.
    /// </summary>
    [Fact]
    public async Task BindingPreview_AppliesTheSameVariantChoiceAsGeneration()
    {
        using var scope = fixture.Services.CreateScope();
        var f = await SeedAsync(scope);

        var csv = $"Ид,ТипКод\n{f.AosrId},{f.AosrCode}\n{f.WorkRegistryId},{f.WorkRegistryCode}\n";
        var sourceId = await SourceAsync(scope, csv, f.UnionTypeId,
            new Dictionary<string, string> { ["АОСР"] = "Ид" },
            new MaterializeDiscriminatorConfig("ТипКод", MaterializeDiscriminatorConfig.ByTypeCode,
                new Dictionary<string, List<Guid>> { ["АОСР"] = [f.AosrTypeId] }));
        await Svc(scope).CreateBindingAsync(new CreateBindingInput(f.ReestrId, sourceId, "Состав", null), default);

        var preview = Assert.Single(await Svc(scope).PreviewBindingsAsync(f.ReestrId, default));
        var rows = Assert.IsType<List<Dictionary<string, object?>>>(preview.Data);

        // Показана одна строка — ровно та, что доедет до документа.
        Assert.Equal("АОСР", Assert.Single(Assert.Single(rows)).Key);
        // И о недоехавшей сказано, а не спрятано в «строк меньше, чем в источнике».
        Assert.Contains("пропущено", preview.Error);
    }

    /// <summary>
    /// Диалог, переключённый в «один вариант на все строки», получает предпросмотр БЕЗ правила —
    /// даже если на источнике оно сохранено.
    ///
    /// Иначе предпросмотр отвечает по вчерашней настройке ровно тогда, когда её меняют: строки
    /// чужих типов приходили бы пустыми, а часть — в «пропущено», хотя после сохранения ничего
    /// этого не будет. Признак «настройку ведёт диалог» — переданный маппинг.
    /// </summary>
    [Fact]
    public async Task MaterializePreview_WithMappingButNoRule_IgnoresTheSavedRule()
    {
        using var scope = fixture.Services.CreateScope();
        var f = await SeedAsync(scope);

        var csv = $"Ид,ТипКод\n{f.AosrId},{f.AosrCode}\n{f.WorkRegistryId},{f.WorkRegistryCode}\n";
        var sourceId = await SourceAsync(scope, csv, f.UnionTypeId,
            new Dictionary<string, string> { ["АОСР"] = "Ид" },
            new MaterializeDiscriminatorConfig("ТипКод", MaterializeDiscriminatorConfig.ByTypeCode,
                new Dictionary<string, List<Guid>> { ["АОСР"] = [f.AosrTypeId] }));

        var preview = await Svc(scope).MaterializePreviewAsync(
            sourceId, 50, f.UnionTypeId,
            new Dictionary<string, string> { ["АОСР"] = "Ид" },
            discriminator: null, byIdColumn: null, default);

        Assert.NotNull(preview);
        // Правило не применялось: обе строки на месте, пропущенных нет, варианта у строк нет.
        Assert.Equal(2, preview!.Rows.Count);
        Assert.Empty(preview.Skipped!);
        Assert.All(preview.Variants!, Assert.Null);
    }

    /// <summary>Настройка, которую валидатор не должен пропустить, до источника не доезжает.</summary>
    [Fact]
    public async Task ContradictoryConfiguration_IsRejectedOnSave()
    {
        using var scope = fixture.Services.CreateScope();
        var f = await SeedAsync(scope);

        await Assert.ThrowsAsync<InvalidRequestException>(() => SourceAsync(scope, "Ид,ТипКод\n1,2\n", f.UnionTypeId,
            new Dictionary<string, string> { ["АОСР"] = "Ид", ["РеестрРабот"] = "Ид" },
            new MaterializeDiscriminatorConfig("ТипКод", MaterializeDiscriminatorConfig.ByTypeCode,
                new Dictionary<string, List<Guid>>
                {
                    ["АОСР"] = [f.AosrTypeId],
                    ["РеестрРабот"] = [f.AosrTypeId],   // тот же тип у двух вариантов
                })));
    }
}
