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
using MediatR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
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
public class InvoiceLineTests(InvoiceLineHost host) : IClassFixture<InvoiceLineHost>, IAsyncLifetime
{
    private const string Password = "Test#12345";

    // Статические по той же причине, что у C1: xUnit создаёт новый экземпляр класса на каждый тест, а
    // посев (типы, организации, позиции номенклатуры) обязан случиться один раз.
    private static readonly SemaphoreSlim SeedGate = new(1, 1);
    private static Guid supplier;
    private static Guid payer;
    private static Guid cable;
    private static Guid conduit;

    /// <summary>
    /// Посев: типы «Организация» и «Номенклатура» заводит ЧЕЛОВЕК (первый) и миграция ядра там, где есть
    /// материалы (второй) — на чистой базе нет ни того, ни другого. Заводим оба, повторяем проекцию типов
    /// модуля (она идемпотентна) и кладём две организации и две позиции номенклатуры.
    /// </summary>
    public async Task InitializeAsync()
    {
        await SeedGate.WaitAsync();
        try
        {
            if (supplier != Guid.Empty) return;

            var organizations = await TypeAsync(CostsRecordTypes.OrganizationCode, "Организация");
            var nomenclature = await TypeAsync(CostsRecordTypes.NomenclatureCode, "Номенклатура");

            using (var scope = host.Services.CreateScope())
                await scope.ServiceProvider.ProjectModuleTypesAsync();

            supplier = await EntryAsync(organizations, "ООО «Кабель-Торг»");
            payer = await EntryAsync(organizations, "ООО «Наша компания»");
            cable = await EntryAsync(nomenclature, "Кабель ВВГнг-LS 3х2,5");
            conduit = await EntryAsync(nomenclature, "Труба гофрированная 20 мм");
        }
        finally
        {
            SeedGate.Release();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

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

    [Fact]
    public async Task Строка_без_позиции_живёт_и_счёт_виден_в_отборе_разобрать()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);

        var view = await LinesAsync(client, invoice, [
            Line(cable, quantity: 10, price: 100m),
            Line(null, quantity: 5, price: 20m, text: "Лоток металлический 100х50 (в справочнике нет)"),
        ]);

        var totals = view.GetProperty("totals");
        Assert.Equal(2, totals.GetProperty("count").GetInt32());
        Assert.Equal(1, totals.GetProperty("withoutNomenclature").GetInt32());
        Assert.Equal("Лоток металлический 100х50 (в справочнике нет)",
            view.GetProperty("lines")[1].GetProperty("supplierText").GetString());

        var queue = await client.GetFromJsonAsync<JsonElement>("/api/costs/invoices?needsParsing=true");
        var found = queue.EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == invoice);
        Assert.Equal(1, found.GetProperty("linesWithoutNomenclature").GetInt32());
        Assert.Equal(2, found.GetProperty("linesCount").GetInt32());
    }

    /// <summary>
    /// Счёт, у которого все строки разобраны, в очереди «Разобрать» не стоит — иначе очередь перестала
    /// бы быть очередью и стала бы вторым реестром.
    /// </summary>
    [Fact]
    public async Task Разобранные_строки_из_отбора_уходят()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);

        await LinesAsync(client, invoice, [Line(null, quantity: 1, price: 1m, text: "ждёт")]);
        var before = await client.GetFromJsonAsync<JsonElement>("/api/costs/invoices?needsParsing=true");
        Assert.Contains(invoice, before.EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));

        await LinesAsync(client, invoice, [Line(cable, quantity: 1, price: 1m)]);
        var after = await client.GetFromJsonAsync<JsonElement>("/api/costs/invoices?needsParsing=true");
        Assert.DoesNotContain(invoice, after.EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
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
        // Падеж — часть утверждения: «у строки 1», а не «у строка 1». Сообщение читает человек.
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
        Assert.Contains("нет в справочнике", await response.Content.ReadAsStringAsync());
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
    public async Task Разобран_отказывает_пока_строка_ждёт_позиции()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client, complete: true);

        await LinesAsync(client, invoice, [
            Line(cable, quantity: 1, price: 1m),
            Line(null, quantity: 1, price: 1m, text: "ждёт разбора"),
        ]);

        var response = await client.PostAsync($"/api/costs/invoices/{invoice}/parsed", null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("строка 2 ждёт позиции номенклатуры", text);
        Assert.Contains("Разобрать", text);

        // ⚠️ Проверяется ЦЕЛАЯ фраза, а не подстрока «строка 2». Первая версия сообщения склеивала
        // «ждут строки» с «строка 2» и выдавала «ждут строки строка 2» — подстрока в этой кашице есть,
        // и сторож её пропустил. Нашёл живой прогон, читающий текст глазами человека.
        Assert.DoesNotContain("строки строка", text);
    }

    [Fact]
    public async Task Разобран_отказывает_без_строк()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client, complete: true);

        var response = await client.PostAsync($"/api/costs/invoices/{invoice}/parsed", null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("строк нет", await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Обязательные поля проверяются ЗДЕСЬ, а не при сохранении (ТЗ COST-6.2): черновик без плательщика
    /// живёт, разобранный счёт — нет. Перечень берётся из объявления типа, а не переписан в коде.
    /// </summary>
    [Fact]
    public async Task Разобран_отказывает_без_обязательных_полей()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);

        await LinesAsync(client, invoice, [Line(cable, quantity: 1, price: 1m)]);

        var response = await client.PostAsync($"/api/costs/invoices/{invoice}/parsed", null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("«Плательщик»", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Разобран_проходит_и_состояние_видно_в_реестре()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client, complete: true);
        await LinesAsync(client, invoice, [Line(cable, quantity: 1, price: 100m, rate: 20)]);

        var response = await client.PostAsync($"/api/costs/invoices/{invoice}/parsed", null);
        await OkAsync(response);

        var view = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Разобран", view.GetProperty("requisites").GetProperty("Состояние").GetString());

        var list = await client.GetFromJsonAsync<JsonElement>("/api/costs/invoices");
        var item = list.EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == invoice);
        Assert.Equal("Разобран", item.GetProperty("state").GetString());
    }

    /// <summary>
    /// Правка строк, оставившая строку без позиции, САМА возвращает счёт в черновик — иначе «разобран»
    /// осталось бы утверждением, перестав быть правдой, и отбор «Разобрать» такой счёт не показал бы.
    /// </summary>
    [Fact]
    public async Task Правка_строк_возвращает_разобранный_счёт_в_черновик()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client, complete: true);
        await LinesAsync(client, invoice, [Line(cable, quantity: 1, price: 100m)]);
        await OkAsync(await client.PostAsync($"/api/costs/invoices/{invoice}/parsed", null));

        var view = await LinesAsync(client, invoice, [
            Line(cable, quantity: 1, price: 100m),
            Line(null, quantity: 1, price: 1m, text: "дописали строку, позиции ещё нет"),
        ]);

        Assert.Equal("Черновик", view.GetProperty("requisites").GetProperty("Состояние").GetString());

        var queue = await client.GetFromJsonAsync<JsonElement>("/api/costs/invoices?needsParsing=true");
        Assert.Contains(invoice, queue.EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task Возврат_в_черновик_решением_человека()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client, complete: true);
        await LinesAsync(client, invoice, [Line(cable, quantity: 1, price: 100m)]);
        await OkAsync(await client.PostAsync($"/api/costs/invoices/{invoice}/parsed", null));

        var response = await client.PostAsync($"/api/costs/invoices/{invoice}/draft", null);
        await OkAsync(response);

        var view = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Черновик", view.GetProperty("requisites").GetProperty("Состояние").GetString());
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
            var existing = await mediator.Send(new ListCommonDataRefsQuery([typeId], "Реле РЭК"));
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

    // ── Помощники ─────────────────────────────────────────────────────────────

    /// <summary>Строка счёта так, как её присылает форма.</summary>
    private static Dictionary<string, object?> Line(
        Guid? nomenclature, decimal quantity, decimal price, decimal? rate = null, string? text = null,
        decimal? amount = null, decimal? vat = null, Guid? id = null) =>
        new()
        {
            ["id"] = id?.ToString(),
            ["nomenclature"] = nomenclature is { } value ? Reference(value) : null,
            ["supplierText"] = text,
            ["quantity"] = quantity,
            ["price"] = price,
            ["vatRate"] = rate,
            ["vatAmount"] = vat,
            ["amount"] = amount,
        };

    private static Dictionary<string, object?> Reference(Guid id) =>
        new() { ["$ref"] = "catalog", ["entryId"] = id.ToString() };

    private static async Task<JsonElement> LinesAsync(
        HttpClient client, Guid invoice, object[] lines)
    {
        var response = await client.PutAsJsonAsync($"/api/costs/invoices/{invoice}/lines", new { lines });
        await OkAsync(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>
    /// Завести счёт. <paramref name="complete" /> — со всеми обязательными полями: такой счёт годится
    /// для перехода «разобран», а без них переход отказывает (и это отдельный тест).
    /// </summary>
    private static async Task<Guid> CreateAsync(HttpClient client, bool complete = false)
    {
        var requisites = new Dictionary<string, object?>
        {
            ["Номер"] = $"СЧ-{Guid.NewGuid().ToString()[..6]}",
            ["Дата"] = "2026-09-29",
            ["Поставщик"] = Reference(supplier),
            ["Плательщик"] = complete ? Reference(payer) : null,
        };

        var response = await client.PostAsJsonAsync("/api/costs/invoices", new { requisites });
        await OkAsync(response);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    /// <summary>Сколько записей «строки счёта изменены» стоит в журнале у этого счёта.</summary>
    private async Task<int> RecordsAsync(Guid invoice)
    {
        using var scope = host.Services.CreateScope();
        var journal = scope.ServiceProvider.GetRequiredService<IActivityLog>();
        var records = await journal.ReadAsync(0, 200, "costs.invoice.lines");
        return records.Count(r => r.TargetId == invoice.ToString());
    }

    private static async Task<JsonElement> ReadAsync(HttpClient client, Guid invoice) =>
        await client.GetFromJsonAsync<JsonElement>($"/api/costs/invoices/{invoice}");

    private static async Task OkAsync(HttpResponseMessage response) =>
        Assert.True(response.IsSuccessStatusCode,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

    private async Task<Guid> TypeAsync(string code, string name)
    {
        using var scope = host.Services.CreateScope();
        var types = scope.ServiceProvider.GetRequiredService<IRepository<DocumentType>>();

        var found = await types.FindAsync(t => t.Code == code);
        if (found.Count > 0) return found[0].Id;

        var created = DocumentType.Create(name, code, DocumentTypeKind.Composite, null,
            JsonDocument.Parse("""{"fields":[]}"""), TypeOwner.Core, TypeVisibility.Shared);
        await types.AddAsync(created);
        await types.SaveChangesAsync();
        return created.Id;
    }

    /// <summary>
    /// Запись справочника — ТАК, КАК ЕЁ ЗАВОДИТ ЭКРАН: общие данные (<c>domain_objects</c>). Сойдя с
    /// дороги экрана, тест снова начал бы подтверждать сам себя — ровно это и случилось в C1, когда
    /// помощник писал в таблицу прежней модели, из которой читал порт.
    /// </summary>
    /// <para>⚠️ Заводится, только если такой записи ещё нет. База между прогонами НЕ сбрасывается (как и
    /// у C1), а статические поля класса — да: посев без этой проверки на втором прогоне давал бы вторую
    /// «Трубу гофрированную», и тест поиска падал бы на дубле, которого в коде нет.</para>
    private async Task<Guid> EntryAsync(Guid typeId, string name)
    {
        using var scope = host.Services.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var known = await mediator.Send(new ListCommonDataRefsQuery([typeId], name));
        if (known.FirstOrDefault(r => r.DisplayName == name) is { } found) return found.Id;

        var created = await mediator.Send(new CreateCommonDataEntryCommand(name, typeId,
            JsonDocument.Parse($$"""{"Наименование":"{{name}}"}"""), CatalogScope.System, null, null));
        return created.Id;
    }

    private async Task<(HttpClient Client, Guid Id)> SignInAsync(string role)
    {
        var email = $"line_{Guid.NewGuid():N}@test.local";
        Guid id;

        using (var scope = host.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
            id = user.Id;
        }

        var client = host.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("accessToken").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (client, id);
    }
}

/// <summary>
/// Хост со включённым модулем счетов и своей базой (C2, issue #1078) — по той же причине, что у
/// <see cref="InvoiceHost" />: состав системных ролей приводится при старте к объявленному, и хост с
/// другим набором модулей менял бы права ролям у соседних классов.
/// </summary>
public sealed class InvoiceLineHost : IntegrationTestFixture
{
    private static string ConnectionString { get; } = Dedicated();

    private static string Dedicated()
    {
        var builder = new NpgsqlConnectionStringBuilder(TestConnectionString);
        builder.Database += "_lines";
        return builder.ConnectionString;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        var overrides = new Dictionary<string, string?>
        {
            ["Modules:Enabled"] = "id,costs",
            ["ConnectionStrings:Postgres"] = ConnectionString,
        };

        foreach (var (key, value) in overrides) builder.UseSetting(key, value);
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(overrides));
    }
}
