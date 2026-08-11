using System.Text;
using System.Text.Json;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Generation;
using BHS.CRG.Domain.Documents;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Материализация «существующий документ по Ид» (issue #725, дополнение к #715).
///
/// Живой кейс: источник «Протоколы ЭОМ» несёт колонку с идентификаторами уже существующих
/// документов комплекта, а материализовать его можно было только СБОРКОЙ объекта из колонок — то
/// есть копией данных, живущей отдельно от самого документа. Маппинг <c>doc-ref</c>-полей (#715)
/// случая не покрывает: у типа-документа таких полей нет, ссылаться надо не полем, а всей строкой.
/// </summary>
[Collection("Integration")]
public class MaterializeByIdModeTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static IMediator M(IServiceScope s) => s.ServiceProvider.GetRequiredService<IMediator>();
    private static IDataSetService Svc(IServiceScope s) => s.ServiceProvider.GetRequiredService<IDataSetService>();
    private static JsonDocument J(string singleQuoted) => JsonDocument.Parse(singleQuoted.Replace('\'', '"'));

    /// <summary>Типы «АОСР» (цель ссылки) и «Реестр» со списком документов этого типа.</summary>
    private static async Task<(DocumentType Aosr, DocumentType Reestr)> SeedTypesAsync(IMediator m)
    {
        var aosr = await m.Send(new CreateDocumentTypeCommand("АОСР", $"AOSR{Guid.NewGuid():N}"[..12],
            DocumentTypeKind.Document, null, J("{'fields':[{'key':'Номер','type':'string'}]}")));
        var reestr = await m.Send(new CreateDocumentTypeCommand("Реестр", $"REG{Guid.NewGuid():N}"[..12],
            DocumentTypeKind.Document, null,
            J($"{{'fields':[{{'key':'Документы','type':'doc-array','typeId':'{aosr.Id}'}}]}}")));
        return (aosr, reestr);
    }

    /// <summary>Комплект с реестром и одним АОСР (Номер = 17).</summary>
    private static async Task<(Guid ReestrId, Guid AosrId)> SeedSetAsync(IMediator m, Guid reestrTypeId, Guid aosrTypeId)
    {
        var construction = await m.Send(new CreateConstructionCommand("Объект", Guid.NewGuid()));
        var section = await m.Send(new CreateSectionCommand(construction.Id, "Раздел"));
        var set = await m.Send(new CreateDocumentSetCommand(section.Id, "Комплект"));
        var aosr = await m.Send(new AddDocumentToSetCommand(set.Id, aosrTypeId));
        await m.Send(new UpdateRequisitesCommand(aosr.Id, J("{'Номер':'17'}")));
        var reestr = await m.Send(new AddDocumentToSetCommand(set.Id, reestrTypeId));
        return (reestr.Id, aosr.Id);
    }

    /// <summary>Источник, материализованный ссылкой на документ по колонке <paramref name="idColumn"/>.</summary>
    private static async Task<Guid> ByIdSourceAsync(IServiceScope scope, string csv, Guid typeId, string idColumn)
    {
        var svc = Svc(scope);
        var file = await svc.UploadFileAsync(
            new UploadFileInput(Encoding.UTF8.GetBytes(csv), "docs.csv", "text/csv", "Тест", "System", null), default);
        var candidate = (await svc.DetectSourceCandidatesAsync(file.Id, default)).Single();
        var source = await svc.CreateSourceAsync(file.Id, new CreateSourceInput("Протоколы", candidate.SheetOrPath, null), default);
        await svc.SetMaterializationAsync(source.Id, typeId, mapping: new(), discriminator: null, byIdColumn: idColumn, default);
        return source.Id;
    }

    /// <summary>Полный проход резолва — тот же порядок, что у генерации PDF.</summary>
    private static async Task<GenerationContext> ResolveAsync(
        IServiceScope scope, Guid instanceId, List<ResolutionDiagnostic>? diagnostics = null)
    {
        var inst = await M(scope).Send(new GetDocumentInstanceQuery(instanceId));
        var view = DocumentView.From(inst!);
        var entity = scope.ServiceProvider.GetRequiredService<IEntityResolver>();
        var ctx = await entity.ResolveAsync(view);
        await scope.ServiceProvider.GetRequiredService<IDataSetResolver>().InjectAsync(ctx, view, diagnostics, default);
        // Второй проход: именно он разворачивает добавленные привязкой ссылки на документы.
        await entity.ResolveContextRefsAsync(ctx, view.DocumentSetId);
        return ctx;
    }

    /// <summary>
    /// Строка источника доезжает до контекста ЖИВЫМ документом, а не его копией из колонок.
    ///
    /// Проверяем по результату полного резолва, а не по промежуточной форме <c>$ref</c>: обещание
    /// пользователю в том, что в шаблон попадут реквизиты того самого документа, на который он
    /// показал колонкой, — и что они меняются вместе с ним, а не остаются снимком.
    /// </summary>
    [Fact]
    public async Task RowWithDocumentId_BecomesTheLiveDocument()
    {
        using var scope = fixture.Services.CreateScope();
        var m = M(scope);

        var (aosrType, reestrType) = await SeedTypesAsync(m);
        var (reestrId, aosrId) = await SeedSetAsync(m, reestrType.Id, aosrType.Id);

        var sourceId = await ByIdSourceAsync(scope, $"Ид\n{aosrId}\n", aosrType.Id, "Ид");
        // Привязка материализованного источника — типизированный указатель: своего маппинга нет.
        await Svc(scope).CreateBindingAsync(new CreateBindingInput(reestrId, sourceId, "Документы", null), default);

        var ctx = await ResolveAsync(scope, reestrId);

        var rows = (JsonElement)ctx.Data["Документы"]!;
        var doc = Assert.Single(rows.EnumerateArray());
        Assert.False(doc.TryGetProperty("$ref", out _), "ссылка осталась неразрешённой");
        Assert.Equal("17", doc.GetProperty("Номер").GetString());
    }

    /// <summary>
    /// Строка без идентификатора в реестр не попадает, и об этом сказано.
    ///
    /// Положить ссылку без Ид значило бы завести битую ссылку там, где её нет: сканер объявил бы
    /// целевую запись удалённой — то есть назвал бы выдуманную беду вместо настоящей («в колонке
    /// пусто» / «выбрана соседняя колонка»).
    /// </summary>
    [Fact]
    public async Task RowsWithoutId_AreSkipped_AndCountedWithReasons()
    {
        using var scope = fixture.Services.CreateScope();
        var m = M(scope);

        var (aosrType, reestrType) = await SeedTypesAsync(m);
        var (reestrId, aosrId) = await SeedSetAsync(m, reestrType.Id, aosrType.Id);

        // Вторая колонка нужна, чтобы строка с пустым Ид не оказалась пустой строкой файла — такую
        // парсер отбрасывает, и проверять было бы нечего.
        var sourceId = await ByIdSourceAsync(scope,
            $"Ид,Пометка\n{aosrId},годная\n,пусто\nне-идентификатор,мусор\n", aosrType.Id, "Ид");
        await Svc(scope).CreateBindingAsync(new CreateBindingInput(reestrId, sourceId, "Документы", null), default);

        var diagnostics = new List<ResolutionDiagnostic>();
        var ctx = await ResolveAsync(scope, reestrId, diagnostics);

        var rows = ((JsonElement)ctx.Data["Документы"]!).EnumerateArray().ToList();
        Assert.Single(rows);
        Assert.Equal("17", rows[0].GetProperty("Номер").GetString());

        var warning = Assert.Single(diagnostics, d => d.Path == "Документы");
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains("колонка с идентификатором документа пуста", warning.Message);
        Assert.Contains("в колонке не идентификатор документа", warning.Message);
    }

    /// <summary>
    /// Скалярная привязка в этом режиме бессмысленна: у ссылки нет полей, раскладывать по ключам
    /// контекста нечего. Отказ словами — не формальность: молча положенное «ничего» выглядит как
    /// «источник пуст», а это ровно та тишина, ради которой заведён #715.
    /// </summary>
    [Fact]
    public async Task ScalarBinding_InByIdMode_IsRefusedInWords()
    {
        using var scope = fixture.Services.CreateScope();
        var m = M(scope);

        var (aosrType, reestrType) = await SeedTypesAsync(m);
        var (reestrId, aosrId) = await SeedSetAsync(m, reestrType.Id, aosrType.Id);

        var sourceId = await ByIdSourceAsync(scope, $"Ид\n{aosrId}\n", aosrType.Id, "Ид");
        await Svc(scope).CreateBindingAsync(new CreateBindingInput(reestrId, sourceId, null, null), default);

        var diagnostics = new List<ResolutionDiagnostic>();
        var ctx = await ResolveAsync(scope, reestrId, diagnostics);

        var problem = Assert.Single(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Contains("ссылкой на существующий документ", problem.Message);
        Assert.False(ctx.Data.ContainsKey("Документы"));
    }

    /// <summary>
    /// Предпросмотр привязки показывает НАИМЕНОВАНИЯ документов, а отсутствующий по идентификатору
    /// назван отсутствующим. Идентификатор в таблице человеку не говорит ничего, а битая ссылка,
    /// показанная как рабочая, расходится с генерацией — то есть врёт ровно на том экране, куда
    /// идут проверять.
    /// </summary>
    [Fact]
    public async Task BindingPreview_ShowsNames_AndNamesTheMissingOne()
    {
        using var scope = fixture.Services.CreateScope();
        var m = M(scope);

        var (aosrType, reestrType) = await SeedTypesAsync(m);
        var (reestrId, aosrId) = await SeedSetAsync(m, reestrType.Id, aosrType.Id);

        var sourceId = await ByIdSourceAsync(scope, $"Ид\n{aosrId}\n{Guid.NewGuid()}\n", aosrType.Id, "Ид");
        await Svc(scope).CreateBindingAsync(new CreateBindingInput(reestrId, sourceId, "Документы", null), default);

        var preview = Assert.Single(await Svc(scope).PreviewBindingsAsync(reestrId, default));

        Assert.Equal("tabular", preview.Mode);
        var rows = Assert.IsType<List<Dictionary<string, object?>>>(preview.Data);
        Assert.Equal(2, rows.Count);
        // Имя документу не задавали — отображается имя типа (общая конвенция показа документов).
        Assert.Equal("АОСР", rows[0]["Документ"]);
        Assert.Equal("документ не найден", rows[1]["Документ"]);
    }

    /// <summary>
    /// Предпросмотр материализации ведёт НЕСОХРАНЁННУЮ настройку диалога (issue #294): переключение
    /// режима обязано быть видно сразу, иначе экран отвечает по вчерашней настройке именно тогда,
    /// когда её меняют.
    /// </summary>
    [Fact]
    public async Task MaterializePreview_FollowsUnsavedByIdColumn()
    {
        using var scope = fixture.Services.CreateScope();
        var m = M(scope);

        var (aosrType, reestrType) = await SeedTypesAsync(m);
        var (_, aosrId) = await SeedSetAsync(m, reestrType.Id, aosrType.Id);

        // Источник ещё НЕ материализован — настройку целиком ведёт диалог.
        var svc = Svc(scope);
        var file = await svc.UploadFileAsync(
            new UploadFileInput(Encoding.UTF8.GetBytes($"Ид\n{aosrId}\n"), "docs.csv", "text/csv", "Тест", "System", null), default);
        var candidate = (await svc.DetectSourceCandidatesAsync(file.Id, default)).Single();
        var source = await svc.CreateSourceAsync(file.Id, new CreateSourceInput("Протоколы", candidate.SheetOrPath, null), default);

        var preview = await svc.MaterializePreviewAsync(source.Id, 50, aosrType.Id,
            new Dictionary<string, string>(), discriminator: null, byIdColumn: "Ид", default);

        Assert.NotNull(preview);
        Assert.Null(preview!.Error);
        Assert.Equal("АОСР", Assert.Single(preview.Rows)["Документ"]);
    }

    /// <summary>
    /// Документ ЧУЖОГО комплекта предпросмотр называет чужим, а не показывает наименованием.
    ///
    /// Условия здесь обязаны совпадать с резолвером (<c>ResolveDocumentInstanceAsync</c> берёт документ
    /// только из своего комплекта): показав наименование, экран назвал бы исправной ссылку, которая
    /// при генерации останется висячей, а сканер объявит её удалённой записью — то есть ровно ту
    /// ложь предпросмотра, ради снятия которой наименования и заведены.
    /// </summary>
    [Fact]
    public async Task DocumentFromAnotherSet_IsNamedForeign_NotShownAsWorking()
    {
        using var scope = fixture.Services.CreateScope();
        var m = M(scope);

        var (aosrType, reestrType) = await SeedTypesAsync(m);
        var (reestrId, _) = await SeedSetAsync(m, reestrType.Id, aosrType.Id);
        // Второй комплект той же стройки — его АОСР для первого реестра посторонний.
        var (_, foreignAosrId) = await SeedSetAsync(m, reestrType.Id, aosrType.Id);

        var sourceId = await ByIdSourceAsync(scope, $"Ид\n{foreignAosrId}\n", aosrType.Id, "Ид");
        await Svc(scope).CreateBindingAsync(new CreateBindingInput(reestrId, sourceId, "Документы", null), default);

        var preview = Assert.Single(await Svc(scope).PreviewBindingsAsync(reestrId, default));
        var rows = Assert.IsType<List<Dictionary<string, object?>>>(preview.Data);
        Assert.Equal("документ другого комплекта — ссылка не развернётся", Assert.Single(rows)["Документ"]);

        // И это правда: генерация такую ссылку действительно не разворачивает.
        var ctx = await ResolveAsync(scope, reestrId);
        var doc = Assert.Single(((JsonElement)ctx.Data["Документы"]!).EnumerateArray());
        Assert.True(doc.TryGetProperty("$ref", out _), "чужой документ неожиданно развернулся");
    }

    /// <summary>
    /// Колонки нет вовсе — это другая беда, чем пустая ячейка, и названа она отдельно. Свалив их в
    /// одну причину, мы отправили бы человека проверять ячейки, которых нет: файл перезалили с
    /// переименованным заголовком, и искать надо колонку.
    /// </summary>
    [Fact]
    public async Task MissingColumn_IsNamedSeparately_NotAsEmptyCell()
    {
        using var scope = fixture.Services.CreateScope();
        var m = M(scope);

        var (aosrType, reestrType) = await SeedTypesAsync(m);
        var (reestrId, aosrId) = await SeedSetAsync(m, reestrType.Id, aosrType.Id);

        var svc = Svc(scope);
        var file = await svc.UploadFileAsync(new UploadFileInput(
            Encoding.UTF8.GetBytes($"ИдДокумента\n{aosrId}\n"), "docs.csv", "text/csv", "Тест", "System", null), default);
        var candidate = (await svc.DetectSourceCandidatesAsync(file.Id, default)).Single();
        var source = await svc.CreateSourceAsync(file.Id, new CreateSourceInput("Протоколы", candidate.SheetOrPath, null), default);
        // Колонка выбрана та, которой в источнике нет (заголовок переименован после настройки).
        await svc.SetMaterializationAsync(source.Id, aosrType.Id, new(), discriminator: null, byIdColumn: "Ид", default);
        await svc.CreateBindingAsync(new CreateBindingInput(reestrId, source.Id, "Документы", null), default);

        var diagnostics = new List<ResolutionDiagnostic>();
        await ResolveAsync(scope, reestrId, diagnostics);

        var warning = Assert.Single(diagnostics, d => d.Path == "Документы");
        Assert.Contains("нет колонки", warning.Message);
    }

    /// <summary>
    /// Настройка «по Ид» переживает копирование источника (issue #717): копия — «тот же источник,
    /// другой фильтр», и настраивать режим заново значило бы терять работу, ради которой копию делают.
    /// </summary>
    [Fact]
    public async Task DuplicatedSource_KeepsByIdMode()
    {
        using var scope = fixture.Services.CreateScope();
        var m = M(scope);

        var (aosrType, reestrType) = await SeedTypesAsync(m);
        var (_, aosrId) = await SeedSetAsync(m, reestrType.Id, aosrType.Id);

        var sourceId = await ByIdSourceAsync(scope, $"Ид\n{aosrId}\n", aosrType.Id, "Ид");
        var copy = await Svc(scope).DuplicateSourceAsync(sourceId, null, default);

        Assert.Equal("Ид", copy!.MaterializeByIdColumn);
        Assert.Equal(aosrType.Id, copy.MaterializeTypeId);
    }
}
