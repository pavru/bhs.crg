using System.Text;
using System.Text.Json;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Infrastructure.DataSets;
using BHS.CRG.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Соседи правки обработки по той же беде (issue #1141): редактор извлечения и диалог материализации
/// тоже заполнены из копии источника на странице и сохраняют ЗАМЕЩЕНИЕМ. С устаревшей копии они молча
/// возвращали прежнее значение туда, где другой человек только что поставил своё, — поэтому и они
/// называют версию, а служба её сверяет.
///
/// <para>Источник здесь файловый: у системного извлечение не редактируется вовсе.</para>
/// </summary>
[Collection("Integration")]
public class SourceVersionedEditsTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    private static readonly byte[] Csv = Encoding.UTF8.GetBytes("A,B\n1,2\n3,4\n");
    private const string Sort = """[{"column":"A","direction":"desc"}]""";

    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Вызов службы — каждый в своей области, как отдельный запрос: общий контекст базы отдал
    /// бы источник из памяти, каким тот был до чужой правки.</summary>
    private async Task<T> CallAsync<T>(Func<IDataSetService, Task<T>> call)
    {
        using var scope = fixture.Services.CreateScope();
        return await call(scope.ServiceProvider.GetRequiredService<IDataSetService>());
    }

    private async Task<DataSetSource> StoredAsync(Guid id)
    {
        using var scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .DataSetSources.AsNoTracking().FirstAsync(s => s.Id == id);
    }

    /// <summary>Источник на CSV — каким его получила страница: с обеими версиями.</summary>
    private Task<DataSetSourceDto> SourceAsync() => CallAsync(async svc =>
    {
        var file = await svc.UploadFileAsync(new UploadFileInput(Csv, "test.csv", "text/csv", "Тест", "System", null), default);
        var candidate = (await svc.DetectSourceCandidatesAsync(file.Id, TestAccess.All, default)).Single();
        return await svc.CreateSourceAsync(file.Id, new CreateSourceInput("Данные", candidate.SheetOrPath, null), TestAccess.All, default);
    });

    private async Task<Guid> RowTypeAsync()
    {
        using var scope = fixture.Services.CreateScope();
        var type = await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new CreateDocumentTypeCommand(
            "ROW", "ROW", DocumentTypeKind.Composite, null,
            JsonDocument.Parse("""{"fields":[{"key":"Поле","type":"string","required":false}]}""")));
        return type.Id;
    }

    private Task<DataSetSourceDto?> SortAsync(Guid id) => CallAsync(svc => svc.SetSourceProcessingAsync(
        id, new SetSourceProcessingInput { SortSpec = ProcessingPart.Of(JsonDocument.Parse(Sort).RootElement.Clone()) },
        TestAccess.All, default));

    [Fact]
    public async Task Правка_извлечения_с_устаревшей_копии_отклоняется()
    {
        var seen = await SourceAsync();
        // Тем временем источник правит другой: версия обработки включает извлечение, и наоборот —
        // редактор извлечения открыт с копии, чья обработка уже не та.
        await SortAsync(seen.Id);

        var refusal = await Assert.ThrowsAsync<ConflictException>(() => CallAsync(svc => svc.UpdateSourceAsync(
            seen.Id, new UpdateSourceInput("Иначе", seen.SheetOrPath, null, seen.ProcessingVersion), default)));

        Assert.Contains("тем временем изменили", refusal.Message);
        Assert.Equal("Данные", (await StoredAsync(seen.Id)).Name);

        // С текущей версией — сохраняется; без версии служба не сверяет (так зовёт код, не страница).
        var current = SourceProcessingVersion.Of(await StoredAsync(seen.Id));
        Assert.NotNull(await CallAsync(svc => svc.UpdateSourceAsync(
            seen.Id, new UpdateSourceInput("Иначе", seen.SheetOrPath, null, current), default)));
        Assert.NotNull(await CallAsync(svc => svc.UpdateSourceAsync(
            seen.Id, new UpdateSourceInput("Ещё иначе", seen.SheetOrPath, null), default)));
        Assert.Equal("Ещё иначе", (await StoredAsync(seen.Id)).Name);
    }

    [Fact]
    public async Task Материализация_с_устаревшей_копии_отклоняется_и_чужая_цела()
    {
        var seen = await SourceAsync();
        var rowType = await RowTypeAsync();
        var theirs = await CallAsync(svc => svc.SetMaterializationAsync(
            seen.Id, rowType, new() { ["Поле"] = "A" }, discriminator: null, byIdColumn: null, default));

        // Диалог открыт до чужой настройки и «снимает» материализацию, которой на его копии и не было.
        var refusal = await Assert.ThrowsAsync<ConflictException>(() => CallAsync(svc => svc.SetMaterializationAsync(
            seen.Id, null, null, discriminator: null, byIdColumn: null, default, seen.MaterializationVersion)));

        Assert.Contains("тем временем изменили", refusal.Message);
        Assert.Equal(rowType, (await StoredAsync(seen.Id)).MaterializeTypeId);

        // Версия из ответа на сохранение годится для следующего: маппинг лежит в jsonb, и текст после
        // записи другой — отпечаток считается по значению.
        Assert.Equal(theirs!.MaterializationVersion, SourceProcessingVersion.OfMaterialization(await StoredAsync(seen.Id)));
        Assert.NotNull(await CallAsync(svc => svc.SetMaterializationAsync(
            seen.Id, rowType, new() { ["Поле"] = "B" }, discriminator: null, byIdColumn: null, default,
            theirs.MaterializationVersion)));
        Assert.Contains("\"B\"", (await StoredAsync(seen.Id)).MaterializeMapping);
    }

    /// <summary>
    /// Версий две, и зависимость между ними в одну сторону. Диалог материализации сопоставляет поля с
    /// колонками источника — правка обработки его копию обесценивает. Диалогу сортировки всё равно, во
    /// что источник материализуется, — чужая правка материализации его останавливать не должна.
    /// </summary>
    [Fact]
    public async Task Обработка_двигает_версию_материализации_а_материализация_версию_обработки_нет()
    {
        var seen = await SourceAsync();
        var rowType = await RowTypeAsync();

        var materialized = await CallAsync(svc => svc.SetMaterializationAsync(
            seen.Id, rowType, new() { ["Поле"] = "A" }, discriminator: null, byIdColumn: null, default));
        Assert.Equal(seen.ProcessingVersion, materialized!.ProcessingVersion);
        Assert.NotEqual(seen.MaterializationVersion, materialized.MaterializationVersion);

        var sorted = await SortAsync(seen.Id);
        Assert.NotEqual(materialized.ProcessingVersion, sorted!.ProcessingVersion);
        Assert.NotEqual(materialized.MaterializationVersion, sorted.MaterializationVersion);
        await Assert.ThrowsAsync<ConflictException>(() => CallAsync(svc => svc.SetMaterializationAsync(
            seen.Id, null, null, discriminator: null, byIdColumn: null, default, materialized.MaterializationVersion)));
    }
}
