using System.Text.Json;
using BHS.CRG.Application.Backup;
using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Infrastructure.Backup;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Перечень работ в резервной копии (ТЗ CORE-10, issue #964): выгрузили, стёрли, восстановили,
/// сошлось — с ТЕМИ ЖЕ идентификаторами.
///
/// <para>Своим файлом, а не строками в <c>BackupServiceTests.RoundTrip.cs</c>: тот стоит в
/// храповике размера на своём уровне, и дописывать в склад ради одной сущности значит поднимать
/// порог файлу, который читают целиком.</para>
///
/// <para><b>Зачем прогон вообще, если таблица в этапе 1 пуста.</b> Именно поэтому: секция манифеста,
/// которую никто не наполняет, — это непроверенный путь восстановления, и обнаружился бы он у
/// заказчика, после импорта сметы в этапе 3. Здесь позиция заводится руками, и путь становится
/// настоящим.</para>
/// </summary>
[Collection("Integration")]
public class BackupWorkPlanTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private BackupService Backup(IServiceScope scope) => new(
        scope.ServiceProvider.GetRequiredService<AppDbContext>(),
        scope.ServiceProvider.GetRequiredService<IBlobStorage>(),
        NullLogger<BackupService>.Instance,
        scope.ServiceProvider.GetRequiredService<Application.Activity.IActivityLog>());

    [Fact]
    public async Task Перечень_переживает_выгрузку_и_восстановление()
    {
        _ = fixture.CreateClient();

        // ── Данные ────────────────────────────────────────────────────────────
        var itemId = Guid.NewGuid();
        Guid workType, unit, construction, section;
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var type = DocumentType.Create("Вид работы", "ВидРаботы", DocumentTypeKind.Composite, null,
                JsonDocument.Parse("""{"fields":[]}"""), TypeOwner.Core, TypeVisibility.Shared);
            db.DocumentTypes.Add(type);

            var work = DomainObject.Create(type.Id, "Прокладка кабеля",
                JsonDocument.Parse("{}"), CatalogScope.System, null);
            var unitObj = DomainObject.Create(type.Id, "м",
                JsonDocument.Parse("{}"), CatalogScope.System, null);
            db.DomainObjects.AddRange(work, unitObj);

            var c = Construction.Create("Стройка", Guid.NewGuid());
            db.Constructions.Add(c);
            await db.SaveChangesAsync();

            var s = Section.Create(c.Id, "ЭОМ-1");
            db.Sections.Add(s);
            await db.SaveChangesAsync();

            db.WorkPlanItems.Add(WorkPlanItem.Restore(
                itemId, work.Id, c.Id, s.Id, unitObj.Id, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();

            (workType, unit, construction, section) = (work.Id, unitObj.Id, c.Id, s.Id);
        }

        // ── Выгрузка ──────────────────────────────────────────────────────────
        byte[] zipBytes;
        using (var scope = fixture.Services.CreateScope())
        {
            // Проектные данные едут только в ПОЛНОЙ копии (issue #833) — перечень работ тоже.
            var (zip, _) = await Backup(scope).ExportAsync(BackupScope.Full);
            await using var _handle = zip;
            using var ms = new MemoryStream();
            await zip.CopyToAsync(ms);
            zipBytes = ms.ToArray();
        }

        // ── Стираем перечень ──────────────────────────────────────────────────
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.WorkPlanItems.ExecuteDeleteAsync();
            Assert.Empty(await db.WorkPlanItems.ToListAsync());
        }

        // ── Восстановление ────────────────────────────────────────────────────
        using (var scope = fixture.Services.CreateScope())
            Assert.True((await Backup(scope).ImportAsync(new MemoryStream(zipBytes))).Success);

        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var item = Assert.Single(await db.WorkPlanItems.AsNoTracking().ToListAsync());

            // Идентификатор ТОТ ЖЕ: на позицию ссылаются модули, и новый идентификатор оставил бы
            // их ссылки висеть — восстановление выглядело бы успешным, а план и факт потеряли бы
            // то, к чему относятся.
            Assert.Equal(itemId, item.Id);
            Assert.Equal(workType, item.WorkTypeId);
            Assert.Equal(construction, item.ConstructionId);
            Assert.Equal(section, item.SectionId);
            Assert.Equal(unit, item.UnitId);
        }
    }
}
