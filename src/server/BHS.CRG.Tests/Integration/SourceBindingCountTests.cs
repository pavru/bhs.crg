using BHS.CRG.Application.Common;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Счётчик привязок источника (issue #417): без него пользователь удаляет источник вслепую —
/// ссылающиеся на него привязки молча перестают работать.
///
/// Отдельно защищается смысл null: ответ ОДИНОЧНОЙ мутации счётчик не несёт, и UI не должен
/// показывать из-за этого ложный ноль — «не считали» и «привязок нет» обязаны различаться.
/// </summary>
[Collection("Integration")]
public class SourceBindingCountTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<(Guid fileId, Guid boundSourceId, Guid freeSourceId, IServiceScope scope)> SeedAsync(int bindings)
    {
        var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var blob = scope.ServiceProvider.GetRequiredService<IBlobStorage>();
        var blobPath = await blob.UploadAsync("m.csv", new MemoryStream("A,B\n1,2"u8.ToArray()), "text/csv");

        var file = DataSetFile.Create("Реестр", DataSetFormat.Csv, blobPath, CatalogScope.System, null);
        var bound = file.AddSource("С привязками", "default", "[]", 1);
        var free = file.AddSource("Без привязок", "other", "[]", 1);
        db.DataSetFiles.Add(file);
        db.DataSetSources.AddRange(bound, free);

        for (var i = 0; i < bindings; i++)
            db.DataSetBindings.Add(DataSetBinding.For(Guid.NewGuid(), bound.Id, null, "{}"));

        await db.SaveChangesAsync();
        return (file.Id, bound.Id, free.Id, scope);
    }

    [Fact]
    public async Task ListFiles_CarriesBindingCountPerSource()
    {
        var (fileId, boundId, freeId, scope) = await SeedAsync(bindings: 3);
        using (scope)
        {
            var svc = scope.ServiceProvider.GetRequiredService<IDataSetService>();
            var file = (await svc.ListFilesAsync(nameof(CatalogScope.System), null, default))
                .Single(f => f.Id == fileId);

            Assert.Equal(3, file.Sources.Single(s => s.Id == boundId).BindingCount);
            // Ноль, а не null: в списке счёт запрашивали — «привязок нет» это факт, а не неизвестность.
            Assert.Equal(0, file.Sources.Single(s => s.Id == freeId).BindingCount);
        }
    }

    [Fact]
    public async Task ListSources_CarriesBindingCount()
    {
        var (fileId, boundId, _, scope) = await SeedAsync(bindings: 2);
        using (scope)
        {
            var svc = scope.ServiceProvider.GetRequiredService<IDataSetService>();
            var sources = await svc.ListSourcesAsync(fileId, default);
            Assert.Equal(2, sources.Single(s => s.Id == boundId).BindingCount);
        }
    }

    [Fact]
    public async Task SingleSourceMutation_LeavesCountUnknown_NotZero()
    {
        var (_, boundId, _, scope) = await SeedAsync(bindings: 2);
        using (scope)
        {
            var svc = scope.ServiceProvider.GetRequiredService<IDataSetService>();
            // Переименование возвращает один источник и счёт не запрашивает. Если бы здесь пришёл 0,
            // UI мигнул бы «привязок нет» у источника, на который ссылаются две.
            var renamed = await svc.RenameSourceAsync(boundId, "Новое имя", default);
            Assert.NotNull(renamed);
            Assert.Null(renamed!.BindingCount);
        }
    }
}
