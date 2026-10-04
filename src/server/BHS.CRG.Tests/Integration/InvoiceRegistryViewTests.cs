using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Tables;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Costs.Tables;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// «Реестр счетов» — готовое представление таблицы счетов (задача G4, issue #1097; ТЗ COST-20.1,
/// CORE-33): что описание таблицы его отдаёт, что оно открывается своим составом колонок и что его
/// нижняя строка говорит правду о себе.
///
/// <para>⚠️ Приёмочная сверка «итог реестра по стройке = цифра COST-20» здесь НЕ стоит и стоять не
/// может: она действует при отборе периода по дате оплаты, а оплаты в модуле ещё нет (C5, issue #1082).
/// Стоит вторая половина критерия — та, что про умолчание реестра: при оси «дата счёта» итог равен
/// сумме счетов периода, а расхождение с затратами НАЗВАНО подписью
/// (<see cref="Период_по_дате_счёта_назван_подписью_и_итог_равен_сумме_счетов_периода" />).</para>
/// </summary>
[Collection("Integration")]
public sealed class InvoiceRegistryViewTests(InvoiceLineHost host) : ModuleTableSeededTests(host)
{
    private const string Amount = InvoiceTable.AmountKey;
    private const string Unmatched = InvoiceTable.UnmatchedKey;

    private readonly InvoiceLineHost host = host;

    /// <summary>
    /// Представление приходит в описании таблицы и ОТКРЫВАЕТСЯ: запрос его колонками, итогами и
    /// сортировкой отвечает строками, а колонки приходят в названном порядке. Объявление, которое
    /// годно на вид, но на которое таблица отказывает, сторож объявления не поймал бы.
    /// </summary>
    [Fact]
    public async Task Описание_отдаёт_реестр_и_таблица_открывается_его_настройкой()
    {
        var (supplier, _) = await SignInAsync("Supplier");
        await CreateAsync(supplier);

        var described = await GetAsync(supplier, $"/api/tables/{Address}/columns");
        var registry = Assert.Single(described.GetProperty("views").EnumerateArray());
        Assert.Equal(InvoiceTable.RegistryView, registry.GetProperty("code").GetString());
        Assert.Equal("Реестр счетов", registry.GetProperty("title").GetString());

        var columns = Strings(registry.GetProperty("columns"));
        var totals = registry.GetProperty("totals").EnumerateArray().Select(t => t.GetProperty("column").GetString()!).ToList();
        var sort = registry.GetProperty("sort").EnumerateArray()
            .Select(s => $"{s.GetProperty("column").GetString()}:{(s.GetProperty("descending").GetBoolean() ? "desc" : "asc")}");

        // Каждая колонка представления и каждое предложенное место отбора — колонка этой таблицы.
        var known = Keys(described);
        Assert.Empty(columns.Except(known));
        Assert.Empty(Strings(registry.GetProperty("filters")).Except(known));

        var table = await GetAsync(supplier,
            $"/api/tables/{Address}?columns={Join(columns)}&totals={Join(totals)}&sort={Join(sort)}&limit=5");

        Assert.Equal(columns, Keys(table));
        Assert.All(totals, t => Assert.True(table.GetProperty("totals").TryGetProperty(t, out _), $"итога «{t}» нет"));
        Assert.NotEmpty(table.GetProperty("rows").EnumerateArray());
    }

    /// <summary>
    /// Представление правами не сужается: тому, у кого суммы закрыты, приходит оно же — а закрытые
    /// колонки называют причину сами. Вырежи мы их из представления, человек увидел бы реестр без сумм
    /// и не узнал бы, что они в нём есть.
    /// </summary>
    [Fact]
    public async Task Реестр_приходит_целиком_и_тому_у_кого_суммы_закрыты()
    {
        var (supplier, _) = await SignInAsync("Supplier");
        var (waybills, _) = await SignInAsync(await RoleAsync("costs.waybill.read"));

        var full = Assert.Single((await GetAsync(supplier, $"/api/tables/{Address}/columns")).GetProperty("views").EnumerateArray());
        var described = await GetAsync(waybills, $"/api/tables/{Address}/columns");
        var narrow = Assert.Single(described.GetProperty("views").EnumerateArray());

        Assert.Equal(Strings(full.GetProperty("columns")), Strings(narrow.GetProperty("columns")));

        var closed = described.GetProperty("columns").EnumerateArray()
            .Where(c => c.GetProperty("unavailable").GetString() == TableColumnReasons.NoRight)
            .Select(c => c.GetProperty("key").GetString()!).ToList();
        Assert.Contains(Amount, closed);
        Assert.Contains(Amount, Strings(narrow.GetProperty("columns")));
    }

    /// <summary>
    /// Умолчание реестра — период по дате счёта, как в таблице заказчика. Итог тогда равен сумме счетов
    /// периода — и с «Затратами по стройке» (по оплаченным в периоде) не сходится. Это не прячется:
    /// колонка суммы называет ось подписью. Без отбора по дате подписи нет — называть нечего.
    /// </summary>
    [Fact]
    public async Task Период_по_дате_счёта_назван_подписью_и_итог_равен_сумме_счетов_периода()
    {
        var (client, _) = await SignInAsync("Admin");
        var tag = Tag();
        await InvoiceAsync(client, $"{tag}-1", "2026-08-31", 100m);
        await InvoiceAsync(client, $"{tag}-2", "2026-09-01", 250.50m);
        await InvoiceAsync(client, $"{tag}-3", "2026-09-30", 1_000m);
        await InvoiceAsync(client, $"{tag}-4", "2026-10-01", 7_000m);

        var september = await ReadAsync(client,
            $"columns=Номер,{Amount},Итого&totals={Amount},Итого&filter={Own(tag, Period("2026-09-01", "2026-09-30"))}");

        Assert.Equal([$"{tag}-2", $"{tag}-3"], Numbers(september));
        Assert.Equal(1_250.50m, september.GetProperty("totals").GetProperty(Amount).GetProperty("sum").GetDecimal());
        Assert.Equal(InvoiceTable.ByIssueDateNote, Column(september, Amount).GetProperty("note").GetString());

        // Ось — свойство отбора, а не одной колонки: реестр ставит итог под ОБЕ суммы, и второй под
        // отбором периода значит то же — «за счета, выставленные в периоде». Оговорка стоит под каждым.
        Assert.Equal(InvoiceTable.ByIssueDateNote, TotalNote(september, Amount));
        Assert.Equal(InvoiceTable.ByIssueDateNote, TotalNote(september, "Итого"));
        // А клетка «Суммы к оплате» значит прежнее — её заголовок подписи не получает.
        Assert.Equal(JsonValueKind.Null, Column(september, "Итого").GetProperty("note").ValueKind);

        // День платежа — третья ось, и самая похожая на «затраты за период»: она тоже называет себя и
        // тоже говорит, где затраты (ревизия Архитектора, решение владельца 05.10.2026).
        var paidOn = Condition(InvoiceTable.PaidOnKey, "between", "2026-09-01", "2026-09-30");
        var byPayment = await ReadAsync(client, $"columns=Номер,{Amount},Итого&totals={Amount},Итого&filter={Own(tag, paidOn)}");
        Assert.Equal(InvoiceTable.ByPaidOnNote, Column(byPayment, Amount).GetProperty("note").GetString());
        Assert.Equal(InvoiceTable.ByPaidOnNote, TotalNote(byPayment, "Итого"));
        Assert.EndsWith("затраты периода — отбор «Учётный период»", InvoiceTable.ByPaidOnNote);
        var twoDates = await ReadAsync(client,
            $"columns=Номер,{Amount}&totals={Amount}&filter={Own(tag, Group(paidOn, Period("2026-09-01", "2026-09-30")))}");
        Assert.Equal(InvoiceTable.ByIssueDateAndPaidOnNote, TotalNote(twoDates, Amount));

        var all = await ReadAsync(client, $"columns=Номер,{Amount},Итого&totals={Amount},Итого&filter={Own(tag)}");
        Assert.Equal(JsonValueKind.Null, Column(all, Amount).GetProperty("note").ValueKind);
        Assert.Null(TotalNote(all, Amount));
        Assert.Null(TotalNote(all, "Итого"));
    }

    /// <summary>
    /// Отбор и по объекту, и по периоду: подпись называет ОБА сужения. «Доля: Комарова» под таким
    /// отбором утверждала бы, что сумма сужена одним способом.
    /// </summary>
    [Fact]
    public async Task Под_отбором_по_объекту_и_периоду_подпись_называет_оба_сужения()
    {
        var (client, _) = await SignInAsync("Admin");
        var tag = Tag();
        var (a, nameA) = await ObjectAsync("Комарова");
        var (b, _) = await ObjectAsync("Ливнёвка");

        // Сентябрь: 600 ₽, из них 200 на А. Октябрь: 900 ₽ целиком на А — в период не входит.
        var first = await InvoiceAsync(client, $"{tag}-1", "2026-09-10", 600m);
        var lines = await LinesAsync(client, first, [Line(cable, quantity: 3, price: 200m)]);
        await AllocateAsync(client, first, LineId(lines, 1), [Part(a, quantity: 1), Part(b, quantity: 2)]);

        var second = await InvoiceAsync(client, $"{tag}-2", "2026-10-05", 900m);
        lines = await LinesAsync(client, second, [Line(cable, quantity: 1, price: 900m)]);
        await AllocateAsync(client, second, LineId(lines, 1), [Part(a, quantity: 1)]);

        var both = Group(Condition(InvoiceTable.ObjectsKey, "eq", nameA), Period("2026-09-01", "2026-09-30"));
        var table = await ReadAsync(client, $"columns=Номер,{Amount},Итого&totals={Amount},Итого&filter={Own(tag, both)}");

        Assert.Equal([$"{tag}-1"], Numbers(table));
        Assert.Equal($"доля: {nameA}; {InvoiceTable.ByIssueDateNote}", Column(table, Amount).GetProperty("note").GetString());
        // Доля, а не счёт целиком: 200, а не 600 — полная сумма стоит рядом, в «Сумме к оплате».
        Assert.Equal(200m, table.GetProperty("totals").GetProperty(Amount).GetProperty("sum").GetDecimal());
        Assert.Equal(600m, table.GetProperty("totals").GetProperty("Итого").GetProperty("sum").GetDecimal());
        // Под итогом доли — оба сужения; под итогом счёта целиком — только ось: долей он не стал.
        Assert.Equal($"доля: {nameA}; {InvoiceTable.ByIssueDateNote}", TotalNote(table, Amount));
        Assert.Equal(InvoiceTable.ByIssueDateNote, TotalNote(table, "Итого"));
    }

    /// <summary>
    /// «Строк без позиции»: число в клетке, отбор, сортировка и итог считают одно и то же. У счёта, где
    /// разбирать нечего, клетка пуста, а не «0» — иначе отбор «не пусто» отдавал бы все счета.
    /// </summary>
    [Fact]
    public async Task Строк_без_позиции_клетка_отбор_и_итог_считают_одно()
    {
        var (client, _) = await SignInAsync("Admin");
        var tag = Tag();

        var waiting = await InvoiceAsync(client, $"{tag}-1", "2026-09-10", 300m);
        await LinesAsync(client, waiting,
        [
            Line(cable, quantity: 1, price: 100m),
            Line(null, quantity: 1, price: 100m, text: "Кабель какой-то"),
            Line(null, quantity: 1, price: 100m, text: "Труба какая-то"),
        ]);
        var one = await InvoiceAsync(client, $"{tag}-2", "2026-09-11", 100m);
        await LinesAsync(client, one, [Line(null, quantity: 1, price: 100m, text: "Что-то")]);
        var parsed = await InvoiceAsync(client, $"{tag}-3", "2026-09-12", 100m);
        await LinesAsync(client, parsed, [Line(conduit, quantity: 1, price: 100m)]);
        await InvoiceAsync(client, $"{tag}-4", "2026-09-13", 100m);

        var table = await ReadAsync(client, $"columns=Номер,{Unmatched}&totals={Unmatched}&filter={Own(tag)}");
        Assert.Equal(
            [($"{tag}-1", 2m), ($"{tag}-2", 1m), ($"{tag}-3", null), ($"{tag}-4", (decimal?)null)],
            table.GetProperty("rows").EnumerateArray().Select(r => (r.GetProperty("Номер").GetString()!, Number(r, Unmatched))));
        Assert.Equal(3m, table.GetProperty("totals").GetProperty(Unmatched).GetProperty("sum").GetDecimal());
        Assert.Equal(2, table.GetProperty("totals").GetProperty(Unmatched).GetProperty("count").GetInt64());

        // Очередь — «не пусто»; порядок — у кого больше ждёт, тот выше.
        var queue = await ReadAsync(client,
            $"columns=Номер&sort={Unmatched}:desc&filter={Own(tag, Condition(Unmatched, "is_not_empty"))}");
        Assert.Equal([$"{tag}-1", $"{tag}-2"], Numbers(queue));

        var many = await ReadAsync(client, $"columns=Номер&filter={Own(tag, Condition(Unmatched, "gt", "1"))}");
        Assert.Equal([$"{tag}-1"], Numbers(many));
    }

    /// <summary>
    /// Колонка — факт о строках, очередь «Разобрать» — правило поверх него: отклонённый счёт в очередь
    /// не входит (issue #1166), а строки без позиции у него остаются, и счётчик в списке счетов у него
    /// прежний. Реестр обязан давать ТУ ЖЕ очередь, что экран счетов, — названным рецептом: «не пусто»
    /// и «не отклонён». Разойдись они, у снабженца было бы две очереди с одним названием (ревью PR #1177).
    /// </summary>
    [Fact]
    public async Task Очередь_разобрать_в_реестре_та_же_что_на_экране_счетов()
    {
        var (client, _) = await SignInAsync("Admin");
        var tag = Tag();

        var waiting = await InvoiceAsync(client, $"{tag}-1", "2026-09-10", 100m);
        await LinesAsync(client, waiting, [Line(null, quantity: 1, price: 100m, text: "Ждёт")]);
        var rejected = await InvoiceAsync(client, $"{tag}-2", "2026-09-11", 200m);
        await LinesAsync(client, rejected,
            [Line(null, quantity: 1, price: 100m, text: "Не ждёт"), Line(null, quantity: 1, price: 100m, text: "И это")]);
        var parsed = await InvoiceAsync(client, $"{tag}-3", "2026-09-12", 100m);
        await LinesAsync(client, parsed, [Line(cable, quantity: 1, price: 100m)]);

        // Перехода в «отклонён» в API ещё нет — состояние ставится запросом (см. InvoiceParsedReferenceTests).
        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<CostsDbContext>().Database
                .ExecuteSqlInterpolatedAsync($"UPDATE costs.invoices SET state = {"Rejected"} WHERE id = {rejected}");

        // Факт: строки без позиции у отклонённого счёта есть, и колонка их называет.
        var facts = await ReadAsync(client, $"columns=Номер,{Unmatched}&filter={Own(tag, Condition(Unmatched, "is_not_empty"))}");
        Assert.Equal(
            [($"{tag}-1", 1m), ($"{tag}-2", (decimal?)2m)],
            facts.GetProperty("rows").EnumerateArray().Select(r => (r.GetProperty("Номер").GetString()!, Number(r, Unmatched))));

        // Очередь: то же, без отклонённых.
        var queue = await ReadAsync(client, "columns=Номер&filter=" + Own(tag, Group(
            Condition(Unmatched, "is_not_empty"), Condition("Состояние", "neq", "Отклонён"))));

        var own = new[] { waiting, rejected, parsed };
        var onScreen = (await client.GetFromJsonAsync<JsonElement>("/api/costs/invoices?needsParsing=true"))
            .EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).Where(own.Contains).ToList();

        Assert.Equal([waiting], onScreen);
        Assert.Equal([$"{tag}-1"], Numbers(queue));
    }

    // ── Отборы ────────────────────────────────────────────────────────────────

    private static string Condition(string column, string op, params string[] values) => values.Length switch
    {
        0 => $$"""{"type":"condition","column":"{{column}}","op":"{{op}}"}""",
        1 => $$"""{"type":"condition","column":"{{column}}","op":"{{op}}","value":{{JsonSerializer.Serialize(values[0])}}}""",
        _ => $$"""{"type":"condition","column":"{{column}}","op":"{{op}}","values":{{JsonSerializer.Serialize(values)}}}""",
    };

    private static string Period(string from, string to) => Condition("Дата", "between", from, to);

    private static string Group(params string[] conditions) =>
        $$"""{"type":"group","logic":"and","children":[{{string.Join(",", conditions)}}]}""";

    /// <summary>Свои счета и, если задано, условие — стенд общий, чужие строки отсекает метка в номере.</summary>
    private static string Own(string tag, string? condition = null) => Uri.EscapeDataString(
        condition is null
            ? Group(Condition("Номер", "starts_with", tag))
            : Group(Condition("Номер", "starts_with", tag), condition));

    // ── Чтение ────────────────────────────────────────────────────────────────

    private static async Task<JsonElement> GetAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        await OkAsync(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Таблица под запросом; порядок по номеру — у каждого чтения, кроме проверяющих сортировку.</summary>
    private static Task<JsonElement> ReadAsync(HttpClient client, string query) =>
        GetAsync(client, $"/api/tables/{Address}?{query}{(query.Contains("sort=") ? "" : "&sort=Номер")}");

    private static List<string> Keys(JsonElement table) =>
        [.. table.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("key").GetString()!)];

    private static List<string> Strings(JsonElement list) => [.. list.EnumerateArray().Select(i => i.GetString()!)];

    private static List<string> Numbers(JsonElement table) =>
        [.. table.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("Номер").GetString()!)];

    private static decimal? Number(JsonElement row, string key) =>
        row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDecimal() : null;

    /// <summary>Подпись под итогом колонки; null — оговорки нет.</summary>
    private static string? TotalNote(JsonElement table, string key) =>
        table.GetProperty("totals").GetProperty(key).GetProperty("note").GetString();

    private static JsonElement Column(JsonElement table, string key) =>
        table.GetProperty("columns").EnumerateArray().Single(c => c.GetProperty("key").GetString() == key);

    private static string Join(IEnumerable<string> items) => Uri.EscapeDataString(string.Join(",", items));

    // ── Посев ─────────────────────────────────────────────────────────────────

    private static string Tag() => $"Р{Guid.NewGuid().ToString("N")[..6]}";

    private static async Task<Guid> InvoiceAsync(HttpClient client, string number, string date, decimal total)
    {
        var requisites = new Dictionary<string, object?>
        {
            ["Номер"] = number,
            ["Дата"] = date,
            ["Поставщик"] = Reference(supplier),
            ["Итого"] = total,
        };

        var response = await client.PostAsJsonAsync("/api/costs/invoices", new { requisites });
        await OkAsync(response);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    /// <summary>Стройка с известным названием: условие отбора называет объект именем.</summary>
    private async Task<(Guid Id, string Name)> ObjectAsync(string name)
    {
        var unique = $"{name} {Guid.NewGuid().ToString()[..6]}";
        using var scope = host.Services.CreateScope();
        var site = await scope.ServiceProvider.GetRequiredService<IMediator>()
            .Send(new CreateConstructionCommand(unique, Guid.NewGuid()));
        return (site.Id, unique);
    }
}
