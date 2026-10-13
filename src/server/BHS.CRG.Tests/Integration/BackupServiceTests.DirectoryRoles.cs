using System.IO.Compression;
using System.Text.Json;
using BHS.CRG.Application.Backup;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Schema;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Копия, снятая до появления ролей справочников, разворачивается СО справочниками (issue #1101).
///
/// <para>Код находит организации и номенклатуру по ролям. Старая копия ролей не несёт — и без
/// этого правила восстановленная установка поднялась бы зелёной, а каждый счёт в ней остался бы без
/// поставщика: ни одного отказа при восстановлении, ни одного при старте.</para>
/// </summary>
public partial class BackupServiceTests
{
    private const string LegacyOrganization =
        """{"fields":[{"key":"Наименование","type":"complex"},{"key":"ИНН","type":"primitive"}]}""";

    private static BackupDocumentType LegacyType(string code, string schema) =>
        new(Guid.NewGuid(), code, code, "Composite", null, false,
            JsonDocument.Parse(schema).RootElement.Clone(), JsonDocument.Parse("{}").RootElement.Clone(),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Module: "core");

    private async Task<JsonElement> SchemaOfAsync(string code)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.DocumentTypes.AsNoTracking().SingleAsync(t => t.Code == code)).Schema.RootElement.Clone();
    }

    [Fact]
    public async Task Копия_прежней_версии_получает_роли_справочников_и_тэги_полей()
    {
        // Признака в манифесте нет — так выглядит копия, снятая до переноса.
        var report = await ImportManifestAsync(ManifestWith(
        [
            LegacyType("Организация", LegacyOrganization),
            LegacyType("Номенклатура", """{"fields":[]}"""),
        ]));

        var organization = await SchemaOfAsync("Организация");
        Assert.Equal(FunctionalTag.RoleOrganization, organization.GetProperty("tags")[0].GetString());
        Assert.Equal(FunctionalTag.OrgName, organization.GetProperty("fields")[0].GetProperty("tags")[0].GetString());
        Assert.Equal(FunctionalTag.OrgTaxId, organization.GetProperty("fields")[1].GetProperty("tags")[0].GetString());
        Assert.Equal(FunctionalTag.RoleNomenclature,
            (await SchemaOfAsync("Номенклатура")).GetProperty("tags")[0].GetString());

        // И об этом сказано: роли назначены не человеком, их стоит проверить.
        Assert.Contains(report.Warnings, w => w.Contains("ролей справочников") && w.Contains("«Организация»"));

        // Главное — справочник после восстановления НАХОДИТСЯ тем же расчётом, которым его ищет порт.
        using var scope = fixture.Services.CreateScope();
        var all = await scope.ServiceProvider.GetRequiredService<AppDbContext>().DocumentTypes.AsNoTracking().ToListAsync();
        var tags = scope.ServiceProvider.GetRequiredService<TagCatalog>();
        var role = RoleResolution.Role(FunctionalTag.RoleOrganization, all);
        Assert.Equal("Организация", role.Root!.Code);
        Assert.Equal("ИНН", RoleResolution.Field(role, tags.Find(FunctionalTag.OrgTaxId)!, all).Key);
    }

    /// <summary>
    /// Копия, снятая ПОСЛЕ переноса, берётся как есть: снятая в ней роль — решение администратора, и
    /// возвращать её восстановлением значило бы отменить его молча.
    /// </summary>
    [Fact]
    public async Task Копия_знающая_о_ролях_берётся_как_есть()
    {
        var report = await ImportManifestAsync(ManifestWith(
            [LegacyType("Организация", LegacyOrganization)]) with { KnowsDirectoryRoles = true });

        var organization = await SchemaOfAsync("Организация");
        Assert.False(organization.TryGetProperty("tags", out _));
        Assert.False(organization.GetProperty("fields")[1].TryGetProperty("tags", out _));
        Assert.DoesNotContain(report.Warnings, w => w.Contains("ролей справочников"));
    }

    /// <summary>
    /// Роль занята типом, который уже живёт в системе, — прежний тип из копии её не получает: копию
    /// разворачивают и поверх работающей установки.
    /// </summary>
    [Fact]
    public async Task Роль_занятую_в_живой_системе_копия_прежней_версии_не_переставляет()
    {
        await fixture.ResetDatabaseAsync();
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.DocumentTypes.Add(Domain.Documents.DocumentType.Restore(
                Guid.NewGuid(), "Контрагент", "Контрагент", Domain.Documents.DocumentTypeKind.Composite, null,
                JsonDocument.Parse($$"""{"tags":["{{FunctionalTag.RoleOrganization}}"],"fields":[]}"""),
                JsonDocument.Parse("{}"), false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, false));
            await db.SaveChangesAsync();
        }

        await ImportManifestZipAsync(ManifestWith([LegacyType("Организация", LegacyOrganization)]));

        Assert.False((await SchemaOfAsync("Организация")).TryGetProperty("tags", out _));
        Assert.Equal(FunctionalTag.RoleOrganization, (await SchemaOfAsync("Контрагент")).GetProperty("tags")[0].GetString());
    }

    [Fact]
    public async Task Новая_копия_говорит_что_знает_о_ролях()
    {
        using var scope = fixture.Services.CreateScope();
        var (zipStream, _) = await Backup(scope).ExportAsync(BackupScope.Configuration);
        await using var handle = zipStream;
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);
        await using var entry = zip.GetEntry("manifest.json")!.Open();
        var manifest = (await JsonSerializer.DeserializeAsync<BackupManifest>(entry))!;

        Assert.True(manifest.KnowsDirectoryRoles);
    }
}
