using System.Text;
using BHS.CRG.Application.Common;
using BHS.CRG.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Отсутствующие пути — о многих сразу (issue #1439): тот же ответ, что «размера нет» у
/// <see cref="IBlobStorage.GetSizeAsync" />, но реестр спрашивается один раз, а хранилище — только о
/// зарегистрированных путях.
///
/// <para>Ради чего: проверка файлов после восстановления спрашивала о каждом пути вне архива по
/// одному, и у копии, снятой при недоступном хранилище, это были тысячи пар «реестр + хранилище»
/// внутри HTTP-запроса. Ответ обязан остаться прежним — ускорение, которое меняет «нет» на «есть»,
/// молча спрятало бы потерянные файлы.</para>
/// </summary>
[Collection("Integration")]
public class BlobFindAbsentTests(IntegrationTestFixture fx)
{
    private static Stream Content(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task Ответ_тот_же_что_у_размера_по_одному()
    {
        await fx.ResetDatabaseAsync();
        using var scope = fx.Services.CreateScope();
        var blob = scope.ServiceProvider.GetRequiredService<IBlobStorage>();
        var storage = fx.Services.GetRequiredService<FakeBlobStorage>();

        var kept = await blob.UploadAsync("на-месте.pdf", Content("1"), "application/pdf");
        var lost = await blob.UploadAsync("пропал.pdf", Content("2"), "application/pdf");
        // Пропал мимо приложения: запись в реестре осталась, объекта в хранилище нет.
        await storage.DeleteAsync(lost);
        // Лежит в хранилище, но не наш: приложение его не создавало и не отдаёт.
        var foreign = "fake/2026.01.02/" + Guid.NewGuid() + "_чужой.pdf";
        await storage.PutAsync(foreign, Content("3"), "application/pdf");
        var nowhere = "fake/2026.01.02/" + Guid.NewGuid() + "_нигде.pdf";

        string[] paths = [kept, lost, foreign, nowhere, kept];
        var absent = await blob.FindAbsentAsync(paths);

        foreach (var path in paths.Distinct())
            Assert.Equal(await blob.GetSizeAsync(path) is null, absent.Contains(path));
        Assert.Equal(new[] { lost, foreign, nowhere }.Order(StringComparer.Ordinal), absent.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Реестр спрашивается ОДИН раз за все пути, хранилище — только о зарегистрированных. Так выглядит
    /// восстановление старой копии на новой установке: путей вне архива тысячи, и в реестре нет ни
    /// одного. Прежде на каждый путь был свой запрос к реестру — их и считает этот тест: обёртка
    /// открывает область видимости на каждый запрос к базе.
    /// </summary>
    [Fact]
    public async Task Реестр_один_раз_хранилище_только_о_зарегистрированных()
    {
        await fx.ResetDatabaseAsync();
        var storage = fx.Services.GetRequiredService<FakeBlobStorage>();
        var scopes = new CountingScopes(fx.Services.GetRequiredService<IServiceScopeFactory>());
        IBlobStorage blob = new RegisteredBlobStorage(storage, scopes, NullLogger<RegisteredBlobStorage>.Instance);
        var kept = await blob.UploadAsync("на-месте.pdf", Content("1"), "application/pdf");
        var unknown = Enumerable.Range(0, 500).Select(i => $"fake/2026.01.02/{Guid.NewGuid()}_{i}.pdf").ToList();

        var (opened, asked) = (scopes.Opened, storage.SizeQueries);
        var absent = await blob.FindAbsentAsync(unknown.Append(kept));

        Assert.Equal(1, scopes.Opened - opened);
        Assert.Equal(1, storage.SizeQueries - asked);
        Assert.Equal(unknown.Count, absent.Count);
        Assert.DoesNotContain(kept, absent);
    }

    /// <summary>Считает открытые области видимости — по одной на запрос обёртки к реестру.</summary>
    private sealed class CountingScopes(IServiceScopeFactory inner) : IServiceScopeFactory
    {
        public int Opened => _opened;
        private int _opened;

        public IServiceScope CreateScope()
        {
            Interlocked.Increment(ref _opened);
            return inner.CreateScope();
        }
    }

    /// <summary>Недоступное хранилище — отказ, а не «все пропали» (issue #1427): ускорение его не глотает.</summary>
    [Fact]
    public async Task Недоступное_хранилище_отказ_а_не_пропавшие()
    {
        await fx.ResetDatabaseAsync();
        using var scope = fx.Services.CreateScope();
        var blob = scope.ServiceProvider.GetRequiredService<IBlobStorage>();
        var storage = fx.Services.GetRequiredService<FakeBlobStorage>();
        var kept = await blob.UploadAsync("на-месте.pdf", Content("1"), "application/pdf");

        try
        {
            storage.Offline = true;
            await Assert.ThrowsAsync<BlobStorageUnavailableException>(() => blob.FindAbsentAsync([kept]));
        }
        finally { storage.Offline = false; }
    }

    [Fact]
    public async Task Пустой_вопрос_пустой_ответ()
    {
        using var scope = fx.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<IBlobStorage>().FindAbsentAsync([]));
    }
}
