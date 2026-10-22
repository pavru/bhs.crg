using System.Text.Json;
using BHS.CRG.Modules.Costs.Endpoints;

namespace BHS.CRG.Tests.Costs;

/// <summary>
/// Запись «прочитано, в счёт не положено» (issue #1484): как хранится и как отдаётся. Сбор по причинам —
/// <c>InvoiceScanHeaderTests</c> и <c>InvoiceScanLineKeysTests</c> (нет поля, не заполняется, повтор ключа),
/// <c>InvoiceFromScanTests.Unplaced</c> и <c>.Targets</c> (не разобрано, занято, охрана; хранение при любом
/// исходе строк).
/// </summary>
public class InvoiceScanUnplacedTests
{
    /// <summary>Слова причин — на клиенте по коду: код — договор, и он не меняется молча.</summary>
    [Fact]
    public void Шесть_кодов_причин()
    {
        Assert.Equal(["no-field", "not-recognizable", "unparsed", "guard", "occupied", "duplicate-key"],
            InvoiceScanUnplaced.Reasons);
    }

    /// <summary>Хранится именами API: вид отдаёт записанное как есть, клиент читает те же имена.</summary>
    [Fact]
    public void Хранится_и_отдаётся_именами_API()
    {
        UnplacedRead[] list =
        [
            InvoiceScanUnplaced.InLine(3, " Артикл", "А-1", InvoiceScanUnplaced.NoField),
            InvoiceScanUnplaced.InHeader("Код", "X-1", InvoiceScanUnplaced.Guard, "Код договора — «Д-» и цифры"),
        ];

        using var stored = InvoiceScanUnplaced.Stored(list);
        var first = stored.RootElement[0];
        Assert.Equal(("line", 3, " Артикл", "no-field"), (first.GetProperty("place").GetString(), first.GetProperty("row").GetInt32(),
            first.GetProperty("key").GetString(), first.GetProperty("reason").GetString()));
        Assert.Equal(list, InvoiceScanUnplaced.Shown(stored));
    }

    /// <summary>Нет записи — «не знаем» (распознавание до списка), а не «пусто»; пустой список — пусто.</summary>
    [Fact]
    public void Нет_списка_не_то_же_что_пустой()
    {
        Assert.Null(InvoiceScanUnplaced.Shown(null));
        using var empty = InvoiceScanUnplaced.Stored([]);
        Assert.Equal(JsonValueKind.Array, empty.RootElement.ValueKind);
        Assert.Empty(InvoiceScanUnplaced.Shown(empty)!);
    }

    /// <summary>Значение — не длиннее 40 знаков, тем же правилом, что у замечаний.</summary>
    [Fact]
    public void Значение_сокращено()
    {
        var read = InvoiceScanUnplaced.InHeader("Ключ", new string('я', 50), InvoiceScanUnplaced.NoField);
        Assert.Equal(new string('я', 40) + "…", read.Value);
    }
}
