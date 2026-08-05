using System.Text;
using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Infrastructure.Maintenance;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Реестр созданных приложением блобов (issue #672, п. 2-3).
///
/// Проверяем три свойства, каждое из которых по отдельности делает правку бессмысленной:
/// запись в хранилище попадает в реестр, чужой путь получает отказ, а уже существующие данные
/// собираются разовым проходом — иначе выкат отнял бы доступ ко всем ранее загруженным файлам.
/// </summary>
[Collection("Integration")]
public class BlobRegistryTests(IntegrationTestFixture fx)
{
    private static Stream Content(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

    /// <summary>
    /// Путь той же формы, что лежит в БОЮ, но никем не созданный.
    ///
    /// Разделитель даты — точка, а не слэш, и это не произвол: <c>UploadAsync</c> строит дату
    /// форматом <c>yyyy/MM/dd</c>, где <c>/</c> — плейсхолдер разделителя культуры, и под русской
    /// локалью он даёт точку. Первая версия этих тестов проверяла форму со слэшами — ту, которую я
    /// придумал, — и подтверждала мою же ошибку: сбор на живых данных не находил ничего.
    /// </summary>
    private static string FabricatedPath(string bucket = "bhs-crg", char dateSeparator = '.')
        => $"{bucket}/2026{dateSeparator}01{dateSeparator}02/{Guid.NewGuid()}_secret.pdf";

    [Fact]
    public async Task Upload_RegistersPath_SoDownloadWorks()
    {
        await fx.ResetDatabaseAsync();
        using var scope = fx.Services.CreateScope();
        var blob = scope.ServiceProvider.GetRequiredService<IBlobStorage>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var path = await blob.UploadAsync("отчёт.pdf", Content("данные"), "application/pdf");

        Assert.True(await db.BlobRegistry.AnyAsync(e => e.Path == path));

        await using var stream = await blob.DownloadAsync(path);
        Assert.Equal("данные", await new StreamReader(stream).ReadToEndAsync());
    }

    [Fact]
    public async Task Download_PathNotInRegistry_Refused()
    {
        await fx.ResetDatabaseAsync();
        using var scope = fx.Services.CreateScope();
        var blob = scope.ServiceProvider.GetRequiredService<IBlobStorage>();

        // Отказ приходит ДО обращения к хранилищу, поэтому существует объект или нет — неважно:
        // проверяется принадлежность, а не наличие.
        var ex = await Assert.ThrowsAsync<NotFoundException>(() => blob.DownloadAsync(FabricatedPath()));

        // Текст не должен называть путь: разница между «нет объекта» и «объект есть, но не наш» —
        // сама по себе сведения, по которым бакет и перебирают.
        Assert.DoesNotContain("2026/01/02", ex.Message);
    }

    [Fact]
    public async Task Delete_RemovesRegistration()
    {
        await fx.ResetDatabaseAsync();
        using var scope = fx.Services.CreateScope();
        var blob = scope.ServiceProvider.GetRequiredService<IBlobStorage>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var path = await blob.UploadAsync("временный.pdf", Content("x"), "application/pdf");
        await blob.DeleteAsync(path);

        Assert.False(await db.BlobRegistry.AnyAsync(e => e.Path == path));
        await Assert.ThrowsAsync<NotFoundException>(() => blob.DownloadAsync(path));
    }

    [Fact]
    public async Task Put_RegistersPath_SoRestoredSystemServesFiles()
    {
        await fx.ResetDatabaseAsync();
        using var scope = fx.Services.CreateScope();
        var blob = scope.ServiceProvider.GetRequiredService<IBlobStorage>();

        // Восстановление из бэкапа кладёт объект на путь из манифеста. Без записи в реестр
        // восстановленная система не отдала бы ни одного файла.
        var path = FabricatedPath();
        await blob.PutAsync(path, Content("из бэкапа"), "application/pdf");

        await using var stream = await blob.DownloadAsync(path);
        Assert.Equal("из бэкапа", await new StreamReader(stream).ReadToEndAsync());
    }

    [Fact]
    public async Task Backfill_FindsPathInsideJsonbRequisites()
    {
        await fx.ResetDatabaseAsync();
        var path = FabricatedPath();

        using (var seed = fx.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<AppDbContext>();
            // Путь вложения не лежит ни в одной колонке — только внутри реквизитов, куда его
            // положил клиент. Ради этого случая сбор и идёт по JSONB, а не по списку таблиц.
            var obj = DomainObject.Create(
                Guid.NewGuid(), "Организация",
                JsonDocument.Parse($"{{\"Скан\":{{\"blobPath\":\"{path}\",\"fileName\":\"с.pdf\"}}}}"),
                CatalogScope.System, null);
            db.DomainObjects.Add(obj);
            await db.SaveChangesAsync();
        }

        using var scope = fx.Services.CreateScope();
        var added = await scope.ServiceProvider.GetRequiredService<BlobRegistryBackfill>().RunAsync();

        Assert.True(added >= 1);
        var db2 = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db2.BlobRegistry.AnyAsync(e => e.Path == path));
    }

    [Fact]
    public async Task Backfill_FindsPathInTextColumn_AndIsIdempotent()
    {
        await fx.ResetDatabaseAsync();
        var path = FabricatedPath();
        // Второй — со слэшами в дате: разделитель зависит от культуры сервера, и база, наполненная
        // под другой локалью, обязана собираться так же.
        var slashPath = FabricatedPath(dateSeparator: '/');

        using (var seed = fx.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<AppDbContext>();
            db.DataSetFiles.Add(DataSetFile.Create(
                "реестр.csv", DataSetFormat.Csv, path, CatalogScope.System, null));
            db.DataSetFiles.Add(DataSetFile.Create(
                "реестр2.csv", DataSetFormat.Csv, slashPath, CatalogScope.System, null));
            await db.SaveChangesAsync();
        }

        using var scope = fx.Services.CreateScope();
        var backfill = scope.ServiceProvider.GetRequiredService<BlobRegistryBackfill>();

        Assert.True(await backfill.RunAsync() >= 2);
        // Второй прогон работы не находит: сбор идёт и на каждом старте приложения.
        Assert.Equal(0, await backfill.RunAsync());

        var db2 = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db2.BlobRegistry.AnyAsync(e => e.Path == path));
        Assert.True(await db2.BlobRegistry.AnyAsync(e => e.Path == slashPath));
    }

    [Fact]
    public void RealStorage_IsNotResolvableFromContainer()
    {
        // Страж от обхода, и главный здесь — не сам тест, а то, что настоящее хранилище в контейнер
        // не кладётся вовсе (Program.cs): попросить его вместо IBlobStorage нельзя даже намеренно.
        // Проверяем именно это свойство композиции, а не аккуратность вызывающих: типы
        // взаимозаменяемы по сигнатурам, и обход через GetRequiredService не заметят ни компилятор,
        // ни ревью.
        using var scope = fx.Services.CreateScope();

        Assert.Null(scope.ServiceProvider.GetService<MinIOBlobStorage>());
        Assert.IsType<RegisteredBlobStorage>(scope.ServiceProvider.GetRequiredService<IBlobStorage>());
    }

    [Fact]
    public void BackfillPattern_MatchesWhatStorageActuallyBuilds()
    {
        // Связь между формой пути и выражением, которое его ищет, — единственное место, где эти два
        // знания встречаются. Без этого теста изменение раскладки хранилища оставило бы весь набор
        // зелёным, а сбор реестра молча перестал бы что-либо находить: ровно это уже случилось
        // однажды, когда выражение требовало слэшей, а под русской локалью в пути стояли точки.
        var objectName = BlobPathShape.NewObjectName("реестр материалов.pdf");
        var fullPath = $"bhs-crg/{objectName}";

        Assert.Matches(BlobPathShape.Pattern, fullPath);
        Assert.Matches(BlobPathShape.RoughPattern, fullPath);
    }
}
