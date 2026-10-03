using BHS.CRG.Modules.Costs.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Сохранение набора строк пишет только изменившееся, а время правки счёта следует за строками
/// (issue #1171, находка ревью PR #1116).
///
/// <para>Форма присылает строки ЦЕЛИКОМ на каждое сохранение. Раньше каждая присланная строка
/// получала новое «когда правили»: правка одной из ста — сто обновлений, и у девяноста девяти время
/// врало. Счёт при этом своё время не менял вовсе.</para>
///
/// <para>Время строк читается из базы, а не из ответа: в ответе его нет, и проверять надо то, что
/// легло.</para>
/// </summary>
[Collection("Integration")]
public class InvoiceLineWriteCostTests(InvoiceLineHost host) : InvoiceLineTestBase(host)
{
    [Fact]
    public async Task Правка_одной_строки_не_трогает_остальные()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);
        var view = await LinesAsync(client, invoice,
        [
            Line(cable, quantity: 1, price: 10m),
            Line(conduit, quantity: 2, price: 20m),
            Line(cable, quantity: 3, price: 30m),
        ]);
        var before = await StampsAsync(invoice);

        await LinesAsync(client, invoice,
        [
            Line(cable, quantity: 1, price: 10m, id: LineId(view, 1)),
            Line(conduit, quantity: 5, price: 20m, id: LineId(view, 2)),
            Line(cable, quantity: 3, price: 30m, id: LineId(view, 3)),
        ]);
        var after = await StampsAsync(invoice);

        Assert.Equal(before[LineId(view, 1)], after[LineId(view, 1)]);
        Assert.Equal(before[LineId(view, 3)], after[LineId(view, 3)]);
        // А изменённая — записана: «время не сдвинулось» зелено и у сохранения, которое не пишет ничего.
        Assert.True(after[LineId(view, 2)] > before[LineId(view, 2)]);
    }

    /// <summary>
    /// Значения те же, а место другое — это тоже правка: номер строки лежит в ней самой. Не заметь
    /// сохранение перестановки, набор вернулся бы в прежнем порядке.
    /// </summary>
    [Fact]
    public async Task Перестановка_строк_записывает_переставленные()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);
        var view = await LinesAsync(client, invoice,
        [
            Line(cable, quantity: 1, price: 10m),
            Line(conduit, quantity: 2, price: 20m),
            Line(cable, quantity: 3, price: 30m),
        ]);
        var before = await StampsAsync(invoice);

        var swapped = await LinesAsync(client, invoice,
        [
            Line(conduit, quantity: 2, price: 20m, id: LineId(view, 2)),
            Line(cable, quantity: 1, price: 10m, id: LineId(view, 1)),
            Line(cable, quantity: 3, price: 30m, id: LineId(view, 3)),
        ]);
        var after = await StampsAsync(invoice);

        Assert.Equal(LineId(view, 2), LineId(swapped, 1));
        Assert.Equal(LineId(view, 1), LineId(swapped, 2));
        Assert.True(after[LineId(view, 1)] > before[LineId(view, 1)]);
        Assert.True(after[LineId(view, 2)] > before[LineId(view, 2)]);
        Assert.Equal(before[LineId(view, 3)], after[LineId(view, 3)]);
    }

    /// <summary>
    /// Правка строк сдвигает время правки СЧЁТА, повторная отправка того же набора — нет. Обе половины
    /// нужны: первая зелена у кода, который двигает время всегда, вторая — у кода, который не двигает
    /// никогда.
    /// </summary>
    [Fact]
    public async Task Время_правки_счёта_следует_за_строками()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);
        var created = await UpdatedAsync(client, invoice);

        var view = await LinesAsync(client, invoice, [Line(cable, quantity: 1, price: 10m)]);
        var withLines = await UpdatedAsync(client, invoice);
        Assert.True(withLines > created);

        await LinesAsync(client, invoice, [Line(cable, quantity: 1, price: 10m, id: LineId(view, 1))]);
        Assert.Equal(withLines, await UpdatedAsync(client, invoice));

        await LinesAsync(client, invoice, [Line(cable, quantity: 2, price: 10m, id: LineId(view, 1))]);
        var edited = await UpdatedAsync(client, invoice);
        Assert.True(edited > withLines);

        // Удаление строк — тоже правка счёта.
        await LinesAsync(client, invoice, []);
        Assert.True(await UpdatedAsync(client, invoice) > edited);
    }

    /// <summary>
    /// Время правки счёта — ПРОЧИТАННОЕ заново, а не из ответа на запись: ответ несёт время с точностью
    /// часов .NET (100 нс), а база хранит до микросекунды, и одно и то же время из двух источников
    /// «различалось» бы на хвост.
    /// </summary>
    private static async Task<DateTimeOffset> UpdatedAsync(HttpClient client, Guid invoice) =>
        (await ReadAsync(client, invoice)).GetProperty("updatedAt").GetDateTimeOffset();

    private async Task<Dictionary<Guid, DateTimeOffset>> StampsAsync(Guid invoice)
    {
        using var scope = host.Services.CreateScope();
        var costs = scope.ServiceProvider.GetRequiredService<CostsDbContext>();
        return await costs.InvoiceLines.AsNoTracking()
            .Where(l => l.InvoiceId == invoice)
            .ToDictionaryAsync(l => l.Id, l => l.UpdatedAt);
    }
}
