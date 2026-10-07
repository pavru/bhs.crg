using System.Text.Json;
using BHS.CRG.Application.Backup;
using BHS.CRG.Domain.Recognition;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Costs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Владелец профиля распознавания из копии — по каталогу этой сборки, а не из копии (issue #1077,
/// ревью PR #1254).
///
/// <para>Копия помнит владельца на день снятия. «Счёт на оплату» с тех пор переехал от ядра к модулю
/// счетов: правленый профиль из прежней копии вернулся бы подписанным «core» — и на установке без
/// модуля счетов стоял бы в списке под «Общие», отвечая на чтение «модуль выключен». Сидер приводит
/// владельца при старте, но после восстановления он не запускается.</para>
/// </summary>
public partial class BackupServiceTests
{
    private static BackupManifest RecognitionManifest(params BackupRecognitionProfile[] profiles) =>
        new(SchemaVersion: Infrastructure.Backup.BackupService.CurrentSchemaVersion,
            AppVersion: Infrastructure.Backup.BackupService.CurrentAppVersion,
            CreatedAt: DateTimeOffset.UtcNow,
            DocumentTypes: [], Templates: [], CatalogEntities: [], CommonDataEntries: [],
            RecognitionProfiles: profiles);

    private static BackupRecognitionProfile ProfileInCopy(Guid id, string name, string? code, string module) =>
        new(id, name, code, nameof(RecognitionProfileKind.Invoice),
            JsonDocument.Parse("""[{"name":"НомерСчёта","description":"Номер","type":"string"}]""").RootElement.Clone(),
            Shape: null, IsBuiltIn: code is not null, IsModified: true,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Module: module);

    /// <summary>
    /// Ломается, если вернуть владельца из копии (<c>item.Module</c>) в восстановлении профилей.
    /// </summary>
    [Fact]
    public async Task Restore_stamps_the_owner_of_a_recognition_profile_by_this_build_not_by_the_copy()
    {
        Guid builtIn;
        var custom = Guid.NewGuid();
        using (var scope = fixture.Services.CreateScope())
            builtIn = await scope.ServiceProvider.GetRequiredService<AppDbContext>().RecognitionProfiles
                .Where(p => p.Code == CostsRecognitionProfiles.InvoiceCode).Select(p => p.Id).SingleAsync();

        try
        {
            // Как в копии, снятой до переезда: и правленый заводской, и свой профиль вида — у ядра.
            var report = await ImportManifestZipAsync(RecognitionManifest(
                ProfileInCopy(builtIn, "Счёт на оплату", CostsRecognitionProfiles.InvoiceCode, "core"),
                ProfileInCopy(custom, "Свой счёт", code: null, "core")));

            Assert.True(report.Success);
            using var scope = fixture.Services.CreateScope();
            var owners = await scope.ServiceProvider.GetRequiredService<AppDbContext>().RecognitionProfiles
                .AsNoTracking().Where(p => p.Id == builtIn || p.Id == custom).ToDictionaryAsync(p => p.Id, p => p.Module);
            Assert.Equal("costs", owners[builtIn]);
            Assert.Equal("costs", owners[custom]);
        }
        finally
        {
            // Профили — конфигурация, общая чистка их не трогает: правленый заводской и свой убираем
            // сами, а заводской переутверждает сидер, как при старте.
            using var scope = fixture.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.RecognitionProfiles.Where(p => p.Id == builtIn || p.Id == custom).ExecuteDeleteAsync();
            await scope.ServiceProvider.GetRequiredService<Application.Recognition.IRecognitionProfileProvider>()
                .ReseedBuiltInAsync();
        }
    }
}
