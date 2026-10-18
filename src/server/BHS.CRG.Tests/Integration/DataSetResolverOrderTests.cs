using System.Text;
using System.Text.Json;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Generation;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Порядок привязок наборов данных в контексте генерации закреплён ключом (issue #1402).
///
/// <para>Найдено репетицией выпуска: после <c>pg_restore</c> у документа сменился порядок
/// <c>_dataSets</c> и нумерация вложений. Опасный случай — две привязки в одно поле: итог зависел от
/// физического порядка строк в базе, и копия давала не тот документ, что оригинал.</para>
///
/// <para>⚠️ Порядок кучи в тестовой базе сам не воспроизводится (после TRUNCATE планировщик идёт по
/// индексу). Поэтому привязки, их источники и файлы вставляются в порядке, ОБРАТНОМ ключу, индексное
/// чтение на соединении выключено, и предусловие — запрос формы резолвера без порядка отдаёт их не по
/// ключу — утверждается: иначе при смене плана сторож онемел бы молча.</para>
/// </summary>
[Collection("Integration")]
public class DataSetResolverOrderTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static JsonDocument J(string singleQuoted) => JsonDocument.Parse(singleQuoted.Replace('\'', '"'));

    [Fact]
    public async Task Две_привязки_в_одно_поле_побеждает_позже_заведённая_при_любом_порядке_строк()
    {
        using var scope = fixture.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IDataSetService>();
        var m = scope.ServiceProvider.GetRequiredService<IMediator>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var rowType = await m.Send(new CreateDocumentTypeCommand("ROW", "ROW", DocumentTypeKind.Composite, null,
            J("{'fields':[{'key':'Поле','type':'string','required':false}]}")));
        var docType = await m.Send(new CreateDocumentTypeCommand("REESTR", "REESTR", DocumentTypeKind.Document, null,
            J($"{{'fields':[{{'key':'Строки','type':'array','typeId':'{rowType.Id}'}}]}}")));
        var construction = await m.Send(new CreateConstructionCommand("Объект", Guid.NewGuid()));
        var section = await m.Send(new CreateSectionCommand(construction.Id, "Раздел"));
        var set = await m.Send(new CreateDocumentSetCommand(section.Id, "Комплект"));
        var instance = await m.Send(new AddDocumentToSetCommand(set.Id, docType.Id));

        async Task<Guid> SourceAsync(string name, string value)
        {
            var file = await svc.UploadFileAsync(new UploadFileInput(
                Encoding.UTF8.GetBytes($"A\n{value}\n"), $"{name}.csv", "text/csv", name, "System", null), default);
            var candidate = (await svc.DetectSourceCandidatesAsync(file.Id, TestAccess.All, default)).Single();
            var source = await svc.CreateSourceAsync(file.Id, new CreateSourceInput(name, candidate.SheetOrPath, null), TestAccess.All, default);
            await svc.SetMaterializationAsync(source.Id, rowType.Id, new() { ["Поле"] = "A" }, discriminator: null, byIdColumn: null, default);
            return source.Id;
        }

        // Источники и файлы — тоже в порядке, обратном ключу: запрос резолвера соединяет привязку с
        // источником и файлом, и база вправе вести выдачу по любой из трёх таблиц. Заведи их по ключу —
        // порядок совпал бы с правильным случайно, и сторож молчал бы (так и было при первой пробе).
        var laterSource = await SourceAsync("Поздний", "поздняя");
        var earlierSource = await SourceAsync("Ранний", "ранняя");

        // Обе привязки — в одно поле, и вставлены в порядке, обратном ключу: сначала позже заведённая.
        // Напрямую, а не службой: время создания задаётся явно (так их и возвращает копия).
        var at = DateTimeOffset.UtcNow.AddDays(-1);
        var later = DataSetBinding.Restore(Guid.NewGuid(), instance.Id, laterSource, "Строки", "{}", at.AddMinutes(5), at.AddMinutes(5));
        var earlier = DataSetBinding.Restore(Guid.NewGuid(), instance.Id, earlierSource, "Строки", "{}", at, at);
        db.DataSetBindings.Add(later);
        await db.SaveChangesAsync();
        db.DataSetBindings.Add(earlier);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // Читать подряд, как база, живущая дольше одного теста (см. описание класса). Настройка — на
        // соединении области; резолвер берёт тот же контекст, а значит, и это соединение.
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("SET enable_indexscan = off; SET enable_bitmapscan = off");
        // Предусловие: запрос той же формы, что у резолвера, но без порядка, отдаёт привязки НЕ по ключу.
        var unordered = (await db.DataSetBindings.Include(b => b.Source).ThenInclude(s => s.File)
            .Where(b => b.OwnerId == instance.Id).AsNoTracking().ToListAsync()).Select(b => b.Id).ToList();
        Assert.Equal([later.Id, earlier.Id], unordered);

        var inst = await m.Send(new GetDocumentInstanceQuery(instance.Id));
        var view = DocumentView.From(inst!);
        var ctx = await scope.ServiceProvider.GetRequiredService<IEntityResolver>().ResolveAsync(view);
        await scope.ServiceProvider.GetRequiredService<IDataSetResolver>().InjectAsync(ctx, view, TestAccess.All, null, default);

        var rows = (JsonElement)ctx.Data["Строки"]!;
        Assert.Equal("поздняя", rows.EnumerateArray().Single().GetProperty("Поле").GetString());
    }
}
