using System.Text.Json;
using BHS.CRG.Application.Backup;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Тэги примитивного типа переживают резервную копию (найдено сторожем полей копии, issue #1185).
/// До этого их не было в записи манифеста: восстановление очищало их молча.
/// </summary>
public partial class BackupServiceTests
{
    private static BackupManifest PrimitiveManifest(Guid id, string code, string[]? tags) =>
        new(SchemaVersion: Infrastructure.Backup.BackupService.CurrentSchemaVersion,
            AppVersion: Infrastructure.Backup.BackupService.CurrentAppVersion,
            CreatedAt: DateTimeOffset.UtcNow,
            DocumentTypes: [], Templates: [], CatalogEntities: [], CommonDataEntries: [],
            PrimitiveTypes:
            [
                new BackupPrimitiveType(id, "Марка кабеля", code, "string", null,
                    JsonDocument.Parse("{}").RootElement.Clone(),
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, tags),
            ]);

    private async Task<List<string>> PrimitiveTagsAsync(Guid id)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.PrimitiveTypes.AsNoTracking().Where(p => p.Id == id).Select(p => p.AllowedTags).SingleAsync();
    }

    [Fact]
    public async Task Restore_PrimitiveType_BringsItsTags()
    {
        var id = Guid.NewGuid();

        var report = await ImportManifestZipAsync(PrimitiveManifest(id, $"mark-{id:N}", ["material.mark"]));

        Assert.True(report.Success);
        Assert.Equal(["material.mark"], await PrimitiveTagsAsync(id));
    }

    /// <summary>Копия без поля — «не знаю», а не «тэгов нет»: тэги существующего типа остаются.</summary>
    [Fact]
    public async Task Restore_CopyWithoutPrimitiveTags_LeavesExistingTagsAlone()
    {
        var id = Guid.NewGuid();
        var code = $"mark-{id:N}";
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.PrimitiveTypes.Add(PrimitiveType.Restore(
                id, "Марка кабеля", code, "string", null, JsonDocument.Parse("{}"),
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, ["material.mark"]));
            await db.SaveChangesAsync();
        }

        var report = await ImportManifestZipAsync(PrimitiveManifest(id, code, tags: null));

        Assert.True(report.Success);
        Assert.Equal(["material.mark"], await PrimitiveTagsAsync(id));
    }
}
