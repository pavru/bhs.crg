using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Recognition;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Domain.Recognition;
using BHS.CRG.Infrastructure.DataSets;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Infrastructure.Recognition;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SharpPdfDocument = PdfSharpCore.Pdf.PdfDocument;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Привязка профиля распознавания к группе листов (issue #410) — ради неё делался весь эпик #405:
/// группа БЕЗ функционального тэга становится распознаваемой таблицей.
///
/// Под защитой два молчаливых способа всё сломать: (1) привязка теряется при любой пересборке
/// группировки, потому что группы собираются позиционным конструктором; (2) группа без тэга не
/// проходит предикат «табличная» — и её источник УДАЛЯЕТСЯ вместе с привязками как осиротевший.
/// </summary>
[Collection("Integration")]
public class RecognitionProfileBindingTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly Guid DocId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static byte[] MakePdf(int pageCount)
    {
        using var doc = new SharpPdfDocument();
        for (var i = 0; i < pageCount; i++) doc.AddPage();
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }

    /// <summary>Группа-документ БЕЗ тэга таблицы — «произвольная таблица» вроде «Список деталей ТКШ».</summary>
    private static GostGroupingData UntaggedGrouping(Guid? profileId = null) => new(
        [new GostGroupingGroup(GostGroupKind.Document, "A113", "Список деталей ТКШ1",
            [new GostGroupingPage(0, new Dictionary<string, string?>()), new GostGroupingPage(1, new Dictionary<string, string?>())],
            Tags: null, Id: DocId, ProfileId: profileId)],
        ManuallyEdited: false);

    private async Task<(Guid fileId, IServiceScope scope)> SeedAsync(GostGroupingData grouping)
    {
        var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await RecognitionProfileSeeder.SeedAsync(db);
        db.ChangeTracker.Clear();

        var blobStorage = scope.ServiceProvider.GetRequiredService<IBlobStorage>();
        using var upload = new MemoryStream(MakePdf(3));
        var blobPath = await blobStorage.UploadAsync("test.pdf", upload, "application/pdf");

        var file = DataSetFile.Create("Альбом", DataSetFormat.Pdf, blobPath, CatalogScope.System, null);
        file.SetGrouping(JsonSerializer.Serialize(grouping));
        db.DataSetFiles.Add(file);
        await db.SaveChangesAsync();
        return (file.Id, scope);
    }

    private static async Task<Guid> CustomTableProfileAsync(AppDbContext db)
    {
        var profile = RecognitionProfile.Create(
            "Список деталей шкафа", RecognitionProfileKind.Table,
            fields: RecognitionProfileJson.WriteFields([]),
            rowColumns: RecognitionProfileJson.WriteFields([
                new RecognitionProfileField("Поз", "Позиция"),
                new RecognitionProfileField("Наименование", "Наименование детали"),
                new RecognitionProfileField("Количество", "Количество, шт", "number"),
            ]),
            shape: RecognitionProfileJson.WriteShape(new RecognitionTableShape(TwoTierHeader: true)));
        db.RecognitionProfiles.Add(profile);
        await db.SaveChangesAsync();
        return profile.Id;
    }

    private static async Task<Guid> AddTableSourceAsync(AppDbContext db, Guid fileId, Guid groupId)
    {
        var file = await db.DataSetFiles.Include(f => f.Sources).FirstAsync(f => f.Id == fileId);
        var table = file.AddSource("Таблица", PdfProfiles.GostTableMarkerPrefix + groupId, "[]", 0);
        db.DataSetSources.Add(table);
        await db.SaveChangesAsync();
        return table.Id;
    }

    // ── Разблокировка произвольных таблиц ────────────────────────────────────────

    [Fact]
    public async Task TableSpec_UntaggedGroup_ResolvesOnlyWithBoundProfile()
    {
        var (fileId, scope) = await SeedAsync(UntaggedGrouping());
        using (scope)
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var svc = scope.ServiceProvider.GetRequiredService<DataSetPdfRecognitionService>();
            var group = UntaggedGrouping().Groups[0];

            // Без тэга и без профиля таблица нераспознаваема — это исходное состояние произвольной формы.
            Assert.Null(await svc.ResolveTableSpecAsync(group, default));
            Assert.False(await svc.IsTableGroupAsync(group, default));

            var profileId = await CustomTableProfileAsync(db);
            var bound = group with { ProfileId = profileId };

            var spec = await svc.ResolveTableSpecAsync(bound, default);
            Assert.NotNull(spec);
            Assert.Equal(["Поз", "Наименование", "Количество"], spec!.Value.Columns.Select(c => c.Path));
            Assert.Equal(RecognitionProfileKind.Table, spec.Value.Kind);
            Assert.True(spec.Value.Shape!.TwoTierHeader);   // флаги формы доезжают до промпта
            Assert.True(await svc.IsTableGroupAsync(bound, default));
        }
        _ = fileId;
    }

    [Fact]
    public async Task TableSpec_BoundProfile_WinsOverTag()
    {
        var (_, scope) = await SeedAsync(UntaggedGrouping());
        using (scope)
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var svc = scope.ServiceProvider.GetRequiredService<DataSetPdfRecognitionService>();
            var profileId = await CustomTableProfileAsync(db);

            // Единая цепочка приоритета: профиль на группе перекрывает встроенный профиль по тэгу.
            var group = UntaggedGrouping().Groups[0] with
            {
                Tags = ["gostDoc.cableJournal"],
                ProfileId = profileId,
            };
            var spec = await svc.ResolveTableSpecAsync(group, default);
            Assert.Equal(RecognitionProfileKind.Table, spec!.Value.Kind);   // не CableJournal
            Assert.Contains(spec.Value.Columns, c => c.Path == "Поз");
        }
    }

    [Fact]
    public async Task TableSpec_DeletedProfile_FallsBackToTag_NotBroken()
    {
        var (_, scope) = await SeedAsync(UntaggedGrouping());
        using (scope)
        {
            var svc = scope.ServiceProvider.GetRequiredService<DataSetPdfRecognitionService>();
            // Профиль удалён (или ещё не создан), но тэг остался — деградируем к тэгу, а не считаем
            // группу нетабличной: иначе удаление профиля молча снесло бы её источник.
            var group = UntaggedGrouping().Groups[0] with
            {
                Tags = ["gostDoc.specification"],
                ProfileId = Guid.NewGuid(),
            };
            var spec = await svc.ResolveTableSpecAsync(group, default);
            Assert.NotNull(spec);
            Assert.Equal(RecognitionProfileKind.Table, spec!.Value.Kind);
            Assert.True(await svc.IsTableGroupAsync(group, default));
        }
    }

    // ── Сохранность привязки ─────────────────────────────────────────────────────

    [Fact]
    public async Task SetDocumentProfile_Persists_AndMarksTableStale()
    {
        var (fileId, scope) = await SeedAsync(UntaggedGrouping());
        using (scope)
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var profileId = await CustomTableProfileAsync(db);
            var svc = scope.ServiceProvider.GetRequiredService<IDataSetService>();

            var dto = await svc.SetDocumentProfileAsync(fileId, firstPageIndex: 0, profileId, default);
            Assert.NotNull(dto);
            Assert.Equal(profileId, dto!.Groups[0].ProfileId);

            // Снятие привязки возвращает группу к прежней цепочке.
            var cleared = await svc.SetDocumentProfileAsync(fileId, 0, null, default);
            Assert.Null(cleared!.Groups[0].ProfileId);
        }
    }

    [Fact]
    public async Task SetDocumentProfile_RejectsNonTabularProfile()
    {
        var (fileId, scope) = await SeedAsync(UntaggedGrouping());
        using (scope)
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var stamp = await db.RecognitionProfiles.AsNoTracking()
                .FirstAsync(p => p.Code == BuiltInProfileCodes.TitleBlock);
            var svc = scope.ServiceProvider.GetRequiredService<IDataSetService>();

            // К группе листов привязывается профиль ТАБЛИЦЫ — штамп сюда не годится.
            await Assert.ThrowsAsync<ArgumentException>(
                () => svc.SetDocumentProfileAsync(fileId, 0, stamp.Id, default));
        }
    }

    [Fact]
    public async Task ApplyGrouping_KeepsProfileBinding_AndDoesNotOrphanSource()
    {
        var (fileId, scope) = await SeedAsync(UntaggedGrouping());
        using (scope)
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var profileId = await CustomTableProfileAsync(db);
            var svc = scope.ServiceProvider.GetRequiredService<IDataSetService>();
            await svc.SetDocumentProfileAsync(fileId, 0, profileId, default);
            var tableId = await AddTableSourceAsync(db, fileId, DocId);

            // Ручная правка разбиения: группа пересобирается с нуля позиционным конструктором и
            // приходит БЕЗ тэга. Привязка обязана пережить это, а источник — не осиротеть.
            await svc.ApplyGroupingAsync(fileId, new ApplyGroupingInput(
                [new GostGroupingGroupDto(GostGroupKind.Document, "A113", "Список деталей ТКШ1", [0, 1], null)]), default);

            var file = await db.DataSetFiles.AsNoTracking().FirstAsync(f => f.Id == fileId);
            var grouping = GostGroupingSerialization.Parse(file.Grouping)!;
            Assert.Equal(profileId, grouping.Groups.Single(g => g.Kind == GostGroupKind.Document).ProfileId);

            Assert.NotNull(await db.DataSetSources.AsNoTracking().FirstOrDefaultAsync(s => s.Id == tableId));
        }
    }

    [Fact]
    public async Task Candidates_UntaggedGroupWithProfile_AppearsAsPendingTable()
    {
        var (fileId, scope) = await SeedAsync(UntaggedGrouping());
        using (scope)
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var profileId = await CustomTableProfileAsync(db);
            var svc = scope.ServiceProvider.GetRequiredService<IDataSetService>();

            // До привязки кандидата нет — группа не табличная.
            Assert.DoesNotContain(await svc.DetectSourceCandidatesAsync(fileId, default),
                c => c.SheetOrPath.StartsWith(PdfProfiles.GostTableMarkerPrefix, StringComparison.Ordinal));

            await svc.SetDocumentProfileAsync(fileId, 0, profileId, default);

            var candidate = Assert.Single(await svc.DetectSourceCandidatesAsync(fileId, default),
                c => c.SheetOrPath == PdfProfiles.GostTableMarkerPrefix + DocId);
            Assert.Equal(0, candidate.FirstPageIndex);   // «таблица не распознана» → кнопка «Распознать»
            Assert.Contains("Список деталей ТКШ1", candidate.Name);
        }
    }
}
