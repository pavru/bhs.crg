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
/// Маппинг полей-ссылок на документы из колонки с Ид (issue #715).
///
/// Живой кейс: системный источник «Документы комплекта» даёт колонку с точными идентификаторами
/// документов, а замапить их было НЕЧЕМ — грамматика маппинга знала запись каталога (@@ref),
/// встроенный объект (@@inline) и файл (@@file), но не ссылку на документ. Настроенная
/// материализация при этом оставалась пустой и молча складывала в поле массив пустых объектов.
///
/// Нового токена не заводили: поле <c>doc-ref</c> само объявляет, что в нём лежит, поэтому хватает
/// обычного «поле → колонка» и приведения по объявленному типу.
/// </summary>
[Collection("Integration")]
public class DataSetDocRefMappingTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static IMediator M(IServiceScope s) => s.ServiceProvider.GetRequiredService<IMediator>();
    private static IDataSetService Svc(IServiceScope s) => s.ServiceProvider.GetRequiredService<IDataSetService>();
    private static JsonDocument J(string singleQuoted) => JsonDocument.Parse(singleQuoted.Replace('\'', '"'));

    /// <summary>Комплект с одним документом-целью; возвращает Ид комплекта и Ид цели.</summary>
    private static async Task<(Guid SetId, Guid ReestrId, Guid AosrId)> SeedSetAsync(IMediator m, Guid reestrTypeId, Guid aosrTypeId)
    {
        var construction = await m.Send(new CreateConstructionCommand("Объект", Guid.NewGuid()));
        var section = await m.Send(new CreateSectionCommand(construction.Id, "Раздел"));
        var set = await m.Send(new CreateDocumentSetCommand(section.Id, "Комплект"));
        var aosr = await m.Send(new AddDocumentToSetCommand(set.Id, aosrTypeId));
        await m.Send(new UpdateRequisitesCommand(aosr.Id, J("{'Номер':'17'}")));
        var reestr = await m.Send(new AddDocumentToSetCommand(set.Id, reestrTypeId));
        return (set.Id, reestr.Id, aosr.Id);
    }

    private static async Task<Guid> MaterializedSourceAsync(
        IServiceScope scope, string csv, Guid rowTypeId, Dictionary<string, string>? mapping)
    {
        var svc = Svc(scope);
        var file = await svc.UploadFileAsync(
            new UploadFileInput(Encoding.UTF8.GetBytes(csv), "docs.csv", "text/csv", "Тест", "System", null), default);
        var candidate = (await svc.DetectSourceCandidatesAsync(file.Id, default)).Single();
        var source = await svc.CreateSourceAsync(file.Id, new CreateSourceInput("Документы", candidate.SheetOrPath, null), default);
        await svc.SetMaterializationAsync(source.Id, rowTypeId, mapping, discriminator: null, byIdColumn: null, default);
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
        // Второй проход: именно он разворачивает добавленные привязкой ссылки.
        await entity.ResolveContextRefsAsync(ctx, view.DocumentSetId);
        return ctx;
    }

    /// <summary>
    /// Ячейка с Ид документа доезжает до контекста РАЗВЁРНУТЫМ документом, а не строкой и не
    /// висячей ссылкой. Проверяем через полный проход резолва, а не по промежуточной форме: форма
    /// <c>$ref</c> — деталь, а обещание пользователю в том, что в шаблон попадут реквизиты
    /// документа, на который он показал колонкой.
    /// </summary>
    [Fact]
    public async Task DocRefField_FromIdColumn_ResolvesToTheDocument()
    {
        using var scope = fixture.Services.CreateScope();
        var m = M(scope);

        var aosrType = await m.Send(new CreateDocumentTypeCommand("АОСР", $"AOSR{Guid.NewGuid():N}"[..12],
            DocumentTypeKind.Document, null, J("{'fields':[{'key':'Номер','type':'string'}]}")));
        var reestrType = await m.Send(new CreateDocumentTypeCommand("Реестр", $"REG{Guid.NewGuid():N}"[..12],
            DocumentTypeKind.Document, null, J("{'fields':[]}")));
        var rowType = await m.Send(new CreateDocumentTypeCommand("СтрокаРеестра", $"ROW{Guid.NewGuid():N}"[..12],
            DocumentTypeKind.Composite, null,
            J($"{{'fields':[{{'key':'Документ','type':'doc-ref','typeId':'{aosrType.Id}'}}]}}")));

        var (_, reestrId, aosrId) = await SeedSetAsync(m, reestrType.Id, aosrType.Id);

        // Колонка «Ид» — ровно то, что даёт системный источник «Документы комплекта».
        var sourceId = await MaterializedSourceAsync(scope, $"Ид\n{aosrId}\n", rowType.Id,
            new Dictionary<string, string> { ["Документ"] = "Ид" });
        await Svc(scope).CreateBindingAsync(new CreateBindingInput(reestrId, sourceId, "Строки", null), default);

        var ctx = await ResolveAsync(scope, reestrId);

        var rows = (JsonElement)ctx.Data["Строки"]!;
        var docRef = Assert.Single(rows.EnumerateArray()).GetProperty("Документ");
        Assert.Equal(JsonValueKind.Object, docRef.ValueKind);
        // Ссылка РАЗРЕШЕНА: сырого маркера не осталось, а реквизиты цели на месте.
        Assert.False(docRef.TryGetProperty("$ref", out _), "ссылка осталась неразрешённой");
        Assert.Equal("17", docRef.GetProperty("Номер").GetString());
    }

    /// <summary>
    /// Пустая ячейка — отсутствие ссылки, посторонний текст — остаётся видимым.
    ///
    /// Разница существенна: подменив мусор на пустоту, мы спрятали бы «в колонке не тот
    /// идентификатор» под «в колонке ничего нет», а это две разные беды и чинятся они по-разному.
    /// </summary>
    [Fact]
    public async Task EmptyCell_LeavesNoReference_And_GarbageStaysVisible()
    {
        using var scope = fixture.Services.CreateScope();
        var m = M(scope);

        var aosrType = await m.Send(new CreateDocumentTypeCommand("АОСР", $"AOSR{Guid.NewGuid():N}"[..12],
            DocumentTypeKind.Document, null, J("{'fields':[{'key':'Номер','type':'string'}]}")));
        var reestrType = await m.Send(new CreateDocumentTypeCommand("Реестр", $"REG{Guid.NewGuid():N}"[..12],
            DocumentTypeKind.Document, null, J("{'fields':[]}")));
        var rowType = await m.Send(new CreateDocumentTypeCommand("СтрокаРеестра", $"ROW{Guid.NewGuid():N}"[..12],
            DocumentTypeKind.Composite, null,
            J($"{{'fields':[{{'key':'Документ','type':'doc-ref','typeId':'{aosrType.Id}'}}]}}")));

        var (_, reestrId, _) = await SeedSetAsync(m, reestrType.Id, aosrType.Id);

        // Вторая колонка нужна, чтобы строка с пустым Ид не оказалась пустой строкой файла — такую
        // парсер отбрасывает, и проверять было бы нечего.
        var sourceId = await MaterializedSourceAsync(scope, "Ид,Пометка\n,пусто\nне-идентификатор,мусор\n", rowType.Id,
            new Dictionary<string, string> { ["Документ"] = "Ид" });
        await Svc(scope).CreateBindingAsync(new CreateBindingInput(reestrId, sourceId, "Строки", null), default);

        var diagnostics = new List<ResolutionDiagnostic>();
        var ctx = await ResolveAsync(scope, reestrId, diagnostics);
        var rows = ((JsonElement)ctx.Data["Строки"]!).EnumerateArray().ToList();

        Assert.Equal(2, rows.Count);
        // Пустая ячейка ключа не создаёт вовсе (идиома issue #544 — шаблоны читают через at/dig).
        Assert.False(rows[0].TryGetProperty("Документ", out _));
        Assert.Equal("не-идентификатор", rows[1].GetProperty("Документ").GetString());

        // И об этом СКАЗАНО. Молчать здесь было нельзя: поля-ссылки ValueTypeScanner пропускает
        // намеренно, оставшихся $ref не будет — строка уехала бы в шаблон вместо документа, и ни
        // один проход о ней бы не заикнулся. Так выглядит выбор соседней колонки в диалоге.
        var warning = Assert.Single(diagnostics, d => d.Severity == DiagnosticSeverity.Warning);
        Assert.Contains("не идентификатор", warning.Message);
        Assert.Contains("не-идентификатор", warning.Message);
        // Пустая ячейка поводом для предупреждения НЕ является: там просто нет значения.
        Assert.EndsWith("[1].Документ", warning.Path);
    }

    /// <summary>
    /// Маппинг из одних пустых значений — тот же пустой маппинг. Проверка по числу ключей
    /// пропускала бы <c>{"Документ":""}</c>: ключ есть, строить нечего, массив пустышек тот же.
    /// Определение «пусто» здесь обязано совпадать с тем, по которому маппинг вообще выбирается.
    /// </summary>
    [Fact]
    public async Task MaterializeMappingOfEmptyValues_IsTreatedAsEmpty()
    {
        using var scope = fixture.Services.CreateScope();
        var m = M(scope);

        var reestrType = await m.Send(new CreateDocumentTypeCommand("Реестр", $"REG{Guid.NewGuid():N}"[..12],
            DocumentTypeKind.Document, null, J("{'fields':[]}")));
        var rowType = await m.Send(new CreateDocumentTypeCommand("СтрокаРеестра", $"ROW{Guid.NewGuid():N}"[..12],
            DocumentTypeKind.Composite, null, J("{'fields':[{'key':'Поле','type':'string'}]}")));
        var aosrType = await m.Send(new CreateDocumentTypeCommand("АОСР", $"AOSR{Guid.NewGuid():N}"[..12],
            DocumentTypeKind.Document, null, J("{'fields':[]}")));

        var (_, reestrId, _) = await SeedSetAsync(m, reestrType.Id, aosrType.Id);

        var sourceId = await MaterializedSourceAsync(scope, "A,B\n1,2\n", rowType.Id,
            new Dictionary<string, string> { ["Поле"] = "" });
        await Svc(scope).CreateBindingAsync(new CreateBindingInput(reestrId, sourceId, "Строки", null), default);

        var diagnostics = new List<ResolutionDiagnostic>();
        var ctx = await ResolveAsync(scope, reestrId, diagnostics);

        Assert.Contains(diagnostics, d => d.Severity == DiagnosticSeverity.Error && d.Message.Contains("маппинг колонок пуст"));
        Assert.False(ctx.Data.ContainsKey("Строки"));
    }

    /// <summary>
    /// Скалярная привязка кладёт собранный объект тем же способом, что и табличная.
    ///
    /// Разница была неочевидной и дорогой: оба страховочных прохода — доразрешение ссылок и поиск
    /// оставшихся <c>$ref</c> — пропускают всё, что не <c>JsonElement</c>. Сырой маркер из скалярной
    /// ветки уехал бы в data.json и в сохранённые данные записи неразрешённым и незамеченным.
    /// </summary>
    [Fact]
    public async Task ScalarBinding_DocRefField_IsResolvedToo()
    {
        using var scope = fixture.Services.CreateScope();
        var m = M(scope);

        var aosrType = await m.Send(new CreateDocumentTypeCommand("АОСР", $"AOSR{Guid.NewGuid():N}"[..12],
            DocumentTypeKind.Document, null, J("{'fields':[{'key':'Номер','type':'string'}]}")));
        // Поле-ссылка объявлено у САМОГО документа — маппинг материализации ложится на его поля.
        var reestrType = await m.Send(new CreateDocumentTypeCommand("Реестр", $"REG{Guid.NewGuid():N}"[..12],
            DocumentTypeKind.Document, null,
            J($"{{'fields':[{{'key':'Основание','type':'doc-ref','typeId':'{aosrType.Id}'}}]}}")));

        var (_, reestrId, aosrId) = await SeedSetAsync(m, reestrType.Id, aosrType.Id);

        var sourceId = await MaterializedSourceAsync(scope, $"Ид\n{aosrId}\n", reestrType.Id,
            new Dictionary<string, string> { ["Основание"] = "Ид" });
        // targetFieldKey = null — скалярный режим: первая строка заполняет поля документа.
        await Svc(scope).CreateBindingAsync(new CreateBindingInput(reestrId, sourceId, null, null), default);

        var ctx = await ResolveAsync(scope, reestrId);

        var docRef = Assert.IsType<JsonElement>(ctx.Data["Основание"]);
        Assert.False(docRef.TryGetProperty("$ref", out _), "ссылка осталась неразрешённой");
        Assert.Equal("17", docRef.GetProperty("Номер").GetString());
    }

    /// <summary>
    /// Предпросмотр привязок — первый экран, куда идут выяснять, почему поле пустое. Он обязан
    /// назвать причину, а не показывать таблицу пустых объектов: генерация об этом уже говорит,
    /// а предпросмотр молчал.
    /// </summary>
    [Fact]
    public async Task BindingPreview_NamesTheEmptyMaterializeMapping()
    {
        using var scope = fixture.Services.CreateScope();
        var m = M(scope);

        var reestrType = await m.Send(new CreateDocumentTypeCommand("Реестр", $"REG{Guid.NewGuid():N}"[..12],
            DocumentTypeKind.Document, null, J("{'fields':[]}")));
        var rowType = await m.Send(new CreateDocumentTypeCommand("СтрокаРеестра", $"ROW{Guid.NewGuid():N}"[..12],
            DocumentTypeKind.Composite, null, J("{'fields':[{'key':'Поле','type':'string'}]}")));
        var aosrType = await m.Send(new CreateDocumentTypeCommand("АОСР", $"AOSR{Guid.NewGuid():N}"[..12],
            DocumentTypeKind.Document, null, J("{'fields':[]}")));

        var (_, reestrId, _) = await SeedSetAsync(m, reestrType.Id, aosrType.Id);

        var sourceId = await MaterializedSourceAsync(scope, "A,B\n1,2\n3,4\n", rowType.Id, mapping: new());
        await Svc(scope).CreateBindingAsync(new CreateBindingInput(reestrId, sourceId, "Строки", null), default);

        var preview = Assert.Single(await Svc(scope).PreviewBindingsAsync(reestrId, default));

        Assert.Equal("error", preview.Mode);
        Assert.Contains("маппинг колонок пуст", preview.Error);
    }

    /// <summary>
    /// Материализация настроена, а маппинг пуст — это ошибка, и она названа словами.
    ///
    /// Ровно так выглядел живой кейс: тип для материализации выбран, маппинг пуст (маппить было
    /// нечем), и в поле уезжал массив пустых объектов. Снаружи это неотличимо от «источник отдал
    /// пустые строки» — то есть настройка не работала молча.
    /// </summary>
    [Fact]
    public async Task MaterializedSourceWithEmptyMapping_ReportsError_AndLeavesFieldUnfilled()
    {
        using var scope = fixture.Services.CreateScope();
        var m = M(scope);

        var reestrType = await m.Send(new CreateDocumentTypeCommand("Реестр", $"REG{Guid.NewGuid():N}"[..12],
            DocumentTypeKind.Document, null, J("{'fields':[]}")));
        var rowType = await m.Send(new CreateDocumentTypeCommand("СтрокаРеестра", $"ROW{Guid.NewGuid():N}"[..12],
            DocumentTypeKind.Composite, null, J("{'fields':[{'key':'Поле','type':'string'}]}")));
        var aosrType = await m.Send(new CreateDocumentTypeCommand("АОСР", $"AOSR{Guid.NewGuid():N}"[..12],
            DocumentTypeKind.Document, null, J("{'fields':[]}")));

        var (_, reestrId, _) = await SeedSetAsync(m, reestrType.Id, aosrType.Id);

        var sourceId = await MaterializedSourceAsync(scope, "A,B\n1,2\n3,4\n", rowType.Id, mapping: new());
        await Svc(scope).CreateBindingAsync(new CreateBindingInput(reestrId, sourceId, "Строки", null), default);

        var diagnostics = new List<ResolutionDiagnostic>();
        var ctx = await ResolveAsync(scope, reestrId, diagnostics);

        var problem = Assert.Single(diagnostics, d => d.Path == "Строки");
        Assert.Equal(DiagnosticSeverity.Error, problem.Severity);
        Assert.Contains("маппинг колонок пуст", problem.Message);
        // Массива пустышек больше нет — поле не заполнено, как и сказано в сообщении.
        Assert.False(ctx.Data.ContainsKey("Строки"));
    }
}
