using System.Text.Json;
using BHS.CRG.Application.Backup;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Templates;
using BHS.CRG.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Копия, снятая до уникальных индексов версий шаблона, восстанавливается (id-template-version-integrity,
/// D7, задача 4.2): группы приводятся к правилам разовой починки, отчёт называет изменения, а выбор
/// погашенной версии у документов копии пересаживается на действующую.
/// </summary>
public partial class BackupServiceTests
{
    private static readonly DateTimeOffset Day0 = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

    private static BackupTemplate Tpl(Guid id, Guid type, string name, int version, bool active, int day, bool isDefault = false)
        => new(id, type, name, $"= {name} v{version}", version, active, isDefault, Day0.AddDays(day), Day0.AddDays(day));

    private static BackupDocumentType DocType(Guid id) =>
        new(id, "АОСР", $"aosr-{id:N}", "Document", null, false,
            JsonDocument.Parse("""{"fields":[]}""").RootElement.Clone(),
            JsonDocument.Parse("{}").RootElement.Clone(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private async Task<Guid> SetAsync()
    {
        using var scope = fixture.Services.CreateScope();
        var m = scope.ServiceProvider.GetRequiredService<IMediator>();
        var c = await m.Send(new CreateConstructionCommand("Объект", Guid.NewGuid()));
        var s = await m.Send(new CreateSectionCommand(c.Id, "Раздел"));
        return (await m.Send(new CreateDocumentSetCommand(s.Id, "Комплект"))).Id;
    }

    [Fact]
    public async Task Restore_CopyWithDuplicateVersions_IsRepaired_AndReportNamesChanges()
    {
        var type = Guid.NewGuid();
        Guid early = Guid.NewGuid(), late = Guid.NewGuid(), c3 = Guid.NewGuid(), d3 = Guid.NewGuid(), e4 = Guid.NewGuid();
        var setId = await SetAsync();
        Guid docId = Guid.NewGuid(), fileId = Guid.NewGuid();

        var manifest = ManifestWith([DocType(type)]) with
        {
            Templates =
            [
                // #1924 в копии: две действующие v1.
                Tpl(early, type, "Основной", 1, true, 1, isDefault: true),
                Tpl(late, type, "Основной", 1, true, 2),
                // #1920 в копии: два номера v3 при действующей v4.
                Tpl(c3, type, "Краткий", 3, false, 1),
                Tpl(d3, type, "Краткий", 3, false, 2),
                Tpl(e4, type, "Краткий", 4, true, 3),
            ],
            Documents =
            [
                new BackupDocument(docId, setId, type, "Акт", JsonDocument.Parse("{}").RootElement.Clone(), [],
                    "Draft", 0, null, $"[\"{early}\"]", $$$"""{"{{{early}}}":{"a":1}}""",
                    JsonDocument.Parse("{}").RootElement.Clone(),
                    [new BackupGeneratedFile(fileId, "Pdf", $"{docId}-early.pdf", early, Day0, Day0)],
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            ],
        };

        var report = await ImportManifestZipAsync(manifest);

        Assert.True(report.Success, string.Join("; ", report.Warnings));
        Assert.Contains(report.Warnings, w => w.Contains("«Основной»") && w.Contains("погашена"));
        Assert.Contains(report.Warnings, w => w.Contains("«Основной»") && w.Contains("перенумерована в v2"));
        Assert.Contains(report.Warnings, w => w.Contains("«Краткий»") && w.Contains("перенумерована в v5"));
        Assert.Contains(report.Warnings, w => w.Contains("Документы комплектов") && w.Contains("погашенные версии"));

        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.Templates.AsNoTracking().Where(t => t.DocumentTypeId == type).ToDictionaryAsync(t => t.Id);
        Assert.False(stored[early].IsActive);
        Assert.True(stored[late].IsActive);
        Assert.True(stored[late].IsDefault);
        Assert.Equal(2, stored[late].Version);
        Assert.Equal(3, stored[c3].Version);
        Assert.Equal(5, stored[d3].Version);
        Assert.True(stored[e4].IsActive);

        var doc = await db.DocumentFacets.AsNoTracking().SingleAsync(f => f.ObjectId == docId);
        Assert.Equal([late], JsonSerializer.Deserialize<List<Guid>>(doc.TemplateIds!));
        Assert.Contains(late.ToString(), doc.TemplateParams);
        // Файл выбранной погашенной версии — вместе с выбором: иначе следующая генерация добавила бы второй.
        Assert.Equal(late, (await db.GeneratedFiles.AsNoTracking().SingleAsync(f => f.Id == fileId)).TemplateId);
    }

    [Fact]
    public async Task Restore_CopyMeetsExistingTemplateOfSameName_KeepsOneActive()
    {
        // Здесь шаблон «Основной» уже есть (заведён после снятия копии), копия несёт свой «Основной»
        // под другим идентификатором — вливаться им в одну группу без приведения значило бы 23505.
        var type = Guid.NewGuid();
        Assert.True((await ImportManifestZipAsync(ManifestWith([DocType(type)]))).Success);
        Guid here;
        using (var scope = fixture.Services.CreateScope())
            here = (await scope.ServiceProvider.GetRequiredService<IMediator>()
                .Send(new CreateTemplateCommand(type, "Основной", "= здесь"))).Id;
        var fromCopy = Guid.NewGuid();

        var report = await ImportManifestZipAsync(ManifestWith([DocType(type)]) with
        {
            Templates = [Tpl(fromCopy, type, "Основной", 1, true, 1)],
        });

        Assert.True(report.Success, string.Join("; ", report.Warnings));
        using var check = fixture.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await db.Templates.AsNoTracking().Where(t => t.DocumentTypeId == type).ToListAsync();
        Assert.Equal(2, rows.Count);
        // Здешняя создана позже копии — остаётся действующей; копия — v1, здешняя перенумерована в v2.
        Assert.Equal(here, Assert.Single(rows, t => t.IsActive).Id);
        Assert.Equal([1, 2], rows.Select(t => t.Version).Order());
    }
}
