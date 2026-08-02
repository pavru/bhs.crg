using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.DataSnapshots;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Generation;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Domain.Documents;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Системный набор данных (issue #580): сырьё — не файл, а данные самой системы. Проверяется
/// сквозной путь консолидации «Документы комплекта»: кандидат → источник → строки → привязка →
/// контекст генерации, — и то, что у такого набора нет файловых операций.
/// </summary>
[Collection("Integration")]
public class SystemDataSetTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static IDataSetService Svc(IServiceScope s) => s.ServiceProvider.GetRequiredService<IDataSetService>();
    private static IMediator M(IServiceScope s) => s.ServiceProvider.GetRequiredService<IMediator>();
    private static JsonDocument J(string singleQuoted) => JsonDocument.Parse(singleQuoted.Replace('\'', '"'));

    /// <summary>Комплект с двумя документами: у одного заполнены тэгированные реквизиты, у второго нет имени.</summary>
    private static async Task<(Guid setId, Guid namedId, Guid unnamedId, string typeName)> SeedSetAsync(IServiceScope scope)
    {
        var m = M(scope);

        // Тэги, а не имена полей: провайдер обязан находить номер/дату/листы в чужой схеме.
        var docType = await m.Send(new CreateDocumentTypeCommand("АОСР", "AOSR", DocumentTypeKind.Document, null,
            J("""
              {'fields':[
                {'key':'Номер','type':'string','required':false,'tags':['doc.number']},
                {'key':'Дата','type':'date','required':false,'tags':['doc.date']},
                {'key':'Листов','type':'number','required':false,'tags':['doc.pageCount']}
              ]}
              """)));

        var construction = await m.Send(new CreateConstructionCommand("Объект", Guid.NewGuid()));
        var section = await m.Send(new CreateSectionCommand(construction.Id, "ЭОМ"));
        var set = await m.Send(new CreateDocumentSetCommand(section.Id, "Комплект 1"));

        var named = await m.Send(new AddDocumentToSetCommand(set.Id, docType.Id));
        await m.Send(new RenameDocumentInstanceCommand(named.Id, "АОСР №1"));
        await m.Send(new UpdateRequisitesCommand(named.Id,
            J("{'Номер':'7И-СОТВ 1','Дата':'2026-05-06','Листов':4}")));

        var unnamed = await m.Send(new AddDocumentToSetCommand(set.Id, docType.Id));

        return (set.Id, named.Id, unnamed.Id, docType.Name);
    }

    [Fact]
    public async Task SetDocuments_Candidate_BecomesSourceWithLiveRows()
    {
        using var scope = fixture.Services.CreateScope();
        var svc = Svc(scope);
        var (setId, namedId, _, typeName) = await SeedSetAsync(scope);

        var file = await svc.CreateSystemFileAsync(new CreateSystemFileInput("Set", setId.ToString(), null), default);

        var candidate = Assert.Single(await svc.DetectSourceCandidatesAsync(file.Id, default));
        Assert.Equal("system:set-documents", candidate.SheetOrPath);

        var source = await svc.CreateSourceAsync(file.Id,
            new CreateSourceInput("Документы", candidate.SheetOrPath, null), default);

        var preview = await svc.PreviewSourceAsync(source.Id, 50, default);
        Assert.NotNull(preview);
        Assert.Equal(2, preview.Rows.Count);

        string? Cell(int rowIndex, string column) =>
            preview.Rows[rowIndex][preview.Columns.ToList().IndexOf(column)];

        Assert.Equal("АОСР №1", Cell(0, "Наименование"));
        Assert.Equal("7И-СОТВ 1", Cell(0, "НомерДокумента"));
        Assert.Equal("2026-05-06", Cell(0, "ДатаДокумента"));
        Assert.Equal("4", Cell(0, "КоличествоЛистов"));
        Assert.Equal("Черновик", Cell(0, "Статус"));
        Assert.Equal("1", Cell(0, "НомерПП"));
        Assert.Equal(namedId.ToString(), Cell(0, "Ид"));

        // Документ без своего имени показывается именем типа, а не пустой ячейкой.
        Assert.Equal(typeName, Cell(1, "Наименование"));
    }

    /// <summary>Данные живые: документ, добавленный после создания источника, попадает в строки без «обновить».</summary>
    [Fact]
    public async Task SetDocuments_RowsFollowSetComposition()
    {
        using var scope = fixture.Services.CreateScope();
        var svc = Svc(scope);
        var m = M(scope);
        var (setId, namedId, _, _) = await SeedSetAsync(scope);

        var file = await svc.CreateSystemFileAsync(new CreateSystemFileInput("Set", setId.ToString(), null), default);
        var source = await svc.CreateSourceAsync(file.Id,
            new CreateSourceInput("Документы", "system:set-documents", null), default);

        var typeId = (await m.Send(new GetDocumentInstanceQuery(namedId)))!.CompositeTypeId;
        await m.Send(new AddDocumentToSetCommand(setId, typeId));

        var preview = await svc.PreviewSourceAsync(source.Id, 50, default);
        Assert.Equal(3, preview!.Rows.Count);
    }

    /// <summary>Ради чего всё: строки консолидации доходят до контекста генерации обычной привязкой.</summary>
    [Fact]
    public async Task SetDocuments_BoundToArrayField_ReachesGenerationContext()
    {
        using var scope = fixture.Services.CreateScope();
        var svc = Svc(scope);
        var m = M(scope);
        var (setId, _, _, _) = await SeedSetAsync(scope);

        var rowType = await m.Send(new CreateDocumentTypeCommand("СтрокаРеестра", "REG_ROW", DocumentTypeKind.Composite, null,
            J("{'fields':[{'key':'Наименование','type':'string','required':false},{'key':'Номер','type':'string','required':false}]}")));
        var registryType = await m.Send(new CreateDocumentTypeCommand("Реестр документов", "REGISTRY", DocumentTypeKind.Document, null,
            J($"{{'fields':[{{'key':'Строки','type':'array','required':false,'typeId':'{rowType.Id}'}}]}}")));
        var registry = await m.Send(new AddDocumentToSetCommand(setId, registryType.Id));

        var file = await svc.CreateSystemFileAsync(new CreateSystemFileInput("Set", setId.ToString(), null), default);
        var source = await svc.CreateSourceAsync(file.Id,
            new CreateSourceInput("Документы", "system:set-documents", null), default);
        await svc.CreateBindingAsync(new CreateBindingInput(registry.Id, source.Id, "Строки",
            new() { ["Наименование"] = "Наименование", ["Номер"] = "НомерДокумента" }), default);

        var instance = await m.Send(new GetDocumentInstanceQuery(registry.Id));
        var view = DocumentView.From(instance!);
        var ctx = await scope.ServiceProvider.GetRequiredService<IEntityResolver>().ResolveAsync(view, default);
        await scope.ServiceProvider.GetRequiredService<IDataSetResolver>().InjectAsync(ctx, view, null, default);

        var rows = (JsonElement)ctx.Data["Строки"]!;
        Assert.Equal(JsonValueKind.Array, rows.ValueKind);
        // Реестр — тоже документ комплекта и попадает в собственный перечень (решение по #580):
        // отфильтровать его при желании можно штатным фильтром строк источника.
        Assert.Equal(3, rows.GetArrayLength());
        Assert.Equal("7И-СОТВ 1", rows[0].GetProperty("Номер").GetString());
    }

    /// <summary>Системный источник виден внешнему агенту как детерминированная консолидация, не как парсинг.</summary>
    [Fact]
    public async Task SetDocuments_SnapshotOrigin_IsSystem()
    {
        using var scope = fixture.Services.CreateScope();
        var svc = Svc(scope);
        var (setId, _, _, _) = await SeedSetAsync(scope);

        var file = await svc.CreateSystemFileAsync(new CreateSystemFileInput("Set", setId.ToString(), null), default);
        var source = await svc.CreateSourceAsync(file.Id,
            new CreateSourceInput("Документы", "system:set-documents", null), default);

        var detail = await scope.ServiceProvider.GetRequiredService<IDataSnapshotService>()
            .GetSourceAsync(source.Id, default);

        Assert.Equal(DataOrigin.System, detail!.Origin);
        Assert.False(detail.Stale);
    }

    [Fact]
    public async Task SystemFile_HasNoFileOperations()
    {
        using var scope = fixture.Services.CreateScope();
        var svc = Svc(scope);
        var (setId, _, _, _) = await SeedSetAsync(scope);

        var file = await svc.CreateSystemFileAsync(new CreateSystemFileInput("Set", setId.ToString(), null), default);

        Assert.Null(await svc.DownloadFileAsync(file.Id, default));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.ReplaceFileAsync(file.Id,
            new ReplaceFileInput([1, 2, 3], "x.csv", "text/csv", null), default));

        // Повторное создание на том же уровне возвращает тот же набор, а не второй такой же.
        var again = await svc.CreateSystemFileAsync(new CreateSystemFileInput("Set", setId.ToString(), null), default);
        Assert.Equal(file.Id, again.Id);

        Assert.True(await svc.DeleteFileAsync(file.Id, default));
    }

    /// <summary>
    /// Сборка комплекта отбирает документы, читающие данные системы, одним запросом через две
    /// навигации (привязка → источник → набор). Если EF перестанет его транслировать, реестр молча
    /// соберётся по устаревшим метаданным соседей — поэтому предикат проверяется отдельно.
    /// </summary>
    [Fact]
    public async Task SystemBoundOwners_AreFoundByBindingQuery()
    {
        using var scope = fixture.Services.CreateScope();
        var svc = Svc(scope);
        var m = M(scope);
        var (setId, namedId, unnamedId, _) = await SeedSetAsync(scope);

        var file = await svc.CreateSystemFileAsync(new CreateSystemFileInput("Set", setId.ToString(), null), default);
        var source = await svc.CreateSourceAsync(file.Id,
            new CreateSourceInput("Документы", "system:set-documents", null), default);
        await svc.CreateBindingAsync(new CreateBindingInput(namedId, source.Id, "Строки", new()), default);

        var bindings = await scope.ServiceProvider.GetRequiredService<IRepository<DataSetBinding>>()
            .FindAsync(b => b.Source.File.Format == DataSetFormat.System, default);

        Assert.Equal(namedId, Assert.Single(bindings).OwnerId);
        Assert.DoesNotContain(bindings, b => b.OwnerId == unnamedId);
    }

    /// <summary>Консолидация «Документы комплекта» бессмысленна вне комплекта — кандидат не предлагается.</summary>
    [Fact]
    public async Task SetDocuments_NotOfferedOutsideSetScope()
    {
        using var scope = fixture.Services.CreateScope();
        var svc = Svc(scope);

        var file = await svc.CreateSystemFileAsync(new CreateSystemFileInput("System", null, null), default);
        Assert.Empty(await svc.DetectSourceCandidatesAsync(file.Id, default));
    }

    /// <summary>
    /// Что система готова консолидировать на уровне — известно ДО создания набора (issue #606).
    /// Без этого интерфейс предлагал набор на разделе, где выбирать потом нечего.
    /// </summary>
    [Fact]
    public async Task SystemCandidates_KnownBeforeFileExists()
    {
        using var scope = fixture.Services.CreateScope();
        var svc = Svc(scope);
        var (setId, _, _, _) = await SeedSetAsync(scope);

        var inSet = await svc.ListSystemCandidatesAsync("Set", setId, default);
        Assert.Equal(SystemDataSets.SetDocumentsMarker, Assert.Single(inSet).SheetOrPath);

        // Раздел/стройка/система: консолидировать нечего — кнопку показывать незачем.
        Assert.Empty(await svc.ListSystemCandidatesAsync("Section", Guid.NewGuid(), default));
        Assert.Empty(await svc.ListSystemCandidatesAsync("Construction", Guid.NewGuid(), default));
        Assert.Empty(await svc.ListSystemCandidatesAsync("System", null, default));

        await Assert.ThrowsAsync<ArgumentException>(
            () => svc.ListSystemCandidatesAsync("Ерунда", null, default));
    }

    /// <summary>
    /// Число строк системного источника считается на чтении (issue #613). Кэш, записанный при
    /// создании, обновлять нечем — файла нет, определение источника не редактируется, — поэтому
    /// «N строк» в списках и в MCP-срезе обязано пересчитываться, иначе оно молча врёт после
    /// первого же добавленного в комплект документа.
    /// </summary>
    [Fact]
    public async Task SetDocuments_RowCount_IsLiveEverywhereItIsShown()
    {
        using var scope = fixture.Services.CreateScope();
        var svc = Svc(scope);
        var m = M(scope);
        var (setId, namedId, _, _) = await SeedSetAsync(scope);

        var file = await svc.CreateSystemFileAsync(new CreateSystemFileInput("Set", setId.ToString(), null), default);
        var source = await svc.CreateSourceAsync(file.Id,
            new CreateSourceInput("Документы", SystemDataSets.SetDocumentsMarker, null), default);
        Assert.Equal(2, source.CachedRowCount);

        var typeId = (await m.Send(new GetDocumentInstanceQuery(namedId)))!.CompositeTypeId;
        await m.Send(new AddDocumentToSetCommand(setId, typeId));

        // Список источников набора и оба списка наборов — из них берут подпись выпадающие списки привязок.
        Assert.Equal(3, Assert.Single(await svc.ListSourcesAsync(file.Id, default)).CachedRowCount);
        Assert.Equal(3, Assert.Single(
            Assert.Single(await svc.ListFilesAsync("Set", setId, default)).Sources).CachedRowCount);
        Assert.Equal(3, Assert.Single(
            (await svc.ListAvailableFilesAsync(setId, default)).Single(f => f.Id == file.Id).Sources).CachedRowCount);

        // MCP-срез: внешний агент не должен видеть число, расходящееся с выдачей get_rows.
        var snapshots = scope.ServiceProvider.GetRequiredService<IDataSnapshotService>();
        Assert.Equal(3, (await snapshots.GetSourceAsync(source.Id))!.RowCount);
        Assert.Equal(3, Assert.Single((await snapshots.GetDatasetAsync(file.Id))!.Sources).RowCount);
        Assert.Equal(3, (await snapshots.GetRowsAsync(source.Id, 0, 50))!.TotalRows);
    }

    /// <summary>
    /// Шаблон обработки применим и к системному источнику, но его extraction — не лист файла, а
    /// выбор консолидации: подменять маркер нельзя (парсера у формата System нет, прежде здесь
    /// падало «Нет парсера для формата System»). Фильтр/колонки/сортировка при этом переносятся.
    /// </summary>
    [Fact]
    public async Task SetDocuments_ProcessingTemplate_AppliesWithoutTouchingExtraction()
    {
        using var scope = fixture.Services.CreateScope();
        var svc = Svc(scope);
        var (setId, _, _, _) = await SeedSetAsync(scope);

        var file = await svc.CreateSystemFileAsync(new CreateSystemFileInput("Set", setId.ToString(), null), default);
        var source = await svc.CreateSourceAsync(file.Id,
            new CreateSourceInput("Документы", SystemDataSets.SetDocumentsMarker, null), default);

        // Шаблон «из файловой жизни»: с извлечением (лист) и фильтром.
        var template = await svc.CreateProcessingTemplateAsync(new CreateProcessingTemplateInput(
            "Только с номером", "Лист1", null,
            JsonSerializer.Deserialize<object>(
                """{"type":"condition","column":"НомерДокумента","op":"is_not_empty"}"""),
            null, null), default);

        var updated = await svc.ApplyProcessingTemplateAsync(source.Id, template.Id, default);

        Assert.Equal(SystemDataSets.SetDocumentsMarker, updated!.SheetOrPath);
        var preview = await svc.PreviewSourceAsync(source.Id, 50, default);
        Assert.Equal("7И-СОТВ 1", Assert.Single(preview!.Rows)[preview.Columns.ToList().IndexOf("НомерДокумента")]);
    }
}
