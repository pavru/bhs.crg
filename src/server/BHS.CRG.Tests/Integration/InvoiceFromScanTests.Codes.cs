using System.Text.Json;
using BHS.CRG.Modules.Costs;
using BHS.CRG.Modules.Costs.Data;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Счёт из файла: строки по ключам колонок профиля (issue #1480). Отдельной частью — по занятию: здесь
/// колонки, которые администратор добавил в профиль сам. Разбор каждого значения — в
/// <c>InvoiceScanLineKeysTests</c>, здесь — путь целиком, через очередь и запись строк.
/// </summary>
public sealed partial class InvoiceFromScanTests
{
    /// <summary>
    /// Колонка «Артикул», добавленная в профиль, ложится в строку; длинный артикул — в примечание строки;
    /// колонка, которой в счёте некуда лечь, названа в примечаниях распознавания, а не потеряна молча и не
    /// дописана в примечание строки — то поле человека (ревью PR #1485).
    /// </summary>
    [Fact]
    public async Task Артикул_из_профиля_ложится_а_лишняя_колонка_названа()
    {
        var (client, _) = await SignInAsync("Supplier");
        var scan = Scan();
        var longCode = new string('К', InvoiceLine.SupplierCodeLength + 1);
        host.Recognition.On(scan, () => Task.FromResult(Read(Header(number: "СЧ-513"),
            With(Row("Кабель", "м", "10", "5,00", "50,00"), (CostsRecognitionProfiles.LineCode, "ВВГ-325")),
            With(Row("Труба", "м", "2", "7,00", "14,00"), (CostsRecognitionProfiles.LineCode, longCode)),
            With(Row("Муфта", "шт", "1", "3,00", "3,00"), ("Страна", "Россия")))));

        var id = (await FromScanAsync(client, scan)).GetProperty("invoice").GetProperty("id").GetGuid();
        var recognition = await OutcomeAsync(client, id);
        Assert.Equal("done", recognition.GetProperty("state").GetString());
        Assert.Contains(recognition.GetProperty("notes").EnumerateArray(),
            n => n.GetString()!.StartsWith("Прочитано, в счёт не положено: «Страна» — у строки счёта нет поля с таким ключом"));

        var lines = (await ReadAsync(client, id)).GetProperty("lines");
        Assert.Equal("ВВГ-325", lines[0].GetProperty("supplierCode").GetString());
        Assert.Equal(JsonValueKind.Null, lines[0].GetProperty("note").ValueKind);

        Assert.Equal(JsonValueKind.Null, lines[1].GetProperty("supplierCode").ValueKind);
        Assert.Contains($"длиннее {InvoiceLine.SupplierCodeLength} знаков", lines[1].GetProperty("note").GetString());

        Assert.Equal(JsonValueKind.Null, lines[2].GetProperty("supplierCode").ValueKind);
        Assert.Equal(JsonValueKind.Null, lines[2].GetProperty("note").ValueKind);
    }

    private static IReadOnlyDictionary<string, string?> With(IReadOnlyDictionary<string, string?> row, (string Key, string Value) extra) =>
        new Dictionary<string, string?>(row) { [extra.Key] = extra.Value };
}
