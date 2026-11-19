using BHS.CRG.Application.Activity;
using BHS.CRG.Application.Backup;
using BHS.CRG.Infrastructure.Backup;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Копия переносит канал записи журнала (ТЗ CORE-28.1, CORE-26, issue #1336, design D6), а копия, снятая
/// до канала, читается тем же правилом, что и миграция: без автора — <c>system</c>, с автором — <c>ui</c>.
/// </summary>
public partial class BackupServiceTests
{
    [Fact]
    public async Task Канал_журнала_переживает_копию_а_старая_копия_получает_его_по_автору()
    {
        Guid agent = Guid.NewGuid(), legacyPerson = Guid.NewGuid(), legacySystem = Guid.NewGuid(),
            newer = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
        var code = ActivityActions.UserRoleChanged.Code;

        var report = await ImportManifestAsync(new BackupManifest(
            SchemaVersion: BackupService.CurrentSchemaVersion,
            AppVersion: BackupService.CurrentAppVersion,
            CreatedAt: DateTimeOffset.UtcNow,
            DocumentTypes: [], Templates: [], CatalogEntities: [], CommonDataEntries: [],
            ActivityLog:
            [
                new BackupActivityRecord(agent, at, code, Guid.NewGuid(), "Иван", "u1", "u1@test", null, "агентом",
                    agent, true, Channel: "mcp"),
                // Копия до канала: поля нет вовсе — в JSON старого формата оно и придёт пустым.
                new BackupActivityRecord(legacyPerson, at, code, Guid.NewGuid(), "Пётр", "u2", "u2@test", null, "руками"),
                new BackupActivityRecord(legacySystem, at, ActivityActions.ModulesChanged.Code, null, "Система",
                    null, null, null, "id"),
                // Копия более новой версии: канала, которого эта не знает, не подменяем «интерфейсом».
                new BackupActivityRecord(newer, at, code, Guid.NewGuid(), "Анна", "u3", "u3@test", null, "ассистентом",
                    newer, true, Channel: "assistant"),
            ]));
        Assert.True(report.Success);

        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var channels = await db.ActivityRecords.AsNoTracking()
            .Where(r => r.Id == agent || r.Id == legacyPerson || r.Id == legacySystem || r.Id == newer)
            .ToDictionaryAsync(r => r.Id, r => r.Channel);

        Assert.Equal("mcp", channels[agent]);
        Assert.Equal("ui", channels[legacyPerson]);
        Assert.Equal("system", channels[legacySystem]);
        Assert.Equal("assistant", channels[newer]);
    }
}
