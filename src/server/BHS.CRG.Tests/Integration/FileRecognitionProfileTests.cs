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
/// Профили распознавания, привязанные к НАБОРУ (issue #412): штамп, обложка/титул и счёт читают файл
/// целиком, поэтому их профиль живёт на наборе — в отличие от таблиц (issue #410, привязка к группе).
/// </summary>
[Collection("Integration")]
public class FileRecognitionProfileTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<(Guid fileId, IServiceScope scope)> SeedAsync()
    {
        var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.RecognitionProfiles.RemoveRange(db.RecognitionProfiles.Where(p => p.Code == null));
        await db.SaveChangesAsync();
        await RecognitionProfileSeeder.SeedAsync(db);
        db.ChangeTracker.Clear();

        using var doc = new SharpPdfDocument();
        doc.AddPage();
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        var blobPath = await scope.ServiceProvider.GetRequiredService<IBlobStorage>()
            .UploadAsync("a.pdf", new MemoryStream(ms.ToArray()), "application/pdf");

        var file = DataSetFile.Create("Альбом", DataSetFormat.Pdf, blobPath, CatalogScope.System, null);
        db.DataSetFiles.Add(file);
        await db.SaveChangesAsync();
        return (file.Id, scope);
    }

    private static async Task<Guid> CustomStampProfileAsync(AppDbContext db)
    {
        // Свой штамп: заводские поля + собственное «Стадия». Системные поля обязаны остаться.
        var builtIn = await db.RecognitionProfiles.AsNoTracking()
            .FirstAsync(p => p.Code == BuiltInProfileCodes.TitleBlock);
        var fields = RecognitionProfileJson.ReadFields(builtIn.Fields).ToList();
        fields.Add(new RecognitionProfileField("Стадия", "Стадия документации"));

        var profile = RecognitionProfile.Create(
            "Штамп с полем Стадия", RecognitionProfileKind.TitleBlock,
            fields: RecognitionProfileJson.WriteFields(fields));
        db.RecognitionProfiles.Add(profile);
        await db.SaveChangesAsync();
        return profile.Id;
    }

    [Fact]
    public async Task SetAndResolve_BoundProfileWins_OverBuiltIn()
    {
        var (fileId, scope) = await SeedAsync();
        using (scope)
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var profileId = await CustomStampProfileAsync(db);
            var svc = scope.ServiceProvider.GetRequiredService<IDataSetService>();

            Assert.True(await svc.SetFileRecognitionProfilesAsync(
                fileId, new Dictionary<string, Guid?> { ["TitleBlock"] = profileId }, default));

            var file = await db.DataSetFiles.AsNoTracking().FirstAsync(f => f.Id == fileId);
            var map = DataSetPdfRecognitionService.ParseFileProfileMap(file.RecognitionProfiles);
            Assert.Equal(profileId, map["TitleBlock"]);

            // Снятие привязки → пустая карта схлопывается в null, «нет привязок» единообразно.
            await svc.SetFileRecognitionProfilesAsync(
                fileId, new Dictionary<string, Guid?> { ["TitleBlock"] = null }, default);
            var cleared = await db.DataSetFiles.AsNoTracking().FirstAsync(f => f.Id == fileId);
            Assert.Null(cleared.RecognitionProfiles);
        }
    }

    [Fact]
    public async Task Set_RejectsKindMismatch_AndGroupScopedKinds()
    {
        var (fileId, scope) = await SeedAsync();
        using (scope)
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var stampId = await CustomStampProfileAsync(db);
            var svc = scope.ServiceProvider.GetRequiredService<IDataSetService>();

            // Профиль штампа в слот обложки не годится — вид определяет применяемый промпт.
            await Assert.ThrowsAsync<ArgumentException>(() => svc.SetFileRecognitionProfilesAsync(
                fileId, new Dictionary<string, Guid?> { ["CoverTitle"] = stampId }, default));

            // Табличные виды привязываются к ГРУППЕ ЛИСТОВ, а не к набору.
            await Assert.ThrowsAsync<ArgumentException>(() => svc.SetFileRecognitionProfilesAsync(
                fileId, new Dictionary<string, Guid?> { ["Table"] = null }, default));

            await Assert.ThrowsAsync<ArgumentException>(() => svc.SetFileRecognitionProfilesAsync(
                fileId, new Dictionary<string, Guid?> { ["НетТакого"] = null }, default));
        }
    }

    [Fact]
    public async Task ParseFileProfileMap_BrokenJson_DoesNotBreakRecognition()
    {
        // Распознавание важнее привязки: сломанная карта → работаем на встроенных профилях, не падаем.
        Assert.Empty(DataSetPdfRecognitionService.ParseFileProfileMap("не json"));
        Assert.Empty(DataSetPdfRecognitionService.ParseFileProfileMap(null));
        Assert.Empty(DataSetPdfRecognitionService.ParseFileProfileMap("{}"));
    }

    [Fact]
    public void Scope_SeparatesFileLevelKindsFromGroupLevel()
    {
        // «Есть табличная часть» для этого не годится: у счёта она есть (Товары), но привязывается
        // он к набору. Поэтому область — отдельное свойство вида.
        Assert.True(RecognitionKinds.IsTabular(RecognitionProfileKind.Invoice));
        Assert.False(RecognitionKinds.IsGroupScoped(RecognitionProfileKind.Invoice));

        Assert.True(RecognitionKinds.IsGroupScoped(RecognitionProfileKind.Table));
        Assert.True(RecognitionKinds.IsGroupScoped(RecognitionProfileKind.CableJournal));
        Assert.False(RecognitionKinds.IsGroupScoped(RecognitionProfileKind.TitleBlock));
        Assert.False(RecognitionKinds.IsGroupScoped(RecognitionProfileKind.CoverTitle));
    }

    [Fact]
    public async Task FileDto_ExposesProfileMap()
    {
        var (fileId, scope) = await SeedAsync();
        using (scope)
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var profileId = await CustomStampProfileAsync(db);
            var svc = scope.ServiceProvider.GetRequiredService<IDataSetService>();
            await svc.SetFileRecognitionProfilesAsync(
                fileId, new Dictionary<string, Guid?> { ["TitleBlock"] = profileId }, default);

            var files = await svc.ListFilesAsync(nameof(CatalogScope.System), null, default);
            var dto = files.Single(f => f.Id == fileId);
            Assert.Equal(profileId, dto.RecognitionProfiles!["TitleBlock"]);
        }
    }
}
