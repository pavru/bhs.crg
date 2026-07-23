using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Templates;
using BHS.CRG.Domain.Documents;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Инвалидация вывода документов при изменении шаблона (issue #362, фаза 2): документы, чей
/// сгенерированный PDF устарел, сбрасываются в Draft. Проверяем три триггера (in-place правка,
/// смена дефолта, новая версия) и точность выборки (пиннутые vs no-pin).
/// </summary>
[Collection("Integration")]
public class TemplateInvalidationTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private IMediator Mediator(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<IMediator>();
    private readonly Guid _userId = Guid.NewGuid();

    private async Task<Guid> CreateSetAsync()
    {
        using var scope = fixture.Services.CreateScope();
        var m = Mediator(scope);
        var c = await m.Send(new CreateConstructionCommand("Объект", _userId));
        var s = await m.Send(new CreateSectionCommand(c.Id, "Раздел"));
        var set = await m.Send(new CreateDocumentSetCommand(s.Id, "Комплект"));
        return set.Id;
    }

    private async Task<Guid> CreateDocTypeAsync(string code)
    {
        using var scope = fixture.Services.CreateScope();
        var dt = await Mediator(scope).Send(
            new CreateDocumentTypeCommand(code, code, DocumentTypeKind.Document, null, JsonDocument.Parse(@"{""fields"":[]}")));
        return dt.Id;
    }

    private async Task<Domain.Templates.Template> CreateTemplateAsync(Guid dtId, string name)
    {
        using var scope = fixture.Services.CreateScope();
        return await Mediator(scope).Send(new CreateTemplateCommand(dtId, name, "content v1"));
    }

    // Добавляет документ; опционально пиннит на шаблон; помечает как сгенерированный (Status=Generated + файл).
    // Каждый шаг — в своём scope (свежий DbContext), иначе токен конкуренции между командами рассинхронится.
    private async Task<Guid> AddGeneratedDocAsync(Guid setId, Guid dtId, Guid? pinTemplateId = null)
    {
        Guid docId;
        using (var s = fixture.Services.CreateScope())
            docId = (await Mediator(s).Send(new AddDocumentToSetCommand(setId, dtId))).Id;

        if (pinTemplateId.HasValue)
            using (var s = fixture.Services.CreateScope())
                await Mediator(s).Send(new SetDocumentTemplateCommand(docId, pinTemplateId.Value)); // сброс в Draft — ок, до генерации

        using (var s = fixture.Services.CreateScope())
        {
            var repo = s.ServiceProvider.GetRequiredService<IDomainObjectRepository>();
            var fileRepo = s.ServiceProvider.GetRequiredService<IRepository<GeneratedFile>>();
            var obj = await repo.GetByIdAsync(docId);
            // Как в GenerateDocumentHandler: файл регистрируем как Added через его репозиторий
            // (иначе EF в трекнутой коллекции считает его Modified → UPDATE несуществующей строки).
            var gf = obj!.AddGeneratedFile(OutputFormat.Pdf, $"blob/{docId}.pdf");
            await fileRepo.AddAsync(gf);
            await repo.SaveChangesAsync();
        }
        return docId;
    }

    private async Task<DocumentStatus> StatusAsync(Guid instanceId)
    {
        using var scope = fixture.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDomainObjectRepository>();
        return (await repo.GetByIdAsync(instanceId))!.Status;
    }

    private async Task<Guid?> PinAsync(Guid instanceId)
    {
        using var scope = fixture.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDomainObjectRepository>();
        return (await repo.GetByIdAsync(instanceId))!.TemplateId;
    }

    private async Task<bool> TemplateExistsAsync(Guid dtId, Guid templateId)
    {
        using var scope = fixture.Services.CreateScope();
        var list = await Mediator(scope).Send(new ListTemplatesQuery(dtId));
        return list.Any(t => t.Id == templateId);
    }

    // ── Удаление версий (issue #364) ─────────────────────────────────────────

    [Fact]
    public async Task DeleteVersion_PinnedWithoutReassign_Throws_AndKeepsTemplate()
    {
        var dtId = await CreateDocTypeAsync("DT_DEL1");
        var t = await CreateTemplateAsync(dtId, "Шаблон");
        var setId = await CreateSetAsync();
        var docId = await AddGeneratedDocAsync(setId, dtId, pinTemplateId: t.Id);

        using (var scope = fixture.Services.CreateScope())
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Mediator(scope).Send(new DeleteTemplateCommand(t.Id, ReassignUsersToDefault: false)));

        Assert.True(await TemplateExistsAsync(dtId, t.Id));           // не удалён
        Assert.Equal(t.Id, await PinAsync(docId));                    // пин на месте
        Assert.Equal(DocumentStatus.Generated, await StatusAsync(docId)); // PDF не тронут
    }

    [Fact]
    public async Task DeleteVersion_PinnedWithReassign_UnpinsResetsAndDeletes()
    {
        var dtId = await CreateDocTypeAsync("DT_DEL2");
        var t = await CreateTemplateAsync(dtId, "Шаблон");
        var setId = await CreateSetAsync();
        var docId = await AddGeneratedDocAsync(setId, dtId, pinTemplateId: t.Id);

        using (var scope = fixture.Services.CreateScope())
            await Mediator(scope).Send(new DeleteTemplateCommand(t.Id, ReassignUsersToDefault: true));

        Assert.False(await TemplateExistsAsync(dtId, t.Id));  // удалён
        Assert.Null(await PinAsync(docId));                   // пин снят → резолв в дефолт
        Assert.Equal(DocumentStatus.Draft, await StatusAsync(docId)); // сброшен в черновик
    }

    [Fact]
    public async Task DeleteVersion_Unused_Deletes()
    {
        var dtId = await CreateDocTypeAsync("DT_DEL3");
        var t = await CreateTemplateAsync(dtId, "Шаблон");

        using (var scope = fixture.Services.CreateScope())
            await Mediator(scope).Send(new DeleteTemplateCommand(t.Id)); // reassign по умолчанию false — ок, не используется

        Assert.False(await TemplateExistsAsync(dtId, t.Id));
    }

    [Fact]
    public async Task Usage_ReportsPinCountsPerVersion()
    {
        var dtId = await CreateDocTypeAsync("DT_USE");
        var t1 = await CreateTemplateAsync(dtId, "Первый");
        var t2 = await CreateTemplateAsync(dtId, "Второй");
        var setId = await CreateSetAsync();
        await AddGeneratedDocAsync(setId, dtId, pinTemplateId: t1.Id);
        await AddGeneratedDocAsync(setId, dtId, pinTemplateId: t1.Id);
        await AddGeneratedDocAsync(setId, dtId); // no-pin — не должен считаться

        using var scope = fixture.Services.CreateScope();
        var usage = await Mediator(scope).Send(new GetTemplatesUsageQuery(dtId));

        Assert.Equal(2, usage[t1.Id].Count);
        Assert.Equal(2, usage[t1.Id].Names.Count);
        Assert.False(usage.ContainsKey(t2.Id)); // без пинов — отсутствует
    }

    // ── In-place правка ──────────────────────────────────────────────────────

    [Fact]
    public async Task InPlaceSave_ResetsDocumentPinnedToVersion()
    {
        var dtId = await CreateDocTypeAsync("DT_INV1");
        var t = await CreateTemplateAsync(dtId, "Шаблон");
        var setId = await CreateSetAsync();
        var docId = await AddGeneratedDocAsync(setId, dtId, pinTemplateId: t.Id);
        Assert.Equal(DocumentStatus.Generated, await StatusAsync(docId));

        using var scope = fixture.Services.CreateScope();
        await Mediator(scope).Send(new SaveTemplateContentCommand(t.Id, "content v1 edited"));

        Assert.Equal(DocumentStatus.Draft, await StatusAsync(docId));
    }

    [Fact]
    public async Task InPlaceSave_ResetsNoPinDoc_WhenVersionIsDefaultActive()
    {
        var dtId = await CreateDocTypeAsync("DT_INV2");
        var t = await CreateTemplateAsync(dtId, "Шаблон");
        using (var s = fixture.Services.CreateScope())
            await Mediator(s).Send(new SetTemplateDefaultCommand(t.Id));
        var setId = await CreateSetAsync();
        var docId = await AddGeneratedDocAsync(setId, dtId); // без пина → резолвится в default-active

        using var scope = fixture.Services.CreateScope();
        await Mediator(scope).Send(new SaveTemplateContentCommand(t.Id, "edited"));

        Assert.Equal(DocumentStatus.Draft, await StatusAsync(docId));
    }

    [Fact]
    public async Task InPlaceSave_DoesNotResetNoPinDoc_WhenVersionNotDefault()
    {
        var dtId = await CreateDocTypeAsync("DT_INV3");
        var tDefault = await CreateTemplateAsync(dtId, "Дефолтный");
        var tOther = await CreateTemplateAsync(dtId, "Другой");
        using (var s = fixture.Services.CreateScope())
            await Mediator(s).Send(new SetTemplateDefaultCommand(tDefault.Id));
        var setId = await CreateSetAsync();
        var docId = await AddGeneratedDocAsync(setId, dtId); // no-pin → резолвится в tDefault

        // Правим НЕ дефолтный шаблон → no-pin документ не затронут.
        using var scope = fixture.Services.CreateScope();
        await Mediator(scope).Send(new SaveTemplateContentCommand(tOther.Id, "edited"));

        Assert.Equal(DocumentStatus.Generated, await StatusAsync(docId));
    }

    [Fact]
    public async Task InPlaceSave_DoesNotResetDocPinnedToOtherVersion()
    {
        var dtId = await CreateDocTypeAsync("DT_INV4");
        var tEdited = await CreateTemplateAsync(dtId, "Правимый");
        var tPinned = await CreateTemplateAsync(dtId, "Пиннутый");
        var setId = await CreateSetAsync();
        var docId = await AddGeneratedDocAsync(setId, dtId, pinTemplateId: tPinned.Id);

        using var scope = fixture.Services.CreateScope();
        await Mediator(scope).Send(new SaveTemplateContentCommand(tEdited.Id, "edited"));

        Assert.Equal(DocumentStatus.Generated, await StatusAsync(docId));
    }

    // ── Смена дефолта ────────────────────────────────────────────────────────

    [Fact]
    public async Task SetDefault_ResetsNoPinDocs_ButNotPinned()
    {
        var dtId = await CreateDocTypeAsync("DT_INV5");
        var t1 = await CreateTemplateAsync(dtId, "Первый");
        var t2 = await CreateTemplateAsync(dtId, "Второй");
        using (var s = fixture.Services.CreateScope())
            await Mediator(s).Send(new SetTemplateDefaultCommand(t1.Id));
        var setId = await CreateSetAsync();
        var noPin = await AddGeneratedDocAsync(setId, dtId);
        var pinned = await AddGeneratedDocAsync(setId, dtId, pinTemplateId: t1.Id);

        // Дефолт t1 → t2: no-pin резолвится в новый default → сброс; пиннутый на t1 не трогаем.
        using var scope = fixture.Services.CreateScope();
        await Mediator(scope).Send(new SetTemplateDefaultCommand(t2.Id));

        Assert.Equal(DocumentStatus.Draft, await StatusAsync(noPin));
        Assert.Equal(DocumentStatus.Generated, await StatusAsync(pinned));
    }

    // ── Новая версия ─────────────────────────────────────────────────────────

    [Fact]
    public async Task NewVersion_ResetsNoPinDocs_WhenVersionDefault_ButNotPinnedToOld()
    {
        var dtId = await CreateDocTypeAsync("DT_INV6");
        var t = await CreateTemplateAsync(dtId, "Шаблон");
        using (var s = fixture.Services.CreateScope())
            await Mediator(s).Send(new SetTemplateDefaultCommand(t.Id));
        var setId = await CreateSetAsync();
        var noPin = await AddGeneratedDocAsync(setId, dtId);
        var pinnedOld = await AddGeneratedDocAsync(setId, dtId, pinTemplateId: t.Id);

        // Новая версия наследует дефолт → default-active сместился → no-pin сброс; пиннутый на
        // старую версию не трогаем (её содержимое не менялось).
        using var scope = fixture.Services.CreateScope();
        await Mediator(scope).Send(new UpdateTemplateCommand(t.Id, "content v2"));

        Assert.Equal(DocumentStatus.Draft, await StatusAsync(noPin));
        Assert.Equal(DocumentStatus.Generated, await StatusAsync(pinnedOld));
    }
}
