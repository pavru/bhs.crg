using System.Text.Json;
using System.Text.Json.Nodes;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Infrastructure.DataSets;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SharpPdfDocument = PdfSharpCore.Pdf.PdfDocument;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// «Вырезать документы заново» (issue #1534) — выход, который называют копия и восстановление, когда
/// частей PDF-набора нет в хранилище.
///
/// <para>Прежний выход — «откройте Разбиение и сохраните» — был невыполним: «Сохранить» неактивна без
/// правки. А сохранение после фиктивной правки ставит отметку ручной правки и не переносит
/// распознанные таблицы групп. Поэтому здесь проверяется не только «части вернулись», но и что
/// разбиение осталось ровно тем, что было.</para>
/// </summary>
[Collection("Integration")]
public class DataSetPdfRecutTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly Guid CableDocId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const string CableRows = """[{"Марка":"ВВГнг 3х2,5","Длина":"48"}]""";
    private const string CableColumns = """[{"name":"Марка","sampleValues":["ВВГнг 3х2,5"]},{"name":"Длина","sampleValues":["48"]}]""";

    /// <summary>
    /// Части пропали из хранилища, а у копии источника «Документы» висячий кэш от прежней перерезки
    /// (так было на проде: восстановление 27.09 из копии без частей и перерезка 16.07 до #1149).
    /// Редактор считает документы без файла, действие режет их заново — и разбиение не меняется:
    /// таблица группы, тэги, id и отметка ручной правки на месте, табличный источник цел.
    /// </summary>
    [Fact]
    public async Task Recut_RestoresParts_UpdatesEveryProjection_KeepsGrouping()
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var blob = scope.ServiceProvider.GetRequiredService<IBlobStorage>();
        var fake = scope.ServiceProvider.GetRequiredService<FakeBlobStorage>();
        var svc = scope.ServiceProvider.GetRequiredService<IDataSetService>();
        var (fileId, registryId, copyId, tableId) = await SeedAsync(db, blob);

        // Первая нарезка — тем же действием: частей у засеянного разбиения ещё нет.
        var first = await svc.RecutGroupingAsync(fileId, default);
        Assert.Equal(0, first!.MissingParts);
        var stale = await RowPathsAsync(db, copyId);
        Assert.Equal(2, stale.Count);

        // Вторая нарезка уносит первые части как осиротевшие; копии возвращаем кэш с ними — висячий.
        await svc.RecutGroupingAsync(fileId, default);
        var copy = await db.DataSetSources.SingleAsync(s => s.Id == copyId);
        copy.UpdateCache(copy.CachedSchema, 2, JsonSerializer.Serialize(
            stale.Select(p => new Dictionary<string, string> { [GostGroupingProjection.FilePathColumn] = p })));
        await db.SaveChangesAsync();
        // И сами нынешние части пропадают — как после восстановления из копии без них.
        foreach (var part in GostGroupingSerialization.ReadPartPaths(await GroupingJsonAsync(db, fileId))!)
            await blob.DeleteAsync(part);

        Assert.Equal(4, (await svc.GetPagesAsync(fileId, default))!.MissingParts);

        var recut = await svc.RecutGroupingAsync(fileId, default);

        Assert.Equal(0, recut!.MissingParts);
        Assert.Equal(0, (await svc.GetPagesAsync(fileId, default))!.MissingParts);
        db.ChangeTracker.Clear();
        var grouping = GostGroupingSerialization.Parse(await GroupingJsonAsync(db, fileId))!;
        var parts = GostGroupingSerialization.PartPaths(grouping);
        Assert.Equal(2, parts.Count);
        Assert.All(parts, p => Assert.True(fake.Exists(p), $"части {p} нет в хранилище"));
        // Обе проекции «Документы» — и реестр, и копия с висячим кэшем — указывают на новые части.
        Assert.True(parts.SetEquals(await RowPathsAsync(db, registryId)), "реестр указывает не на новые части");
        Assert.True(parts.SetEquals(await RowPathsAsync(db, copyId)), "копия реестра указывает не на новые части");

        // Разбиение — то же: отметка ручной правки, id, тэги и распознанная таблица.
        Assert.True(grouping.ManuallyEdited);
        var cable = Assert.Single(grouping.Groups, g => g.Id == CableDocId);
        Assert.Equal(["gostDoc.cableJournal"], cable.Tags);
        Assert.Equal(CableRows, cable.TableData);
        Assert.Equal(CableColumns, cable.TableColumns);
        Assert.False(cable.TableStale);
        // Кэш источника лежит jsonb: порядок ключей у базы свой, сравнивается содержимое.
        var table = await db.DataSetSources.SingleAsync(s => s.Id == tableId);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(CableRows), JsonNode.Parse(table.CachedData!)),
            $"таблица источника изменилась: {table.CachedData}");
    }

    /// <summary>Файла набора нет — резать не из чего: отказ с выходом, а не сбой чтения.</summary>
    [Fact]
    public async Task Recut_WithoutSetFile_IsRefusedWithExit()
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var blob = scope.ServiceProvider.GetRequiredService<IBlobStorage>();
        var (fileId, _, _, _) = await SeedAsync(db, blob);
        await blob.DeleteAsync((await db.DataSetFiles.SingleAsync(f => f.Id == fileId)).BlobPath);

        var refusal = await Assert.ThrowsAsync<ConflictException>(
            () => scope.ServiceProvider.GetRequiredService<IDataSetService>().RecutGroupingAsync(fileId, default));
        Assert.Contains("Загрузите файл набора заново", refusal.Message);
    }

    // ── Помощники ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Альбом из четырёх листов, разбитый вручную на два документа; у второго — распознанный
    /// кабельный журнал и его табличный источник. Источников «Документы» два: реестр и его копия.
    /// </summary>
    private static async Task<(Guid FileId, Guid RegistryId, Guid CopyId, Guid TableId)> SeedAsync(
        AppDbContext db, IBlobStorage blob)
    {
        using var doc = new SharpPdfDocument();
        for (var i = 0; i < 4; i++) doc.AddPage();
        using var pdf = new MemoryStream();
        doc.Save(pdf, false);
        pdf.Position = 0;
        var blobPath = await blob.UploadAsync("album.pdf", pdf, "application/pdf");

        static List<GostGroupingPage> Pages(params int[] indices) =>
            indices.Select(i => new GostGroupingPage(i, new Dictionary<string, string?>())).ToList();
        var grouping = new GostGroupingData(
        [
            new GostGroupingGroup(GostGroupKind.Document, "01-ЭМ", "Пояснительная записка", Pages(0, 1),
                Id: Guid.Parse("11111111-1111-1111-1111-111111111111")),
            new GostGroupingGroup(GostGroupKind.Document, "02-ЭМ", "Кабельный журнал", Pages(2, 3),
                ["gostDoc.cableJournal"], CableDocId, TableData: CableRows, TableColumns: CableColumns),
        ], ManuallyEdited: true);

        var file = DataSetFile.Create("Альбом ЭМ", DataSetFormat.Pdf, blobPath, CatalogScope.System, null);
        file.SetGrouping(JsonSerializer.Serialize(grouping));
        var registry = file.AddSource("Документы", PdfProfiles.GostDocumentsMarker, "[]", 0);
        var copy = file.AddSource("Спецификация", PdfProfiles.GostDocumentsMarker, "[]", 0);
        var table = file.AddSource("Кабельный журнал", PdfProfiles.GostTableMarkerPrefix + CableDocId,
            CableColumns, 1, null, CableRows);
        db.DataSetFiles.Add(file);
        db.DataSetSources.AddRange(registry, copy, table);
        await db.SaveChangesAsync();
        return (file.Id, registry.Id, copy.Id, table.Id);
    }

    private static async Task<string?> GroupingJsonAsync(AppDbContext db, Guid fileId) =>
        (await db.DataSetFiles.AsNoTracking().SingleAsync(f => f.Id == fileId)).Grouping;

    private static async Task<HashSet<string>> RowPathsAsync(AppDbContext db, Guid sourceId) =>
        GostGroupingSerialization.ReadRowFilePaths(
            (await db.DataSetSources.AsNoTracking().SingleAsync(s => s.Id == sourceId)).CachedData)!;
}
