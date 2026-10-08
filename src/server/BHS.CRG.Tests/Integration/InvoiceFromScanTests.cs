using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BHS.CRG.Application.Activity;
using BHS.CRG.Domain.Recognition;
using BHS.CRG.Modules.Costs;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Путь «скан → черновик счёта» (ТЗ COST-8, COST-6.2; задача B1b, issue #1077).
///
/// <para><b>Сторож задачи:</b> распознанные поля помечены до подтверждения, а неудача даёт названную
/// причину — не пустой черновик. Ломается он подстановкой пустых значений вместо отказа: тогда тест
/// <see cref="Отказ_распознавания_называет_причину_и_не_трогает_счёт" /> видит «прочитано» там, где
/// движок не ответил.</para>
///
/// <para>Проверяется через НАСТОЯЩУЮ очередь: черновик заводит адрес, читает скан фоновая задача, а
/// тест ждёт исхода тем же опросом, что и форма.</para>
/// </summary>
public sealed class InvoiceFromScanTests(InvoiceScanHost host)
    : InvoiceLineTestBase(host), IClassFixture<InvoiceScanHost>
{
    private static readonly string[] Fields =
    [
        CostsRecognitionProfiles.Number, CostsRecognitionProfiles.Date, CostsRecognitionProfiles.Supplier,
        CostsRecognitionProfiles.SupplierTaxId, CostsRecognitionProfiles.Payer, CostsRecognitionProfiles.PayerTaxId,
        CostsRecognitionProfiles.Basis, CostsRecognitionProfiles.Total, CostsRecognitionProfiles.VatTotal,
    ];

    private static readonly string[] Columns =
    [
        CostsRecognitionProfiles.LineName, CostsRecognitionProfiles.LineUnit, CostsRecognitionProfiles.LineQuantity,
        CostsRecognitionProfiles.LinePrice, CostsRecognitionProfiles.LineAmount,
    ];

    // ── Сторожа задачи ────────────────────────────────────────────────────────

    [Fact]
    public async Task Скан_даёт_черновик_с_помеченными_полями_и_строками()
    {
        var (client, _) = await SignInAsync("Supplier");
        var scan = Scan();
        host.Recognition.On(scan, () => Task.FromResult(Read(
            Header(number: "СЧ-417", date: "12 марта 2026 г.", total: "12 000,00 руб.", vat: "2 000,00",
                basis: "Договор № 5 от 01.02.2026", supplier: "ООО «Кабель-Торг»", supplierTaxId: "7701234567"),
            Row("Кабель ВВГнг-LS 3х2,5", "м", "100", "100,00", "10 000,00"),
            Row("Труба гофр. 20", "м", "4", "500,00", "2 000,00"))));

        var created = await FromScanAsync(client, scan, "Счёт 417.pdf");
        var id = created.GetProperty("invoice").GetProperty("id").GetGuid();

        // Черновик заведён СРАЗУ и уже со сканом: распознавание — помощь, а не условие.
        Assert.Equal("Счёт 417.pdf",
            created.GetProperty("invoice").GetProperty("requisites").GetProperty("Скан").GetProperty("fileName").GetString());
        Assert.Contains(created.GetProperty("recognition").GetProperty("state").GetString(), (string[])["running", "done"]);

        var recognition = await OutcomeAsync(client, id);
        Assert.Equal("done", recognition.GetProperty("state").GetString());
        Assert.Equal("сценарий", recognition.GetProperty("engine").GetString());

        var view = await ReadAsync(client, id);
        var requisites = view.GetProperty("requisites");
        Assert.Equal("СЧ-417", requisites.GetProperty("Номер").GetString());
        Assert.Equal("2026-03-12", requisites.GetProperty("Дата").GetString());
        Assert.Equal("Договор № 5 от 01.02.2026", requisites.GetProperty("Назначение").GetString());
        Assert.Equal(12000m, requisites.GetProperty("Итого").GetDecimal());
        Assert.Equal(2000m, requisites.GetProperty("ВТомЧислеНДС").GetDecimal());
        Assert.Equal("Черновик", requisites.GetProperty("Состояние").GetString());

        // Главное: всё, что вписал не человек, помечено — и ничего сверх того.
        Assert.Equal(["ВТомЧислеНДС", "Дата", "Итого", "Назначение", "Номер"], Unconfirmed(view));

        var lines = view.GetProperty("lines");
        Assert.Equal(2, lines.GetArrayLength());
        Assert.Equal("Кабель ВВГнг-LS 3х2,5", lines[0].GetProperty("supplierText").GetString());
        Assert.Equal(100m, lines[0].GetProperty("quantity").GetDecimal());
        Assert.Equal(10000m, lines[0].GetProperty("amount").GetDecimal());
        // Номенклатуру выбирает человек: строка ждёт в очереди «Разобрать», а не сведена наугад.
        Assert.Equal(JsonValueKind.Null, lines[0].GetProperty("nomenclatureId").ValueKind);

        // Стороны в счёт не вписаны (сопоставление — отдельной частью задачи), но прочитанное не потеряно.
        Assert.Equal(JsonValueKind.Null, requisites.GetProperty("Поставщик").ValueKind);
        Assert.Equal("7701234567",
            recognition.GetProperty("values").GetProperty(CostsRecognitionProfiles.SupplierTaxId).GetString());

        Assert.Equal(1, await JournalAsync(id, "costs.invoice.recognized"));
    }

    /// <summary>
    /// ⚠️ Тот самый тест, ради которого сторож. Движок не ответил — и счёт обязан остаться таким, каким
    /// был: ни поля, ни метки, ни строки, ни новой версии. «Пустые значения вместо отказа» выглядели бы
    /// как done с пустыми полями, и человек принял бы пустой черновик за прочитанный счёт без номера.
    /// </summary>
    [Fact]
    public async Task Отказ_распознавания_называет_причину_и_не_трогает_счёт()
    {
        var (client, _) = await SignInAsync("Supplier");
        var scan = Scan();
        host.Recognition.On(scan, () => Task.FromException<ModuleRecognitionResult>(new RecognitionRefusedException(
            RecognitionRefusal.Unavailable, "Движок распознавания не ответил: превышено время ожидания.")));

        var created = await FromScanAsync(client, scan);
        var id = created.GetProperty("invoice").GetProperty("id").GetGuid();
        var version = created.GetProperty("invoice").GetProperty("version").GetString();

        var recognition = await OutcomeAsync(client, id);
        Assert.Equal("failed", recognition.GetProperty("state").GetString());
        Assert.Equal("Unavailable", recognition.GetProperty("reason").GetString());
        Assert.Contains("превышено время ожидания", recognition.GetProperty("error").GetString());
        Assert.True(recognition.GetProperty("canStart").GetBoolean());
        Assert.Equal(JsonValueKind.Null, recognition.GetProperty("values").ValueKind);

        var view = await ReadAsync(client, id);
        Assert.Equal(JsonValueKind.Null, view.GetProperty("requisites").GetProperty("Номер").ValueKind);
        Assert.Empty(Unconfirmed(view));
        Assert.Equal(0, view.GetProperty("lines").GetArrayLength());
        Assert.Equal(version, view.GetProperty("version").GetString());
        Assert.Equal(0, await JournalAsync(id, "costs.invoice.recognized"));
    }

    /// <summary>«Прочитал, но пусто» — тоже отказ, и порт называет его сам (NoAnswer).</summary>
    [Fact]
    public async Task Непрочитанный_документ_отличим_от_недоступного_движка()
    {
        var (client, _) = await SignInAsync("Supplier");
        var scan = Scan(); // сценария для файла нет — подменный порт отвечает NoAnswer

        var id = (await FromScanAsync(client, scan)).GetProperty("invoice").GetProperty("id").GetGuid();

        var recognition = await OutcomeAsync(client, id);
        Assert.Equal("failed", recognition.GetProperty("state").GetString());
        Assert.Equal("NoAnswer", recognition.GetProperty("reason").GetString());
    }

    /// <summary>
    /// «Распознавать некому» — не отказ запроса: черновик со сканом заведён, а форма получает причину,
    /// по которой его придётся заполнить руками (ТЗ COST-8: «не настроено» различимо).
    /// </summary>
    [Fact]
    public async Task Ненастроенное_распознавание_не_мешает_завести_черновик_и_названо()
    {
        var (client, _) = await SignInAsync("Supplier");
        host.Recognition.NotReady = new RecognitionRefusedException(
            RecognitionRefusal.NotConfigured, "Распознавание не настроено: движок не выбран.");
        try
        {
            var created = await FromScanAsync(client, Scan(), "Счёт 9.pdf");

            var recognition = created.GetProperty("recognition");
            Assert.Equal("failed", recognition.GetProperty("state").GetString());
            Assert.Equal("NotConfigured", recognition.GetProperty("reason").GetString());
            Assert.Contains("не настроено", recognition.GetProperty("error").GetString());

            var view = await ReadAsync(client, created.GetProperty("invoice").GetProperty("id").GetGuid());
            Assert.Equal("Счёт 9.pdf", view.GetProperty("requisites").GetProperty("Скан").GetProperty("fileName").GetString());
        }
        finally
        {
            host.Recognition.NotReady = null;
        }
    }

    // ── Распознанное — предложение ────────────────────────────────────────────

    /// <summary>
    /// Пока скан читается, человек вправе заполнять черновик. Его значение побеждает: занятое поле не
    /// затирается и не помечается, а прочитанное уходит в предложения.
    /// </summary>
    [Fact]
    public async Task Поле_заполненное_человеком_не_затирается_а_прочитанное_предложено()
    {
        var (client, _) = await SignInAsync("Supplier");
        var scan = Scan();
        var gate = Hold(scan, Read(Header(number: "СЧ-500", date: "05.04.2026")));
        Guid id;
        try
        {
            id = (await FromScanAsync(client, scan)).GetProperty("invoice").GetProperty("id").GetGuid();
            Assert.Equal("running", (await RecognitionAsync(client, id)).GetProperty("state").GetString());

            // Правка формы во время распознавания проходит: постановка версию счёта не двигала.
            var patched = await RequisitesWithAsync(client, id, "Номер", "РУЧНОЙ-1");
            await OkAsync(await client.PutAsJsonAsync($"/api/costs/invoices/{id}", new { requisites = patched }));
        }
        finally
        {
            gate.SetResult();
        }

        var recognition = await OutcomeAsync(client, id);
        Assert.Equal("done", recognition.GetProperty("state").GetString());
        Assert.Equal("СЧ-500", recognition.GetProperty("offers").GetProperty("Номер").GetString());

        var view = await ReadAsync(client, id);
        Assert.Equal("РУЧНОЙ-1", view.GetProperty("requisites").GetProperty("Номер").GetString());
        Assert.Equal("2026-04-05", view.GetProperty("requisites").GetProperty("Дата").GetString());
        Assert.Equal(["Дата"], Unconfirmed(view));
    }

    /// <summary>
    /// Свои строки у счёта уже есть — распознанные сами не добавляются (какие из них дубль, знает
    /// только человек), но и не пропадают: лежат предложением.
    /// </summary>
    [Fact]
    public async Task Строки_добавляются_только_в_счёт_без_строк()
    {
        var (client, _) = await SignInAsync("Supplier");
        var scan = Scan();
        var gate = Hold(scan, Read(Header(number: "СЧ-501"),
            Row("Кабель", "м", "10", "5,00", "50,00"), Row("Труба", "м", "1", "7,00", "7,00")));
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
        Assert.Equal("done", recognition.GetProperty("state").GetString());
        Assert.Equal(2, recognition.GetProperty("lines").GetArrayLength());
        Assert.Contains(recognition.GetProperty("notes").EnumerateArray(), n => n.GetString()!.Contains("не добавлены"));

        var view = await ReadAsync(client, id);
        var line = Assert.Single(view.GetProperty("lines").EnumerateArray());
        Assert.Equal(cable, line.GetProperty("nomenclatureId").GetGuid());
        Assert.Equal("СЧ-501", view.GetProperty("requisites").GetProperty("Номер").GetString());
    }

    /// <summary>
    /// Значение, которое не читается однозначно, в поле не попадает: «1.234» — тысяча двести тридцать
    /// четыре или один и двести тридцать четыре тысячных? Неверно угаданная сумма выглядит как верная.
    /// </summary>
    [Fact]
    public async Task Неоднозначная_сумма_остаётся_предложением_а_поле_пустым()
    {
        var (client, _) = await SignInAsync("Supplier");
        var scan = Scan();
        host.Recognition.On(scan, () => Task.FromResult(Read(
            Header(number: "СЧ-502", date: "01.02.26", total: "1.234"),
            Row("Кабель", "м", "1.234", "1.250", "12,34"))));

        var id = (await FromScanAsync(client, scan)).GetProperty("invoice").GetProperty("id").GetGuid();
        var recognition = await OutcomeAsync(client, id);

        Assert.Equal("done", recognition.GetProperty("state").GetString());
        Assert.Equal("1.234", recognition.GetProperty("offers").GetProperty("Итого").GetString());
        Assert.Equal("01.02.26", recognition.GetProperty("offers").GetProperty("Дата").GetString());

        var view = await ReadAsync(client, id);
        Assert.Equal(JsonValueKind.Null, view.GetProperty("requisites").GetProperty("Итого").ValueKind);
        Assert.Equal(JsonValueKind.Null, view.GetProperty("requisites").GetProperty("Дата").ValueKind);
        Assert.Equal(["Номер"], Unconfirmed(view));

        // В строке — то же правило: непрочитанное число названо в примечании, а не угадано.
        var line = Assert.Single(view.GetProperty("lines").EnumerateArray());
        Assert.Equal(1.234m, line.GetProperty("quantity").GetDecimal());
        Assert.Equal(JsonValueKind.Null, line.GetProperty("price").ValueKind);
        // Цена — деньгами: «1.250» в графе цены бывает и тысячей двести пятьюдесятью, а количество
        // «1.234» читается дробью. Один и тот же вид числа, разные правила — и это не случайность.
        Assert.Contains("цена «1.250»", line.GetProperty("note").GetString());
    }

    /// <summary>
    /// Пределы строки — те же, что у формы: длина, точность колонки. Форма на нарушение отвечает
    /// отказом; здесь значение уходит в примечание, а не округляется молча и не роняет распознавание
    /// отказом базы вместе с шапкой.
    /// </summary>
    [Fact]
    public async Task Значение_строки_которое_не_помещается_уходит_в_примечание()
    {
        var (client, _) = await SignInAsync("Supplier");
        var scan = Scan();
        var unit = new string('м', InvoiceLine.UnitLength + 1);
        host.Recognition.On(scan, () => Task.FromResult(Read(Header(number: "СЧ-506"),
            Row("Кабель", unit, "0,1255", "45,678", "5,73"),
            Row("Труба", "м", "2", "10,50", "9999999999999,00"),
            new Dictionary<string, string?> { [CostsRecognitionProfiles.LineName] = " ", [CostsRecognitionProfiles.LineUnit] = null })));

        var id = (await FromScanAsync(client, scan)).GetProperty("invoice").GetProperty("id").GetGuid();
        var recognition = await OutcomeAsync(client, id);
        Assert.Equal("done", recognition.GetProperty("state").GetString());

        var view = await ReadAsync(client, id);
        Assert.Equal("СЧ-506", view.GetProperty("requisites").GetProperty("Номер").GetString());

        // Пустая строка скана строкой счёта не стала.
        var lines = view.GetProperty("lines");
        Assert.Equal(2, lines.GetArrayLength());

        var first = lines[0];
        Assert.Equal(JsonValueKind.Null, first.GetProperty("unit").ValueKind);
        Assert.Equal(JsonValueKind.Null, first.GetProperty("quantity").ValueKind);
        Assert.Equal(JsonValueKind.Null, first.GetProperty("price").ValueKind);
        Assert.Equal(5.73m, first.GetProperty("amount").GetDecimal());
        var note = first.GetProperty("note").GetString()!;
        Assert.Contains("количество «0,1255»", note);
        Assert.Contains("цена «45,678»", note);
        Assert.Contains($"длиннее {InvoiceLine.UnitLength} знаков", note);

        // Сумма больше, чем здесь бывает, не записана — и НЕ подменена посчитанной 2 × 10,50: посчитанное
        // выглядело бы как прочитанное, а в скане стоит другое.
        var second = lines[1];
        Assert.Contains("сумма «9999999999999,00»", second.GetProperty("note").GetString());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("amount").ValueKind);
    }

    /// <summary>Таблица не прочиталась — шапка всё равно ложится, а причина названа, а не «строк нет».</summary>
    [Fact]
    public async Task Непрочитанная_таблица_названа_а_шапка_заполнена()
    {
        var (client, _) = await SignInAsync("Supplier");
        var scan = Scan();
        host.Recognition.On(scan, () => Task.FromResult(new ModuleRecognitionResult(
            Fields, Header(number: "СЧ-503"), Columns, [], "таблица пришла не списком строк", "сценарий")));

        var id = (await FromScanAsync(client, scan)).GetProperty("invoice").GetProperty("id").GetGuid();
        var recognition = await OutcomeAsync(client, id);

        Assert.Equal("done", recognition.GetProperty("state").GetString());
        Assert.Contains(recognition.GetProperty("notes").EnumerateArray(),
            n => n.GetString()!.Contains("таблица пришла не списком строк"));
        Assert.Equal("СЧ-503", (await ReadAsync(client, id)).GetProperty("requisites").GetProperty("Номер").GetString());
    }

    // ── Повтор, замена скана, прерванная задача ───────────────────────────────

    /// <summary>
    /// Прежний скан читается — заменить его нельзя: прочитанное легло бы в счёт от бумаги, которой у
    /// него уже нет. И второй запуск по тому же файлу — отказ.
    /// </summary>
    [Fact]
    public async Task Пока_скан_распознаётся_его_не_заменить_и_второй_раз_не_запустить()
    {
        var (client, _) = await SignInAsync("Supplier");
        var scan = Scan();
        var gate = Hold(scan, Read(Header(number: "СЧ-504")));
        Guid id;
        try
        {
            id = (await FromScanAsync(client, scan)).GetProperty("invoice").GetProperty("id").GetGuid();

            using var form = Form(Scan(), "Другой.pdf", "application/pdf");
            var replaced = await client.PostAsync($"/api/costs/invoices/{id}/scan", form);
            Assert.Equal(HttpStatusCode.Conflict, replaced.StatusCode);
            Assert.Contains("распознаётся", await replaced.Content.ReadAsStringAsync());

            var again = await client.PostAsync($"/api/costs/invoices/{id}/recognition", null);
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
            Assert.Contains("уже распознаётся", await again.Content.ReadAsStringAsync());
        }
        finally
        {
            gate.SetResult();
        }

        Assert.Equal("done", (await OutcomeAsync(client, id)).GetProperty("state").GetString());
    }

    /// <summary>
    /// Второе нажатие, прошедшее проверку раньше, чем первое сохранило номер задачи: очередь отвечает
    /// «уже выполняется». Это отказ второму — и ничего больше: запись, по которой работает первая
    /// задача, остаётся её записью, и первая дочитывает скан.
    /// </summary>
    [Fact]
    public async Task Второй_запуск_не_отменяет_работу_первого()
    {
        var (client, _) = await SignInAsync("Supplier");
        var scan = Scan();
        var gate = Hold(scan, Read(Header(number: "СЧ-507")));
        Guid id;
        try
        {
            id = (await FromScanAsync(client, scan)).GetProperty("invoice").GetProperty("id").GetGuid();

            // То самое окно: запись ждёт исхода, а номера задачи в ней ещё (или уже) нет.
            using (var scope = host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<CostsDbContext>();
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE costs.invoice_recognitions SET job_id = NULL WHERE invoice_id = {0}", id);
            }

            var again = await client.PostAsync($"/api/costs/invoices/{id}/recognition", null);
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
            Assert.Contains("уже распознаётся", await again.Content.ReadAsStringAsync());
        }
        finally
        {
            gate.SetResult();
        }

        var recognition = await OutcomeAsync(client, id, wait: "failed");
        Assert.Equal("done", recognition.GetProperty("state").GetString());
        Assert.Equal("СЧ-507", (await ReadAsync(client, id)).GetProperty("requisites").GetProperty("Номер").GetString());
    }

    /// <summary>
    /// Правка формы пришла одновременно с исходом распознавания, и слияние проиграло ей запись.
    /// Прочитанное не выбрасывается: счёт перечитывается, и то же прочитанное раскладывается заново —
    /// уже мимо поля, которое человек только что заполнил. Иначе за тот же файл платили бы движку дважды.
    /// </summary>
    [Fact]
    public async Task Слияние_проигравшее_правке_формы_повторяется_а_не_выбрасывает_прочитанное()
    {
        var (client, _) = await SignInAsync("Supplier");
        var scan = Scan();
        var gate = Hold(scan, Read(Header(number: "СЧ-508", basis: "Договор № 8")));
        Guid id;
        try
        {
            id = (await FromScanAsync(client, scan)).GetProperty("invoice").GetProperty("id").GetGuid();
            host.BetweenReadAndSave = async () =>
            {
                using var scope = host.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<CostsDbContext>();
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE costs.invoices SET purpose = 'вписано рукой' WHERE id = {0}", id);
            };
        }
        finally
        {
            gate.SetResult();
        }

        var recognition = await OutcomeAsync(client, id);
        Assert.Null(host.BetweenReadAndSave); // помеха действительно случилась
        Assert.Equal("done", recognition.GetProperty("state").GetString());
        Assert.Equal("Договор № 8", recognition.GetProperty("offers").GetProperty("Назначение").GetString());

        var view = await ReadAsync(client, id);
        Assert.Equal("вписано рукой", view.GetProperty("requisites").GetProperty("Назначение").GetString());
        Assert.Equal("СЧ-508", view.GetProperty("requisites").GetProperty("Номер").GetString());
        Assert.Equal(["Номер"], Unconfirmed(view));
    }

    /// <summary>
    /// Скан заменили ПОСЛЕ распознавания: предложения и строки прежнего файла под полями нового счёта
    /// показывать нельзя — запись о другой бумаге читается как «не запускалось».
    /// </summary>
    [Fact]
    public async Task После_замены_скана_прочитанное_с_прежнего_не_показывается()
    {
        var (client, _) = await SignInAsync("Supplier");
        var scan = Scan();
        host.Recognition.On(scan, () => Task.FromResult(Read(Header(number: "СЧ-509", total: "1.234"))));

        var id = (await FromScanAsync(client, scan)).GetProperty("invoice").GetProperty("id").GetGuid();
        Assert.Equal("1.234", (await OutcomeAsync(client, id)).GetProperty("offers").GetProperty("Итого").GetString());

        using (var form = Form(Scan(), "Другой.pdf", "application/pdf"))
            await OkAsync(await client.PostAsync($"/api/costs/invoices/{id}/scan", form));

        var recognition = await RecognitionAsync(client, id);
        Assert.Equal("none", recognition.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, recognition.GetProperty("offers").ValueKind);
        Assert.True(recognition.GetProperty("canStart").GetBoolean());
    }

    /// <summary>
    /// После отказа распознавание запускают ещё раз — по уже приложенному скану. А повтор по
    /// прочитанному счёту меток не стирает: поля, заполненные в прошлый раз, ещё не подтверждены.
    /// </summary>
    [Fact]
    public async Task Повтор_после_отказа_читает_скан_заново_и_метки_не_стирает()
    {
        var (client, _) = await SignInAsync("Supplier");
        var scan = Scan();
        host.Recognition.On(scan, () => Task.FromException<ModuleRecognitionResult>(
            new RecognitionRefusedException(RecognitionRefusal.Unavailable, "Движок занят.") { RetryAfterSeconds = 30 }));

        var id = (await FromScanAsync(client, scan)).GetProperty("invoice").GetProperty("id").GetGuid();
        Assert.Equal("failed", (await OutcomeAsync(client, id)).GetProperty("state").GetString());

        host.Recognition.On(scan, () => Task.FromResult(Read(Header(number: "СЧ-505", date: "2026-05-06"))));
        await OkAsync(await client.PostAsync($"/api/costs/invoices/{id}/recognition", null));
        Assert.Equal("done", (await OutcomeAsync(client, id)).GetProperty("state").GetString());
        Assert.Equal(["Дата", "Номер"], Unconfirmed(await ReadAsync(client, id)));

        // Третий запуск: поля заняты тем же самым — предлагать нечего, метки на месте.
        await OkAsync(await client.PostAsync($"/api/costs/invoices/{id}/recognition", null));
        var recognition = await OutcomeAsync(client, id);
        Assert.Equal("done", recognition.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, recognition.GetProperty("offers").ValueKind);
        Assert.Equal(["Дата", "Номер"], Unconfirmed(await ReadAsync(client, id)));
    }

    /// <summary>
    /// Запись ждёт исхода, а задачи нет (сервер перезапустили, задача упала мимо нас). Отвечать «идёт»
    /// значило бы лгать навсегда: это отказ, и запустить можно снова.
    /// </summary>
    [Fact]
    public async Task Распознавание_без_живой_задачи_названо_прерванным()
    {
        var (client, _) = await SignInAsync("Supplier");
        var id = await CreateAsync(client);
        using (var form = Form(Scan(), "Счёт.pdf", "application/pdf"))
            await OkAsync(await client.PostAsync($"/api/costs/invoices/{id}/scan", form));

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CostsDbContext>();
            var path = (await db.Invoices.AsNoTracking().SingleAsync(i => i.Id == id)).ScanBlobPath!;
            db.InvoiceRecognitions.Add(InvoiceRecognition.Start(id, path));
            await db.SaveChangesAsync();
        }

        var recognition = await RecognitionAsync(client, id);
        Assert.Equal("failed", recognition.GetProperty("state").GetString());
        Assert.Equal("Interrupted", recognition.GetProperty("reason").GetString());
        Assert.True(recognition.GetProperty("canStart").GetBoolean());

        // И замене скана прерванное распознавание не мешает.
        using var other = Form(Scan(), "Другой.pdf", "application/pdf");
        await OkAsync(await client.PostAsync($"/api/costs/invoices/{id}/scan", other));
    }

    // ── Отказы адреса ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("text/plain", "содержимое", "PDF, PNG или JPEG")]
    [InlineData("application/pdf", "", "Файл пуст")]
    public async Task Негодный_файл_отказ_и_черновик_не_заведён(string mime, string body, string expected)
    {
        var (client, _) = await SignInAsync("Supplier");
        var name = $"negodnyj-{Guid.NewGuid():N}.bin";

        using var form = Form(body, name, mime);
        var response = await client.PostAsync("/api/costs/invoices/from-scan", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(expected, await response.Content.ReadAsStringAsync());

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CostsDbContext>();
        Assert.False(await db.Invoices.AnyAsync(i => i.ScanFileName == name));
    }

    [Fact]
    public async Task Счёт_без_скана_распознать_нельзя_и_причина_названа_заранее()
    {
        var (client, _) = await SignInAsync("Supplier");
        var id = await CreateAsync(client);

        var recognition = await RecognitionAsync(client, id);
        Assert.Equal("none", recognition.GetProperty("state").GetString());
        Assert.False(recognition.GetProperty("canStart").GetBoolean());
        Assert.Contains("не приложен скан", recognition.GetProperty("whyNot").GetString());

        var started = await client.PostAsync($"/api/costs/invoices/{id}/recognition", null);
        Assert.Equal(HttpStatusCode.Conflict, started.StatusCode);
        Assert.Contains("не приложен скан", await started.Content.ReadAsStringAsync());
    }

    // ── Помощники ─────────────────────────────────────────────────────────────

    /// <summary>Содержимое «скана» — у каждого теста своё: по нему подменный порт выбирает ответ.</summary>
    internal static string Scan() => $"%PDF-скан {Guid.NewGuid():N}";

    /// <summary>
    /// Ответ, который ждёт разрешения теста: распознавание остаётся «идёт», пока тест делает то, что
    /// человек делает за формой. Отпускать — в <c>finally</c>: очередь у хоста одна.
    /// </summary>
    private TaskCompletionSource Hold(string scan, ModuleRecognitionResult result)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Recognition.On(scan, async () =>
        {
            await gate.Task;
            return result;
        });
        return gate;
    }

    internal static Dictionary<string, string?> Header(
        string? number = null, string? date = null, string? total = null, string? vat = null, string? basis = null,
        string? supplier = null, string? supplierTaxId = null, string? payer = null, string? payerTaxId = null) =>
        new()
        {
            [CostsRecognitionProfiles.Number] = number,
            [CostsRecognitionProfiles.Date] = date,
            [CostsRecognitionProfiles.Supplier] = supplier,
            [CostsRecognitionProfiles.SupplierTaxId] = supplierTaxId,
            [CostsRecognitionProfiles.Payer] = payer,
            [CostsRecognitionProfiles.PayerTaxId] = payerTaxId,
            [CostsRecognitionProfiles.Basis] = basis,
            [CostsRecognitionProfiles.Total] = total,
            [CostsRecognitionProfiles.VatTotal] = vat,
        };

    private static IReadOnlyDictionary<string, string?> Row(
        string name, string unit, string quantity, string price, string amount) =>
        new Dictionary<string, string?>
        {
            [CostsRecognitionProfiles.LineName] = name,
            [CostsRecognitionProfiles.LineUnit] = unit,
            [CostsRecognitionProfiles.LineQuantity] = quantity,
            [CostsRecognitionProfiles.LinePrice] = price,
            [CostsRecognitionProfiles.LineAmount] = amount,
        };

    internal static ModuleRecognitionResult Read(
        Dictionary<string, string?> header, params IReadOnlyDictionary<string, string?>[] rows) =>
        new(Fields, header, Columns, rows, null, "сценарий");

    private static MultipartFormDataContent Form(string body, string fileName, string mime)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        file.Headers.ContentType = new MediaTypeHeaderValue(mime);
        form.Add(file, "file", fileName);
        return form;
    }

    internal static async Task<JsonElement> FromScanAsync(HttpClient client, string scan, string fileName = "Счёт.pdf")
    {
        using var form = Form(scan, fileName, "application/pdf");
        var response = await client.PostAsync("/api/costs/invoices/from-scan", form);
        await OkAsync(response);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    internal static Task<JsonElement> RecognitionAsync(HttpClient client, Guid id) =>
        client.GetFromJsonAsync<JsonElement>($"/api/costs/invoices/{id}/recognition");

    /// <summary>Дождаться исхода — тем же опросом, каким его ждёт форма.</summary>
    /// <param name="wait">Состояние, которое тоже НЕ исход: тест, сам стёрший номер задачи, видит
    /// «failed / прервано», пока задача ещё работает.</param>
    internal static async Task<JsonElement> OutcomeAsync(HttpClient client, Guid id, string? wait = null)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            var recognition = await RecognitionAsync(client, id);
            var state = recognition.GetProperty("state").GetString();
            if (state != "running" && state != wait) return recognition;

            Assert.True(DateTime.UtcNow < deadline, $"Распознавание счёта {id} так и не закончилось.");
            await Task.Delay(50);
        }
    }

    internal static string[] Unconfirmed(JsonElement view) =>
        [.. view.GetProperty("unconfirmed").EnumerateArray().Select(k => k.GetString()!).Order(StringComparer.Ordinal)];

    private async Task<int> JournalAsync(Guid invoice, string action)
    {
        using var scope = host.Services.CreateScope();
        var journal = scope.ServiceProvider.GetRequiredService<IActivityLog>();
        var records = await journal.ReadAsync(0, 200, ActivityVisibility.Whole, action);
        return records.Count(r => r.TargetId == invoice.ToString());
    }
}
