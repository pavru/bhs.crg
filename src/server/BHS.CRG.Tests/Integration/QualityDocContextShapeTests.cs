using System.Text.Json;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Generation;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Единая форма документа качества в контексте генерации (issue #736).
///
/// <para>Он попадает туда двумя путями — развёрнутой instance-ссылкой (#733) и связью
/// материал→документ (#624), — и формы разошлись: ссылка давала реквизиты плюс наименование, штамп
/// типа и скан, а связь — голые реквизиты. Шаблон, написанный на одну форму, молча давал пустоту на
/// другой, и узнать об этом автору было неоткуда: «сертификат» на месте, а <c>.Скан</c> существует
/// только у одного пути. Печать сертификата приложением к акту идёт как раз через связь материала —
/// то есть недостающим оказывался самый нужный ключ.</para>
/// </summary>
[Collection("Integration")]
public class QualityDocContextShapeTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static IMediator M(IServiceScope s) => s.ServiceProvider.GetRequiredService<IMediator>();
    private static JsonDocument J(string singleQuoted) => JsonDocument.Parse(singleQuoted.Replace('\'', '"'));

    private sealed record Seed(Guid SetId, Guid CertTypeId, Guid MaterialTypeId, Guid ActTypeId);

    /// <summary>
    /// Тип материала с полем идентичности и полем документа качества (тэги — как в рабочей
    /// настройке), акт со списком материалов и тип сертификата.
    /// </summary>
    private async Task<Seed> SeedAsync(IServiceScope scope)
    {
        var m = M(scope);
        var certType = await m.Send(new CreateDocumentTypeCommand("Сертификат", $"CERT{Guid.NewGuid():N}"[..12],
            DocumentTypeKind.Document, null, J("{'fields':[{'key':'НомерДок','type':'string'}]}")));

        var materialType = await m.Send(new CreateDocumentTypeCommand("Материал", $"MAT{Guid.NewGuid():N}"[..12],
            DocumentTypeKind.Composite, null, J("""
              {'fields':[
                {'key':'Артикул','type':'string','tags':['identity']},
                {'key':'Качество','type':'complex','tags':['material.qualityDocLink']}
              ]}
              """)));

        var actType = await m.Send(new CreateDocumentTypeCommand("Акт", $"ACT{Guid.NewGuid():N}"[..12],
            DocumentTypeKind.Document, null,
            J($"{{'fields':[{{'key':'Материалы','type':'array','typeId':'{materialType.Id}'}},"
              + "{'key':'Сертификат','type':'doc-ref'}]}")));

        var construction = await m.Send(new CreateConstructionCommand("Объект", Guid.NewGuid()));
        var section = await m.Send(new CreateSectionCommand(construction.Id, "ЭОМ"));
        var set = await m.Send(new CreateDocumentSetCommand(section.Id, "Комплект"));
        return new Seed(set.Id, certType.Id, materialType.Id, actType.Id);
    }

    private static Task<QualityDocument> AddCertAsync(IServiceScope scope, Guid typeId, string name) =>
        M(scope).Send(new CreateQualityDocumentCommand(typeId, name, J("{'НомерДок':'ЕАЭС RU С-CN.1'}"),
            CatalogScope.System, null, QualityDocSource.Manual,
            "quality/scan.pdf", "скан.pdf", "application/pdf"));

    /// <summary>Полный проход резолва — тот же порядок, что у генерации PDF.</summary>
    private static async Task<GenerationContext> ResolveAsync(IServiceScope scope, Guid instanceId)
    {
        var inst = await M(scope).Send(new GetDocumentInstanceQuery(instanceId));
        var view = DocumentView.From(inst!);
        var entity = scope.ServiceProvider.GetRequiredService<IEntityResolver>();
        var ctx = await entity.ResolveAsync(view);
        await scope.ServiceProvider.GetRequiredService<IQualityLinkResolver>().InjectAsync(ctx, view);
        await entity.ResolveContextRefsAsync(ctx, view.DocumentSetId);
        return ctx;
    }

    /// <summary>
    /// Сертификат, пришедший ЧЕРЕЗ СВЯЗЬ МАТЕРИАЛА, несёт наименование, штамп типа и скан — ровно
    /// как пришедший ссылкой. Раньше здесь были голые реквизиты, и «.Скан» в шаблоне давал пустоту.
    /// </summary>
    [Fact]
    public async Task MaterialLink_YieldsEnrichedShape()
    {
        using var scope = fixture.Services.CreateScope();
        var seed = await SeedAsync(scope);
        var cert = await AddCertAsync(scope, seed.CertTypeId, "EKF — автоматы");
        await M(scope).Send(new SetMaterialLinksCommand(
            CatalogScope.Set, seed.SetId, [new MaterialLinkInput("ВВГ-3х2.5", "Кабель")], cert.Id));

        var act = await M(scope).Send(new AddDocumentToSetCommand(seed.SetId, seed.ActTypeId));
        await M(scope).Send(new UpdateRequisitesCommand(act.Id,
            J("{'Материалы':[{'Артикул':'ВВГ-3х2.5'}]}")));

        var ctx = await ResolveAsync(scope, act.Id);
        var material = ((JsonElement)ctx.Data["Материалы"]!).EnumerateArray().Single();
        var quality = material.GetProperty("Качество");

        Assert.Equal("ЕАЭС RU С-CN.1", quality.GetProperty("НомерДок").GetString());
        Assert.Equal("EKF — автоматы", quality.GetProperty(QualityDocShape.DisplayNameKey).GetString());
        Assert.Equal(seed.CertTypeId.ToString(), quality.GetProperty(TypeStamper.TypeIdKey).GetString());

        var scan = quality.GetProperty(QualityDocShape.ScanKey);
        Assert.Equal("file", scan.GetProperty("$type").GetString());
        Assert.Equal("quality/scan.pdf", scan.GetProperty("blobPath").GetString());
    }

    /// <summary>
    /// Обе двери дают ОДИН объект. Проверяем сравнением, а не двумя списками ключей: разойдись формы
    /// снова хоть одним ключом, шаблон опять начнёт работать через раз, а сломается это молча.
    ///
    /// <para>Реквизиты здесь скалярные, и это не случайность: выравнена ОБОЛОЧКА, а разрешение
    /// вложенных instance-ссылок у двух путей по-прежнему разное (см. <see cref="QualityDocShape"/>).
    /// Сравнивать объекты с такой ссылкой внутри значило бы закреплять тестом то, чего изменение
    /// не обещает.</para>
    /// </summary>
    [Fact]
    public async Task BothPaths_ProduceIdenticalShape()
    {
        using var scope = fixture.Services.CreateScope();
        var seed = await SeedAsync(scope);
        var cert = await AddCertAsync(scope, seed.CertTypeId, "Сертификат на кабель");
        await M(scope).Send(new SetMaterialLinksCommand(
            CatalogScope.Set, seed.SetId, [new MaterialLinkInput("ВВГ-3х2.5", null)], cert.Id));

        var act = await M(scope).Send(new AddDocumentToSetCommand(seed.SetId, seed.ActTypeId));
        // Один и тот же сертификат: слева — через связь материала, справа — instance-ссылкой.
        await M(scope).Send(new UpdateRequisitesCommand(act.Id, J(
            "{'Материалы':[{'Артикул':'ВВГ-3х2.5'}],"
            + $"'Сертификат':{{'$ref':'instance','instanceId':'{cert.Id}'}}}}")));

        var ctx = await ResolveAsync(scope, act.Id);
        var viaLink = ((JsonElement)ctx.Data["Материалы"]!).EnumerateArray().Single().GetProperty("Качество");
        var viaRef = (JsonElement)ctx.Data["Сертификат"]!;

        Assert.Equal(
            JsonSerializer.Serialize(viaLink.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)
                .ToDictionary(p => p.Name, p => p.Value.GetRawText())),
            JsonSerializer.Serialize(viaRef.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)
                .ToDictionary(p => p.Name, p => p.Value.GetRawText())));
    }

    /// <summary>
    /// То, что видит ШАБЛОН: после штамповки у сертификата стоит <c>_type</c> его ФАКТИЧЕСКОГО типа,
    /// а не объявленного типа поля.
    ///
    /// <para>Проверяется отдельно от <c>_typeId</c>, потому что это разные вещи: сырой маркер
    /// <c>TypeStamper</c> потребляет и убирает, а шаблон диспетчеризуется по развёрнутому
    /// <c>_type</c>. Поле документа качества объявлено на БАЗОВЫЙ тип («Документ подтверждающий
    /// качество»), а записи имеют конкретные подтипы — сертификат, декларация, отказное письмо, —
    /// и различать их шаблон раньше не мог: через связь материала фактический тип не приезжал.</para>
    /// </summary>
    [Fact]
    public async Task MaterialLink_StampsActualDocumentType()
    {
        using var scope = fixture.Services.CreateScope();
        var seed = await SeedAsync(scope);
        // Подтип базового типа сертификата — как «Сертификат соответствия» под «Документом
        // подтверждающим качество» в рабочей настройке.
        var subType = await M(scope).Send(new CreateDocumentTypeCommand(
            "Сертификат соответствия", $"SUB{Guid.NewGuid():N}"[..12],
            DocumentTypeKind.Document, seed.CertTypeId, J("{'fields':[]}")));
        var cert = await M(scope).Send(new CreateQualityDocumentCommand(
            subType.Id, "EKF — автоматы", J("{'НомерДок':'1'}"),
            CatalogScope.System, null, QualityDocSource.Manual, null, null, null));
        await M(scope).Send(new SetMaterialLinksCommand(
            CatalogScope.Set, seed.SetId, [new MaterialLinkInput("ВВГ-3х2.5", null)], cert.Id));

        var act = await M(scope).Send(new AddDocumentToSetCommand(seed.SetId, seed.ActTypeId));
        await M(scope).Send(new UpdateRequisitesCommand(act.Id, J("{'Материалы':[{'Артикул':'ВВГ-3х2.5'}]}")));

        var ctx = await ResolveAsync(scope, act.Id);
        var types = (await M(scope).Send(new ListDocumentTypesQuery())).ToDictionary(t => t.Id);
        TypeStamper.Stamp(ctx, seed.ActTypeId, types);

        var quality = ((JsonElement)ctx.Data["Материалы"]!).EnumerateArray().Single().GetProperty("Качество");
        Assert.Equal("Сертификат соответствия", quality.GetProperty(TypeStamper.MetaKey).GetProperty("name").GetString());
        // Сырой маркер потреблён штамповкой и в вывод не уезжает.
        Assert.False(quality.TryGetProperty(TypeStamper.TypeIdKey, out _));
    }

    /// <summary>
    /// У документа без скана ключа «Скан» нет вовсе. Пустое вложение было бы хуже его отсутствия:
    /// шаблон не отличил бы «скана нет» от «скан не загрузился».
    /// </summary>
    [Fact]
    public async Task WithoutScan_KeyIsAbsent()
    {
        using var scope = fixture.Services.CreateScope();
        var seed = await SeedAsync(scope);
        var cert = await M(scope).Send(new CreateQualityDocumentCommand(
            seed.CertTypeId, "Без скана", J("{'НомерДок':'1'}"),
            CatalogScope.System, null, QualityDocSource.Manual, null, null, null));
        await M(scope).Send(new SetMaterialLinksCommand(
            CatalogScope.Set, seed.SetId, [new MaterialLinkInput("ВВГ-3х2.5", null)], cert.Id));

        var act = await M(scope).Send(new AddDocumentToSetCommand(seed.SetId, seed.ActTypeId));
        await M(scope).Send(new UpdateRequisitesCommand(act.Id, J("{'Материалы':[{'Артикул':'ВВГ-3х2.5'}]}")));

        var ctx = await ResolveAsync(scope, act.Id);
        var quality = ((JsonElement)ctx.Data["Материалы"]!).EnumerateArray().Single().GetProperty("Качество");

        Assert.False(quality.TryGetProperty(QualityDocShape.ScanKey, out _));
        Assert.Equal("Без скана", quality.GetProperty(QualityDocShape.DisplayNameKey).GetString());
    }
}
