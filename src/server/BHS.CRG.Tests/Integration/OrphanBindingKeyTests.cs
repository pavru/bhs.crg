using System.Text;
using System.Text.Json;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Generation;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Documents;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Осиротевшие привязки наборов данных (issue #737).
///
/// <para>Живой кейс, с которого началось: в комплекте 250701.ЭОМ-1 у документа «Реестр
/// исполнительной документации» поле переименовали «ОсновнойДокументы» → «ОсновныеДокументы».
/// Человек завёл привязку заново, а старая осталась — и продолжала наливать устаревшие данные в
/// ключ, которого в схеме нет. В <c>data.json</c> они попадали, шаблон их не ждал, аудит молчал:
/// он сверяет реквизиты, а разошлась ПРИВЯЗКА. Найти это можно было только глазами в отладочном
/// ZIP.</para>
///
/// <para>Три причины, три проверки: резолвер писал мимо схемы молча, аудит держателей ключа вне
/// реквизитов не видел, переименование ключа привязки не переносило.</para>
/// </summary>
[Collection("Integration")]
public class OrphanBindingKeyTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static IMediator M(IServiceScope s) => s.ServiceProvider.GetRequiredService<IMediator>();
    private static IDataSetService Svc(IServiceScope s) => s.ServiceProvider.GetRequiredService<IDataSetService>();
    private static JsonDocument J(string singleQuoted) => JsonDocument.Parse(singleQuoted.Replace('\'', '"'));

    private sealed record Seed(Guid TypeId, Guid RowTypeId, Guid InstanceId, Guid SourceId);

    /// <summary>
    /// Тип с полем «ОсновныеДокументы» (как в живом кейсе ПОСЛЕ переименования), документ этого
    /// типа и материализованный источник — привязку каждый тест заводит свою.
    /// </summary>
    private async Task<Seed> SeedAsync(IServiceScope scope)
    {
        var m = M(scope);
        var rowType = await m.Send(new CreateDocumentTypeCommand("Строка", $"ROW{Guid.NewGuid():N}"[..12],
            DocumentTypeKind.Composite, null, J("{'fields':[{'key':'Наименование','type':'string'}]}")));
        var docType = await m.Send(new CreateDocumentTypeCommand("Реестр", $"REG{Guid.NewGuid():N}"[..12],
            DocumentTypeKind.Document, null,
            J($"{{'fields':[{{'key':'ОсновныеДокументы','type':'array','typeId':'{rowType.Id}'}}]}}")));

        var construction = await m.Send(new CreateConstructionCommand("Объект", Guid.NewGuid()));
        var section = await m.Send(new CreateSectionCommand(construction.Id, "ЭОМ"));
        var set = await m.Send(new CreateDocumentSetCommand(section.Id, "250701.ЭОМ-1"));
        var instance = await m.Send(new AddDocumentToSetCommand(set.Id, docType.Id));

        var svc = Svc(scope);
        var file = await svc.UploadFileAsync(new UploadFileInput(
            Encoding.UTF8.GetBytes("A\nАкт №1\n"), "docs.csv", "text/csv", "Тест", "System", null), default);
        var candidate = (await svc.DetectSourceCandidatesAsync(file.Id, default)).Single();
        var source = await svc.CreateSourceAsync(file.Id, new CreateSourceInput("Документы", candidate.SheetOrPath, null), default);
        await svc.SetMaterializationAsync(source.Id, rowType.Id,
            new Dictionary<string, string> { ["Наименование"] = "A" }, discriminator: null, byIdColumn: null, default);

        return new Seed(docType.Id, rowType.Id, instance.Id, source.Id);
    }

    private static async Task<GenerationContext> ResolveAsync(
        IServiceScope scope, Guid instanceId, List<ResolutionDiagnostic> diagnostics)
    {
        var inst = await M(scope).Send(new GetDocumentInstanceQuery(instanceId));
        var view = DocumentView.From(inst!);
        var entity = scope.ServiceProvider.GetRequiredService<IEntityResolver>();
        var ctx = await entity.ResolveAsync(view);
        await scope.ServiceProvider.GetRequiredService<IDataSetResolver>().InjectAsync(ctx, view, diagnostics, default);
        return ctx;
    }

    // ── 1. Резолвер: честный отказ вместо молчаливой записи ──────────────────────

    /// <summary>
    /// Отказ — ПРЕДУПРЕЖДЕНИЕ, а не ошибка: Error обрывает выпуск документа целиком
    /// (<c>GenerateDocumentHandler</c> бросает <c>ResolutionValidationException</c> на любой), и
    /// живой комплект с одной устаревшей привязкой перестал бы и генерироваться, и показываться в
    /// предпросмотре. Данные при этом всё равно не пишутся — цель достигнута.
    /// </summary>
    [Fact]
    public async Task Resolver_OrphanTargetKey_Warns_AndWritesNothing()
    {
        using var scope = fixture.Services.CreateScope();
        var seed = await SeedAsync(scope);
        // Привязка на ключ ДО переименования — ровно та, что осталась в живой базе.
        await Svc(scope).CreateBindingAsync(
            new CreateBindingInput(seed.InstanceId, seed.SourceId, "ОсновнойДокументы", null), default);

        var diagnostics = new List<ResolutionDiagnostic>();
        var ctx = await ResolveAsync(scope, seed.InstanceId, diagnostics);

        // Ключа в контексте нет: молча-не-туда хуже честного отказа — иначе данные уезжают в
        // data.json под мёртвым ключом и выглядят как «взялись из ниоткуда».
        Assert.False(ctx.Data.ContainsKey("ОсновнойДокументы"));
        var issue = Assert.Single(diagnostics, d => d.Path == "ОсновнойДокументы");
        Assert.Equal(DiagnosticSeverity.Warning, issue.Severity);
        Assert.Contains("ОсновнойДокументы", issue.Message);
        // Ни одной ошибки: документ обязан остаться выпускаемым.
        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task Resolver_ValidTargetKey_StillFills()
    {
        using var scope = fixture.Services.CreateScope();
        var seed = await SeedAsync(scope);
        await Svc(scope).CreateBindingAsync(
            new CreateBindingInput(seed.InstanceId, seed.SourceId, "ОсновныеДокументы", null), default);

        var diagnostics = new List<ResolutionDiagnostic>();
        var ctx = await ResolveAsync(scope, seed.InstanceId, diagnostics);

        Assert.True(ctx.Data.ContainsKey("ОсновныеДокументы"));
        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    // ── 2. Аудит: держатели ключа вне реквизитов ─────────────────────────────────

    [Fact]
    public async Task Audit_Instance_FindsOrphanBinding()
    {
        using var scope = fixture.Services.CreateScope();
        var seed = await SeedAsync(scope);
        await Svc(scope).CreateBindingAsync(
            new CreateBindingInput(seed.InstanceId, seed.SourceId, "ОсновнойДокументы", null), default);

        var findings = await M(scope).Send(new AuditInstanceQuery(seed.InstanceId));

        var f = Assert.Single(findings, x => x.Code == BindingKeyAuditor.OrphanBinding);
        Assert.Equal("ОсновнойДокументы", f.Path);
        Assert.Contains("Документы", f.Message); // имя источника названо — иначе непонятно, какую привязку править
    }

    /// <summary>
    /// Аудит ТИПА проходит по всем инстансам и добавляет шаблоны привязок самого типа. Шаблон
    /// данных не портит, но следующая созданная по нему привязка родилась бы мёртвой.
    /// </summary>
    [Fact]
    public async Task Audit_Type_FindsOrphanBindingAndTemplate()
    {
        using var scope = fixture.Services.CreateScope();
        var seed = await SeedAsync(scope);
        await Svc(scope).CreateBindingAsync(
            new CreateBindingInput(seed.InstanceId, seed.SourceId, "ОсновнойДокументы", null), default);
        await Svc(scope).CreateTemplateAsync(seed.TypeId,
            new CreateTemplateInput("Старый шаблон", "ОсновнойДокументы", []), default);

        var report = await M(scope).Send(new AuditDocumentTypeQuery(seed.TypeId));

        Assert.Contains(report.Findings, f => f.Code == BindingKeyAuditor.OrphanBinding);
        Assert.Contains(report.Findings, f => f.Code == BindingKeyAuditor.OrphanBindingTemplate);
    }

    [Fact]
    public async Task Audit_ValidBinding_IsNotFlagged()
    {
        using var scope = fixture.Services.CreateScope();
        var seed = await SeedAsync(scope);
        await Svc(scope).CreateBindingAsync(
            new CreateBindingInput(seed.InstanceId, seed.SourceId, "ОсновныеДокументы", null), default);

        var findings = await M(scope).Send(new AuditInstanceQuery(seed.InstanceId));

        Assert.DoesNotContain(findings, x => x.Code == BindingKeyAuditor.OrphanBinding);
    }

    // ── 3. Переименование ключа переносит и привязки ─────────────────────────────

    /// <summary>
    /// Перенос при rename — спутник миграции данных (#357). Не перенеси привязку, и человек,
    /// переименовавший поле, увидел бы пустоту там, где были данные: привязка осталась бы на старом
    /// ключе, а резолвер (после п.1) честно отказался бы её применять.
    /// </summary>
    [Fact]
    public async Task MigrateFieldKey_MovesBindingTargetKey()
    {
        using var scope = fixture.Services.CreateScope();
        var seed = await SeedAsync(scope);
        await Svc(scope).CreateBindingAsync(
            new CreateBindingInput(seed.InstanceId, seed.SourceId, "ОсновнойДокументы", null), default);

        var result = await M(scope).Send(new MigrateFieldKeyCommand(
            seed.TypeId, "ОсновнойДокументы", "ОсновныеДокументы"));

        Assert.Equal(1, result.Bindings);
        var bindings = await Svc(scope).ListBindingsAsync(seed.InstanceId, default);
        Assert.Equal("ОсновныеДокументы", Assert.Single(bindings).TargetFieldKey);

        // И теперь привязка снова заполняет поле — то есть перенос вернул её в строй.
        var diagnostics = new List<ResolutionDiagnostic>();
        var ctx = await ResolveAsync(scope, seed.InstanceId, diagnostics);
        Assert.True(ctx.Data.ContainsKey("ОсновныеДокументы"));
        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task MigrateFieldKey_MovesTemplateKeyAndMappingKeys()
    {
        using var scope = fixture.Services.CreateScope();
        var seed = await SeedAsync(scope);
        await Svc(scope).CreateTemplateAsync(seed.TypeId,
            new CreateTemplateInput("Шаблон", null,
                new Dictionary<string, string> { ["ОсновнойДокументы"] = "A" }), default);

        var result = await M(scope).Send(new MigrateFieldKeyCommand(
            seed.TypeId, "ОсновнойДокументы", "ОсновныеДокументы"));

        Assert.Equal(1, result.Templates);
        var template = Assert.Single(await Svc(scope).ListTemplatesAsync(seed.TypeId, default));
        Assert.True(template.ColumnMappings.ContainsKey("ОсновныеДокументы"));
        Assert.False(template.ColumnMappings.ContainsKey("ОсновнойДокументы"));
    }

    /// <summary>
    /// Ключи маппинга ТАБЛИЧНОЙ привязки принадлежат типу СТРОКИ, а не владельцу, и переименование
    /// поля документа их касаться не должно. Случай не выдуманный: одноимённое поле в обоих типах
    /// («Номер» и у реестра, и у его строки) — обычное дело, и слепой перенос оставил бы в строке
    /// ключ, которого в её типе нет, тихо перестав заполнять колонку.
    /// </summary>
    [Fact]
    public async Task MigrateFieldKey_DoesNotTouchMappingKeysOfTableBinding()
    {
        using var scope = fixture.Services.CreateScope();
        var m = M(scope);
        var svc = Svc(scope);

        // «Наименование» есть И у строки, И у документа — переименовываем документное.
        var rowType = await m.Send(new CreateDocumentTypeCommand("Строка", $"ROW{Guid.NewGuid():N}"[..12],
            DocumentTypeKind.Composite, null, J("{'fields':[{'key':'Наименование','type':'string'}]}")));
        var docType = await m.Send(new CreateDocumentTypeCommand("Реестр", $"REG{Guid.NewGuid():N}"[..12],
            DocumentTypeKind.Document, null,
            J($"{{'fields':[{{'key':'Наименование','type':'string'}},"
              + $"{{'key':'Строки','type':'array','typeId':'{rowType.Id}'}}]}}")));

        var construction = await m.Send(new CreateConstructionCommand("Объект", Guid.NewGuid()));
        var section = await m.Send(new CreateSectionCommand(construction.Id, "ЭОМ"));
        var set = await m.Send(new CreateDocumentSetCommand(section.Id, "Комплект"));
        var instance = await m.Send(new AddDocumentToSetCommand(set.Id, docType.Id));

        var file = await svc.UploadFileAsync(new UploadFileInput(
            Encoding.UTF8.GetBytes("A\nАкт №1\n"), "docs.csv", "text/csv", "Тест", "System", null), default);
        var candidate = (await svc.DetectSourceCandidatesAsync(file.Id, default)).Single();
        var source = await svc.CreateSourceAsync(file.Id, new CreateSourceInput("Документы", candidate.SheetOrPath, null), default);
        // Табличная привязка: свой маппинг по полю ТИПА СТРОКИ.
        await svc.CreateBindingAsync(new CreateBindingInput(instance.Id, source.Id, "Строки",
            new Dictionary<string, string> { ["Наименование"] = "A" }), default);

        await m.Send(new MigrateFieldKeyCommand(docType.Id, "Наименование", "НаименованиеДокумента"));

        var binding = Assert.Single(await svc.ListBindingsAsync(instance.Id, default));
        Assert.Equal("Строки", binding.TargetFieldKey);
        Assert.True(binding.Mapping.ContainsKey("Наименование"), "ключ строки не должен переименовываться");
        Assert.False(binding.Mapping.ContainsKey("НаименованиеДокумента"));
    }

    /// <summary>
    /// В занятую цель не пишем — то же правило, что у миграции данных: если привязку уже
    /// перенастроили руками на новый ключ, её настройка авторитетнее нашей догадки.
    /// </summary>
    [Fact]
    public async Task MigrateFieldKey_DoesNotOverwriteAlreadyCorrectBinding()
    {
        using var scope = fixture.Services.CreateScope();
        var seed = await SeedAsync(scope);
        await Svc(scope).CreateBindingAsync(
            new CreateBindingInput(seed.InstanceId, seed.SourceId, "ОсновныеДокументы", null), default);

        var result = await M(scope).Send(new MigrateFieldKeyCommand(
            seed.TypeId, "ОсновнойДокументы", "ОсновныеДокументы"));

        Assert.Equal(0, result.Bindings);
        Assert.Equal("ОсновныеДокументы", Assert.Single(
            await Svc(scope).ListBindingsAsync(seed.InstanceId, default)).TargetFieldKey);
    }
}
