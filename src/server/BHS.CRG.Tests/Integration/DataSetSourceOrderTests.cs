using System.Text;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.DataSnapshots;
using BHS.CRG.Application.Documents;
using BHS.CRG.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Порядок источников набора закреплён (issue #1149): правка источника не меняет его места.
///
/// Без упорядочивания источники приходят в порядке строк в куче PostgreSQL, а UPDATE кладёт новую
/// версию строки в другое место — и поправленный источник уезжает за соседа. На странице «Наборы
/// данных» это выглядело так: сохранил сортировку у первого источника — он стал вторым.
///
/// Правим именно ПЕРВЫЙ из двух и именно службой: порядок ломает сам UPDATE, и проверка на наборе,
/// который после создания никто не трогал, зелёная при любом коде — куча там совпадает с порядком
/// создания. Имена даны так, чтобы порядок создания расходился с алфавитным: иначе закрепление «по
/// имени» прошло бы этот тест, а для страницы оно означало бы строку, прыгающую при переименовании.
///
/// <para><b>Таблица читается подряд — нарочно</b> (<see cref="ReadLikeLiveBaseAsync" />). После
/// TRUNCATE статистики у таблиц нет, и планировщик, не зная, что источников два, читает их по
/// индексу <c>FileId</c>. А индекс правку переживает: новая версия строки ложится на ту же страницу,
/// запись индекса остаётся прежней и ведёт к ней по цепочке — порядок сохраняется сам собой, и тест
/// зелёный без всякого упорядочивания (проверено: так и было). На живой базе статистика есть,
/// таблица в одну страницу читается подряд — и там источники переставляются.</para>
///
/// <para>И само условие тест УТВЕРЖДАЕТ (<see cref="HeapOrderAsync" />): чтение без порядка после
/// правки обязано отличаться от чтения до неё. Перестанет отличаться — сторож краснеет сам, а не
/// становится зелёным при любом коде, как это уже было однажды.</para>
/// </summary>
[Collection("Integration")]
public class DataSetSourceOrderTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static IDataSetService Svc(IServiceScope s) => s.ServiceProvider.GetRequiredService<IDataSetService>();
    private static AppDbContext Db(IServiceScope s) => s.ServiceProvider.GetRequiredService<AppDbContext>();

    /// <summary>Набор уровня комплекта с CSV-файлом: комплект нужен выдаче «наборы, доступные
    /// комплекту» — она читает наборы своим запросом, и порядок в ней проверяется отдельно.</summary>
    private static async Task<(Guid SetId, DataSetFileDto File, string Marker)> SeedFileAsync(IServiceScope scope)
    {
        var m = scope.ServiceProvider.GetRequiredService<IMediator>();
        var construction = await m.Send(new CreateConstructionCommand("Объект", Guid.NewGuid()));
        var section = await m.Send(new CreateSectionCommand(construction.Id, "ЭОМ"));
        var set = await m.Send(new CreateDocumentSetCommand(section.Id, "Комплект"));

        var svc = Svc(scope);
        var file = await svc.UploadFileAsync(new UploadFileInput(
            Encoding.UTF8.GetBytes("A,B\n1,2\n"), "d.csv", "text/csv", "Счёт", "Set", set.Id.ToString()), default);
        var marker = (await svc.DetectSourceCandidatesAsync(file.Id, TestAccess.All, default)).Single().SheetOrPath;
        return (set.Id, file, marker);
    }

    /// <summary>
    /// Читать таблицы подряд, как их читает база, живущая дольше одного теста (см. описание класса).
    ///
    /// Настройка — на СОЕДИНЕНИИ этой области, а не статистикой: ANALYZE оставил бы её в общей
    /// тестовой базе (TRUNCATE статистику колонок не убирает), и следующие классы планировались бы
    /// не так, как в одиночном запуске. Соединение держим открытым — иначе каждая команда брала бы
    /// из пула новое, без настройки; возвращаясь в пул, оно её сбрасывает.
    /// </summary>
    private static async Task ReadLikeLiveBaseAsync(IServiceScope scope)
    {
        var db = Db(scope);
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("SET enable_indexscan = off; SET enable_bitmapscan = off");
    }

    /// <summary>Источники набора так, как их отдаёт база без упорядочивания.</summary>
    private static Task<List<Guid>> HeapOrderAsync(IServiceScope scope, Guid fileId) =>
        Db(scope).Database
            .SqlQuery<Guid>($"""SELECT "Id" AS "Value" FROM dataset_sources WHERE "FileId" = {fileId}""")
            .ToListAsync();

    /// <summary>Правка обработки — то самое действие, после которого источники менялись местами.</summary>
    private static Task SaveSortAsync(IDataSetService svc, Guid sourceId) =>
        svc.SetSourceProcessingAsync(sourceId, new SetSourceProcessingInput
        {
            SortSpec = ProcessingPart.Of(new[] { new { column = "A", direction = "asc" } }),
        }, TestAccess.All, default);

    [Fact]
    public async Task EditingFirstSource_KeepsItFirst_InEveryListing()
    {
        using var scope = fixture.Services.CreateScope();
        var svc = Svc(scope);
        var (setId, file, marker) = await SeedFileAsync(scope);

        var header = await svc.CreateSourceAsync(file.Id, new CreateSourceInput("Шапка счёта", marker, null), TestAccess.All, default);
        var items = await svc.CreateSourceAsync(file.Id, new CreateSourceInput("Товары счёта", marker, null), TestAccess.All, default);
        Guid[] created = [header.Id, items.Id];
        await ReadLikeLiveBaseAsync(scope);

        // До правки порядок верен и без закрепления — сверяем, чтобы отказ ниже читался как «правка
        // переставила», а не как «порядок был другим с самого начала».
        var heapBefore = await HeapOrderAsync(scope, file.Id);
        await AssertOrderAsync();

        await SaveSortAsync(svc, header.Id);

        Assert.False(heapBefore.SequenceEqual(await HeapOrderAsync(scope, file.Id)),
            "Правка первого источника не переставила строки в чтении без порядка — тест перестал "
            + "воспроизводить дефект и прошёл бы при любом коде. Причину см. в описании класса.");
        await AssertOrderAsync();

        async Task AssertOrderAsync()
        {
            Assert.Equal(created, Ids(Assert.Single(
                await svc.ListFilesAsync("Set", setId, includeInherited: false, TestAccess.All, default)).Sources));
            Assert.Equal(created, Ids(Assert.Single(
                await svc.ListFilesAsync("Set", setId, includeInherited: true, TestAccess.All, default)).Sources));
            Assert.Equal(created, Ids(Assert.Single(await svc.ListAvailableFilesAsync(setId, TestAccess.All, default)).Sources));
            Assert.Equal(created, Ids(await svc.ListSourcesAsync(file.Id, TestAccess.All, default)));
        }
    }

    /// <summary>
    /// Выдача для внешнего агента упорядочена по имени — и остаётся такой. Но наборы, заведённые до
    /// запрета на совпадение имён (issue #717), содержат двойников, а между равными по имени решала
    /// та же куча: агент, запомнивший «второй „Лист“», после чужой правки получал под этим местом
    /// другой источник.
    /// </summary>
    [Fact]
    public async Task Snapshot_KeepsNameOrder_AndNamesakesStayInCreationOrder()
    {
        using var scope = fixture.Services.CreateScope();
        var svc = Svc(scope);
        var (_, file, marker) = await SeedFileAsync(scope);

        var first = await svc.CreateSourceAsync(file.Id, new CreateSourceInput("Лист", marker, null), TestAccess.All, default);
        var second = await svc.CreateSourceAsync(file.Id, new CreateSourceInput("Лист (второй)", marker, null), TestAccess.All, default);
        var acts = await svc.CreateSourceAsync(file.Id, new CreateSourceInput("Акты", marker, null), TestAccess.All, default);

        // Совпадение имён — мимо службы: сама она его уже не допускает, а в базах прошлых версий оно есть.
        var db = Db(scope);
        (await db.DataSetSources.FirstAsync(s => s.Id == second.Id)).Rename("Лист");
        await db.SaveChangesAsync();
        await ReadLikeLiveBaseAsync(scope);

        var heapBefore = await HeapOrderAsync(scope, file.Id);
        await SaveSortAsync(svc, first.Id);
        Assert.False(heapBefore.SequenceEqual(await HeapOrderAsync(scope, file.Id)),
            "Правка источника не переставила строки в чтении без порядка — см. описание класса.");

        var detail = await scope.ServiceProvider.GetRequiredService<IDataSnapshotService>()
            .GetDatasetAsync(file.Id, TestAccess.All);
        // «Акты» созданы последними, а стоят первыми: имя по-прежнему главный ключ.
        Assert.Equal([acts.Id, first.Id, second.Id], detail!.Sources.Select(s => s.Id));
    }

    /// <summary>
    /// Новый источник встаёт в конец, даже если часы сервера отстали от времени создания прежних.
    ///
    /// Проверяются оба входа, которыми источник добавляется к СУЩЕСТВУЮЩЕМУ набору: создание и
    /// копия. У копии правило держится на том, что набор загружен вместе со всеми источниками —
    /// поэтому копируем не последний источник, а первый: о «будущем» соседе набор узнает только
    /// если тот загружен.
    /// </summary>
    [Fact]
    public async Task NewSource_GoesLast_EvenWhenClockIsBehind()
    {
        using var scope = fixture.Services.CreateScope();
        var svc = Svc(scope);
        var (_, file, marker) = await SeedFileAsync(scope);

        var early = await svc.CreateSourceAsync(file.Id, new CreateSourceInput("Шапка", marker, null), TestAccess.All, default);
        var future = await svc.CreateSourceAsync(file.Id, new CreateSourceInput("Товары", marker, null), TestAccess.All, default);

        // «Часы отстали»: источник в базе создан позже, чем сейчас показывает сервер.
        var db = Db(scope);
        await db.Database.ExecuteSqlAsync(
            $"""UPDATE dataset_sources SET "CreatedAt" = now() + interval '1 day' WHERE "Id" = {future.Id}""");
        // Иначе служба получит набор из памяти контекста — с прежним временем источника.
        db.ChangeTracker.Clear();

        var copy = await svc.DuplicateSourceAsync(early.Id, "Шапка — копия", default);
        var created = await svc.CreateSourceAsync(file.Id, new CreateSourceInput("Акты", marker, null), TestAccess.All, default);

        Assert.Equal([early.Id, future.Id, copy!.Id, created.Id],
            Ids(await svc.ListSourcesAsync(file.Id, TestAccess.All, default)));
    }

    private static Guid[] Ids(IEnumerable<DataSetSourceDto> sources) => sources.Select(s => s.Id).ToArray();
}
