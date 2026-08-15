using System.Text;
using System.Text.Json;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.DataSnapshots;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Generation;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Infrastructure.DataSets;
using BHS.CRG.Infrastructure.Persistence;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Каскад доменов instance-ссылки (issue #733): не найдя цель среди документов комплекта, резолвер
/// ищет её среди документов качества, видимых из скоп-цепочки документа-владельца.
///
/// <para>Живой случай, ради которого каскад заведён: источник «Документы качества» отдаёт в колонке
/// «Ид» идентификаторы записей библиотеки, материализация строит по ним <c>{"$ref":"instance"}</c>,
/// а прежний резолвер искал цель только среди документов комплекта — union с <c>doc-ref</c>
/// собирался молча и не связывался ни при какой настройке.</para>
///
/// <para>Проверяется обе стороны каскада: сам разворот (форма, штамп типа, скан) и его границы —
/// видимость по областям и неприкосновенность прежнего поведения документов комплекта. Рядом —
/// предпросмотр (<see cref="MaterializeByIdMode"/>): разойдись он с резолвером, экран называл бы
/// «не найденной» ссылку, которую генерация разворачивает штатно.</para>
/// </summary>
[Collection("Integration")]
public class EntityResolverQualityCascadeTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static IMediator M(IServiceScope s) => s.ServiceProvider.GetRequiredService<IMediator>();
    private static JsonDocument J(string singleQuoted) => JsonDocument.Parse(singleQuoted.Replace('\'', '"'));
    private readonly Guid _userId = Guid.NewGuid();

    private sealed record Seed(Guid SetId, Guid SectionId, Guid ConstructionId, Guid DocTypeId, Guid CertTypeId);

    private async Task<Seed> SeedAsync()
    {
        using var scope = fixture.Services.CreateScope();
        var m = M(scope);
        var construction = await m.Send(new CreateConstructionCommand("Объект", _userId));
        var section = await m.Send(new CreateSectionCommand(construction.Id, "ЭОМ"));
        var set = await m.Send(new CreateDocumentSetCommand(section.Id, "250701.ЭОМ-1"));
        var docType = await m.Send(new CreateDocumentTypeCommand(
            "Акт", "AOSR", DocumentTypeKind.Document, null, J("{'fields':[]}")));
        var certType = await m.Send(new CreateDocumentTypeCommand(
            "Сертификат", "CERT", DocumentTypeKind.Document, null, J("{'fields':[]}")));
        return new Seed(set.Id, section.Id, construction.Id, docType.Id, certType.Id);
    }

    private static Task<QualityDocument> AddQualityAsync(
        IServiceScope scope, Guid typeId, string name, string requisites,
        CatalogScope scope_, Guid? scopeId,
        string? scanBlobPath = null, string? scanFileName = null, string? scanMimeType = null)
        => M(scope).Send(new CreateQualityDocumentCommand(
            typeId, name, J(requisites), scope_, scopeId, QualityDocSource.Manual,
            scanBlobPath, scanFileName, scanMimeType));

    /// <summary>Документ комплекта, чьи реквизиты несут instance-ссылку на <paramref name="targetId"/>.</summary>
    private async Task<Guid> DocRefencingAsync(Seed seed, Guid targetId)
    {
        using var scope = fixture.Services.CreateScope();
        var inst = await M(scope).Send(new AddDocumentToSetCommand(seed.SetId, seed.DocTypeId));
        await M(scope).Send(new UpdateRequisitesCommand(inst.Id,
            J($"{{'Качество':{{'$ref':'instance','instanceId':'{targetId}'}}}}")));
        return inst.Id;
    }

    private async Task<JsonElement> ResolveFieldAsync(Guid instanceId, string key)
    {
        using var scope = fixture.Services.CreateScope();
        var inst = await M(scope).Send(new GetDocumentInstanceQuery(instanceId));
        var resolver = scope.ServiceProvider.GetRequiredService<IEntityResolver>();
        var ctx = await resolver.ResolveAsync(DocumentView.From(inst!));
        return (JsonElement)ctx.Data[key]!;
    }

    // ── Разворот ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task QualityDocument_ResolvedByInstanceRef_WithRequisitesTypeAndScan()
    {
        var seed = await SeedAsync();
        Guid qualityId;
        using (var scope = fixture.Services.CreateScope())
            qualityId = (await AddQualityAsync(scope, seed.CertTypeId, "EKF — автоматы",
                "{'НомерДок':'ЕАЭС RU С-CN.1','Завод':'EKF'}",
                CatalogScope.Set, seed.SetId, "quality/scan.pdf", "скан.pdf", "application/pdf")).Id;

        var resolved = await ResolveFieldAsync(await DocRefencingAsync(seed, qualityId), "Качество");

        // Ссылка развёрнута: сырого $ref не осталось (иначе сканер объявил бы её висячей).
        Assert.False(resolved.TryGetProperty("$ref", out _));

        // Реквизиты — как есть.
        Assert.Equal("ЕАЭС RU С-CN.1", resolved.GetProperty("НомерДок").GetString());
        Assert.Equal("EKF", resolved.GetProperty("Завод").GetString());

        // Наименование и тип — свойства самой записи, в реквизитах их нет.
        Assert.Equal("EKF — автоматы", resolved.GetProperty("displayName").GetString());
        Assert.Equal(seed.CertTypeId.ToString(), resolved.GetProperty("_typeId").GetString());

        // Скан — файл-вложением той же формы, что понимает TypstFileMaterializer.
        var scan = resolved.GetProperty("Скан");
        Assert.Equal("file", scan.GetProperty("$type").GetString());
        Assert.Equal("quality/scan.pdf", scan.GetProperty("blobPath").GetString());
        Assert.Equal("скан.pdf", scan.GetProperty("fileName").GetString());
        Assert.Equal("application/pdf", scan.GetProperty("mimeType").GetString());
    }

    /// <summary>
    /// Без скана ключа нет вовсе. Пустое вложение шаблон не отличил бы от «скан не загрузился», а
    /// одноимённый реквизит оно бы затёрло.
    /// </summary>
    [Fact]
    public async Task QualityDocument_WithoutScan_HasNoScanKey()
    {
        var seed = await SeedAsync();
        Guid qualityId;
        using (var scope = fixture.Services.CreateScope())
            qualityId = (await AddQualityAsync(scope, seed.CertTypeId, "Декларация",
                "{'НомерДок':'Д-1'}", CatalogScope.Set, seed.SetId)).Id;

        var resolved = await ResolveFieldAsync(await DocRefencingAsync(seed, qualityId), "Качество");

        Assert.Equal("Д-1", resolved.GetProperty("НомерДок").GetString());
        Assert.False(resolved.TryGetProperty("Скан", out _));
    }

    /// <summary>Библиотека видна по цепочке вверх: документ уровня стройки разворачивается в комплекте.</summary>
    [Fact]
    public async Task QualityDocument_OnConstructionLevel_VisibleFromSet()
    {
        var seed = await SeedAsync();
        Guid qualityId;
        using (var scope = fixture.Services.CreateScope())
            qualityId = (await AddQualityAsync(scope, seed.CertTypeId, "Общий сертификат",
                "{'НомерДок':'С-2'}", CatalogScope.Construction, seed.ConstructionId)).Id;

        var resolved = await ResolveFieldAsync(await DocRefencingAsync(seed, qualityId), "Качество");

        Assert.Equal("Общий сертификат", resolved.GetProperty("displayName").GetString());
    }

    /// <summary>
    /// Видимость НЕ глобальна: документ качества чужой стройки не разворачивается, и ссылка остаётся
    /// сырой — то есть корректно флагается как неразрешённая, а не подмешивает чужие данные.
    ///
    /// <para>Отказ при этом ПОМЕЧЕН причиной: «есть, но не отсюда» — не «записи нет», и человеку эти
    /// два случая говорят разное (см. <see cref="QualityOutOfScope_Diagnostic_SaysRecordExists"/>).</para>
    /// </summary>
    [Fact]
    public async Task QualityDocument_OutsideScopeChain_NotResolved()
    {
        var seed = await SeedAsync();
        Guid foreignConstructionId, qualityId;
        using (var scope = fixture.Services.CreateScope())
        {
            foreignConstructionId = (await M(scope).Send(new CreateConstructionCommand("Чужой объект", _userId))).Id;
            qualityId = (await AddQualityAsync(scope, seed.CertTypeId, "Чужой сертификат",
                "{'НомерДок':'X-1'}", CatalogScope.Construction, foreignConstructionId)).Id;
        }

        var resolved = await ResolveFieldAsync(await DocRefencingAsync(seed, qualityId), "Качество");

        Assert.Equal("instance", resolved.GetProperty("$ref").GetString());
        Assert.False(resolved.TryGetProperty("displayName", out _));
        Assert.Equal(RefUnresolved.QualityOutOfScope, resolved.GetProperty(RefUnresolved.Key).GetString());
    }

    /// <summary>
    /// Диагностика различает два отказа. Сказать «запись не найдена или удалена» про живой документ
    /// качества — послать человека искать то, что на месте; чинится это перемещением документа выше
    /// по оси, а не поиском. Ровно ради такого различения и заведён <c>RefUnresolved</c> (#723).
    /// </summary>
    [Fact]
    public async Task QualityOutOfScope_Diagnostic_SaysRecordExists()
    {
        var seed = await SeedAsync();
        Guid qualityId;
        using (var scope = fixture.Services.CreateScope())
        {
            var foreign = await M(scope).Send(new CreateConstructionCommand("Чужой объект", _userId));
            qualityId = (await AddQualityAsync(scope, seed.CertTypeId, "Чужой сертификат",
                "{'НомерДок':'X-1'}", CatalogScope.Construction, foreign.Id)).Id;
        }
        var ownerId = await DocRefencingAsync(seed, qualityId);

        using var s = fixture.Services.CreateScope();
        var inst = await M(s).Send(new GetDocumentInstanceQuery(ownerId));
        var resolver = s.ServiceProvider.GetRequiredService<IEntityResolver>();
        var ctx = await resolver.ResolveAsync(DocumentView.From(inst!));
        var diagnostics = new List<ResolutionDiagnostic>();
        ResolutionScanner.ScanLeftoverRefs(ctx, diagnostics);

        var d = Assert.Single(diagnostics, x => x.Code == "quality-out-of-scope");
        Assert.Contains("вне области видимости", d.Message);
        Assert.DoesNotContain(diagnostics, x => x.Code == "leftover-ref");
    }

    /// <summary>
    /// Ссылка в никуда по-прежнему помечается как отсутствие цели — новая причина не поглотила
    /// старую (иначе «документ удалён» перестало бы диагностироваться вовсе).
    /// </summary>
    [Fact]
    public async Task UnknownId_Diagnostic_StaysNotFound()
    {
        var seed = await SeedAsync();
        var ownerId = await DocRefencingAsync(seed, Guid.NewGuid());

        using var s = fixture.Services.CreateScope();
        var inst = await M(s).Send(new GetDocumentInstanceQuery(ownerId));
        var resolver = s.ServiceProvider.GetRequiredService<IEntityResolver>();
        var ctx = await resolver.ResolveAsync(DocumentView.From(inst!));

        var diagnostics = new List<ResolutionDiagnostic>();
        ResolutionScanner.ScanLeftoverRefs(ctx, diagnostics);

        Assert.Single(diagnostics, x => x.Code == "leftover-ref");
    }

    /// <summary>
    /// Каскад срабатывает ТОЛЬКО при промахе: документ комплекта разворачивается как прежде, и
    /// одноимённого поиска в библиотеке не происходит.
    /// </summary>
    [Fact]
    public async Task SetDocument_StillResolvedFirst_CascadeNotUsed()
    {
        var seed = await SeedAsync();
        Guid targetId;
        using (var scope = fixture.Services.CreateScope())
        {
            var target = await M(scope).Send(new AddDocumentToSetCommand(seed.SetId, seed.DocTypeId));
            await M(scope).Send(new UpdateRequisitesCommand(target.Id, J("{'Номер':'АОСР-7'}")));
            targetId = target.Id;
        }

        var resolved = await ResolveFieldAsync(await DocRefencingAsync(seed, targetId), "Качество");

        Assert.Equal("АОСР-7", resolved.GetProperty("Номер").GetString());
        Assert.Equal(seed.DocTypeId.ToString(), resolved.GetProperty("_typeId").GetString());
        Assert.False(resolved.TryGetProperty("displayName", out _));   // форма документа комплекта прежняя
    }

    /// <summary>Ссылка в никуда остаётся сырой — каскад не превращает промах обоих доменов в разворот.</summary>
    [Fact]
    public async Task UnknownId_StaysUnresolved()
    {
        var seed = await SeedAsync();

        var resolved = await ResolveFieldAsync(await DocRefencingAsync(seed, Guid.NewGuid()), "Качество");

        Assert.Equal("instance", resolved.GetProperty("$ref").GetString());
    }

    // ── Предпросмотр: те же условия, что у резолвера ─────────────────────────────

    [Fact]
    public async Task Preview_QualityDocument_LabeledByName_NotNotFound()
    {
        var seed = await SeedAsync();
        Guid qualityId;
        using (var scope = fixture.Services.CreateScope())
            qualityId = (await AddQualityAsync(scope, seed.CertTypeId, "EKF — автоматы",
                "{'НомерДок':'ЕАЭС RU С-CN.1'}", CatalogScope.Set, seed.SetId)).Id;

        using var s = fixture.Services.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
        var labels = await MaterializeByIdMode.ResolveLabelsAsync(db, [qualityId], seed.SetId, default);

        Assert.Equal("EKF — автоматы", labels[qualityId]);
        Assert.False(MaterializeByIdMode.IsProblem(labels[qualityId]));
    }

    [Fact]
    public async Task Preview_QualityDocumentOutsideScope_LabeledAsProblem()
    {
        var seed = await SeedAsync();
        Guid qualityId;
        using (var scope = fixture.Services.CreateScope())
        {
            var foreign = await M(scope).Send(new CreateConstructionCommand("Чужой объект", _userId));
            qualityId = (await AddQualityAsync(scope, seed.CertTypeId, "Чужой сертификат",
                "{'НомерДок':'X-1'}", CatalogScope.Construction, foreign.Id)).Id;
        }

        using var s = fixture.Services.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
        var labels = await MaterializeByIdMode.ResolveLabelsAsync(db, [qualityId], seed.SetId, default);

        Assert.Equal(MaterializeByIdMode.ForeignScopeQualityLabel, labels[qualityId]);
        Assert.True(MaterializeByIdMode.IsProblem(labels[qualityId]));
    }

    /// <summary>
    /// Комплект неизвестен (источник живёт выше и используется в разных) — цепочки не существует,
    /// проверяем лишь то, что запись есть. Та же терпимость, что у документов комплекта.
    /// </summary>
    [Fact]
    public async Task Preview_QualityDocument_WithoutSet_LabeledByName()
    {
        var seed = await SeedAsync();
        Guid qualityId;
        using (var scope = fixture.Services.CreateScope())
            qualityId = (await AddQualityAsync(scope, seed.CertTypeId, "Системный сертификат",
                "{'НомерДок':'S-1'}", CatalogScope.System, null)).Id;

        using var s = fixture.Services.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
        var labels = await MaterializeByIdMode.ResolveLabelsAsync(db, [qualityId], null, default);

        Assert.Equal("Системный сертификат", labels[qualityId]);
    }

    /// <summary>Идентификатор, которого нет ни в одном домене, меток не получает — предпросмотр
    /// назовёт его «не найден» сам.</summary>
    [Fact]
    public async Task Preview_UnknownId_NoLabel()
    {
        var seed = await SeedAsync();

        using var s = fixture.Services.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
        var labels = await MaterializeByIdMode.ResolveLabelsAsync(db, [Guid.NewGuid()], seed.SetId, default);

        Assert.Empty(labels);
    }

    // ── Смежные потребители instance-ссылки ──────────────────────────────────────

    /// <summary>
    /// Внешнее чтение (MCP <c>get_document</c>) отдаёт реквизиты документа качества, а не стаб
    /// «сходи за ним отдельным вызовом».
    ///
    /// <para>Свёртка снимка заменяет развёрнутый документ на <c>{"$document": id}</c> в расчёте, что
    /// агент дочитает его сам, — но дочитать документ качества внешнему чтению пока негде: тем же
    /// идентификатором <c>get_document</c> не найдёт ничего. Свернув его, мы выбросили бы только что
    /// найденные данные и подменили их мёртвым указателем.</para>
    /// </summary>
    [Fact]
    public async Task Snapshot_QualityDocument_NotFoldedIntoDeadPointer()
    {
        var seed = await SeedAsync();
        Guid qualityId;
        using (var scope = fixture.Services.CreateScope())
            qualityId = (await AddQualityAsync(scope, seed.CertTypeId, "EKF — автоматы",
                "{'НомерДок':'ЕАЭС RU С-CN.1'}", CatalogScope.Set, seed.SetId)).Id;
        var ownerId = await DocRefencingAsync(seed, qualityId);

        using var s = fixture.Services.CreateScope();
        var snapshot = s.ServiceProvider.GetRequiredService<IDomainSnapshotService>();
        var detail = await snapshot.GetDocumentAsync(ownerId);

        var quality = detail!.Requisites.GetProperty("Качество");
        Assert.False(quality.TryGetProperty("$document", out _), "документ качества свёрнут в нефетчабельный адрес");
        Assert.Equal("ЕАЭС RU С-CN.1", quality.GetProperty("НомерДок").GetString());
    }

    /// <summary>
    /// Документ комплекта по-прежнему сворачивается — новый домен не отменил экономию, ради которой
    /// свёртка сделана: за документом комплекта агент сходит тем же <c>get_document</c>.
    /// </summary>
    [Fact]
    public async Task Snapshot_SetDocument_StillFolded()
    {
        var seed = await SeedAsync();
        Guid targetId;
        using (var scope = fixture.Services.CreateScope())
        {
            var target = await M(scope).Send(new AddDocumentToSetCommand(seed.SetId, seed.DocTypeId));
            await M(scope).Send(new UpdateRequisitesCommand(target.Id, J("{'Номер':'АОСР-7'}")));
            targetId = target.Id;
        }
        var ownerId = await DocRefencingAsync(seed, targetId);

        using var s = fixture.Services.CreateScope();
        var snapshot = s.ServiceProvider.GetRequiredService<IDomainSnapshotService>();
        var detail = await snapshot.GetDocumentAsync(ownerId);

        Assert.Equal(targetId.ToString(), detail!.Requisites.GetProperty("Качество").GetProperty("$document").GetString());
    }

    /// <summary>
    /// Копирование в другой комплект НЕ стирает ссылку на документ качества уровня System: она
    /// разрешается и в новом расположении. Скраб исходит из того, что instance-ссылки структурно
    /// same-set, — с появлением второго домена это верно уже не для всех, и стереть такую ссылку
    /// значило бы молча выбросить рабочие данные, доложив о них как о «ссылках на документы комплекта».
    /// </summary>
    [Fact]
    public async Task CopyToAnotherSet_KeepsVisibleQualityRef()
    {
        var seed = await SeedAsync();
        Guid qualityId, targetSetId;
        using (var scope = fixture.Services.CreateScope())
        {
            qualityId = (await AddQualityAsync(scope, seed.CertTypeId, "Системный сертификат",
                "{'НомерДок':'S-1'}", CatalogScope.System, null)).Id;
            targetSetId = (await M(scope).Send(new CreateDocumentSetCommand(seed.SectionId, "Цель"))).Id;
        }
        var ownerId = await DocRefencingAsync(seed, qualityId);

        using var s = fixture.Services.CreateScope();
        var result = await M(s).Send(new CopyDocumentToSetCommand(ownerId, targetSetId, CopyStrategy.SmartCleanup));

        using var data = result.Instance.Data;
        var kept = data.RootElement.GetProperty("Качество");
        Assert.Equal(qualityId.ToString(), kept.GetProperty("instanceId").GetString());
        Assert.DoesNotContain(result.Warnings, w => w.Kind == "doc-ref");
    }

    /// <summary>
    /// А ссылку на документ качества, который в целевом расположении НЕ виден, скраб убирает вместе
    /// с остальными неразрешимыми — иначе копия унесла бы заведомо висячую ссылку.
    /// </summary>
    [Fact]
    public async Task CopyToAnotherConstruction_StripsInvisibleQualityRef()
    {
        var seed = await SeedAsync();
        Guid qualityId, targetSetId;
        using (var scope = fixture.Services.CreateScope())
        {
            qualityId = (await AddQualityAsync(scope, seed.CertTypeId, "Сертификат стройки",
                "{'НомерДок':'C-1'}", CatalogScope.Construction, seed.ConstructionId)).Id;
            var otherConstruction = await M(scope).Send(new CreateConstructionCommand("Другой объект", _userId));
            var otherSection = await M(scope).Send(new CreateSectionCommand(otherConstruction.Id, "ЭОМ"));
            targetSetId = (await M(scope).Send(new CreateDocumentSetCommand(otherSection.Id, "Цель"))).Id;
        }
        var ownerId = await DocRefencingAsync(seed, qualityId);

        using var s = fixture.Services.CreateScope();
        var result = await M(s).Send(new CopyDocumentToSetCommand(ownerId, targetSetId, CopyStrategy.SmartCleanup));

        using var data = result.Instance.Data;
        Assert.False(data.RootElement.TryGetProperty("Качество", out _));
        Assert.Contains(result.Warnings, w => w.Kind == "doc-ref");
    }

    // ── Сквозной путь живого кейса ───────────────────────────────────────────────

    /// <summary>
    /// Кейс комплекта 250701.ЭОМ-1 целиком: источник отдаёт колонку «Ид» с идентификаторами
    /// библиотеки качества, маппинг кладёт её в <c>doc-ref</c>-поле, и в контекст приезжает
    /// развёрнутый документ качества со сканом — не строка и не висячая ссылка.
    ///
    /// <para>Проверяется полным проходом резолва (тот же порядок, что у генерации PDF), а не по
    /// промежуточной форме: обещание пользователю — реквизиты в шаблоне, а <c>$ref</c> деталь.
    /// Именно этот путь до issue #733 молча давал пустое поле при корректной настройке.</para>
    /// </summary>
    [Fact]
    public async Task EndToEnd_BoundSource_MaterializesQualityDocumentIntoContext()
    {
        var seed = await SeedAsync();
        using var scope = fixture.Services.CreateScope();
        var m = M(scope);
        var svc = scope.ServiceProvider.GetRequiredService<IDataSetService>();

        var quality = await AddQualityAsync(scope, seed.CertTypeId, "EKF — автоматы",
            "{'НомерДок':'ЕАЭС RU С-CN.1'}", CatalogScope.Set, seed.SetId,
            "quality/scan.pdf", "скан.pdf", "application/pdf");

        // Строка источника: одно doc-ref-поле, нацеленное на ТИП документа качества — так союз
        // «Документ качества» и собирается в UI, типы у них настоящие DocumentTypes.
        var rowType = await m.Send(new CreateDocumentTypeCommand("СтрокаКачества", $"QROW{Guid.NewGuid():N}"[..12],
            DocumentTypeKind.Composite, null,
            J($"{{'fields':[{{'key':'Документ','type':'doc-ref','typeId':'{seed.CertTypeId}'}}]}}")));

        // Свой тип владельца с объявленным полем «Качество»: резолвер не пишет в ключ вне схемы
        // (issue #737), а общий тип «Акт» из Seed полей не имеет — он нужен другим тестам пустым.
        var ownerType = await m.Send(new CreateDocumentTypeCommand("АктСКачеством", $"AQ{Guid.NewGuid():N}"[..12],
            DocumentTypeKind.Document, null,
            J($"{{'fields':[{{'key':'Качество','type':'array','typeId':'{rowType.Id}'}}]}}")));
        var owner = await m.Send(new AddDocumentToSetCommand(seed.SetId, ownerType.Id));

        var file = await svc.UploadFileAsync(new UploadFileInput(
            Encoding.UTF8.GetBytes($"Ид\n{quality.Id}\n"), "quality.csv", "text/csv", "Тест", "System", null), default);
        var candidate = (await svc.DetectSourceCandidatesAsync(file.Id, default)).Single();
        var source = await svc.CreateSourceAsync(file.Id,
            new CreateSourceInput("Документы качества", candidate.SheetOrPath, null), default);
        await svc.SetMaterializationAsync(source.Id, rowType.Id,
            new Dictionary<string, string> { ["Документ"] = "Ид" }, discriminator: null, byIdColumn: null, default);
        await svc.CreateBindingAsync(new CreateBindingInput(owner.Id, source.Id, "Качество", null), default);

        var inst = await m.Send(new GetDocumentInstanceQuery(owner.Id));
        var view = DocumentView.From(inst!);
        var entity = scope.ServiceProvider.GetRequiredService<IEntityResolver>();
        var ctx = await entity.ResolveAsync(view);
        await scope.ServiceProvider.GetRequiredService<IDataSetResolver>().InjectAsync(ctx, view, null, default);
        // Второй проход — именно он разворачивает ссылки, добавленные привязкой.
        await entity.ResolveContextRefsAsync(ctx, view.DocumentSetId);

        var rows = (JsonElement)ctx.Data["Качество"]!;
        var docRef = Assert.Single(rows.EnumerateArray()).GetProperty("Документ");
        Assert.False(docRef.TryGetProperty("$ref", out _), "ссылка осталась неразрешённой");
        Assert.Equal("ЕАЭС RU С-CN.1", docRef.GetProperty("НомерДок").GetString());
        Assert.Equal("EKF — автоматы", docRef.GetProperty("displayName").GetString());
        Assert.Equal("quality/scan.pdf", docRef.GetProperty("Скан").GetProperty("blobPath").GetString());
    }
}
