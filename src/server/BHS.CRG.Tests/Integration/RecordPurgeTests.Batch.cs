using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Costs;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Пакетная очистка записей, занятых только данными выключенных или снятых модулей (issue #1245).
/// Правила те же, что у одиночного удаления; свои — «всё или ничего» и разбор записей порознь.
/// </summary>
public partial class RecordPurgeTests
{
    /// <summary>
    /// СТОРОЖ ЗАДАЧИ. Из четырёх названных записей в пакет входят две занятые снятым модулем;
    /// свободная и занятая включённым модулем названы с причиной. Неверное число — отказ целиком;
    /// верное — удалены обе, и журнал называет каждую.
    /// </summary>
    [Fact]
    public async Task Пакет_удаляет_занятые_снятым_модулем_и_называет_каждую_в_журнале()
    {
        var (client, _) = await SignInAsync(SystemRoles.Admin);
        var (first, second, free, busy) =
            (await OwnPositionAsync(), await OwnPositionAsync(), await OwnPositionAsync(), await OwnPositionAsync());
        await LinesAsync(client, await CreateAsync(client), [Line(busy, quantity: 1, price: 10)]);
        // Одна схема держит обе записи: числа обязаны разойтись по записям, а не слиться в общее.
        await using var probe = await HeldTogetherAsync((first, 2), (second, 1));

        var preview = await PreviewAsync(client, new { ids = new[] { first, second, free, busy } });
        Assert.Equal(3, preview.GetProperty("references").GetInt32());
        Assert.Equal(
            new[] { (first, 2), (second, 1) }.OrderBy(r => r.Item1),
            preview.GetProperty("records").EnumerateArray()
                .Select(r => (r.GetProperty("id").GetGuid(), r.GetProperty("references").GetInt32())).OrderBy(r => r.Item1));
        var excluded = preview.GetProperty("excluded").EnumerateArray()
            .ToDictionary(e => e.GetProperty("id").GetGuid(), e => e.GetProperty("reason").GetString()!);
        Assert.Contains("обычным путём", excluded[free]);
        Assert.Contains("включённого модуля", excluded[busy]);

        // Неверное общее число — отказ целиком, со свежим предпросмотром.
        var wrong = await client.PostAsJsonAsync("/api/common-data/purge-batch",
            new { records = new[] { Seen(first, 2), Seen(second, 1) }, references = 2 });
        Assert.Equal(HttpStatusCode.Conflict, wrong.StatusCode);
        var again = await wrong.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("Число ссылок не совпало", again.GetProperty("error").GetString());
        Assert.Equal(3, again.GetProperty("preview").GetProperty("references").GetInt32());

        // Запись, занятая включённым модулем, пакет не проходит — и не удаляется никто. Общее число
        // названо то, что сошлось бы без неё: отказ обязан прийти от самой записи, а не от суммы.
        var withBusy = await client.PostAsJsonAsync("/api/common-data/purge-batch",
            new { records = new[] { Seen(first, 2), Seen(second, 1), Seen(busy, 1) }, references = 3 });
        Assert.Equal(HttpStatusCode.Conflict, withBusy.StatusCode);
        Assert.Contains("удалить теперь нельзя записей: 1", await withBusy.Content.ReadAsStringAsync());
        foreach (var id in new[] { first, second, busy })
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/common-data/{id}")).StatusCode);
        Assert.Empty(await PurgedAsync(first));

        var done = await client.PostAsJsonAsync("/api/common-data/purge-batch",
            new { records = new[] { Seen(first, 2), Seen(second, 1) }, references = 3 });
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        var result = await done.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((2, 3), (result.GetProperty("records").GetInt32(), result.GetProperty("references").GetInt32()));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/common-data/{first}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/common-data/{second}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/common-data/{free}")).StatusCode);

        var written = Assert.Single(await PurgedAsync(first));
        Assert.Contains($"{probe.Schema}.things.held", written.Before);
        Assert.Equal("потеряно ссылок: 2; удалено пакетом, записей в нём: 2", written.After);
        Assert.Equal("потеряно ссылок: 1; удалено пакетом, записей в нём: 2", Assert.Single(await PurgedAsync(second)).After);
    }

    /// <summary>Без права закрыты все три адреса пакета — и предпросмотр: разбивку без права не показывают.</summary>
    [Fact]
    public async Task Без_права_адреса_пакета_закрыты()
    {
        var (client, _) = await SignInAsync(SystemRoles.IdEngineer);
        var record = await OwnPositionAsync();
        await using var probe = await HeldTogetherAsync((record, 1));

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/common-data/purge-batch/available")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync("/api/common-data/purge-batch/preview", new { ids = new[] { record } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/common-data/purge-batch",
            new { records = new[] { Seen(record, 1) }, references = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/common-data/{record}")).StatusCode);
    }

    /// <summary>
    /// Всё или ничего. У одной записи пакета число ссылок выросло после предпросмотра, а общее число
    /// человек «угадал» — пакет всё равно не проходит: подтверждено не то, что удаляется. Вторая
    /// запись, с которой ничего не случилось, остаётся на месте.
    /// </summary>
    [Fact]
    public async Task Изменившаяся_запись_останавливает_весь_пакет()
    {
        var (client, _) = await SignInAsync(SystemRoles.Admin);
        var (grown, steady) = (await OwnPositionAsync(), await OwnPositionAsync());
        await using var probe = await HeldTogetherAsync((grown, 2), (steady, 1));

        var purge = await client.PostAsJsonAsync("/api/common-data/purge-batch",
            new { records = new[] { Seen(grown, 1), Seen(steady, 2) }, references = 3 });

        Assert.Equal(HttpStatusCode.Conflict, purge.StatusCode);
        var body = await purge.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("изменилось число ссылок", body.GetProperty("error").GetString());
        Assert.Equal(2, body.GetProperty("preview").GetProperty("records").GetArrayLength());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/common-data/{steady}")).StatusCode);
        Assert.Empty(await PurgedAsync(steady));

        // И запись, которой уже нет, — тоже расхождение: человек подтверждал список с ней.
        var gone = await client.PostAsJsonAsync("/api/common-data/purge-batch",
            new { records = new[] { Seen(grown, 2), Seen(Guid.NewGuid(), 1) }, references = 3 });
        Assert.Equal(HttpStatusCode.Conflict, gone.StatusCode);
        Assert.Contains("уже нет", await gone.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/common-data/{grown}")).StatusCode);
    }

    /// <summary>
    /// Отказ ядра пакет не обходит: запись, на которую ссылается другой объект ядра, в предпросмотре
    /// названа не вошедшей — с причиной ядра, — а названная в пакете останавливает его целиком.
    /// </summary>
    [Fact]
    public async Task Отказ_ядра_пакет_не_обходит()
    {
        var (client, _) = await SignInAsync(SystemRoles.Admin);
        var (held, clean) = (await OwnPositionAsync(), await OwnPositionAsync());
        await using var probe = await HeldTogetherAsync((held, 1), (clean, 1));
        await ReferringAsync(held);

        var preview = await PreviewAsync(client, new { ids = new[] { held, clean } });
        Assert.Equal(clean, Assert.Single(preview.GetProperty("records").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Contains("ссылаются другие объекты",
            Assert.Single(preview.GetProperty("excluded").EnumerateArray()).GetProperty("reason").GetString());

        var purge = await client.PostAsJsonAsync("/api/common-data/purge-batch",
            new { records = new[] { Seen(held, 1), Seen(clean, 1) }, references = 2 });
        Assert.Equal(HttpStatusCode.Conflict, purge.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/common-data/{clean}")).StatusCode);
    }

    /// <summary>
    /// Готовый отбор: действующие записи типа на уровне, занятые только снятым модулем. Свободная
    /// запись и архивная в него не входят (архивные — решение владельца 12.10.2026), и «не вошедшими»
    /// не названы: отбор их не искал. Признак «есть что искать» отвечает «да», пока схема на месте.
    /// </summary>
    [Fact]
    public async Task Готовый_отбор_находит_действующие_записи_типа_занятые_снятым_модулем()
    {
        var (client, _) = await SignInAsync(SystemRoles.Admin);
        var type = await TypeAsync(CostsRecordTypes.NomenclatureCode, "Номенклатура");
        var (held, free, archived) = (await OwnPositionAsync(), await OwnPositionAsync(), await OwnPositionAsync());
        await using var probe = await HeldTogetherAsync((held, 2), (archived, 1));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/common-data/{archived}/archive", null)).StatusCode);

        var available = await client.GetFromJsonAsync<JsonElement>("/api/common-data/purge-batch/available");
        Assert.True(available.GetProperty("available").GetBoolean());

        var preview = await PreviewAsync(client, new { typeId = type, scope = "System" });

        var ids = preview.GetProperty("records").EnumerateArray().Select(r => r.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(held, ids);
        Assert.DoesNotContain(free, ids);
        Assert.DoesNotContain(archived, ids);
        Assert.DoesNotContain(preview.GetProperty("excluded").EnumerateArray(),
            e => e.GetProperty("id").GetGuid() == free || e.GetProperty("id").GetGuid() == archived);

        // А названная галочкой архивная запись в пакет входит — с признаком.
        var named = await PreviewAsync(client, new { ids = new[] { archived } });
        Assert.True(Assert.Single(named.GetProperty("records").EnumerateArray()).GetProperty("archived").GetBoolean());
    }

    private static object Seen(Guid id, int references) => new { id, references };

    private static async Task<JsonElement> PreviewAsync(HttpClient client, object request)
    {
        var response = await client.PostAsJsonAsync("/api/common-data/purge-batch/preview", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Одна подставная схема, держащая несколько записей — каждую своим числом строк.</summary>
    private async Task<Probe> HeldTogetherAsync(params (Guid Record, int Rows)[] held)
    {
        var schema = $"probe_{Guid.NewGuid():N}";
        await SqlAsync($"CREATE SCHEMA {schema}");
        await SqlAsync($"CREATE TABLE {schema}.things (id uuid PRIMARY KEY, held uuid)");
        foreach (var (record, rows) in held)
            for (var i = 0; i < rows; i++)
                await SqlAsync($"INSERT INTO {schema}.things VALUES (gen_random_uuid(), @p0)", record);
        return new Probe(schema, () => SqlAsync($"DROP SCHEMA {schema} CASCADE"));
    }
}
