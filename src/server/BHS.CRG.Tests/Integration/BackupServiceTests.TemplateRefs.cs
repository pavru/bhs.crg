using System.Text.Json;
using System.Text.Json.Nodes;
using BHS.CRG.Application.Backup;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Templates;
using BHS.CRG.Domain.Documents;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Копия со ссылками документа на шаблоны, которых нет (issue #1960) — часть
/// <see cref="BackupServiceTests" />. Такие несут все копии рабочей базы, снятые до разовой чистки:
/// восстановление дословно вернуло бы их туда, куда запись их уже не пускает.
/// </summary>
public partial class BackupServiceTests
{
    [Fact]
    public async Task Restore_DocumentTemplateRefsToMissingTemplates_ArePruned_AndCounted()
    {
        Guid typeId, setId, kept, foreign;
        using (var scope = fixture.Services.CreateScope())
        {
            var m = scope.ServiceProvider.GetRequiredService<IMediator>();
            typeId = (await m.Send(new CreateDocumentTypeCommand("DT_BK_REFS", "DT_BK_REFS", DocumentTypeKind.Document,
                null, JsonDocument.Parse("""{"fields":[]}""")))).Id;
            kept = (await m.Send(new CreateTemplateCommand(typeId, "Живой", "content"))).Id;
            // Шаблон есть, но ЧУЖОГО типа — генерация его не видит, ссылка такая же висячая.
            var otherType = (await m.Send(new CreateDocumentTypeCommand("DT_BK_OTHER", "DT_BK_OTHER",
                DocumentTypeKind.Document, null, JsonDocument.Parse("""{"fields":[]}""")))).Id;
            foreign = (await m.Send(new CreateTemplateCommand(otherType, "Чужой", "content"))).Id;
            var c = await m.Send(new CreateConstructionCommand("Объект", Guid.NewGuid()));
            var s = await m.Send(new CreateSectionCommand(c.Id, "Раздел"));
            setId = (await m.Send(new CreateDocumentSetCommand(s.Id, "Комплект"))).Id;
        }

        var gone = Guid.NewGuid();
        var docId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var empty = JsonDocument.Parse("{}").RootElement.Clone();
        var manifest = ManifestWith([]) with
        {
            Documents =
            [
                new BackupDocument(docId, setId, typeId, "АОСР 1", empty, [], "Draft", 0,
                    gone, JsonSerializer.Serialize(new[] { gone, kept, foreign }),
                    $"{{\"{kept}\":{{\"x\":1}},\"{gone}\":{{\"y\":2}}}}", empty, [], now, now),
            ],
        };

        var report = await ImportManifestZipAsync(manifest);

        Assert.True(report.Success, string.Join("; ", report.Warnings));
        Assert.Contains(report.Warnings, w => w.Contains("ссылок документов на шаблоны: 4"));
        using (var scope = fixture.Services.CreateScope())
        {
            var doc = (await scope.ServiceProvider.GetRequiredService<IDomainObjectRepository>().GetByIdAsync(docId))!;
            Assert.Null(doc.TemplateId);
            // jsonb хранит JSON по-своему — сравниваем по смыслу.
            Assert.Equal([kept], JsonSerializer.Deserialize<Guid[]>(doc.TemplateIds!));
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse($"{{\"{kept}\":{{\"x\":1}}}}"), JsonNode.Parse(doc.TemplateParams!)));
        }
    }
}
