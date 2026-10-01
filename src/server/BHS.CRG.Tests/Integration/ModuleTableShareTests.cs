using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Documents;
using BHS.CRG.Infrastructure.DataSets;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Условие по дочернему зерну и «доля по разноске» (задача G1c, часть 2, issue #1090; ТЗ CORE-33,
/// COST-20.1).
///
/// <para>«Объект» — перечень строек и статей, на которые разнесён счёт; условие по нему спрашивает
/// «есть ли часть на этот объект». Под таким отбором «Сумма» показывает долю счёта на названные объекты
/// и говорит об этом подписью, а полная сумма остаётся в «Сумма к оплате».</para>
///
/// <para>Главные проверки — <see cref="Под_отбором_по_объекту_сумма_становится_долей_и_говорит_об_этом" />
/// (показать полную сумму вместо доли — тест падает) и
/// <see cref="Условие_по_объекту_даёт_одни_строки_в_памяти_и_в_запросе" />.</para>
/// </summary>
public sealed class ModuleTableShareTests(InvoiceLineHost host) : ModuleTableSeededTests(host)
{
    private const string Objects = "ОбъектыРазноски";
    private const string Amount = "СуммаПоОтбору";

    private readonly InvoiceLineHost host = host;

    /// <summary>
    /// Пять счетов: 1 — 100 ₽, одной строкой на три объекта поровну (33,33 + 33,33 + 33,34: копейка
    /// округления в последней части); 2 — 500 ₽ целиком на А; 3 — 1 000 ₽ без строк, суммой: 250 на А и
    /// 750 на Б; 4 — 700 ₽ целиком на Б; 5 — 900 ₽, не разнесён.
    /// </summary>
    private sealed record Shared(string Tag, string A, string B, string Store, Guid SiteB);

    [Fact]
    public async Task Под_отбором_по_объекту_сумма_становится_долей_и_говорит_об_этом()
    {
        var (client, _) = await SignInAsync("Admin");
        var seed = await SeedSharesAsync(client);

        var table = await ReadAsync(client,
            $"columns=Номер,{Amount},Итого,{Objects}&totals={Amount},Итого&filter={Own(seed, Is(seed.A))}");

        // Три счёта с частью на А. «Сумма» — доля на А, «Сумма к оплате» — счёт целиком.
        Assert.Equal(
            [($"{seed.Tag}-1", 33.33m, 100m), ($"{seed.Tag}-2", 500m, 500m), ($"{seed.Tag}-3", 250m, 1000m)],
            Rows(table).Select(r => (r.Number, r.Amount!.Value, r.Total!.Value)));

        // Подпись меняется — иначе доля выглядела бы суммой счёта (сторож блока G1).
        var amount = Column(table, Amount);
        Assert.Equal($"доля: {seed.A}", amount.GetProperty("note").GetString());
        Assert.True(amount.GetProperty("dependsOnFilter").GetBoolean());
        Assert.Equal(JsonValueKind.Null, Column(table, "Итого").GetProperty("note").ValueKind);

        // Итог — по долям, а не по счетам: 783,33, а не 1 600.
        var totals = table.GetProperty("totals");
        Assert.Equal(783.33m, totals.GetProperty(Amount).GetProperty("sum").GetDecimal());
        Assert.Equal(3, totals.GetProperty(Amount).GetProperty("count").GetInt64());
        Assert.Equal(33.33m, totals.GetProperty(Amount).GetProperty("min").GetDecimal());
        Assert.Equal(500m, totals.GetProperty(Amount).GetProperty("max").GetDecimal());
        Assert.Equal(1600m, totals.GetProperty("Итого").GetProperty("sum").GetDecimal());

        // Перечень объектов — названиями, каждый по разу, по алфавиту.
        Assert.Equal([seed.A, seed.B, seed.Store], Rows(table)[0].Objects);
    }

    /// <summary>Итог доли — по ВСЕМУ отбору, как любой итог: страница в одну строку его не меняет.</summary>
    [Fact]
    public async Task Итог_доли_считается_по_всему_отбору_а_не_по_странице()
    {
        var (client, _) = await SignInAsync("Admin");
        var seed = await SeedSharesAsync(client);

        var table = await ReadAsync(client,
            $"columns=Номер,{Amount}&limit=1&totals={Amount}&filter={Own(seed, Is(seed.A))}");

        Assert.Single(Rows(table));
        Assert.Equal(3, table.GetProperty("count").GetInt32());
        Assert.Equal(783.33m, table.GetProperty("totals").GetProperty(Amount).GetProperty("sum").GetDecimal());
    }

    /// <summary>
    /// Долю считает арифметика счёта, а не пропорция: копейка округления лежит в последней части —
    /// той, что на статье. Пропорцией в запросе вышло бы 33,33, и итог по объектам не сошёлся бы со
    /// счётом на копейку.
    /// </summary>
    [Fact]
    public async Task Доля_сходится_со_счётом_до_копейки()
    {
        var (client, _) = await SignInAsync("Admin");
        var seed = await SeedSharesAsync(client);

        async Task<decimal?> ShareAsync(string filter) =>
            Rows(await ReadAsync(client, $"columns=Номер,{Amount}&filter={Own(seed, filter)}"))
                .Single(r => r.Number == $"{seed.Tag}-1").Amount;

        var parts = new[] { await ShareAsync(Is(seed.A)), await ShareAsync(Is(seed.B)), await ShareAsync(Is(seed.Store)) };

        Assert.Equal([33.33m, 33.33m, 33.34m], parts);
        Assert.Equal(100m, parts.Sum());
    }

    [Fact]
    public async Task Доля_считается_на_все_названные_объекты()
    {
        var (client, _) = await SignInAsync("Admin");
        var seed = await SeedSharesAsync(client);

        // Перечень: А и Б — доля на оба.
        var listed = await ReadAsync(client,
            $"columns=Номер,{Amount}&totals={Amount}&filter={Own(seed, In(seed.A, seed.B))}");
        Assert.Equal([66.66m, 500m, 1000m, 700m], Rows(listed).Select(r => r.Amount!.Value));
        Assert.Equal($"доля: {seed.A}; {seed.B}", Column(listed, Amount).GetProperty("note").GetString());
        Assert.Equal(2266.66m, listed.GetProperty("totals").GetProperty(Amount).GetProperty("sum").GetDecimal());

        // «А или статья» группой «любое»: каждая ветка называет объект — доля на оба.
        var either = await ReadAsync(client,
            $"columns=Номер,{Amount}&filter={Own(seed, Or(Is(seed.A), Is(seed.Store)))}");
        Assert.Equal(66.67m, Rows(either).Single(r => r.Number == $"{seed.Tag}-1").Amount);
        Assert.Equal($"доля: {seed.A}; {seed.Store}", Column(either, Amount).GetProperty("note").GetString());
    }

    /// <summary>
    /// Отбор, который объектов НЕ называет, суммы не трогает: под «объект не А», «разнесён» и «А или
    /// дорогой счёт» в отборе есть счета без единой части на А, и «доля на А» у них была бы выдумкой.
    /// </summary>
    [Fact]
    public async Task Без_названного_объекта_сумма_остаётся_суммой_счёта()
    {
        var (client, _) = await SignInAsync("Admin");
        var seed = await SeedSharesAsync(client);

        var filters = new[]
        {
            Own(seed),
            Own(seed, Condition(Objects, "neq", seed.A)),
            Own(seed, """{"type":"condition","column":"ОбъектыРазноски","op":"is_not_empty"}"""),
            Own(seed, Or(Is(seed.A), Condition("Итого", "gt", "800"))),
        };

        foreach (var filter in filters)
        {
            var table = await ReadAsync(client, $"columns=Номер,{Amount},Итого&totals={Amount},Итого&filter={filter}");

            Assert.All(Rows(table), r => Assert.Equal(r.Total, r.Amount));
            Assert.Equal(JsonValueKind.Null, Column(table, Amount).GetProperty("note").ValueKind);
            Assert.Equal(
                table.GetProperty("totals").GetProperty("Итого").GetProperty("sum").GetDecimal(),
                table.GetProperty("totals").GetProperty(Amount).GetProperty("sum").GetDecimal());
        }
    }

    /// <summary>
    /// Пока разноска суммой ждёт пересчёта по появившимся строкам, деньги счёта лежат в ней — в частях
    /// счёта, а не строк. Правило одно на реестр и на будущий отчёт «Затраты по стройке».
    /// </summary>
    [Fact]
    public async Task Пока_разноска_суммой_ждёт_пересчёта_деньги_лежат_в_ней()
    {
        var (client, _) = await SignInAsync("Admin");
        var tag = Tag();
        var (site, name) = await ObjectAsync("Ожидание");

        var invoice = await InvoiceAsync(client, $"{tag}-1", 1_000m);
        await DocumentAsync(client, invoice, [Part(site, amount: 300m)]);
        await LinesAsync(client, invoice, [Line(cable, quantity: 50, price: 20m)]);

        var table = await ReadAsync(client, $"columns=Номер,{Amount}&filter={Own(tag, Is(name))}");

        Assert.Equal(300m, Rows(table).Single().Amount);
    }

    /// <summary>
    /// Условие «сумма больше миллиона» по колонке, которая под соседним условием становится долей,
    /// меняло бы смысл молча. Отбор и сортировка по ней отказывают — и называют причину.
    /// </summary>
    [Fact]
    public async Task По_колонке_зависящей_от_отбора_не_отбирают_и_не_сортируют()
    {
        var (client, _) = await SignInAsync("Admin");

        var filtered = await client.GetAsync(
            $"/api/tables/{Address}?filter={Uri.EscapeDataString(Condition(Amount, "gt", "100"))}");
        var sorted = await client.GetAsync($"/api/tables/{Address}?sort={Amount}:desc");

        foreach (var refusal in new[] { filtered, sorted })
        {
            Assert.Equal(HttpStatusCode.Conflict, refusal.StatusCode);
            Assert.Contains("зависит от самого отбора", await refusal.Content.ReadAsStringAsync());
        }

        // Экран узнаёт об этом заранее: операторов у колонки нет.
        var table = await ReadAsync(client, $"columns={Amount}&limit=1");
        Assert.Empty(Column(table, Amount).GetProperty("operators").EnumerateArray());
    }

    /// <summary>Подпись смысла — только тому, кто колонку видит: закрытой подписывать нечего.</summary>
    [Fact]
    public async Task Закрытая_сумма_приходит_с_причиной_а_не_с_подписью_доли()
    {
        var (admin, _) = await SignInAsync("Admin");
        var seed = await SeedSharesAsync(admin);
        var (narrow, _) = await SignInAsync(await RoleAsync("costs.waybill.read"));

        var table = await ReadAsync(narrow, $"columns=Номер,{Amount}&totals={Amount}&filter={Own(seed, Is(seed.A))}");

        var amount = Column(table, Amount);
        Assert.Equal("no-right", amount.GetProperty("unavailable").GetString());
        Assert.Equal(JsonValueKind.Null, amount.GetProperty("note").ValueKind);
        Assert.All(table.GetProperty("rows").EnumerateArray(), r => Assert.False(r.TryGetProperty(Amount, out _)));
        Assert.False(table.GetProperty("totals").TryGetProperty(Amount, out _));
    }

    [Fact]
    public async Task Условие_по_объекту_даёт_одни_строки_в_памяти_и_в_запросе()
    {
        var (client, user) = await SignInAsync("Admin");
        var seed = await SeedSharesAsync(client);

        // Отбор → сколько своих счетов обязано подойти. Число записано, а не выведено из исполнителя.
        var cases = new (string Name, string Filter, int Expected)[]
        {
            ("есть часть на объект — в другом регистре", Is(seed.A.ToUpperInvariant()), 3),
            ("нет НИ ОДНОЙ части на объект", Condition(Objects, "neq", seed.A), 2),
            ("есть часть на любой из списка", In(seed.A, seed.Store), 3),
            ("нет частей ни на один из списка", Condition(Objects, "not_in", seed.A, seed.B), 1),
            ("название объекта содержит", Condition(Objects, "contains", seed.B[..5].ToLowerInvariant()), 3),
            ("ни одно название не содержит", Condition(Objects, "not_contains", seed.B[..5]), 2),
            ("название начинается", Condition(Objects, "starts_with", seed.Store[..4].ToLowerInvariant()), 1),
            ("название заканчивается", Condition(Objects, "ends_with", seed.A[^6..]), 3),
            ("не разнесён", """{"type":"condition","column":"ОбъектыРазноски","op":"is_empty"}""", 1),
            ("разнесён", """{"type":"condition","column":"ОбъектыРазноски","op":"is_not_empty"}""", 4),
            ("объект или дорогой счёт", Or(Is(seed.B), Condition("Итого", "gt", "800")), 4),
        };

        var failures = await CompareAsync(client, user, seed.Tag, cases);
        Assert.True(failures.Count == 0, "Исполнители разошлись:\n  " + string.Join("\n  ", failures));
    }

    /// <summary>
    /// Стройку удалили в ядре — разноска на неё осталась. Счёт не становится «не разнесённым»: объект
    /// назван «объект удалён» у обоих исполнителей, и найти такие счета можно отбором.
    /// </summary>
    [Fact]
    public async Task Удалённый_объект_назван_а_счёт_не_считается_неразнесённым()
    {
        var (client, user) = await SignInAsync("Admin");
        var seed = await SeedSharesAsync(client);

        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new DeleteConstructionCommand(seed.SiteB));

        var cases = new (string Name, string Filter, int Expected)[]
        {
            ("разнесён на удалённый объект", Is("объект удалён"), 3),
            ("не разнесён — счёт с удалённым объектом сюда не попадает",
                """{"type":"condition","column":"ОбъектыРазноски","op":"is_empty"}""", 1),
            ("удалённого объекта нет", Condition(Objects, "not_contains", "удалён"), 2),
            ("по прежнему названию удалённый объект не находится", Is(seed.B), 0),
        };

        var failures = await CompareAsync(client, user, seed.Tag, cases);
        Assert.True(failures.Count == 0, "Исполнители разошлись:\n  " + string.Join("\n  ", failures));

        var lost = Rows(await ReadAsync(client, $"columns=Номер,{Objects}&filter={Own(seed)}"))
            .Single(r => r.Number == $"{seed.Tag}-4");
        Assert.Equal(["объект удалён"], lost.Objects);
    }

    [Fact]
    public async Task Сортировка_по_объекту_идёт_по_первому_названию_и_неразнесённые_в_конце()
    {
        var (client, _) = await SignInAsync("Admin");
        var seed = await SeedSharesAsync(client);

        async Task<List<string>> OrderAsync(string sort) =>
            [.. Rows(await ReadAsync(client, $"columns=Номер&sort={sort}&totals={Objects}&filter={Own(seed)}"))
                .Select(r => r.Number[^1..])];

        // Счета 1–3 начинаются с А, 4 — с Б, 5 не разнесён; равные — свежие сверху, как по умолчанию.
        Assert.Equal(["3", "2", "1", "4", "5"], await OrderAsync(Objects));
        Assert.Equal(["4", "3", "2", "1", "5"], await OrderAsync($"{Objects}:desc"));

        // Итог по перечню — сколько счетов разнесено.
        var table = await ReadAsync(client, $"columns=Номер&totals={Objects}&filter={Own(seed)}");
        Assert.Equal(4, table.GetProperty("totals").GetProperty(Objects).GetProperty("count").GetInt64());
    }

    /// <summary>
    /// Набор данных отбирает строки сам, уже после чтения: доля посчиталась бы в нём без отбора —
    /// полной суммой под именем доли. Поэтому колонка в набор не едет; перечень едет одной клеткой.
    /// </summary>
    [Fact]
    public async Task В_набор_данных_перечень_едет_клеткой_а_сумма_по_отбору_не_едет()
    {
        var (client, user) = await SignInAsync("Admin");
        var seed = await SeedSharesAsync(client);

        var provided = await MemoryRowsAsync(user);

        Assert.DoesNotContain(provided.Columns, c => c.Name == Amount);
        Assert.Contains(provided.Columns, c => c.Name == "Итого");
        Assert.False(provided.Types!.Kinds.ContainsKey(Amount));
        Assert.Equal("list", provided.Types.Kinds[Objects]);

        var first = provided.Rows.Single(r => r["Номер"] == $"{seed.Tag}-1");
        Assert.False(first.ContainsKey(Amount));
        Assert.Equal($"{seed.A}\n{seed.B}\n{seed.Store}", first[Objects]);
        Assert.Null(provided.Rows.Single(r => r["Номер"] == $"{seed.Tag}-5")[Objects]);
    }

    // ── Отборы ────────────────────────────────────────────────────────────────

    private static string Condition(string column, string op, params string[] values) => values.Length == 1
        ? $$"""{"type":"condition","column":"{{column}}","op":"{{op}}","value":{{JsonSerializer.Serialize(values[0])}}}"""
        : $$"""{"type":"condition","column":"{{column}}","op":"{{op}}","values":{{JsonSerializer.Serialize(values)}}}""";

    private static string Is(string name) => Condition(Objects, "eq", name);

    private static string In(params string[] names) =>
        $$"""{"type":"condition","column":"{{Objects}}","op":"in","values":{{JsonSerializer.Serialize(names)}}}""";

    private static string Or(params string[] conditions) =>
        $$"""{"type":"group","logic":"or","children":[{{string.Join(",", conditions)}}]}""";

    /// <summary>Свои счета и, если задано, условие — стенд общий, чужие строки отсекает метка в номере.</summary>
    private static string Raw(string tag, string? condition = null) =>
        $$"""{"type":"group","logic":"and","children":[{{Condition("Номер", "starts_with", tag)}}{{(condition is null ? "" : "," + condition)}}]}""";

    private static string Own(string tag, string? condition = null) => Uri.EscapeDataString(Raw(tag, condition));

    private static string Own(Shared seed, string? condition = null) => Own(seed.Tag, condition);

    /// <summary>Каждый отбор — обоими исполнителями; что разошлось или дало не то число строк.</summary>
    private async Task<List<string>> CompareAsync(
        HttpClient client, Guid user, string tag, (string Name, string Filter, int Expected)[] cases)
    {
        var inMemory = await MemoryRowsAsync(user);
        var failures = new List<string>();

        foreach (var (name, condition, expected) in cases)
        {
            var filter = Raw(tag, condition);
            var sql = await SqlAsync(client, filter);
            var memory = Numbers(DataSetRowFilterExecutor.Apply(filter, [.. inMemory.Rows], "пара", inMemory.Types));

            if (!sql.SequenceEqual(memory))
                failures.Add($"«{name}»: запрос к базе вернул [{string.Join(", ", sql)}], в памяти [{string.Join(", ", memory)}]");
            else if (sql.Count != expected)
                failures.Add($"«{name}»: оба исполнителя вернули {sql.Count} строк, а обязаны {expected}: [{string.Join(", ", sql)}]");
        }

        return failures;
    }

    // ── Чтение ────────────────────────────────────────────────────────────────

    private sealed record Row(string Number, decimal? Amount, decimal? Total, string[] Objects);

    private static async Task<JsonElement> ReadAsync(HttpClient client, string query)
    {
        // Порядок по номеру — у каждого чтения, кроме тех, что проверяют сортировку сами.
        var sort = query.Contains("sort=") ? "" : "&sort=Номер";
        var response = await client.GetAsync($"/api/tables/{Address}?{query}{sort}");
        await OkAsync(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static List<Row> Rows(JsonElement table) => [.. table.GetProperty("rows").EnumerateArray().Select(r => new Row(
        r.GetProperty("Номер").GetString()!,
        Number(r, Amount),
        Number(r, "Итого"),
        r.TryGetProperty(Objects, out var objects) ? [.. objects.EnumerateArray().Select(o => o.GetString()!)] : []))];

    private static decimal? Number(JsonElement row, string key) =>
        row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDecimal() : null;

    private static JsonElement Column(JsonElement table, string key) =>
        table.GetProperty("columns").EnumerateArray().Single(c => c.GetProperty("key").GetString() == key);

    // ── Посев ─────────────────────────────────────────────────────────────────

    private static string Tag() => $"Д{Guid.NewGuid().ToString("N")[..6]}";

    private async Task<Shared> SeedSharesAsync(HttpClient client)
    {
        var tag = Tag();
        var (a, nameA) = await ObjectAsync("Комарова");
        var (b, nameB) = await ObjectAsync("Ливнёвка");
        var (store, nameStore) = await ArticleAsync(client, "Склад");

        // 1 — строка на три объекта поровну: 100 ₽ на три даёт копейку округления в последней части.
        var first = await InvoiceAsync(client, $"{tag}-1", 100m);
        var lines = await LinesAsync(client, first, [Line(cable, quantity: 3, price: 33.33m, amount: 100m)]);
        await AllocateAsync(client, first, LineId(lines, 1),
        [
            Part(a, quantity: 1), Part(b, quantity: 1),
            new Dictionary<string, object?> { ["article"] = store.ToString(), ["quantity"] = 1m },
        ]);

        // 2 — целиком на А.
        var second = await InvoiceAsync(client, $"{tag}-2", 500m);
        lines = await LinesAsync(client, second, [Line(cable, quantity: 2, price: 250m)]);
        await AllocateAsync(client, second, LineId(lines, 1), [Part(a, quantity: 2)]);

        // 3 — без строк, суммой: 250 на А и 750 на Б.
        var third = await InvoiceAsync(client, $"{tag}-3", 1_000m);
        await DocumentAsync(client, third, [Part(a, amount: 250m), Part(b, amount: 750m)]);

        // 4 — целиком на Б.
        var fourth = await InvoiceAsync(client, $"{tag}-4", 700m);
        lines = await LinesAsync(client, fourth, [Line(cable, quantity: 1, price: 700m)]);
        await AllocateAsync(client, fourth, LineId(lines, 1), [Part(b, quantity: 1)]);

        // 5 — не разнесён.
        await InvoiceAsync(client, $"{tag}-5", 900m);

        return new Shared(tag, nameA, nameB, nameStore, b);
    }

    private static async Task<Guid> InvoiceAsync(HttpClient client, string number, decimal total)
    {
        var requisites = new Dictionary<string, object?>
        {
            ["Номер"] = number,
            ["Дата"] = "2026-09-29",
            ["Поставщик"] = Reference(supplier),
            ["Итого"] = total,
        };

        var response = await client.PostAsJsonAsync("/api/costs/invoices", new { requisites });
        await OkAsync(response);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    /// <summary>Разноска счёта целиком, суммой, — пока строк нет (F2).</summary>
    private static async Task DocumentAsync(HttpClient client, Guid invoice, object[] parts)
    {
        var stamp = (await ReadAsync(client, invoice)).GetProperty("allocation").GetProperty("stamp").GetString();
        await OkAsync(await client.PutAsJsonAsync($"/api/costs/invoices/{invoice}/allocation",
            new { lines = Array.Empty<object>(), document = parts, stamp }));
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

    private static async Task<(Guid Id, string Name)> ArticleAsync(HttpClient client, string name)
    {
        var unique = $"{name} {Guid.NewGuid().ToString()[..6]}";
        var response = await client.PostAsJsonAsync("/api/costs/articles", new { name = unique });
        await OkAsync(response);
        return ((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid(), unique);
    }
}
