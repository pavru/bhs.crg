using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Modules;
using BHS.CRG.Application.Activity;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Costs;
using BHS.CRG.Modules.Costs.Data;
using MediatR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Строки счёта: позиция номенклатуры ссылкой, сверка сумм, переход «разобран» (задача C2 этапа 2,
/// issue #1078, ТЗ COST-7, COST-7.2, COST-6.2, COST-9).
///
/// <para>На живом хосте, как и C1: проверять надо именно то, что строки живут в схеме модуля, что
/// ссылка разрешается через справочник ЯДРА и что отказы доезжают до клиента кодами, а не исключениями.
/// Подделок портов здесь нет — с ними тест сходился бы сам с собой.</para>
///
/// <para>Своим файлом, а не дописано к <see cref="InvoiceRecordTests" />: тот и без того один из самых
/// длинных файлов решения, а файл-склад читают ЦЕЛИКОМ ради одной правки (храповик размера, #1041).</para>
/// </summary>
[Collection("Integration")]
public class InvoiceLineTests(InvoiceLineHost host) : InvoiceLineTestBase(host)
{
    [Fact]
    public async Task Строки_сохраняются_набором_и_приезжают_со_счётом()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);

        var view = await LinesAsync(client, invoice, [
            Line(cable, quantity: 100, price: 48.5m, rate: 20, text: "Кабель ВВГнг-LS 3х2.5"),
            Line(conduit, quantity: 50, price: 12m, rate: 20),
        ]);

        var lines = view.GetProperty("lines");
        Assert.Equal(2, lines.GetArrayLength());
        Assert.Equal([1, 2], lines.EnumerateArray().Select(l => l.GetProperty("ordinal").GetInt32()));
        Assert.Equal("Кабель ВВГнг-LS 3х2,5", lines[0].GetProperty("nomenclatureName").GetString());

        // Сумма строки не присылалась — её досчитал сервер: 100 × 48,50 и 50 × 12,00.
        Assert.Equal(4850m, lines[0].GetProperty("amount").GetDecimal());
        Assert.Equal(5450m, view.GetProperty("totals").GetProperty("amount").GetDecimal());

        // Счёт перечитан отдельным запросом: строки обязаны приезжать с ним, а не только в ответе записи.
        var reread = await ReadAsync(client, invoice);
        Assert.Equal(2, reread.GetProperty("lines").GetArrayLength());
    }

    /// <summary>
    /// Главный сторож задачи: наименование ТЕКСТОМ вместо ссылки на справочник — отказ (ТЗ COST-7).
    /// Разреши это — и строка учёта осталась бы словами поставщика, которые не сведёт ни один отчёт.
    /// </summary>
    [Fact]
    public async Task Наименование_текстом_вместо_позиции_отвергается()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);

        var response = await client.PutAsJsonAsync($"/api/costs/invoices/{invoice}/lines", new
        {
            lines = new object[] { new { nomenclature = "Кабель ВВГнг-LS 3х2,5", quantity = 100 } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("Позицию выбирают из справочника номенклатуры", text);
        // Отказ называет ПРИШЕДШЕЕ: «пришло String» отсылало бы человека искать, что именно он вписал.
        Assert.Contains("Кабель ВВГнг-LS 3х2,5", text);
    }

    /// <summary>
    /// Итог «в том числе НДС» считается ПО СТРОКАМ (сторож задачи). Ставка прислана, сумма НДС — нет.
    ///
    /// <para>⚠️ И считается именно «в том числе», а не «сверху»: у строки на 10 000 при ставке 20 % это
    /// 1666,67, а не 2000. Числа здесь выбраны так, чтобы два прочтения ставки давали РАЗНЫЕ ответы —
    /// на круглых 100 × 100 и 20 × 100 разницу было бы видно только в итоге.</para>
    /// </summary>
    [Fact]
    public async Task Итог_НДС_считается_по_строкам()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);

        var view = await LinesAsync(client, invoice, [
            Line(cable, quantity: 100, price: 100m, rate: 20),
            Line(conduit, quantity: 20, price: 100m, rate: 20),
        ]);

        Assert.Equal(1666.67m, view.GetProperty("lines")[0].GetProperty("vatAmount").GetDecimal());
        Assert.Equal(12000m, view.GetProperty("totals").GetProperty("amount").GetDecimal());
        Assert.Equal(2000m, view.GetProperty("totals").GetProperty("vat").GetDecimal());
    }

    /// <summary>
    /// Присланное не пересчитывается: бумага округляет по-своему, и наше «более правильное» число
    /// разошлось бы с тем, что человек видит на скане рядом.
    /// </summary>
    [Fact]
    public async Task Присланные_суммы_не_пересчитываются()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);

        var view = await LinesAsync(client, invoice, [
            Line(cable, quantity: 3, price: 33.33m, rate: 20, amount: 99.98m, vat: 16.66m),
        ]);

        var line = view.GetProperty("lines")[0];
        Assert.Equal(99.98m, line.GetProperty("amount").GetDecimal());
        Assert.Equal(16.66m, line.GetProperty("vatAmount").GetDecimal());
    }

    /// <summary>Числа строками — так их присылает и форма, и вставка из буфера («1 234,56»).</summary>
    [Fact]
    public async Task Числа_строками_разбираются()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);

        var response = await client.PutAsJsonAsync($"/api/costs/invoices/{invoice}/lines", new
        {
            lines = new object[]
            {
                new
                {
                    nomenclature = Reference(cable),
                    quantity = "7,25",
                    price = "1 234,56",
                    vatRate = "20",
                },
            },
        });
        await OkAsync(response);

        var line = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("lines")[0];
        Assert.Equal(7.25m, line.GetProperty("quantity").GetDecimal());
        Assert.Equal(1234.56m, line.GetProperty("price").GetDecimal());
        Assert.Equal(8950.56m, line.GetProperty("amount").GetDecimal());
    }

    [Fact]
    public async Task Ставка_вне_границ_отвергается()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);

        var response = await client.PutAsJsonAsync($"/api/costs/invoices/{invoice}/lines", new
        {
            lines = new object[] { new { nomenclature = Reference(cable), quantity = 1, vatRate = 2000 } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("не ставка НДС", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Ссылка_на_несуществующую_позицию_отвергается()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);

        var response = await client.PutAsJsonAsync($"/api/costs/invoices/{invoice}/lines", new
        {
            lines = new object[] { new { nomenclature = Reference(Guid.NewGuid()), quantity = 1 } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // Падеж — часть утверждения: «у строки 1», а не «у строка 1». Сообщение читает человек,
        // и по нему он решает, что делать; подстрока «нет в справочнике» это расхождение пропускала.
        Assert.Contains("нет в справочнике у строки 1", await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Организация — не номенклатура. Вид записи проверяется, иначе ссылкой на поставщика можно было бы
    /// «разобрать» строку, и отчёт по затратам собрал бы организацию как материал.
    /// </summary>
    [Fact]
    public async Task Ссылка_на_запись_чужого_вида_отвергается()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);

        var response = await client.PutAsJsonAsync($"/api/costs/invoices/{invoice}/lines", new
        {
            lines = new object[] { new { nomenclature = Reference(supplier), quantity = 1 } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("нет в справочнике у строки 1", await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Строка с идентификатором правится НА МЕСТЕ: на строку будет ссылаться разноска (F1), и
    /// пересоздание набора рвало бы ссылки на каждом сохранении формы.
    /// </summary>
    [Fact]
    public async Task Строка_с_идентификатором_правится_на_месте()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);

        var first = await LinesAsync(client, invoice, [
            Line(cable, quantity: 1, price: 10m),
            Line(conduit, quantity: 2, price: 20m),
        ]);
        var keptId = first.GetProperty("lines")[0].GetProperty("id").GetGuid();

        var second = await LinesAsync(client, invoice, [
            Line(cable, quantity: 5, price: 10m, id: keptId),
        ]);

        var line = Assert.Single(second.GetProperty("lines").EnumerateArray());
        Assert.Equal(keptId, line.GetProperty("id").GetGuid());
        Assert.Equal(5m, line.GetProperty("quantity").GetDecimal());
        Assert.Equal(1, second.GetProperty("totals").GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Строка_чужого_счёта_отвергается()
    {
        var (client, _) = await SignInAsync("Admin");
        var mine = await CreateAsync(client);
        var other = await CreateAsync(client);

        var theirs = (await LinesAsync(client, other, [Line(cable, quantity: 1, price: 1m)]))
            .GetProperty("lines")[0].GetProperty("id").GetGuid();

        var response = await client.PutAsJsonAsync($"/api/costs/invoices/{mine}/lines", new
        {
            lines = new object[] { new { id = theirs, nomenclature = Reference(cable), quantity = 1 } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("у этого счёта нет", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Поиск_номенклатуры_находит_по_части_названия()
    {
        var (client, _) = await SignInAsync("Admin");

        var found = await client.GetFromJsonAsync<JsonElement>("/api/costs/nomenclature?query=гофрир");
        var item = Assert.Single(found.GetProperty("items").EnumerateArray());
        Assert.Equal(conduit, item.GetProperty("id").GetGuid());
        Assert.False(found.GetProperty("more").GetBoolean());
    }

    /// <summary>
    /// Неполный ответ назван неполным. Молчание здесь дороже, чем кажется: человек прочтёт отсечение как
    /// «такой позиции нет» и заведёт вторую такую же — сводить затраты после этого придётся вручную.
    /// </summary>
    [Fact]
    public async Task Неполный_список_позиций_назван_неполным()
    {
        var (client, _) = await SignInAsync("Admin");

        using (var scope = host.Services.CreateScope())
        {
            var types = scope.ServiceProvider.GetRequiredService<IRepository<DocumentType>>();
            var typeId = (await types.FindAsync(t => t.Code == CostsRecordTypes.NomenclatureCode)).Single().Id;
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

            // Двадцать шесть — на одну больше, чем отдаёт адрес: ровно столько, чтобы «есть ещё» стало
            // фактом. Заводим один раз на класс — повторный прогон найдёт их уже посеянными.
            var existing = (await mediator.Send(new SearchCommonDataForChoiceQuery([typeId], "Реле РЭК"))).Items;
            for (var index = existing.Count; index < 26; index++)
                await mediator.Send(new CreateCommonDataEntryCommand($"Реле РЭК-{index:00}", typeId,
                    JsonDocument.Parse("{}"), CatalogScope.System, null, null));
        }

        var found = await client.GetFromJsonAsync<JsonElement>("/api/costs/nomenclature?query=Реле РЭК");
        Assert.Equal(25, found.GetProperty("items").GetArrayLength());
        Assert.True(found.GetProperty("more").GetBoolean());
    }

    /// <summary>
    /// Повторная отправка ТОГО ЖЕ набора в журнал не пишет.
    ///
    /// <para>Форма присылает строки целиком на каждое сохранение, и запись «строки изменены» без сверки
    /// с прежним состоянием появлялась бы там, где не изменилось ничего: журнал заполнился бы шумом, а
    /// настоящая правка в нём потерялась бы. Та же причина, по которой в C1 сохранение без правки не
    /// снимает метку.</para>
    /// </summary>
    [Fact]
    public async Task Повторная_отправка_того_же_набора_в_журнал_не_пишет()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);

        var created = await LinesAsync(client, invoice, [Line(cable, quantity: 3, price: 25m, rate: 20)]);
        Assert.Equal(1, await RecordsAsync(invoice));

        // ⚠️ С тем же «id» — так и присылает форма: она получила его в ответе. Тот же набор БЕЗ «id»
        // означал бы другое — «удали эти строки и заведи новые», — и запись в журнал была бы верна.
        var id = created.GetProperty("lines")[0].GetProperty("id").GetGuid();
        await LinesAsync(client, invoice, [Line(cable, quantity: 3, price: 25m, rate: 20, id: id)]);
        Assert.Equal(1, await RecordsAsync(invoice));

        // А настоящая правка — пишется: проверка «записей не прибавилось» зелена и у журнала, который
        // не пишет вовсе.
        await LinesAsync(client, invoice, [Line(cable, quantity: 4, price: 25m, rate: 20, id: id)]);
        Assert.Equal(2, await RecordsAsync(invoice));
    }

    [Fact]
    public async Task Чтение_счетов_не_даёт_права_править_строки()
    {
        var (admin, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(admin);

        var (user, _) = await SignInAsync("User");
        var response = await user.PutAsJsonAsync($"/api/costs/invoices/{invoice}/lines",
            new { lines = Array.Empty<object>() });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// Единица и артикул длиннее своих колонок — отказ с именем поля, а НЕ пятисотый.
    ///
    /// <para>⚠️ Сторож находки ревью, и попасть туда проще, чем кажется: длинную единицу приносит не
    /// опечатка, а вставка из буфера — колонка «Предмет поставки» угадывается единицей, и в поле
    /// ложится фраза на семьдесят знаков. Перебор ловил PostgreSQL (22001) внутри
    /// <c>SaveChangesAsync</c>, где доменных отказов не бывает: наружу уходило «внутренняя ошибка
    /// сервера» без единого слова о поле. Проверено живьём до правки — ровно 500.</para>
    /// </summary>
    [Theory]
    [InlineData("unit", InvoiceLine.UnitLength, "Единица измерения")]
    [InlineData("supplierCode", InvoiceLine.SupplierCodeLength, "Артикул поставщика")]
    public async Task Текст_длиннее_колонки_отказывает_с_именем_поля(string key, int limit, string label)
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);

        var line = Line(cable, quantity: 1, price: 10m);
        line[key] = new string('ш', limit + 1);

        var response = await client.PutAsJsonAsync($"/api/costs/invoices/{invoice}/lines",
            new { lines = new object[] { line } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains(label, text);
        // Номер строки, предел и присланная длина: без них человек не знает ни где, ни насколько.
        Assert.Contains("строка 1", text);
        Assert.Contains($"{limit + 1} знаков", text);
        Assert.Contains($"вмещается {limit}", text);
    }

    /// <summary>
    /// Позиция БЕЗ НАЗВАНИЯ потерянной не считается.
    ///
    /// <para>⚠️ Сторож находки ревью. Запись справочника без имени — состояние законное и живое (в
    /// рабочей базе такие есть), а пикер такие позиции показывает. Форма же различала случаи по
    /// пустому названию: выбранная только что позиция краснела как «не найдена», кнопка снятия у такой
    /// клетки скрыта — и строка не сохранялась вовсе, потому что ссылку было нечем убрать. Разницу
    /// знает только сервер, он её и присылает.</para>
    /// </summary>
    [Fact]
    public async Task Позиция_без_названия_потерянной_не_считается()
    {
        var (client, _) = await SignInAsync("Admin");
        var nameless = await NamelessAsync();
        var invoice = await CreateAsync(client);

        var view = await LinesAsync(client, invoice, [Line(nameless, quantity: 1, price: 10m)]);

        var line = view.GetProperty("lines")[0];
        Assert.Equal(JsonValueKind.Null, line.GetProperty("nomenclatureName").ValueKind);
        Assert.False(line.GetProperty("nomenclatureLost").GetBoolean());
    }

    /// <summary>
    /// Позиция УДАЛЕНА из справочника — это потеря, и она названа потерей. Ссылка при этом остаётся:
    /// стирать её за человека нельзя, он единственный, кто знает, чем заменить.
    /// </summary>
    [Fact]
    public async Task Удалённая_позиция_названа_потерянной()
    {
        var (client, _) = await SignInAsync("Admin");
        var doomed = await EntryAsync(await TypeAsync(CostsRecordTypes.NomenclatureCode, "Номенклатура"),
            $"Позиция под удаление {Guid.NewGuid().ToString()[..6]}");
        var invoice = await CreateAsync(client);
        await LinesAsync(client, invoice, [Line(doomed, quantity: 1, price: 10m)]);

        await ForgetAsync(doomed);

        var line = (await ReadAsync(client, invoice)).GetProperty("lines")[0];
        Assert.Equal(doomed, line.GetProperty("nomenclatureId").GetGuid());
        Assert.True(line.GetProperty("nomenclatureLost").GetBoolean());
    }
}
