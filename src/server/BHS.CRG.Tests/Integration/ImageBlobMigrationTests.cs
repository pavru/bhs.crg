using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Infrastructure.Maintenance;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Разовый перенос картинок из JSONB в блоб-хранилище (issue #522). Главные свойства: сухой прогон
/// ничего не меняет, повторный прогон безвреден, опции размера переживают переезд.
/// </summary>
[Collection("Integration")]
public class ImageBlobMigrationTests(IntegrationTestFixture fx)
{
    private const string Png =
        "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    private static JsonDocument Data(string json) => JsonDocument.Parse(json);

    private async Task<Guid> SeedAsync(string json)
    {
        await fx.ResetDatabaseAsync();
        using var scope = fx.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var obj = DomainObject.Create(Guid.NewGuid(), "Организация", Data(json), CatalogScope.System, null);
        db.DomainObjects.Add(obj);
        await db.SaveChangesAsync();
        return obj.Id;
    }

    private static ImageBlobMigration MigrationFor(IServiceScope scope) =>
        new(scope.ServiceProvider.GetRequiredService<AppDbContext>(),
            scope.ServiceProvider.GetRequiredService<IBlobStorage>());

    [Fact]
    public async Task DryRun_CountsButChangesNothing()
    {
        var id = await SeedAsync("{\"Печать\":{\"src\":\"" + Png + "\",\"width\":\"4cm\"}}");
        using var scope = fx.Services.CreateScope();
        var report = await MigrationFor(scope).RunAsync(dryRun: true);

        Assert.Equal(1, report.Objects);
        // Ровно одна: узел {src, ...} — это ОДНА картинка. Пока обход спускался внутрь него, сухой
        // прогон считал её дважды и врал бы в отчёте, который читает человек.
        Assert.Equal(1, report.Images);
        Assert.True(report.Bytes > 0);

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var saved = await db.DomainObjects.FindAsync(id);
        Assert.Contains("data:image", saved!.Data.RootElement.GetRawText());   // ничего не тронуто
    }

    [Fact]
    public async Task Migrates_KeepsOptions_AndIsIdempotent()
    {
        var id = await SeedAsync(
            "{\"Печать\":{\"src\":\"" + Png + "\",\"width\":\"4cm\",\"align\":\"center\"},"
            + "\"Логотип\":\"" + Png + "\","
            + "\"ИНН\":\"7701234567\"}");
        using (var scope = fx.Services.CreateScope())
        {
            var report = await MigrationFor(scope).RunAsync(dryRun: false);
            Assert.Equal(1, report.Objects);
            Assert.Equal(2, report.Images);   // объект-значение и легаси-строка
        }

        using (var scope = fx.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var saved = await db.DomainObjects.FindAsync(id);
            var root = saved!.Data.RootElement;

            Assert.DoesNotContain("data:image", root.GetRawText());            // байтов в JSONB больше нет
            var stamp = root.GetProperty("Печать");
            Assert.Equal("image", stamp.GetProperty("$type").GetString());
            Assert.False(string.IsNullOrEmpty(stamp.GetProperty("blobPath").GetString()));
            Assert.Equal("4cm", stamp.GetProperty("width").GetString());       // опции пережили переезд
            Assert.Equal("center", stamp.GetProperty("align").GetString());
            Assert.Equal("image", root.GetProperty("Логотип").GetProperty("$type").GetString());
            Assert.Equal("7701234567", root.GetProperty("ИНН").GetString());   // остальное не тронуто
        }

        // Повторный прогон обязан быть безвредным: восстановление старого бэкапа заново впрыскивает
        // data-URI, и миграцию будут запускать снова.
        using (var scope = fx.Services.CreateScope())
        {
            var again = await MigrationFor(scope).RunAsync(dryRun: false);
            Assert.Equal(0, again.Objects);
            Assert.Equal(0, again.Images);
        }
    }

    /// <summary>Картинки бывают во вложенных объектах и в строках массивов — их тоже надо забрать.</summary>
    [Fact]
    public async Task FindsImagesInNestedStructures()
    {
        await SeedAsync(
            "{\"Орг\":{\"Скан\":{\"src\":\"" + Png + "\"}},"
            + "\"Материалы\":[{\"Фото\":\"" + Png + "\"}]}");
        using var scope = fx.Services.CreateScope();
        var report = await MigrationFor(scope).RunAsync(dryRun: false);

        Assert.Equal(2, report.Images);
    }

    /// <summary>
    /// Битый base64 в системе ожидаем — материализатор Typst его молча пропускает. Отказ ОДНОЙ
    /// картинки не должен губить весь прогон: соседняя обязана переехать, а отказ — попасть в отчёт
    /// (issue #532).
    /// </summary>
    [Fact]
    public async Task BrokenImage_DoesNotKillTheRun()
    {
        var id = await SeedAsync(
            "{\"Битая\":\"data:image/png;base64,не-base64!!\","
            + "\"Целая\":\"" + Png + "\"}");

        using var scope = fx.Services.CreateScope();
        var report = await MigrationFor(scope).RunAsync(dryRun: false);

        Assert.Equal(1, report.Images);   // целая переехала
        Assert.Equal(1, report.Failed);   // битая честно посчитана

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var saved = await db.DomainObjects.FindAsync(id);
        var root = saved!.Data.RootElement;
        Assert.Equal("image", root.GetProperty("Целая").GetProperty("$type").GetString());
        Assert.StartsWith("data:image", root.GetProperty("Битая").GetString());   // оставлена как была
    }
}
