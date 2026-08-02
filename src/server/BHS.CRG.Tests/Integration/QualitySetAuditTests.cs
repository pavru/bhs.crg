using System.Text.Json;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Сверка «реестр материалов ↔ карта документов качества» по комплекту (issue #589).
///
/// Проверка не бумажная: ровно её внешний агент делал руками, выгружая обе стороны целиком (151
/// строка реестра против 113 связей) ради вывода в десяток строк. Тест идёт сквозь всю цепочку —
/// составной ключ (#582) строится сервером, резолвер по нему находит связку, сканер (#585) видит
/// непривязанный материал, а предикат области продукции (#586) — сертификат не про этот товар.
/// </summary>
[Collection("Integration")]
public class QualitySetAuditTests(IntegrationTestFixture fx)
{
    private static IMediator M(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<IMediator>();

    private async Task<T> InScopeAsync<T>(Func<IMediator, Task<T>> action)
    {
        using var scope = fx.Services.CreateScope();
        return await action(M(scope));
    }

    private static string Uniq => Guid.NewGuid().ToString("N")[..8];

    /// <summary>Комплект + документ с массивом материалов + сертификат на автоматы EKF.</summary>
    private async Task<(Guid SetId, Guid InstanceId, Guid CertId)> SeedAsync()
    {
        var suffix = Uniq;

        var materialType = await InScopeAsync(m => m.Send(new CreateDocumentTypeCommand(
            $"Материал {suffix}", $"MAT{suffix}"[..11], DocumentTypeKind.Composite, null,
            JsonDocument.Parse("""
                { "fields": [
                    { "key": "Наименование", "type": "string", "tags": ["identity:1"] },
                    { "key": "Артикул", "type": "string", "tags": ["identity:2"] },
                    { "key": "ДокументКачества", "type": "complex", "tags": ["material.qualityDocLink"] } ] }
                """))));

        var docType = await InScopeAsync(m => m.Send(new CreateDocumentTypeCommand(
            $"Реестр материалов {suffix}", $"REG{suffix}"[..11], DocumentTypeKind.Document, null,
            JsonDocument.Parse($$"""
                { "fields": [ { "key": "Материалы", "type": "array", "typeId": "{{materialType.Id}}" } ] }
                """))));

        var certType = await InScopeAsync(m => m.Send(new CreateDocumentTypeCommand(
            $"Сертификат {suffix}", $"CRT{suffix}"[..11], DocumentTypeKind.Document, null,
            JsonDocument.Parse("""{"fields":[{"key":"Продукция","type":"string"}]}"""))));

        var cert = await InScopeAsync(m => m.Send(new CreateQualityDocumentCommand(
            certType.Id, $"EKF — автоматические выключатели {suffix}",
            JsonDocument.Parse("""{"Продукция":"Выключатели автоматические, торговой марки EKF, модель: AV-125"}"""),
            CatalogScope.System, null, QualityDocSource.Manual, null, null, null)));

        var construction = await InScopeAsync(m => m.Send(new CreateConstructionCommand($"Стройка {suffix}", Guid.NewGuid())));
        var section = await InScopeAsync(m => m.Send(new CreateSectionCommand(construction.Id, $"Раздел {suffix}")));
        var set = await InScopeAsync(m => m.Send(new CreateDocumentSetCommand(section.Id, $"Комплект {suffix}")));
        var instance = await InScopeAsync(m => m.Send(new AddDocumentToSetCommand(set.Id, docType.Id)));

        // Три материала: автомат (сертификат по делу), трубка (сертификат не про неё), кабель (без связки).
        await InScopeAsync(m => m.Send(new UpdateRequisitesCommand(instance.Id, JsonDocument.Parse("""
            { "Материалы": [
                { "Наименование": "Выключатель автоматический AV-125 3P 63А EKF", "Артикул": "AV-125-63" },
                { "Наименование": "Трубка термоусаживаемая ТУТ нг 20/10", "Артикул": "TUT-20" },
                { "Наименование": "Кабель ВВГнг 3х2.5", "Артикул": "VVG-3x25" } ] }
            """))));

        // Связки заводим ключами, которые строит сервер: порядок компонентов задан identity:1/identity:2.
        await InScopeAsync(m => m.Send(new SetMaterialLinksCommand(CatalogScope.System, null,
        [
            new MaterialLinkInput(IdentityKey.From(["Выключатель автоматический AV-125 3P 63А EKF", "AV-125-63"])),
            new MaterialLinkInput(IdentityKey.From(["Трубка термоусаживаемая ТУТ нг 20/10", "TUT-20"])),
        ], cert.Id)));

        return (set.Id, instance.Id, cert.Id);
    }

    [Fact]
    public async Task Audit_SeparatesMissingLinkFromImplausibleCertificate()
    {
        var (setId, instanceId, _) = await SeedAsync();

        var report = await InScopeAsync(m => m.Send(new QualitySetAuditQuery(setId)));

        Assert.Equal(1, report.Documents);
        Assert.Equal(0, report.Failed);
        // Кабель — без связки вовсе.
        Assert.Equal(1, report.MaterialsWithoutDoc);
        // Трубка — связка есть, но сертификат на автоматы: именно этот случай дал 68 неверных связок.
        Assert.Equal(1, report.ImplausibleDocs);
        // Автомат не в отчёте: связка на месте и сертификат про него.
        Assert.Equal(2, report.Rows.Count);
        Assert.All(report.Rows, r => Assert.Equal(instanceId, r.InstanceId));

        var missing = Assert.Single(report.Rows, r => r.Code == "material-no-quality-doc");
        Assert.Contains("кабель ввгнг 3х2.5 | vvg-3x25", missing.Message);   // составной ключ целиком
        Assert.StartsWith("Материалы[2]", missing.Path);   // адрес строки, а не пересказ

        var implausible = Assert.Single(report.Rows, r => r.Code == "quality-doc-implausible");
        Assert.StartsWith("Материалы[1]", implausible.Path);
    }

    /// <summary>
    /// Несуществующий комплект — отказ, а не «проблем нет». Пустой отчёт на опечатку в
    /// идентификаторе читается как чистая совесть, и это ровно тот молчаливый ноль, из-за которого
    /// неверные связки жили незамеченными.
    /// </summary>
    [Fact]
    public async Task UnknownSet_IsRejected_NotReportedAsClean()
        => await Assert.ThrowsAsync<KeyNotFoundException>(
            () => InScopeAsync(m => m.Send(new QualitySetAuditQuery(Guid.NewGuid()))));

    [Fact]
    public async Task EmptySet_IsQuietAndSaysSo()
    {
        var suffix = Uniq;
        var construction = await InScopeAsync(m => m.Send(new CreateConstructionCommand($"Стройка {suffix}", Guid.NewGuid())));
        var section = await InScopeAsync(m => m.Send(new CreateSectionCommand(construction.Id, $"Раздел {suffix}")));
        var set = await InScopeAsync(m => m.Send(new CreateDocumentSetCommand(section.Id, $"Комплект {suffix}")));

        var report = await InScopeAsync(m => m.Send(new QualitySetAuditQuery(set.Id)));

        Assert.Equal(0, report.Documents);
        Assert.Empty(report.Rows);
    }
}
