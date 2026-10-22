using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Domain.Recognition;
using BHS.CRG.Modules.Costs;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Costs.Endpoints;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// «Прочитано, в счёт не положено» — с причиной, всегда (ТЗ COST-8, issue #1484). Разбор каждой причины —
/// в <c>InvoiceScanHeaderTests</c>, <c>InvoiceScanLineKeysTests</c>; здесь — что список ХРАНИТСЯ при любом
/// исходе строк и что переехавшие в него фразы из замечаний ушли.
///
/// <para><b>Сторож задачи.</b> Ломается он тем, что колонка без поля названа только тогда, когда строки
/// легли в счёт, — так было до #1484: строки ушли в предложение, и о колонке «Артикл» молчали.</para>
/// </summary>
public sealed partial class InvoiceFromScanTests
{
    private static UnplacedRead[] Unplaced(JsonElement recognition) =>
        recognition.GetProperty("unplaced").Deserialize<UnplacedRead[]>(InvoiceScanUnplaced.Json)!;

    /// <summary>Фразы, переехавшие в список: в замечаниях их больше нет.</summary>
    private static void NoMovedNotes(JsonElement recognition) =>
        Assert.DoesNotContain(recognition.GetProperty("notes").EnumerateArray(), n =>
            n.GetString()!.Contains("в счёт не положено") || n.GetString()!.Contains("распознаванием не заполняется")
            || n.GetString()!.Contains("два поля профиля"));

    private static UnplacedRead[] Country(params (int Row, string Value)[] rows) =>
        [.. rows.Select(r => InvoiceScanUnplaced.InLine(r.Row, "Страна", r.Value, InvoiceScanUnplaced.NoField))];

    [Fact]
    public async Task Колонка_без_поля_названа_и_когда_у_счёта_свои_строки()
    {
        var (client, _) = await SignInAsync("Supplier");
        var scan = Scan();
        var gate = Hold(scan, Read(Header(number: "СЧ-1484-1"),
            With(Row("Кабель", "м", "10", "5,00", "50,00"), ("Страна", "Россия")),
            With(Row("Труба", "м", "1", "7,00", "7,00"), ("Страна", "Китай"))));
        Guid id;
        try
        {
            id = (await FromScanAsync(client, scan)).GetProperty("invoice").GetProperty("id").GetGuid();
            await LinesAsync(client, id, [Line(cable, 3, 100)]);
        }
        finally
        {
            gate.SetResult();
        }

        var recognition = await OutcomeAsync(client, id);
        Assert.Equal(2, recognition.GetProperty("lines").GetArrayLength());
        Assert.Equal(Country((1, "Россия"), (2, "Китай")), Unplaced(recognition));
        NoMovedNotes(recognition);
    }

    /// <summary>Отмечено «сверху», бумага «в том числе» — строки не легли, а список тот же.</summary>
    [Fact]
    public async Task Колонка_без_поля_названа_и_когда_строки_не_легли_по_режиму_человека()
    {
        var (client, _) = await SignInAsync("Supplier");
        var scan = Scan();
        host.Recognition.On(scan, () => Task.FromException<ModuleRecognitionResult>(
            new RecognitionRefusedException(RecognitionRefusal.Unavailable, "Движок занят.")));
        var id = (await FromScanAsync(client, scan)).GetProperty("invoice").GetProperty("id").GetGuid();
        var failed = await OutcomeAsync(client, id);
        Assert.Equal("failed", failed.GetProperty("state").GetString());
        // Не завершено — списка нет: «не знаем», а не «пусто».
        Assert.Equal(JsonValueKind.Null, failed.GetProperty("unplaced").ValueKind);

        await OkAsync(await client.PutAsJsonAsync($"/api/costs/invoices/{id}/lines",
            new Dictionary<string, object?> { ["vatMode"] = "OnTop", ["lines"] = Array.Empty<object>() }));
        host.Recognition.On(scan, () => Task.FromResult(Read(Header(number: "СЧ-1484-2", total: "1 830,00", vat: "330,00"),
            Row("Кабель", "м", "10", "122,00", "1 220,00"),
            With(Row("Труба", "м", "5", "122,00", "610,00"), ("Страна", "Россия")))));
        await OkAsync(await client.PostAsync($"/api/costs/invoices/{id}/recognition", null));

        var recognition = await OutcomeAsync(client, id);
        Assert.Contains(recognition.GetProperty("notes").EnumerateArray(), n => n.GetString()!.Contains("суммы строк уже с НДС"));
        Assert.Equal(Country((2, "Россия")), Unplaced(recognition));
    }

    /// <summary>Таблица не прочиталась — список шапки всё равно хранится; пустой — пустым, а не «не знаем».</summary>
    [Fact]
    public async Task Список_хранится_и_при_непрочитанной_таблице()
    {
        var (client, _) = await SignInAsync("Supplier");
        var scan = Scan();
        var header = Header(number: "СЧ-1484-3");
        header["Основанье"] = "опечатка";
        host.Recognition.On(scan, () => Task.FromResult(new ModuleRecognitionResult(
            Fields, header, Columns, [], "таблица пришла не списком строк", "сценарий")));

        var id = (await FromScanAsync(client, scan)).GetProperty("invoice").GetProperty("id").GetGuid();
        var recognition = await OutcomeAsync(client, id);
        Assert.Equal([InvoiceScanUnplaced.InHeader("Основанье", "опечатка", InvoiceScanUnplaced.NoField)], Unplaced(recognition));
        NoMovedNotes(recognition);

        var clean = Scan();
        host.Recognition.On(clean, () => Task.FromResult(Read(Header(number: "СЧ-1484-4"))));
        var other = (await FromScanAsync(client, clean)).GetProperty("invoice").GetProperty("id").GetGuid();
        Assert.Empty(Unplaced(await OutcomeAsync(client, other)));
    }

    /// <summary>
    /// Поле занято человеком — «occupied», не разобрано — «unparsed»; замечание «впишите вручную» остаётся:
    /// оно зовёт к действию над счётом. Совпавшее с полем значение — не «занято»: предлагать нечего.
    /// </summary>
    [Fact]
    public async Task Занятое_и_неразобранное_поле_шапки_в_списке()
    {
        var (client, _) = await SignInAsync("Supplier");
        var scan = Scan();
        var gate = Hold(scan, Read(Header(number: "СЧ-1484-5", date: "05.04.2026", total: "1.234")));
        Guid id;
        try
        {
            id = (await FromScanAsync(client, scan)).GetProperty("invoice").GetProperty("id").GetGuid();
            var patched = await RequisitesWithAsync(client, id, "Номер", "РУЧНОЙ-1484");
            await OkAsync(await client.PutAsJsonAsync($"/api/costs/invoices/{id}", new { requisites = patched }));
        }
        finally
        {
            gate.SetResult();
        }

        var recognition = await OutcomeAsync(client, id);
        Assert.Equal(
        [
            InvoiceScanUnplaced.InHeader(CostsRecognitionProfiles.Total, "1.234", InvoiceScanUnplaced.Unparsed,
                field: InvoiceRequisites.TitleOf(InvoiceRequisites.TotalKey)),
            InvoiceScanUnplaced.InHeader(CostsRecognitionProfiles.Number, "СЧ-1484-5", InvoiceScanUnplaced.Occupied,
                field: InvoiceRequisites.TitleOf(InvoiceRequisites.NumberKey)),
        ], Unplaced(recognition).OrderByDescending(u => u.Reason));
        Assert.Contains(recognition.GetProperty("notes").EnumerateArray(), n => n.GetString()!.Contains("впишите вручную"));
    }

    /// <summary>
    /// Ключ профиля совпал с полем-файлом «Скан» — сквозь настоящие цели ядра (ревью PR #1576): запись с
    /// причиной поля и его подписью. Ломается тем, что ядро пометило поле иначе (например, входом), и
    /// запись пропала молча.
    /// </summary>
    [Fact]
    public async Task Поле_которое_распознавание_не_заполняет_в_списке_с_причиной()
    {
        var (client, _) = await SignInAsync("Supplier");
        var scan = Scan();
        var header = Header(number: "СЧ-1484-6");
        header[InvoiceRequisites.ScanKey] = "счёт.pdf";
        host.Recognition.On(scan, () => Task.FromResult(Read(header)));

        var id = (await FromScanAsync(client, scan)).GetProperty("invoice").GetProperty("id").GetGuid();
        var only = Assert.Single(Unplaced(await OutcomeAsync(client, id)));
        Assert.Equal(
            (InvoiceRequisites.ScanKey, InvoiceScanUnplaced.NotRecognizable, InvoiceRequisites.TitleOf(InvoiceRequisites.ScanKey)),
            (only.Key, only.Reason, only.Field));
        Assert.Contains("прикладывают", only.Detail);
    }
}
