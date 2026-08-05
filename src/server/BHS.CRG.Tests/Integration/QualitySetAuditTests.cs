using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Generation;
using BHS.CRG.Application.Jobs;
using BHS.CRG.Application.Notifications;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Jobs;
using BHS.CRG.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;
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

    /// <summary>Прогон сверки — сервисом, а не запросом MediatR: синхронного вызова у неё нет
    /// (issue #628), запускает её фоновая задача.</summary>
    private async Task<QualityAuditReport> AuditAsync(Guid setId)
    {
        using var scope = fx.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IQualitySetAuditRunner>()
            .RunAsync(setId, QualitySetAuditRunner.DefaultLimit, null, CancellationToken.None);
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

        var report = await AuditAsync(setId);

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
        => await Assert.ThrowsAsync<NotFoundException>(
            () => AuditAsync(Guid.NewGuid()));

    [Fact]
    public async Task EmptySet_IsQuietAndSaysSo()
    {
        var suffix = Uniq;
        var construction = await InScopeAsync(m => m.Send(new CreateConstructionCommand($"Стройка {suffix}", Guid.NewGuid())));
        var section = await InScopeAsync(m => m.Send(new CreateSectionCommand(construction.Id, $"Раздел {suffix}")));
        var set = await InScopeAsync(m => m.Send(new CreateDocumentSetCommand(section.Id, $"Комплект {suffix}")));

        var report = await AuditAsync(set.Id);

        Assert.Equal(0, report.Documents);
        Assert.Empty(report.Rows);
    }

    // ── Фоновый прогон (issue #628) ────────────────────────────────────────────────────────────

    /// <summary>Второй документ того же типа — чтобы «на прогон» отличалось от «на документ».</summary>
    private async Task AddSecondDocumentAsync(Guid setId, Guid firstInstanceId)
    {
        Guid typeId;
        using (var scope = fx.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IDomainObjectRepository>();
            typeId = (await repo.GetByIdAsync(firstInstanceId))!.CompositeTypeId;
        }
        await InScopeAsync(m => m.Send(new AddDocumentToSetCommand(setId, typeId)));
    }

    /// <summary>Считает обращения к справочникам схемы, ничего не подменяя: сам прогон настоящий.</summary>
    private sealed class CountingValidator(IInstanceResolutionValidator inner) : IInstanceResolutionValidator
    {
        public int CatalogLoads;
        public int Validations;

        public Task<SchemaCatalog> LoadCatalogAsync(CancellationToken ct)
        {
            CatalogLoads++;
            return inner.LoadCatalogAsync(ct);
        }

        public Task<IReadOnlyList<ResolutionDiagnostic>> ValidateAsync(Guid instanceId, SchemaCatalog catalog, CancellationToken ct)
        {
            Validations++;
            return inner.ValidateAsync(instanceId, catalog, ct);
        }
    }

    /// <summary>
    /// Справочники схемы читаются ОДИН раз на прогон. До #628 проверка каждого документа тянула все
    /// типы документов и все примитивные типы заново — на комплекте в полсотни документов это сотня
    /// одинаковых запросов подряд, и ровно она делала сверку неподъёмной для HTTP-реквеста.
    /// </summary>
    [Fact]
    public async Task SchemaCatalog_IsReadOncePerRun_AndProgressCountsEveryDocument()
    {
        var (setId, instanceId, _) = await SeedAsync();
        await AddSecondDocumentAsync(setId, instanceId);

        using var scope = fx.Services.CreateScope();
        var counting = new CountingValidator(scope.ServiceProvider.GetRequiredService<IInstanceResolutionValidator>());
        var runner = new QualitySetAuditRunner(
            scope.ServiceProvider.GetRequiredService<IDomainObjectRepository>(),
            scope.ServiceProvider.GetRequiredService<IRepository<DocumentSet>>(),
            scope.ServiceProvider.GetRequiredService<IRepository<QualityAuditRun>>(),
            counting,
            scope.ServiceProvider.GetRequiredService<INotificationService>());

        var progress = new List<string>();
        var report = await runner.RunAsync(setId, QualitySetAuditRunner.DefaultLimit,
            (c, t) => { progress.Add($"{c} из {t}"); return Task.CompletedTask; }, CancellationToken.None);

        Assert.Equal(2, report.Documents);
        Assert.Equal(2, counting.Validations);
        Assert.Equal(1, counting.CatalogLoads);
        // Прогресс — по документу на шаг: индикатор показывает движение, а не два прыжка в конце.
        Assert.Equal(["1 из 2", "2 из 2"], progress);
    }

    /// <summary>
    /// Сверку не запускали — отчёта нет. Пустой отчёт вместо этого утверждал бы, что проверено и
    /// чисто: тот же молчаливый ноль, из-за которого неверные связки жили незамеченными.
    /// </summary>
    [Fact]
    public async Task NeverAudited_HasNoReport_RatherThanCleanOne()
    {
        var (setId, _, _) = await SeedAsync();
        Assert.Null(await InScopeAsync(m => m.Send(new GetQualityAuditQuery(setId))));
    }

    private async Task RunAndStoreAsync(Guid setId, Guid userId)
    {
        using var scope = fx.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IQualitySetAuditRunner>()
            .RunAndStoreAsync(setId, userId, null, CancellationToken.None);
    }

    /// <summary>
    /// Итог фонового прогона переживает сам прогон: спрашивает его другой запрос и в другое время.
    /// Второй прогон ЗАМЕНЯЕТ отчёт — при двух строках «последняя сверка» стала бы выбором наугад.
    /// </summary>
    [Fact]
    public async Task StoredReport_IsReadBackWithItsDate_AndReplacedByNextRun()
    {
        var (setId, _, _) = await SeedAsync();
        var userId = Guid.NewGuid();

        await RunAndStoreAsync(setId, userId);
        var stored = await InScopeAsync(m => m.Send(new GetQualityAuditQuery(setId)));

        Assert.NotNull(stored);
        Assert.Equal(1, stored!.Documents);
        Assert.Equal(1, stored.MaterialsWithoutDoc);
        Assert.Equal(1, stored.ImplausibleDocs);
        Assert.Equal(2, stored.Rows.Count);
        Assert.All(stored.Rows, r => Assert.False(string.IsNullOrWhiteSpace(r.Path)));
        // Дата обязательна: отчёт верен на неё, а с правкой данных устаревает молча.
        Assert.NotNull(stored.CompletedAt);

        // Итог ушёл в колокольчик — задача исчезает из индикатора молча.
        using (var scope = fx.Services.CreateScope())
        {
            var notes = await scope.ServiceProvider.GetRequiredService<INotificationService>().GetAsync(userId);
            Assert.Contains(notes, n => n.Title.StartsWith("Сверка качества"));
        }

        await RunAndStoreAsync(setId, userId);
        using (var scope = fx.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(1, await db.QualityAuditRuns.CountAsync(r => r.SetId == setId));
        }
    }

    /// <summary>
    /// Задача доходит до конца через саму подсистему Job: очередь → фоновый сервис → сохранённый
    /// отчёт. Проверяется именно проводка (вид задачи, разбор в обработчике, разрешение зависимостей
    /// в фоновом scope) — она отказывает целиком и молча, а не одной строкой в диффе.
    /// </summary>
    [Fact]
    public async Task EnqueuedJob_RunsAudit_AndLeavesReport()
    {
        var (setId, _, _) = await SeedAsync();
        var userId = Guid.NewGuid();

        using (var scope = fx.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IJobService>().EnqueueAsync(
                JobKind.AuditQualityLinks, userId, setId, "Сверка качества", null, CancellationToken.None);

        Job? job = null;
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
            using var scope = fx.Services.CreateScope();
            job = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                .Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.TargetId == setId);
            if (job is not null && !job.IsActive) break;
        }

        // Ошибку задачи показываем текстом: «ожидалось Succeeded, получено Failed» отправило бы
        // читателя искать причину в логах фонового сервиса.
        Assert.NotNull(job);
        Assert.True(job!.Status == JobStatus.Succeeded, $"Задача завершилась как {job.Status}: {job.Error}");

        var report = await InScopeAsync(m => m.Send(new GetQualityAuditQuery(setId)));
        Assert.NotNull(report);
        Assert.Equal(1, report!.Documents);
        Assert.Equal(2, report.Rows.Count);
    }
}
