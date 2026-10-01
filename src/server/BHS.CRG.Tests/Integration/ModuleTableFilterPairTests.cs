using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Infrastructure.DataSets;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Парный тест двух исполнителей дерева условий (задача G1c, issue #1090, ТЗ CORE-33).
///
/// <para>Один сохранённый отбор обязан дать одни и те же строки на экране таблицы (запрос к базе,
/// <c>TableSql</c>) и в наборе данных на той же таблице (в памяти, <c>DataSetRowFilterExecutor</c> с
/// видами колонок). Общего кода у исполнителей нет — один считает на строках, другой пишет SQL, —
/// поэтому согласие держит только этот тест: правило, добавленное в один исполнитель и забытое в
/// другом, роняет его на первом же отборе с таким правилом.</para>
///
/// <para>Данные нарочно грязные: в числовом поле схемы лежат и числа, и «12 шт», в поле даты —
/// «скоро», у части счетов нет срока, суммы, поставщика. Именно на них исполнители и расходятся.</para>
/// </summary>
public sealed class ModuleTableFilterPairTests(InvoiceLineHost host) : ModuleTableSeededTests(host)
{
    private const string Address = "costs.invoices";
    private const string Marker = "system:table:" + Address;

    [Fact]
    public async Task Один_отбор_даёт_одни_строки_в_памяти_и_в_запросе()
    {
        var (client, user) = await SignInAsync("Supplier");
        var seed = await SeedAsync(client);

        // Каждый отбор сужен до своих счетов: стенд общий, и чужие строки сравнение только зашумили бы.
        string Own(string condition) =>
            $$"""{"type":"group","logic":"and","children":[{"type":"condition","column":"Номер","op":"starts_with","value":"{{seed.Tag}}"},{{condition}}]}""";
        string One(string column, string op, string value) =>
            Own($$"""{"type":"condition","column":"{{column}}","op":"{{op}}","value":"{{value}}"}""");
        string Many(string column, string op, params string[] values) =>
            Own($$"""{"type":"condition","column":"{{column}}","op":"{{op}}","values":{{JsonSerializer.Serialize(values)}}}""");
        string None(string column, string op) => Own($$"""{"type":"condition","column":"{{column}}","op":"{{op}}"}""");

        // Отбор → сколько своих счетов обязано подойти. Число записано, а не выведено из исполнителя:
        // два исполнителя, одинаково вернувшие «ничего», иначе сошлись бы зелёными.
        var cases = new (string Name, string Filter, int Expected)[]
        {
            ("текст: содержит в другом регистре", One("Назначение", "contains", "КАБЕЛЬ"), 3),
            ("текст: равно в другом регистре", One("Назначение", "eq", "оплата за кабель"), 2),
            ("текст: не содержит — и пустые тоже", One("Назначение", "not_contains", "кабель"), 5),
            ("текст: начинается", One("Назначение", "starts_with", "оплата"), 3),
            ("текст: заканчивается", One("Назначение", "ends_with", "КАБЕЛЬ"), 2),
            ("текст: пусто", None("Назначение", "is_empty"), 3),
            ("текст: входит в список", Many("Назначение", "in", "ОПЛАТА ЗА КАБЕЛЬ", "аванс"), 3),
            ("текст: не входит в список", Many("Назначение", "not_in", "оплата за кабель", "аванс"), 5),

            ("число: равно без хвостовых нулей", One("Итого", "eq", "110"), 2),
            ("число: не равно — и пустые тоже", One("Итого", "neq", "110"), 6),
            ("число: больше", One("Итого", "gt", "100"), 4),
            ("число: меньше — пустые не подходят", One("Итого", "lt", "100"), 2),
            ("число: между", Many("Итого", "between", "50", "110"), 4),
            ("число: входит в список", Many("Итого", "in", "110.00", "5000"), 3),
            ("число: не определено", None("Итого", "is_null"), 2),
            ("число: определено", None("Итого", "is_not_null"), 6),

            ("дата: меньше — без срока не подходят", One("Срок", "lt", "2026-06-01"), 2),
            ("дата: период", Many("Срок", "between", "2026-05-01", "2026-06-30"), 3),
            ("дата: без срока", None("Срок", "is_null"), 4),
            ("дата: равно", One("Срок", "eq", "2026-05-15"), 1),

            ("справочник: поставщик содержит", One("Поставщик", "contains", "кабель"), 5),
            ("справочник: поставщик не содержит — и без поставщика", One("Поставщик", "not_contains", "кабель"), 3),
            ("справочник: поставщика нет", None("Поставщик", "is_empty"), 2),
            ("справочник: состояние оплаты из списка", Many("СостояниеОплаты", "in", "Оплачен", "Частично оплачен"), 3),
            ("справочник: состояние оплаты не равно", One("СостояниеОплаты", "neq", "не оплачен"), 3),

            ("поле схемы, число: больше — текст не подходит", One(seed.Weight, "gt", "10"), 2),
            ("поле схемы, число: равно", One(seed.Weight, "eq", "7"), 2),
            ("поле схемы, число: не равно — текст и пустые тоже", One(seed.Weight, "neq", "7"), 6),
            ("поле схемы, число: меньше", One(seed.Weight, "lt", "10"), 2),
            ("поле схемы, число: не определено — текст определён", None(seed.Weight, "is_null"), 2),
            ("поле схемы, дата: позже", One(seed.Warranty, "gt", "2026-05-01"), 2),
            ("поле схемы, дата: не позже — «скоро» не подходит", One(seed.Warranty, "lte", "2026-05-01"), 1),
            ("поле схемы, текст: содержит", One(seed.Note, "contains", "СРОЧНО"), 2),

            ("группа «или»", Own($$"""{"type":"group","logic":"or","children":[{"type":"condition","column":"Итого","op":"gt","value":"1000"},{"type":"condition","column":"Срок","op":"is_null"}]}"""), 5),
        };

        var inMemory = await MemoryRowsAsync(user);
        var failures = new List<string>();
        foreach (var (name, filter, expected) in cases)
        {
            var sql = await SqlAsync(client, filter);
            var memory = Numbers(DataSetRowFilterExecutor.Apply(filter, [.. inMemory.Rows], "пара", inMemory.Types));

            if (!sql.SequenceEqual(memory))
                failures.Add($"«{name}»: запрос к базе вернул [{string.Join(", ", sql)}], в памяти [{string.Join(", ", memory)}]");
            else if (sql.Count != expected)
                failures.Add($"«{name}»: оба исполнителя вернули {sql.Count} строк, а обязаны {expected}: [{string.Join(", ", sql)}]");
        }

        Assert.True(failures.Count == 0, "Исполнители разошлись:\n  " + string.Join("\n  ", failures));
    }

    // ── Два исполнителя ───────────────────────────────────────────────────────

    /// <summary>Экран таблицы: отбор исполняет запрос к базе.</summary>
    private static async Task<List<string>> SqlAsync(HttpClient client, string filter)
    {
        var response = await client.GetAsync(
            $"/api/tables/{Address}?columns=Номер&limit=1000&filter={Uri.EscapeDataString(filter)}");
        await OkAsync(response);
        var table = await response.Content.ReadFromJsonAsync<JsonElement>();
        return [.. table.GetProperty("rows").EnumerateArray()
            .Select(r => r.GetProperty("Номер").GetString() ?? "")
            .OrderBy(n => n, StringComparer.Ordinal)];
    }

    /// <summary>Набор данных на той же таблице: строки целиком, отбор — в памяти.</summary>
    private async Task<DataSetParseResult> MemoryRowsAsync(Guid user)
    {
        using var scope = host.Services.CreateScope();
        var access = await scope.ServiceProvider.GetRequiredService<DataAccessResolver>().ForUserAsync(user, default);
        var provider = scope.ServiceProvider.GetServices<ISystemDataProvider>().Single(p => p.Handles(Marker));
        return await provider.ProvideAsync(Marker, CatalogScope.System, null, access, default);
    }

    private static List<string> Numbers(IEnumerable<IReadOnlyDictionary<string, string?>> rows) =>
        [.. rows.Select(r => r.GetValueOrDefault("Номер") ?? "").OrderBy(n => n, StringComparer.Ordinal)];
}
