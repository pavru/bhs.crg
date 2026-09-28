using System.Data;
using System.Data.Common;
using System.IO.Compression;
using System.Text.Json;
using BHS.CRG.Api.Modules;
using BHS.CRG.Application.Activity;
using BHS.CRG.Application.Backup;
using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Infrastructure.Backup;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Схема модуля в резервной копии: круг «снял — восстановил» на базе, где есть объект ядра и
/// сославшаяся на него строка модуля (задача A2b этапа 2, issue #1073, ТЗ CORE-4, CORE-29).
///
/// <para>⚠️ Проверяется на ПОДДЕЛЬНОМ модуле, и это единственный способ проверить круг сегодня: у
/// настоящего модуля <c>costs</c> таблиц ещё нет — первая приезжает с C1 (issue #1076), — а копия
/// пустой схемы не доказывает ничего. Поддельны здесь только модуль и его таблица: и
/// <see cref="ModuleSchemaBackup" />, и <see cref="BackupService" />, и база — настоящие. Тот же
/// приём, которым A2a проверяла защиту дописываемых наборов.</para>
///
/// <para>Что с настоящим модулем, проверяет <c>ModuleSchemaTests</c>: его схема названа в копии, снятой
/// живым хостом.</para>
/// </summary>
[Collection("Integration")]
public class ModuleDataBackupTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    private const string ModuleCode = "probe";
    private const string ModuleSchemaName = "probe_data";

    public async Task InitializeAsync()
    {
        _ = fixture.CreateClient();
        await fixture.ResetDatabaseAsync();
        await DropSchemaAsync();
        await CreateSchemaAsync();
    }

    public async Task DisposeAsync() => await DropSchemaAsync();

    /// <summary>
    /// Круг целиком: строка модуля уехала в копию и вернулась из неё той же, а её ссылка на объект
    /// ядра ведёт к живому объекту.
    ///
    /// <para>Это и есть признак готовности A2b словами теста. Значения сверяются ПОИМЁННО, а не по
    /// числу строк: копия несёт строку как <c>to_jsonb</c>, и потеря точности у суммы, часового пояса
    /// у даты или вложенного <c>jsonb</c> выглядела бы успешным восстановлением.</para>
    ///
    /// <para>Сирот проверяем отдельным вопросом к базе: ссылка модуля на объект ядра держится
    /// идентификатором, внешнего ключа сквозь схемы нет (A2a), и оборванную ссылку не заметит никто —
    /// ни база, ни восстановление.</para>
    /// </summary>
    [Fact]
    public async Task Строка_модуля_уезжает_в_копию_и_возвращается_из_неё()
    {
        var objectId = await SeedCoreObjectAsync();
        var invoiceId = await SeedInvoiceAsync(objectId);
        var before = await ReadInvoiceAsync(invoiceId);

        var (archive, manifest) = await ExportAsync(BackupScope.Full);

        var section = Assert.Single(manifest.ModuleData!);
        Assert.Equal(ModuleCode, section.Module);
        Assert.Equal(ModuleSchemaName, section.Schema);

        // Порядок таблиц — от независимых к зависимым, а не по имени: по имени «invoice_lines» шло бы
        // ПЕРЕД «invoices», и восстановление упало бы на внешнем ключе внутри схемы модуля. Ключи
        // внутри схемы модуля разрешены — запрещены только сквозные (A2a), — и у счёта позиции будут.
        Assert.Equal(["invoices", "probe_log", "invoice_lines"], section.Tables.Select(t => t.Table));
        Assert.Single(section.Tables.Single(t => t.Table == "invoices").Rows);
        Assert.Single(section.Tables.Single(t => t.Table == "invoice_lines").Rows);

        // Сносим строки модуля — так выглядит потеря, ради которой копия и существует. Объект ядра
        // оставляем: восстановление обязано вернуть ссылку на него, а не завести второй.
        //
        // Обе таблицы поимённо и в порядке зависимости. У DELETE в PostgreSQL слова CASCADE НЕТ — оно
        // садится в позицию алиаса таблицы и не делает ничего (ревью PR #1108): позиции уходили лишь
        // потому, что EF по соглашению завёл ON DELETE CASCADE у обязательной связи. То есть проверка
        // держалась на том, чего в её собственном тексте не написано.
        await ExecuteAsync($"DELETE FROM {ModuleSchemaName}.invoice_lines");
        await ExecuteAsync($"DELETE FROM {ModuleSchemaName}.invoices");

        var report = await ImportAsync(archive);
        Assert.True(report.Success, string.Join("; ", report.Warnings));

        var after = await ReadInvoiceAsync(invoiceId);
        Assert.Equal(before.ObjectId, after.ObjectId);
        Assert.Equal(before.Number, after.Number);
        Assert.Equal(before.Seq, after.Seq);
        Assert.Equal(before.Amount, after.Amount);
        Assert.Equal(before.IssuedAt, after.IssuedAt);
        Assert.Equal(before.Payload!.RootElement.GetRawText(), after.Payload!.RootElement.GetRawText());
        Assert.Equal(before.Note, after.Note);

        Assert.Equal(1L, await ScalarAsync<long>(
            $"SELECT count(*) FROM {ModuleSchemaName}.invoice_lines WHERE invoice_id = '{invoiceId}'"));

        var orphans = await ScalarAsync<long>(
            $"SELECT count(*) FROM {ModuleSchemaName}.invoices i " +
            "WHERE NOT EXISTS (SELECT 1 FROM domain_objects o WHERE o.\"Id\" = i.object_id)");
        Assert.Equal(0L, orphans);

        // Отчёт называет, что сделал, и по таблицам: иначе «восстановлено» о данных модуля узнать
        // негде — секций манифеста человек не читает.
        var mine = report.ProjectSections!
            .Where(s => s.Label.Contains(ModuleCode, StringComparison.Ordinal)).ToList();
        Assert.Equal(
            [$"Модуль «{ModuleCode}»: invoices", $"Модуль «{ModuleCode}»: invoice_lines"],
            mine.Select(s => s.Label));
        Assert.All(mine, s => Assert.Equal((1, 0), (s.Created, s.Updated)));
    }

    /// <summary>
    /// Повторное восстановление той же копии обновляет строку, а не заводит вторую, — и отчёт говорит
    /// «обновлено», а не «добавлено».
    ///
    /// <para>Ровно так восстановление и применяют в жизни: поверх живой системы, иногда дважды. Вставка
    /// без слияния упала бы на первичном ключе, то есть откатила бы восстановление ЦЕЛИКОМ — из-за
    /// строки, которая уже такая, какой должна быть.</para>
    /// </summary>
    [Fact]
    public async Task Повторное_восстановление_строку_модуля_обновляет_а_не_удваивает()
    {
        var objectId = await SeedCoreObjectAsync();
        var invoiceId = await SeedInvoiceAsync(objectId);
        var (archive, _) = await ExportAsync(BackupScope.Full);

        await ExecuteAsync(
            $"UPDATE {ModuleSchemaName}.invoices SET number = 'правлено вручную', amount = 1");

        var report = await ImportAsync(archive);
        Assert.True(report.Success, string.Join("; ", report.Warnings));

        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM {ModuleSchemaName}.invoices"));
        Assert.Equal("Счёт №1", (await ReadInvoiceAsync(invoiceId)).Number);

        var stat = Assert.Single(report.ProjectSections!
            .Where(s => s.Label.EndsWith("invoices", StringComparison.Ordinal)));
        Assert.Equal(0, stat.Created);
        Assert.Equal(1, stat.Updated);
    }

    /// <summary>
    /// После копии контекст модуля возвращается СЕБЕ: ни соединения ядра, ни его транзакции
    /// (ревью PR #1108).
    ///
    /// <para>Копия — названное исключение из правила «нет общей транзакции», и оно обязано кончаться
    /// вместе с копией. Контекст модуля из контейнера живёт всю область запроса; оставь его на
    /// соединении ядра — и всё, что модуль запишет в этой области ПОСЛЕ копии, молча войдёт в
    /// транзакцию ядра. Это ровно то, что правило запрещает, и заметить это нечем: запросы работают,
    /// данные сохраняются, общая транзакция просто есть.</para>
    ///
    /// <para>Крах при этом не наступает, и проверять его бессмысленно: <c>SetDbConnection</c> второй
    /// раз проходит — своё соединение контекст модуля к тому моменту закрыл (проверено прямо,
    /// двумя копиями в одной области и запросом модуля перед ними). Дефект здесь тихий, поэтому и
    /// сторож смотрит на состояние, а не на отказ.</para>
    /// </summary>
    [Fact]
    public async Task После_копии_контекст_модуля_не_делит_соединение_с_ядром()
    {
        await SeedInvoiceAsync(await SeedCoreObjectAsync());

        using var scope = fixture.Services.CreateScope();
        await using var probe = ProbeProvider();
        using var probeScope = probe.CreateScope();
        var service = Backup(scope, new ModuleSchemaBackup(Registry(), probeScope.ServiceProvider));

        // Контекстом модуля в этой области уже пользовались — так выглядит запрос, который сначала
        // читает данные модуля, а потом снимает копию.
        var moduleDb = probeScope.ServiceProvider.GetRequiredService<ProbeInvoiceContext>();
        _ = await moduleDb.Invoices.CountAsync();

        var first = ReadManifest(await ArchiveAsync(service, BackupScope.Full));
        var second = ReadManifest(await ArchiveAsync(service, BackupScope.Full));

        Assert.Single(Assert.Single(first.ModuleData!).Tables.Single(t => t.Table == "invoices").Rows);
        Assert.Single(Assert.Single(second.ModuleData!).Tables.Single(t => t.Table == "invoices").Rows);

        var coreDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Null(moduleDb.Database.CurrentTransaction);
        Assert.NotSame(coreDb.Database.GetDbConnection(), moduleDb.Database.GetDbConnection());
        Assert.Equal(1, await moduleDb.Invoices.CountAsync());
    }

    /// <summary>
    /// Счётчик таблицы модуля сдвигается за восстановленные значения (ревью PR #1108).
    ///
    /// <para>Без этого строки возвращаются, а последовательность остаётся на чистой установке в начале:
    /// первый же счёт, заведённый модулем после восстановления, отказывает дублем номера — далеко от
    /// восстановления и без всякой видимой связи с ним. Так же поступает <c>pg_dump</c>, и по той же
    /// причине.</para>
    ///
    /// <para>Счётчик у поддельного счёта уникален нарочно: не будь он уникален, устаревшая
    /// последовательность дала бы просто повторяющиеся номера — дефект, который не падает, а
    /// накапливается.</para>
    /// </summary>
    [Fact]
    public async Task Счётчик_таблицы_модуля_сдвигается_после_восстановления()
    {
        var objectId = await SeedCoreObjectAsync();
        await SeedInvoiceAsync(objectId);
        var (archive, _) = await ExportAsync(BackupScope.Full);

        // Так выглядит восстановление на ЧИСТУЮ установку: таблиц нет строк, счётчики в начале.
        await ExecuteAsync(
            $"TRUNCATE {ModuleSchemaName}.invoice_lines, {ModuleSchemaName}.invoices RESTART IDENTITY");

        var report = await ImportAsync(archive);
        Assert.True(report.Success, string.Join("; ", report.Warnings));

        // Следующий счёт модуль заводит как обычно. С отставшим счётчиком здесь был бы отказ по
        // уникальности номера.
        await SeedInvoiceAsync(objectId, number: "Счёт №2");

        Assert.Equal(2L, await ScalarAsync<long>($"SELECT count(*) FROM {ModuleSchemaName}.invoices"));
        Assert.Equal(2L, await ScalarAsync<long>(
            $"SELECT count(DISTINCT seq) FROM {ModuleSchemaName}.invoices"));
    }

    /// <summary>
    /// Таблица модуля без первичного ключа не восстанавливается — и об этом сказано (ревью PR #1108).
    ///
    /// <para>Слить её строки не с чем, а голая вставка удвоила бы их при повторном восстановлении —
    /// а повторное восстановление той же копии в работе дело обычное. Из двух неверных исходов выбран
    /// тот, который называет себя: удвоенные строки уже не различить, а копия никуда не делась.</para>
    /// </summary>
    [Fact]
    public async Task Таблица_модуля_без_первичного_ключа_не_восстанавливается_и_говорит_об_этом()
    {
        await SeedInvoiceAsync(await SeedCoreObjectAsync());
        await ExecuteAsync($"INSERT INTO {ModuleSchemaName}.probe_log (text) VALUES ('след')");

        var (archive, manifest) = await ExportAsync(BackupScope.Full);

        // В копию строка попала — потеря не в снятии, а именно в применении.
        Assert.Single(Assert.Single(manifest.ModuleData!).Tables.Single(t => t.Table == "probe_log").Rows);

        await ExecuteAsync($"DELETE FROM {ModuleSchemaName}.probe_log");
        var report = await ImportAsync(archive);

        Assert.True(report.Success, string.Join("; ", report.Warnings));
        Assert.Equal(0L, await ScalarAsync<long>($"SELECT count(*) FROM {ModuleSchemaName}.probe_log"));
        Assert.Contains(report.Warnings, w =>
            w.Contains("probe_log", StringComparison.Ordinal)
            && w.Contains("нет первичного ключа", StringComparison.Ordinal));
        Assert.DoesNotContain(report.ProjectSections!, s => s.Label.Contains("probe_log", StringComparison.Ordinal));
    }

    /// <summary>
    /// Конфигурационная копия схем модулей не несёт вовсе: <c>null</c>, а не пустой массив.
    ///
    /// <para>Решение A2b: модуль — про проектную работу, и конфигурационная копия остаётся тем, чем
    /// была, включая вес. Проверяется потому, что обратная правка («снимать всегда») выглядела бы
    /// безобидной, а конфигурационная копия разом потяжелела бы на все счета системы.</para>
    /// </summary>
    [Fact]
    public async Task Конфигурационная_копия_данных_модуля_не_несёт()
    {
        await SeedInvoiceAsync(await SeedCoreObjectAsync());

        var (_, manifest) = await ExportAsync(BackupScope.Configuration);

        Assert.Null(manifest.ModuleData);
    }

    /// <summary>
    /// Снимок ядра и модуля — ОДИН: строка, появившаяся в схеме модуля после начала копии, в копию не
    /// попадает.
    ///
    /// <para>Это проверка того самого названного исключения из правила «нет общей транзакции»
    /// (<see cref="IModuleSchemaBackup" />). Чужая запись вставляется ДРУГИМ соединением ровно между
    /// чтением схемы ядра и чтением схемы модуля — момент, которого иначе не поймать, — и уровень
    /// <c>RepeatableRead</c> обязан её скрыть. Уберите <c>SetDbConnection</c>/<c>UseTransaction</c> в
    /// <see cref="ModuleSchemaBackup" />, и контекст модуля пойдёт своим соединением: строк в копии
    /// станет две, то есть в копии окажется счёт, которого в снимке ядра нет.</para>
    /// </summary>
    [Fact]
    public async Task Снимок_ядра_и_схемы_модуля_один()
    {
        var objectId = await SeedCoreObjectAsync();
        await SeedInvoiceAsync(objectId);

        using var scope = fixture.Services.CreateScope();
        await using var probe = ProbeProvider();
        using var probeScope = probe.CreateScope();
        var modules = new InsertsBeforeReading(
            new ModuleSchemaBackup(Registry(), probeScope.ServiceProvider),
            () => SeedInvoiceAsync(objectId, number: "Счёт №2"));

        var manifest = ReadManifest(await ArchiveAsync(Backup(scope, modules), BackupScope.Full));

        var table = Assert.Single(manifest.ModuleData!).Tables
            .Single(t => t.Table == "invoices");
        Assert.Single(table.Rows);
        Assert.DoesNotContain("Счёт №2", table.Rows[0].GetRawText());
    }

    /// <summary>
    /// Данные модуля, которого на этом экземпляре нет, пропускаются С ОГОВОРКОЙ — и не молча.
    ///
    /// <para>Случай будничный: копия снята со сборки, где модуль счетов включён, а восстанавливают её
    /// туда, где он не куплен. Тихий пропуск человек прочитал бы как потерю данных и полез бы искать
    /// их в базе; оговорка говорит, что данные в копии остались и вернутся, когда модуль включат.</para>
    /// </summary>
    [Fact]
    public async Task Данные_неизвестного_модуля_пропускаются_с_оговоркой()
    {
        using var scope = fixture.Services.CreateScope();
        await using var probe = ProbeProvider();
        using var probeScope = probe.CreateScope();
        var warnings = new List<string>();

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync();

        var stats = await new ModuleSchemaBackup(Registry(), probeScope.ServiceProvider).RestoreAsync(
            [new BackupModuleSchema("склад", "склад", [new BackupModuleTable("crates", [])])],
            tx.GetDbTransaction(), warnings, default);

        Assert.Empty(stats);
        var warning = Assert.Single(warnings);
        Assert.Contains("склад", warning);
        Assert.Contains("в копии они остались", warning);
    }

    /// <summary>
    /// Фикстура чистит таблицы схемы модуля между классами тестов — по модели контекста, без списка
    /// имён.
    ///
    /// <para>Сторож нужен потому, что сегодня этот путь не проходит ни один прогон: у модуля
    /// <c>costs</c> таблиц нет, и очистка схем — пустой цикл. С первой таблицей счетов забытая очистка
    /// проявилась бы падением ЧУЖОГО теста со второго прогона, и правили бы упавший тест. Проверка
    /// идёт на поддельном модуле — механизм при этом настоящий, тот же метод фикстуры.</para>
    /// </summary>
    [Fact]
    public async Task Фикстура_чистит_таблицы_схемы_модуля()
    {
        await SeedInvoiceAsync(await SeedCoreObjectAsync());
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM {ModuleSchemaName}.invoices"));

        await using var probe = ProbeProvider();
        using var scope = probe.CreateScope();
        await IntegrationTestFixture.ResetModuleSchemasAsync(scope);

        Assert.Equal(0L, await ScalarAsync<long>($"SELECT count(*) FROM {ModuleSchemaName}.invoices"));
    }

    /// <summary>
    /// Позиция перечня работ, сопоставленная по естественному ключу, названа в отчёте — потому что
    /// ссылка модуля на неё после этого никуда не ведёт.
    ///
    /// <para>Правило «ключ важнее идентификатора» (ревью PR #1056) остаётся, но его прежний довод
    /// («таблиц модулей в копии нет») с этой задачи неверен, и цена решения стала настоящей. Сама
    /// копия исправить её не может — какие колонки модуля держат позицию, знает только модуль, — и
    /// единственное, что здесь можно сделать честно, это не молчать.</para>
    /// </summary>
    [Fact]
    public async Task Сопоставление_позиции_перечня_по_ключу_названо_в_отчёте()
    {
        var objectId = await SeedCoreObjectAsync();
        await SeedInvoiceAsync(objectId);
        var key = await SeedWorkPlanItemAsync(objectId, Guid.NewGuid());

        var (archive, _) = await ExportAsync(BackupScope.Full);

        // Та же позиция с тем же ключом, но другим идентификатором — так выглядит восстановление в
        // систему, которая уже живёт (а двойник по ключу бывает только там).
        await ExecuteAsync("DELETE FROM work_plan_items");
        await SeedWorkPlanItemAsync(objectId, Guid.NewGuid(), key.ConstructionId);

        var report = await ImportAsync(archive);

        Assert.True(report.Success, string.Join("; ", report.Warnings));
        Assert.Contains(report.Warnings, w =>
            w.Contains("сопоставлено с уже существующими по естественному ключу", StringComparison.Ordinal)
            && w.Contains("данные модулей", StringComparison.Ordinal));
    }

    /// <summary>
    /// Копию не снимают внутри чужой транзакции на слабом уровне изоляции — отказ (ревью PR #1108).
    ///
    /// <para>На READ COMMITTED каждая команда видит свой снимок, а таблицы ядра читаются десятками
    /// команд: копия собралась бы из разных моментов времени. Прежде здесь стояло обещание
    /// целостности без проверки, и неверным оно было ещё до схем модулей. Отказ дешевле копии, которая
    /// выглядит копией: у неё ссылки внутри могут не сходиться, а узнаётся это при восстановлении.</para>
    /// </summary>
    [Fact]
    public async Task Копия_внутри_чужой_транзакции_на_слабом_уровне_отказывает()
    {
        using var scope = fixture.Services.CreateScope();
        await using var probe = ProbeProvider();
        using var probeScope = probe.CreateScope();
        var service = Backup(scope, new ModuleSchemaBackup(Registry(), probeScope.ServiceProvider));

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ExportAsync(BackupScope.Full));

        Assert.Contains("ReadCommitted", ex.Message);
        Assert.Contains("снимка", ex.Message);
    }

    /// <summary>
    /// Колонка, которая в копии есть, а в нынешней модели модуля её нет, не теряется молча
    /// (ревью PR #1108).
    ///
    /// <para><c>jsonb_populate_recordset</c> незнакомые ключи просто игнорирует: значения пропали бы у
    /// ВСЕХ строк, а отчёт назвал бы восстановление успешным. Так выглядит копия, снятая более новой
    /// версией модуля. Пропавшая ТАБЛИЦА оговорку получала с самого начала — асимметрия и была
    /// дефектом.</para>
    /// </summary>
    [Fact]
    public async Task Колонка_из_копии_которой_в_модели_нет_не_теряется_молча()
    {
        await SeedInvoiceAsync(await SeedCoreObjectAsync());
        var (_, manifest) = await ExportAsync(BackupScope.Full);

        var invoices = manifest.ModuleData![0].Tables.Single(t => t.Table == "invoices");
        var withGhost = invoices.Rows
            .Select(r => JsonDocument.Parse(r.GetRawText().Insert(1, "\"призрак\": 1,")).RootElement.Clone())
            .ToArray();

        var warnings = new List<string>();
        using var scope = fixture.Services.CreateScope();
        await using var probe = ProbeProvider();
        using var probeScope = probe.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync();

        await new ModuleSchemaBackup(Registry(), probeScope.ServiceProvider).RestoreAsync(
            [new BackupModuleSchema(ModuleCode, ModuleSchemaName, [new BackupModuleTable("invoices", withGhost)])],
            tx.GetDbTransaction(), warnings, default);

        Assert.Contains(warnings, w =>
            w.Contains("призрак", StringComparison.Ordinal)
            && w.Contains("не восстановлены", StringComparison.Ordinal));
    }

    /// <summary>
    /// Пока у модулей нет ни одной строки, тревожной оговорки про их ссылки НЕ БЫВАЕТ
    /// (ревью PR #1108).
    ///
    /// <para>Секция в копии есть у любого модуля со схемой — у <c>costs</c> сегодня она пустая, и это
    /// утверждает отдельный тест. Считай мы «данные модулей есть» по числу секций, предупреждение про
    /// оборванные ссылки выдавалось бы при каждом сопоставлении позиции перечня по ключу: человек шёл
    /// бы искать то, чего нет, а тревога, звучащая без повода, перестаёт значить что-либо.</para>
    /// </summary>
    [Fact]
    public async Task Без_строк_модуля_оговорки_про_ссылки_модуля_нет()
    {
        var objectId = await SeedCoreObjectAsync();
        var key = await SeedWorkPlanItemAsync(objectId, Guid.NewGuid());

        var (archive, manifest) = await ExportAsync(BackupScope.Full);
        Assert.NotEmpty(manifest.ModuleData!);
        Assert.All(manifest.ModuleData!, m => Assert.All(m.Tables, t => Assert.Empty(t.Rows)));

        await ExecuteAsync("DELETE FROM work_plan_items");
        await SeedWorkPlanItemAsync(objectId, Guid.NewGuid(), key.ConstructionId);

        var report = await ImportAsync(archive);

        Assert.True(report.Success, string.Join("; ", report.Warnings));
        Assert.DoesNotContain(report.Warnings, w => w.Contains("данные модулей", StringComparison.Ordinal));
    }

    // ── Хозяйство ─────────────────────────────────────────────────────────────

    private string Connection => fixture.Services.GetRequiredService<IConfiguration>()
        .GetConnectionString("Postgres")!;

    private static ModuleRegistry Registry() => new([new ProbeModule()], []);

    private ServiceProvider ProbeProvider() => new ServiceCollection()
        .AddSingleton(Registry())
        .AddDbContext<ProbeInvoiceContext>(o => o.UseNpgsql(Connection))
        .BuildServiceProvider();

    private BackupService Backup(IServiceScope scope, IModuleSchemaBackup modules) => new(
        scope.ServiceProvider.GetRequiredService<AppDbContext>(),
        scope.ServiceProvider.GetRequiredService<IBlobStorage>(),
        NullLogger<BackupService>.Instance,
        scope.ServiceProvider.GetRequiredService<IActivityLog>(),
        modules);

    /// <summary>Копия целиком в памяти: её читают дважды — как манифест и как вход восстановления.</summary>
    private static async Task<MemoryStream> ArchiveAsync(BackupService service, BackupScope scope)
    {
        var (zip, _) = await service.ExportAsync(scope);
        await using var handle = zip;
        var copy = new MemoryStream();
        await zip.CopyToAsync(copy);
        copy.Position = 0;
        return copy;
    }

    private async Task<(MemoryStream Archive, BackupManifest Manifest)> ExportAsync(BackupScope scope)
    {
        using var s = fixture.Services.CreateScope();
        await using var probe = ProbeProvider();
        using var probeScope = probe.CreateScope();
        var archive = await ArchiveAsync(
            Backup(s, new ModuleSchemaBackup(Registry(), probeScope.ServiceProvider)), scope);
        return (archive, ReadManifest(archive));
    }

    private async Task<RestoreReport> ImportAsync(MemoryStream archive)
    {
        archive.Position = 0;
        using var s = fixture.Services.CreateScope();
        await using var probe = ProbeProvider();
        using var probeScope = probe.CreateScope();
        return await Backup(s, new ModuleSchemaBackup(Registry(), probeScope.ServiceProvider))
            .ImportAsync(archive);
    }

    private static BackupManifest ReadManifest(MemoryStream archive)
    {
        archive.Position = 0;
        using var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
        using var entry = zip.GetEntry("manifest.json")!.Open();
        var manifest = JsonSerializer.Deserialize<BackupManifest>(entry)!;
        archive.Position = 0;
        return manifest;
    }

    private async Task<Guid> SeedCoreObjectAsync()
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;
        var typeId = Guid.NewGuid();
        var objectId = Guid.NewGuid();

        db.DocumentTypes.Add(DocumentType.Restore(
            typeId, "Контрагент", $"cp-{Guid.NewGuid():N}", DocumentTypeKind.Composite, null,
            JsonDocument.Parse("""{"fields":[]}"""), JsonDocument.Parse("{}"), false, now, now, null, false));
        db.DomainObjects.Add(DomainObject.Restore(
            objectId, typeId, "ООО Ромашка", JsonDocument.Parse("{}"),
            CatalogScope.System, null, now, now));
        await db.SaveChangesAsync();

        return objectId;
    }

    /// <summary>Позиция перечня работ: её ключ — вид работы, стройка, раздел и единица измерения.</summary>
    private async Task<(Guid ConstructionId, Guid ObjectId)> SeedWorkPlanItemAsync(
        Guid objectId, Guid itemId, Guid? constructionId = null)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;
        var construction = constructionId ?? Guid.NewGuid();

        if (constructionId is null)
            db.Constructions.Add(Construction.Restore(construction, "Стройка", objectId, null, now, now));
        db.WorkPlanItems.Add(WorkPlanItem.Restore(itemId, objectId, construction, null, objectId, now, now));
        await db.SaveChangesAsync();

        return (construction, objectId);
    }

    private async Task<Guid> SeedInvoiceAsync(Guid objectId, string number = "Счёт №1")
    {
        await using var provider = ProbeProvider();
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProbeInvoiceContext>();
        var id = Guid.NewGuid();

        db.Invoices.Add(new ProbeInvoice
        {
            Id = id,
            ObjectId = objectId,
            Number = number,
            Amount = 1234.56m,
            IssuedAt = DateTimeOffset.UtcNow,
            Payload = JsonDocument.Parse("""{"позиции":[{"наименование":"кабель","количество":12}]}"""),
        });
        db.Lines.Add(new ProbeLine { Id = Guid.NewGuid(), InvoiceId = id, Name = "кабель ВВГнг" });
        await db.SaveChangesAsync();

        return id;
    }

    private async Task<ProbeInvoice> ReadInvoiceAsync(Guid id)
    {
        await using var provider = ProbeProvider();
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProbeInvoiceContext>();
        return await db.Invoices.AsNoTracking().SingleAsync(i => i.Id == id);
    }

    private async Task CreateSchemaAsync()
    {
        await using var provider = ProbeProvider();
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProbeInvoiceContext>();

        // Не EnsureCreated: на непустой базе он молча ничего не делает (урок A2a). Сценарий из модели
        // — тот же путь, каким идёт миграция, — и первым его оператором стоит создание схемы.
        var script = db.Database.GenerateCreateScript();
        Assert.Contains($"CREATE SCHEMA {ModuleSchemaName}", script);
        await ExecuteAsync(script);
    }

    private Task DropSchemaAsync() => ExecuteAsync($"DROP SCHEMA IF EXISTS {ModuleSchemaName} CASCADE");

    private async Task ExecuteAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(Connection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var conn = new NpgsqlConnection(Connection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Обёртка, вставляющая чужую строку ровно перед чтением схемы модуля. Шов нужен потому, что
    /// «между двумя чтениями» изнутри копии не поймать иначе никак.
    /// </summary>
    private sealed class InsertsBeforeReading(IModuleSchemaBackup inner, Func<Task> insert)
        : IModuleSchemaBackup
    {
        public async Task<BackupModuleSchema[]> ReadAsync(DbTransaction transaction, CancellationToken ct)
        {
            await insert();
            return await inner.ReadAsync(transaction, ct);
        }

        public Task<IReadOnlyList<RestoreSectionStat>> RestoreAsync(
            BackupModuleSchema[] data, DbTransaction transaction, List<string> warnings,
            CancellationToken ct) => inner.RestoreAsync(data, transaction, warnings, ct);
    }

    /// <summary>Поддельный модуль: нужен ровно тем, что у него есть схема с таблицей.</summary>
    private sealed class ProbeModule : IAppModule
    {
        public string Code => ModuleCode;

        public string Title => "Поддельный модуль копии";

        public IReadOnlyList<AppPermission> Permissions => [];

        public IReadOnlyList<string> RoutePrefixes => [];

        public ModuleSchema? Schema => new(ModuleSchemaName, typeof(ProbeInvoiceContext));

        public void RegisterServices(IServiceCollection services, IConfiguration configuration) { }

        public void MapEndpoints(IEndpointRouteBuilder endpoints) { }

        public Task InitializeAsync(IServiceProvider services, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>
    /// Поддельный контекст модуля с одной таблицей — такой, какой заведёт C1: ссылка на объект ядра
    /// идентификатором (внешних ключей сквозь схемы не бывает), сумма, дата со смещением и вложенный
    /// <c>jsonb</c>. Типы выбраны нарочно: на них и видно, записала копия значение или приблизила его.
    /// </summary>
    private sealed class ProbeInvoiceContext(DbContextOptions<ProbeInvoiceContext> options)
        : ModuleDbContext(options)
    {
        protected override string Schema => ModuleSchemaName;

        public DbSet<ProbeInvoice> Invoices => Set<ProbeInvoice>();

        /// <summary>Позиции счёта: внешний ключ ВНУТРИ схемы модуля — им и проверяется порядок таблиц.</summary>
        public DbSet<ProbeLine> Lines => Set<ProbeLine>();

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            // Таблица без первичного ключа: такой у модуля быть может (журнал, «пиши и читай»), и
            // восстановить её нечем — проверка на этом и стоит. Строк в ней по умолчанию нет, поэтому
            // остальным проверкам она не мешает.
            builder.Entity<ProbeLog>(e =>
            {
                e.ToTable("probe_log");
                e.HasNoKey();
                e.Property(x => x.Text).HasColumnName("text");
            });
            builder.Entity<ProbeLine>(e =>
            {
                e.ToTable("invoice_lines");
                e.Property(x => x.Id).HasColumnName("id");
                e.Property(x => x.InvoiceId).HasColumnName("invoice_id");
                e.Property(x => x.Name).HasColumnName("name");
                e.HasOne<ProbeInvoice>().WithMany().HasForeignKey(x => x.InvoiceId);
            });
            builder.Entity<ProbeInvoice>(e =>
            {
                e.ToTable("invoices");
                e.Property(x => x.Id).HasColumnName("id");
                e.Property(x => x.ObjectId).HasColumnName("object_id");
                e.Property(x => x.Number).HasColumnName("number");
                // Номер-счётчик: значение кладёт база. Уникален нарочно — см. проверку счётчика.
                e.Property(x => x.Seq).HasColumnName("seq").ValueGeneratedOnAdd();
                e.HasIndex(x => x.Seq).IsUnique();
                e.Property(x => x.Amount).HasColumnName("amount").HasColumnType("numeric(18,2)");
                e.Property(x => x.IssuedAt).HasColumnName("issued_at");
                e.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb");
                e.Property(x => x.Note).HasColumnName("note");
            });
        }
    }

    private sealed class ProbeLog
    {
        public string Text { get; set; } = string.Empty;
    }

    private sealed class ProbeLine
    {
        public Guid Id { get; set; }

        public Guid InvoiceId { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    private sealed class ProbeInvoice
    {
        public Guid Id { get; set; }

        public Guid ObjectId { get; set; }

        public string Number { get; set; } = string.Empty;

        public int Seq { get; set; }

        public decimal Amount { get; set; }

        public DateTimeOffset IssuedAt { get; set; }

        public JsonDocument? Payload { get; set; }

        public string? Note { get; set; }
    }
}
