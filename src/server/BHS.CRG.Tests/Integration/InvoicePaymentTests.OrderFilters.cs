using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Objects;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Costs;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Costs.Endpoints;
using BHS.CRG.Modules.Costs.Tables;
using BHS.CRG.Modules.Data;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Tables;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Отборы «наведите порядок» в таблице счетов (issue #1186): счета со ссылкой на удалённую запись
/// справочника и счета со ссылкой на запись в архиве. Тот же класс, что <c>InvoicePaymentTests.cs</c>:
/// нужны оплата и закрытие периода.
///
/// <para>Задача ломается двумя способами, и оба здесь стерегут: число счётчика расходится с числом строк
/// отбора; в отборе «можно исправить» оказывается счёт, который править нельзя.</para>
/// </summary>
public partial class InvoicePaymentTests
{
    private const string LostColumn = InvoiceTable.LostKey;
    private const string ArchivedColumn = InvoiceTable.ArchivedKey;

    /// <summary>Таблица счетов под условием «колонка равна слову» — как её читает экран.</summary>
    private static async Task<JsonElement> TroubleTableAsync(HttpClient client, string column, string word)
    {
        var filter = JsonSerializer.Serialize(new { type = "condition", column, op = "eq", value = word });
        var response = await client.GetAsync(
            $"/api/tables/costs.invoices?columns=Номер,{column}&filter={Uri.EscapeDataString(filter)}");
        await OkAsync(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static bool Has(JsonElement table, Guid invoice) =>
        table.GetProperty("keys").EnumerateArray().Any(k => k.GetString() == invoice.ToString());

    private static int Count(JsonElement table) => table.GetProperty("count").GetInt32();

    /// <summary>Клетка колонки у счёта — без отбора: что таблица говорит о нём самом.</summary>
    private static async Task<string?> TroubleCellAsync(HttpClient client, string column, Guid invoice)
    {
        var response = await client.GetAsync($"/api/tables/costs.invoices?columns=Номер,{column}&row={invoice}");
        await OkAsync(response);
        var row = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rows")[0];
        return row.TryGetProperty(column, out var cell) && cell.ValueKind == JsonValueKind.String ? cell.GetString() : null;
    }

    private async Task<(Guid Invoice, Guid Position, Guid Site)> InvoiceWithPositionAsync(HttpClient admin, string name)
    {
        var nomenclature = await TypeAsync(CostsRecordTypes.NomenclatureCode, "Номенклатура");
        var position = await EntryAsync(nomenclature, $"{name} {Guid.NewGuid().ToString()[..6]}");
        var (site, _) = await SiteAsync($"Стройка: {name}");

        var invoice = await CreateAsync(admin, complete: true);
        var view = await LinesAsync(admin, invoice, [Line(position, 100, 400)]);
        await AllocateAsync(admin, invoice, LineId(view, 1), [Part(site, quantity: 100)]);
        await OkAsync(await admin.PutAsJsonAsync($"/api/costs/invoices/{invoice}",
            new { requisites = await RequisitesWithAsync(admin, invoice, "Итого", 40_000m) }));
        return (invoice, position, site);
    }

    /// <summary>
    /// Сторож задачи: <b>счёт с потерянной ссылкой попадает в отбор и уходит из него после замены
    /// значения</b>, а число строк отбора равно числу счётчика — до и после.
    /// </summary>
    [Fact]
    public async Task Счёт_с_удалённой_записью_в_отборе_и_уходит_из_него_после_замены()
    {
        var (admin, _) = await SignInAsync("Admin");
        var (invoice, position, _) = await InvoiceWithPositionAsync(admin, "Позиция под замену");

        Assert.Null(await TroubleCellAsync(admin, LostColumn, invoice));
        await ForgetAsync(position);

        var listed = await TroubleTableAsync(admin, LostColumn, InvoiceTable.TroubleFixable);
        Assert.True(Has(listed, invoice));
        Assert.Equal(InvoiceTable.TroubleFixable, await TroubleCellAsync(admin, LostColumn, invoice));
        Assert.Equal((await TallyAsync(admin, "editable")).Invoices, Count(listed));

        // Замена значения — то, что делает человек в форме: в строке выбрана живая позиция.
        var line = (await ReadAsync(admin, invoice)).GetProperty("lines")[0];
        await LinesAsync(admin, invoice, [Line(cable, 100, 400, id: line.GetProperty("id").GetGuid())]);

        var after = await TroubleTableAsync(admin, LostColumn, InvoiceTable.TroubleFixable);
        Assert.False(Has(after, invoice));
        Assert.Equal(Count(listed) - 1, Count(after));
        Assert.Equal((await TallyAsync(admin, "editable")).Invoices, Count(after));
        Assert.Null(await TroubleCellAsync(admin, LostColumn, invoice));
    }

    /// <summary>
    /// <b>Счёт закрытого периода в отбор «можно исправить» не попадает</b> — и не пропадает: у него своё
    /// слово. После отмены закрытия он переезжает сам, потому что нигде не хранится.
    /// </summary>
    [Fact]
    public async Task Счёт_закрытого_периода_назван_отдельно_и_в_отбор_исправимых_не_входит()
    {
        var (admin, _) = await SignInAsync("Admin");
        var today = await TodayAsync();
        var (invoice, position, site) = await InvoiceWithPositionAsync(admin, "Позиция запертая");
        var paidOn = today.AddDays(-5);
        await PayAsync(admin, invoice, paidOn, await PreviewAsync(admin, invoice, paidOn), null);
        await ForgetAsync(position);
        await CloseAsync(site, today.AddDays(-1));

        var locked = InvoiceTable.LostWords[(int)LostMark.Locked];
        Assert.False(Has(await TroubleTableAsync(admin, LostColumn, InvoiceTable.TroubleFixable), invoice));
        var held = await TroubleTableAsync(admin, LostColumn, locked);
        Assert.True(Has(held, invoice));
        Assert.Equal((await TallyAsync(admin, "locked")).Invoices, Count(held));
        Assert.Equal(locked, await TroubleCellAsync(admin, LostColumn, invoice));

        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlRawAsync("TRUNCATE TABLE period_closures");
        Assert.True(Has(await TroubleTableAsync(admin, LostColumn, InvoiceTable.TroubleFixable), invoice));
        Assert.False(Has(await TroubleTableAsync(admin, LostColumn, locked), invoice));
    }

    private static async Task<int> UnfixableAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/costs/lost-references")).GetProperty("unfixable").GetInt32();

    private async Task LoseTypeAsync(Guid invoice)
    {
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlRawAsync(
            "UPDATE costs.invoices SET document_type_id = {0} WHERE id = {1}", Guid.NewGuid(), invoice);
    }

    /// <summary>
    /// <b>Удалённый тип счёта — не «исправьте»</b>: поля, в котором тип заменяют, в форме нет. Счёт назван
    /// своим словом, а ссылка считается своим числом — ни в «можно исправить», ни в «заперто».
    /// </summary>
    [Fact]
    public async Task Счёт_с_удалённым_типом_назван_отдельно_и_в_отбор_исправимых_не_входит()
    {
        var (admin, _) = await SignInAsync("Admin");
        var (editable, unfixable) = (await TallyAsync(admin, "editable"), await UnfixableAsync(admin));
        var orphan = await CreateAsync(admin);
        await LoseTypeAsync(orphan);

        Assert.Equal(InvoiceTable.LostWords[(int)LostMark.TypeOnly], await TroubleCellAsync(admin, LostColumn, orphan));
        Assert.False(Has(await TroubleTableAsync(admin, LostColumn, InvoiceTable.TroubleFixable), orphan));
        Assert.Equal(editable, await TallyAsync(admin, "editable"));
        Assert.Equal(unfixable + 1, await UnfixableAsync(admin));
    }

    /// <summary>
    /// <b>Запертость удалённого типа не меняет</b> (ревью PR #1239): «период закрыт» обещал бы, что после
    /// отмены закрытия счёт исправят, а исправить тип нечем. И наоборот — у счёта с потерей, которую
    /// заменить можно, ссылка на тип не переезжает из числа в число вместе с соседями.
    /// </summary>
    [Fact]
    public async Task Удалённый_тип_считается_одинаково_у_запертого_счёта_и_рядом_с_другой_потерей()
    {
        var (admin, _) = await SignInAsync("Admin");
        var today = await TodayAsync();
        var (invoice, position, site) = await InvoiceWithPositionAsync(admin, "Тип запертого");
        var paidOn = today.AddDays(-5);
        await PayAsync(admin, invoice, paidOn, await PreviewAsync(admin, invoice, paidOn), null);
        await CloseAsync(site, today.AddDays(-1));
        var (locked, editable, unfixable) =
            (await TallyAsync(admin, "locked"), await TallyAsync(admin, "editable"), await UnfixableAsync(admin));

        await LoseTypeAsync(invoice);
        Assert.Equal(InvoiceTable.LostWords[(int)LostMark.TypeOnly], await TroubleCellAsync(admin, LostColumn, invoice));
        Assert.Equal(locked, await TallyAsync(admin, "locked"));
        Assert.Equal(unfixable + 1, await UnfixableAsync(admin));

        // Рядом появилась потеря, которую заменить можно: счёт — в запертых, а ссылка на тип — там же,
        // где была.
        await ForgetAsync(position);
        Assert.Equal(InvoiceTable.LostWords[(int)LostMark.Locked], await TroubleCellAsync(admin, LostColumn, invoice));
        Assert.Equal((locked.References + 1, locked.Invoices + 1), await TallyAsync(admin, "locked"));
        Assert.Equal(editable, await TallyAsync(admin, "editable"));
        Assert.Equal(unfixable + 1, await UnfixableAsync(admin));
    }

    /// <summary>
    /// <b>Запись в архиве зовёт к правке только счёт в работе</b> (решение владельца 07.10.2026):
    /// неоплаченный — в отборе, оплаченный назван отдельно. Ссылка законна в обоих, и удалённой записью
    /// архивная не считается.
    /// </summary>
    [Fact]
    public async Task Запись_в_архиве_в_отборе_только_у_неоплаченного_счёта()
    {
        var (admin, _) = await SignInAsync("Admin");
        var today = await TodayAsync();
        var (open, first, _) = await InvoiceWithPositionAsync(admin, "Позиция в архив, счёт в работе");
        var (paid, second, _) = await InvoiceWithPositionAsync(admin, "Позиция в архив, счёт оплачен");
        await PayAsync(admin, paid, today, await PreviewAsync(admin, paid, today), null);

        using (var scope = host.Services.CreateScope())
        {
            var archive = scope.ServiceProvider.GetRequiredService<IRecordArchive>();
            Assert.Equal(ArchiveOutcome.Changed, await archive.SetAsync(first, archived: true));
            Assert.Equal(ArchiveOutcome.Changed, await archive.SetAsync(second, archived: true));
        }

        var listed = await TroubleTableAsync(admin, ArchivedColumn, InvoiceTable.TroubleFixable);
        Assert.True(Has(listed, open));
        Assert.False(Has(listed, paid));
        Assert.Equal(InvoiceTable.ArchivedWords[(int)ArchivedMark.Paid], await TroubleCellAsync(admin, ArchivedColumn, paid));

        // Архив — не потеря: в колонке удалённых у обоих пусто, и опрос называет состояние как есть.
        Assert.Null(await TroubleCellAsync(admin, LostColumn, open));
        var found = await LostAsync();
        Assert.Contains(found.Archived, f => f.DocumentKey == open && f.TargetId == first && f.Column == "nomenclature_id");
        Assert.DoesNotContain(found.Lost, f => f.DocumentKey == open || f.DocumentKey == paid);

        // Вернули из архива — счёт из отбора ушёл.
        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IRecordArchive>().SetAsync(first, archived: false);
        Assert.False(Has(await TroubleTableAsync(admin, ArchivedColumn, InvoiceTable.TroubleFixable), open));
    }

    /// <summary>Порт, к которому обращаться нельзя: чтение, не спросившее о ссылках, его не зовёт.</summary>
    private sealed class ForbiddenTargets : IModuleReferenceTargets
    {
        public Task<IReadOnlyDictionary<Guid, ReferenceState>> StatesAsync(
            ReferenceTarget target, IReadOnlyCollection<Guid> ids, CancellationToken ct = default) =>
            throw new InvalidOperationException("состояние записей не спрашивали");

        public Task<ReferenceFindings> NotPresentAsync(
            string moduleCode, bool includeArchived, CancellationToken ct = default) =>
            throw new InvalidOperationException("опрос ядра не заказывали");
    }

    /// <summary>
    /// <b>Тот, кто читает таблицу без списка колонок, опроса не получает</b> (ревью PR #1239) — а так её
    /// читает набор данных: «без списка» значит «все колонки». Проверено настоящим путём, через службу
    /// таблиц приложения, а не службой строк, собранной руками: порт подменён тем, что бросает.
    /// </summary>
    [Fact]
    public async Task Чтение_таблицы_без_списка_колонок_ядро_не_опрашивает_и_колонок_о_ссылках_не_несёт()
    {
        var (admin, _) = await SignInAsync("Admin");
        await using var forbidding = host.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IModuleReferenceTargets>();
            services.AddScoped<IModuleReferenceTargets, ForbiddenTargets>();
        }));
        var client = forbidding.CreateClient();
        client.DefaultRequestHeaders.Authorization = admin.DefaultRequestHeaders.Authorization;

        var whole = await client.GetAsync("/api/tables/costs.invoices?limit=5");
        await OkAsync(whole);
        var keys = (await whole.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("columns")
            .EnumerateArray().Select(c => c.GetProperty("key").GetString()).ToList();
        Assert.DoesNotContain(LostColumn, keys);
        Assert.DoesNotContain(ArchivedColumn, keys);
        Assert.Contains(InvoiceRequisites.NumberKey, keys);

        // Набор данных читает тем же запросом — пустым, без списка колонок (ModuleTableDataProvider):
        // чего нет в этом ответе, того нет и в наборе.

        // Объявлены обе — и названы приходящими только по требованию: экран их предлагает.
        var declared = (await client.GetFromJsonAsync<JsonElement>("/api/tables/costs.invoices/columns"))
            .GetProperty("columns").EnumerateArray()
            .Where(c => c.GetProperty("key").GetString() is LostColumn or ArchivedColumn).ToList();
        Assert.Equal(2, declared.Count);
        Assert.All(declared, c => Assert.True(c.GetProperty("onDemand").GetBoolean()));

        // А спросили — опрос идёт, и подменный порт это показывает отказом.
        var asked = await client.GetAsync($"/api/tables/costs.invoices?columns=Номер,{LostColumn}&limit=5");
        Assert.Equal(System.Net.HttpStatusCode.InternalServerError, asked.StatusCode);
    }

    /// <summary>
    /// <b>Об архиве ядро спрашивают, только когда спросили о нём.</b> Ссылок на архивные записи на порядки
    /// больше, чем потерянных, и счётчику потерь, как и колонке удалённых, они не нужны.
    /// </summary>
    [Fact]
    public async Task Архивные_ссылки_читаются_только_для_колонки_архива()
    {
        using var scope = host.Services.CreateScope();
        var services = scope.ServiceProvider;
        var recording = new RecordingTargets(services.GetRequiredService<IModuleReferenceTargets>());
        var rows = new InvoiceTableRows(
            services.GetRequiredService<CostsDbContext>(), services.GetRequiredService<IModuleCatalog>(),
            services.GetRequiredService<AllocationPlacesSource>(), services.GetRequiredService<IModuleClock>(),
            new InvoiceReferenceTrouble(services.GetRequiredService<CostsDbContext>(), recording,
                services.GetRequiredService<IModulePeriods>()));

        await rows.ReadAsync(new ModuleTableQuery(new HashSet<string> { LostColumn }, Guid.Empty, Limit: 1), default);
        await rows.ReadAsync(new ModuleTableQuery(new HashSet<string> { ArchivedColumn }, Guid.Empty, Limit: 1), default);
        await rows.ReadAsync(new ModuleTableQuery(new HashSet<string> { LostColumn, ArchivedColumn }, Guid.Empty, Limit: 1), default);

        Assert.Equal([false, true, true], recording.Asked);
    }

    /// <summary>Порт, который помнит, спрашивали ли его об архиве.</summary>
    private sealed class RecordingTargets(IModuleReferenceTargets inner) : IModuleReferenceTargets
    {
        public List<bool> Asked { get; } = [];

        public Task<IReadOnlyDictionary<Guid, ReferenceState>> StatesAsync(
            ReferenceTarget target, IReadOnlyCollection<Guid> ids, CancellationToken ct = default) =>
            inner.StatesAsync(target, ids, ct);

        public Task<ReferenceFindings> NotPresentAsync(
            string moduleCode, bool includeArchived, CancellationToken ct = default)
        {
            Asked.Add(includeArchived);
            return inner.NotPresentAsync(moduleCode, includeArchived, ct);
        }
    }

    /// <summary>
    /// <b>За опрос ядра платит тот, кто о ссылках спросил.</b> Таблицу читают не только с экрана — наборы
    /// данных, внешний агент; проход по всем держащим колонкам модуля им не нужен. Спросили колонкой,
    /// отбором, сортировкой или итогом — опрос идёт.
    /// </summary>
    [Fact]
    public async Task Таблица_счетов_не_опрашивает_ядро_пока_о_ссылках_не_спросили()
    {
        using var scope = host.Services.CreateScope();
        var services = scope.ServiceProvider;
        var rows = new InvoiceTableRows(
            services.GetRequiredService<CostsDbContext>(), services.GetRequiredService<IModuleCatalog>(),
            services.GetRequiredService<AllocationPlacesSource>(), services.GetRequiredService<IModuleClock>(),
            new InvoiceReferenceTrouble(services.GetRequiredService<CostsDbContext>(), new ForbiddenTargets(),
                services.GetRequiredService<IModulePeriods>()));
        var plain = new HashSet<string> { InvoiceRequisites.NumberKey };

        await rows.ReadAsync(new ModuleTableQuery(plain, Guid.Empty, Limit: 1), default);

        var condition = new TableFilterCondition(LostColumn, ModuleTableColumnKind.Choice, "eq",
            [InvoiceTable.TroubleFixable], word => word == InvoiceTable.TroubleFixable);
        ModuleTableQuery[] asking =
        [
            new(new HashSet<string> { LostColumn }, Guid.Empty),
            new(new HashSet<string> { ArchivedColumn }, Guid.Empty),
            new(plain, Guid.Empty, Filter: new TableFilterGroup(false, [condition])),
            new(plain, Guid.Empty, Sort: [new TableSort(LostColumn, ModuleTableColumnKind.Choice, false)]),
            new(plain, Guid.Empty, Totals: new Dictionary<string, ModuleTableColumnKind> { [ArchivedColumn] = ModuleTableColumnKind.Choice }),
        ];
        foreach (var query in asking)
        {
            // Служба строк живёт в области запроса и опрос помнит: каждому вопросу — своя.
            var fresh = new InvoiceTableRows(
                services.GetRequiredService<CostsDbContext>(), services.GetRequiredService<IModuleCatalog>(),
                services.GetRequiredService<AllocationPlacesSource>(), services.GetRequiredService<IModuleClock>(),
                new InvoiceReferenceTrouble(services.GetRequiredService<CostsDbContext>(), new ForbiddenTargets(),
                    services.GetRequiredService<IModulePeriods>()));
            await Assert.ThrowsAsync<InvalidOperationException>(() => fresh.ReadAsync(query, default));
        }
    }
}
