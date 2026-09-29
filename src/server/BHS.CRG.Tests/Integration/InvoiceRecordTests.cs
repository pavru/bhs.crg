using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BHS.CRG.Api.Modules;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Costs;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Счёт как запись модуля: хранение, адреса и метки «распознано, не подтверждено» (задача C1 этапа 2,
/// issue #1076, ТЗ COST-6, COST-6.2, COST-9, CORE-15, CORE-20.1).
///
/// <para>Проверяется на ЖИВОМ хосте с включённым модулем: таблица в схеме модуля, свой тип, права на
/// каждом адресе. Подделок здесь нет — ни контекста, ни портов, — потому что доказывать надо именно
/// то, что счёт живёт в схеме модуля и виден только через его адреса.</para>
/// </summary>
[Collection("Integration")]
public class InvoiceRecordTests(InvoiceHost host) : IClassFixture<InvoiceHost>, IAsyncLifetime
{
    private const string Password = "Test#12345";

    // ⚠️ Статические, и потому что так надо: xUnit создаёт НОВЫЙ экземпляр класса на каждый тест, а
    // посев (тип «Организация», проекция, две организации) обязан случиться один раз. Хост у этого
    // класса свой и единственный, так что делить их не с кем.
    private static readonly SemaphoreSlim SeedGate = new(1, 1);
    private static Guid supplier;
    private static Guid payer;

    /// <summary>
    /// Тип «Организация» ядро само не заводит: он существует у заказчика потому, что его когда-то
    /// завёл человек. На чистой базе его нет — значит нет и цели у полей «Поставщик» и «Плательщик», и
    /// тип счёта при старте пропускается с записью в журнал (так и задумано, см.
    /// <c>ModuleTypeProjector</c>).
    ///
    /// <para>Поэтому здесь: заводим «Организацию», повторяем проекцию — она идемпотентна — и только
    /// после этого счёт можно завести. Заодно это показывает, что пропуск при отсутствии цели
    /// обратим: появилась цель, появился и тип.</para>
    /// </summary>
    public async Task InitializeAsync()
    {
        await SeedGate.WaitAsync();
        try
        {
            if (supplier != Guid.Empty) return;
            await SeedAsync();
        }
        finally
        {
            SeedGate.Release();
        }
    }

    /// <summary>
    /// ⚠️ База НЕ сбрасывается перед каждым тестом, и это решение. Сброс ждёт фоновых задач и чистит
    /// таблицы, то есть на шестнадцать тестов приходится шестнадцать таких ожиданий — а рядом идут
    /// тесты, которые заводят и удаляют БАЗЫ (перепись миграций), и тесноты они не переносят: их отказ
    /// приходит таймаутом, виноватой выглядит база, а не сосед. Сбрасывать здесь и нечего: счета
    /// заводятся с разными номерами, а читаются по идентификатору — чужие строки им не мешают.
    /// </summary>
    private async Task SeedAsync()
    {
        using var scope = host.Services.CreateScope();
        var types = scope.ServiceProvider.GetRequiredService<IRepository<DocumentType>>();

        if ((await types.FindAsync(t => t.Code == CostsRecordTypes.OrganizationCode)).Count == 0)
        {
            await types.AddAsync(DocumentType.Create(
                "Организация", CostsRecordTypes.OrganizationCode, DocumentTypeKind.Composite, null,
                JsonDocument.Parse("""{"fields":[]}"""), TypeOwner.Core, TypeVisibility.Shared));
            await types.SaveChangesAsync();
        }

        await scope.ServiceProvider.ProjectModuleTypesAsync();

        supplier = await OrganizationAsync("ООО «Кабель-Торг»");
        payer = await OrganizationAsync("ООО «Наша компания»");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── Сторожа задачи ────────────────────────────────────────────────────────

    /// <summary>
    /// Черновик БЕЗ строк сохраняется, читается и виден в списке (ТЗ COST-6.2).
    ///
    /// <para>Это признак готовности задачи, и он же — единственная защита от самого правдоподобного
    /// решения: потребовать строки при сохранении. Счёт приезжает сканом, его заводит фоновое
    /// распознавание, и человек открывает его потом — запрети мы сохранение без строк, черновику
    /// негде было бы жить.</para>
    /// </summary>
    [Fact]
    public async Task Черновик_без_строк_сохраняется_читается_и_виден_в_списке()
    {
        var (client, _) = await SignInAsync("Supplier");

        var created = await CreateAsync(client, Requisites(number: "СЧ-14"));

        var id = created.GetProperty("id").GetGuid();
        Assert.Equal("Черновик", created.GetProperty("requisites").GetProperty("Состояние").GetString());
        Assert.Equal("Не оплачен", created.GetProperty("requisites").GetProperty("СостояниеОплаты").GetString());

        var read = await client.GetFromJsonAsync<JsonElement>($"/api/costs/invoices/{id}");
        Assert.Equal("СЧ-14", read.GetProperty("requisites").GetProperty("Номер").GetString());

        var list = await client.GetFromJsonAsync<JsonElement>("/api/costs/invoices");
        var row = list.EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == id);
        Assert.Equal("СЧ-14", row.GetProperty("number").GetString());
        Assert.Equal("ООО «Кабель-Торг»", row.GetProperty("supplierName").GetString());
        Assert.False(row.GetProperty("hasScan").GetBoolean());
    }

    /// <summary>
    /// Суммы счёта лежат в схеме МОДУЛЯ, а не в общей таблице (ТЗ COST-29, COST-30; первое условие
    /// задачи H1, issue #1104).
    ///
    /// <para>Проверяется по МЕСТУ ХРАНЕНИЯ, а не перечнем путей чтения. Довод записан в H1 и стоит
    /// повторения: путей чтения общей таблицы тридцать три, ни один не отбирает по модулю-владельцу
    /// типа, и перечень такой длины расходится молча. Инвариант хранения не расходится — поэтому
    /// сторож здесь смотрит на носитель типа и на то, что общий путь такую запись заводить
    /// отказывается.</para>
    ///
    /// <para>⚠️ Полная H1 остаётся за собой: перепись обращений к наборам контекста модуля, журнал под
    /// одним <c>core.audit.read</c> и живой прогон по новым путям этапа. Здесь — только то, что
    /// приехало вместе с первыми суммами, и приехало тем же PR, как H1 и требует.</para>
    /// </summary>
    [Fact]
    public async Task Суммы_счёта_хранит_модуль_а_не_общая_таблица()
    {
        using var scope = host.Services.CreateScope();
        var types = scope.ServiceProvider.GetRequiredService<IRepository<DocumentType>>();
        var invoice = (await types.FindAsync(t => t.Code == CostsRecordTypes.InvoiceCode)).Single();

        Assert.Equal(TypeStorage.ModuleTable, invoice.Storage);
        Assert.Equal("costs", invoice.Module);

        // Денежные тэги есть, и они на полях типа, который хранит МОДУЛЬ. Тот же тэг на типе из общей
        // таблицы — то, что H1 обязана запретить; здесь доказывается вторая половина: у наших полей
        // носитель правильный.
        var schema = invoice.Schema.RootElement.GetProperty("fields").EnumerateArray().ToList();
        var total = schema.Single(f => f.GetProperty("key").GetString() == "Итого");
        Assert.Contains(CostsRecordTypes.TotalTag,
            total.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));

        // Общим путём такой объект не заводится — 409 с названием модуля (TypeStorageRules).
        var (admin, _) = await SignInAsync("Admin");
        var refused = await admin.PostAsJsonAsync("/api/common-data", new
        {
            compositeTypeId = invoice.Id,
            displayName = "Счёт мимо модуля",
            data = "{}",
        });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
    }

    /// <summary>
    /// Тип счёта спроецирован: системные поля на месте, у составных полей — цель из ядра
    /// (ТЗ CORE-20.2, CORE-30).
    ///
    /// <para>Цель названа КОДОМ в объявлении модуля, а в схеме обязана стоять идентификатором: код в
    /// каждой установке один, а идентификатор свой. Не разрешись он — поле не нарисовалось бы и не
    /// заполнилось.</para>
    /// </summary>
    [Fact]
    public async Task Тип_счёта_получил_системные_поля_и_разрешённые_цели()
    {
        using var scope = host.Services.CreateScope();
        var types = scope.ServiceProvider.GetRequiredService<IRepository<DocumentType>>();
        var invoice = (await types.FindAsync(t => t.Code == CostsRecordTypes.InvoiceCode)).Single();
        var organization = (await types.FindAsync(t => t.Code == CostsRecordTypes.OrganizationCode)).Single();

        var fields = invoice.Schema.RootElement.GetProperty("fields").EnumerateArray()
            .ToDictionary(f => f.GetProperty("key").GetString()!, f => f);

        var supplierField = fields["Поставщик"];
        Assert.Equal(organization.Id.ToString(), supplierField.GetProperty("typeId").GetString());
        Assert.Equal("module", supplierField.GetProperty("origin").GetString());

        // Состояния запирает модуль: их кладёт код, и охрана записи не даёт тронуть их руками.
        Assert.True(fields["Состояние"].GetProperty("locked").GetBoolean());

        // Номер человек заполняет сам — значит поле заведено модулем, но не заперто. Разница обязана
        // быть явной: запертое обязательное поле, которого код не заполняет, не заполнит никто.
        Assert.False(fields["Номер"].GetProperty("locked").GetBoolean());
    }

    /// <summary>
    /// Метки «распознано, не подтверждено» переживают повторное открытие черновика — ГЛАВНЫЙ сторож
    /// решения от 29.09.2026.
    ///
    /// <para>Прежняя формулировка («распознанные поля помечены до подтверждения») была зелёной на
    /// клиентском механизме, который не переживает даже перезагрузку страницы: метка лежала в
    /// состоянии компонента. А черновик создаёт ФОНОВАЯ задача, и человек открывает его потом — то
    /// есть в том единственном сценарии, ради которого метка заводится, её не существовало.</para>
    /// </summary>
    [Fact]
    public async Task Метки_распознанного_переживают_повторное_чтение()
    {
        var (client, _) = await SignInAsync("Supplier");

        var created = await CreateAsync(client, Requisites(number: "СЧ-15"), ["Номер", "Итого"]);
        var id = created.GetProperty("id").GetGuid();

        var read = await client.GetFromJsonAsync<JsonElement>($"/api/costs/invoices/{id}");

        Assert.Equal(["Итого", "Номер"], Unconfirmed(read));
    }

    /// <summary>
    /// Правка поля снимает метку с ЭТОГО поля и не снимает с соседних (решение 29.09.2026, п. 1).
    /// </summary>
    [Fact]
    public async Task Правка_поля_снимает_метку_только_с_него()
    {
        var (client, _) = await SignInAsync("Supplier");
        var id = (await CreateAsync(client, Requisites(number: "СЧ-16"), ["Номер", "Итого"]))
            .GetProperty("id").GetGuid();

        var updated = await UpdateAsync(client, id, Requisites(number: "СЧ-16-исправлен"));

        Assert.Equal(["Итого"], Unconfirmed(updated));
    }

    /// <summary>
    /// Сохранение БЕЗ правки метку не снимает (решение 29.09.2026, п. 2).
    ///
    /// <para>На вид противоестественно — сохранил же, — и именно поэтому стоит сторожем: <c>D4</c>
    /// сохраняет черновик до того, как его увидел человек. Снимай метки сохранение, распознанный счёт
    /// приезжал бы уже «проверенным».</para>
    ///
    /// <para>Сравниваются ЗНАЧЕНИЯ, а не текст JSON: форма присылает счёт целиком, и «пришло ли поле»
    /// верно для всех полей сразу.</para>
    /// </summary>
    [Fact]
    public async Task Сохранение_без_правки_метку_не_снимает()
    {
        var (client, _) = await SignInAsync("Supplier");
        var created = await CreateAsync(client, Requisites(number: "СЧ-17", total: 1234.50m),
            ["Номер", "Итого", "Дата"]);
        var id = created.GetProperty("id").GetGuid();

        // Возвращаем ровно то, что отдал сервер: так делает форма, открытая и сохранённая без правок.
        var same = created.GetProperty("requisites").Deserialize<JsonElement>();
        var updated = await UpdateAsync(client, id, Writable(same));

        Assert.Equal(["Дата", "Итого", "Номер"], Unconfirmed(updated));
    }

    /// <summary>
    /// «Всё верно» снимает метки названного блока и не трогает поля соседнего (решение 29.09.2026).
    ///
    /// <para>Поля блока перечисляет КЛИЕНТ: блок — видимая группа формы, и сервер о ней не знает.
    /// Придумай он группы сам, «Всё верно» подтверждало бы поля, которых человек на экране не
    /// видел.</para>
    /// </summary>
    [Fact]
    public async Task Всё_верно_снимает_метки_блока_и_не_трогает_соседний()
    {
        var (client, _) = await SignInAsync("Supplier");
        var id = (await CreateAsync(client, Requisites(number: "СЧ-18", total: 900m),
                ["Номер", "Дата", "Итого"]))
            .GetProperty("id").GetGuid();

        var response = await client.PostAsJsonAsync($"/api/costs/invoices/{id}/confirmed",
            new { fields = new[] { "Номер", "Дата" } });
        response.EnsureSuccessStatusCode();

        Assert.Equal(["Итого"], Unconfirmed(await response.Content.ReadFromJsonAsync<JsonElement>()));
    }

    /// <summary>
    /// «Всё верно» без перечня полей — отказ, а не «снять все».
    ///
    /// <para>Подтверждение ничего — промах клиента, и самое дорогое из прочтений промаха здесь именно
    /// «снять все»: метки исчезли бы разом, а вернуть их было бы нечем.</para>
    /// </summary>
    [Fact]
    public async Task Подтверждение_без_перечня_полей_отказывает()
    {
        var (client, _) = await SignInAsync("Supplier");
        var id = (await CreateAsync(client, Requisites(number: "СЧ-19"), ["Номер"]))
            .GetProperty("id").GetGuid();

        var response = await client.PostAsJsonAsync($"/api/costs/invoices/{id}/confirmed",
            new { fields = Array.Empty<string>() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var still = await client.GetFromJsonAsync<JsonElement>($"/api/costs/invoices/{id}");
        Assert.Equal(["Номер"], Unconfirmed(still));
    }

    /// <summary>
    /// Метки правкой счёта НЕ задаются: их ставит тот, кто заполнил поля, а снимает правка или
    /// «Всё верно». Иначе форма, приславшая метки заново, возвращала бы снятые.
    /// </summary>
    [Fact]
    public async Task Метки_правкой_счёта_не_задаются()
    {
        var (client, _) = await SignInAsync("Supplier");
        var id = (await CreateAsync(client, Requisites(number: "СЧ-20"))).GetProperty("id").GetGuid();

        var response = await client.PutAsJsonAsync($"/api/costs/invoices/{id}",
            new { requisites = Requisites(number: "СЧ-20"), unconfirmed = new[] { "Номер" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// Состояния правкой не задаются: их двигают действия (ТЗ COST-9). Отказ, а не тихий пропуск —
    /// пропущенное значение выглядело бы записанным.
    /// </summary>
    [Fact]
    public async Task Состояние_счёта_правкой_не_задаётся()
    {
        var (client, _) = await SignInAsync("Supplier");
        var id = (await CreateAsync(client, Requisites(number: "СЧ-21"))).GetProperty("id").GetGuid();

        var response = await client.PutAsJsonAsync($"/api/costs/invoices/{id}", new
        {
            requisites = new Dictionary<string, object?> { ["Номер"] = "СЧ-21", ["Состояние"] = "Разобран" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// Дубликат «поставщик + номер + дата» назван, но сохранение ПРОХОДИТ (ТЗ COST-6.2).
    ///
    /// <para>Оговорка, а не запрет: у поставщика бывает два счёта с одним номером в один день, и
    /// человек знает об этом больше нас. Поэтому и индекс в таблице не уникальный.</para>
    /// </summary>
    [Fact]
    public async Task Дубликат_назван_а_сохранение_проходит()
    {
        var (client, _) = await SignInAsync("Supplier");

        var first = await CreateAsync(client, Requisites(number: "СЧ-22"));
        var second = await CreateAsync(client, Requisites(number: "СЧ-22"));

        var duplicates = second.GetProperty("duplicates").EnumerateArray().ToList();
        Assert.Single(duplicates);
        Assert.Equal(first.GetProperty("id").GetGuid(), duplicates[0].GetProperty("id").GetGuid());
        Assert.Equal("СЧ-22", duplicates[0].GetProperty("number").GetString());
    }

    /// <summary>
    /// Скан прикладывается и читается тем же правом, что счёт. Хранилище — ядра, портом: два
    /// хранилища в продукте означали бы две резервные копии, из которых сходится одна.
    /// </summary>
    [Fact]
    public async Task Скан_прикладывается_и_читается()
    {
        var (client, _) = await SignInAsync("Supplier");
        var id = (await CreateAsync(client, Requisites(number: "СЧ-23"))).GetProperty("id").GetGuid();

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes("%PDF-1.4 скан"));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", "Счёт 22.pdf");

        var attached = await client.PostAsync($"/api/costs/invoices/{id}/scan", form);
        attached.EnsureSuccessStatusCode();

        var view = await attached.Content.ReadFromJsonAsync<JsonElement>();
        var scan = view.GetProperty("requisites").GetProperty("Скан");
        Assert.Equal("file", scan.GetProperty("$type").GetString());
        Assert.Equal("Счёт 22.pdf", scan.GetProperty("fileName").GetString());

        var content = await client.GetAsync($"/api/costs/invoices/{id}/scan");
        content.EnsureSuccessStatusCode();
        Assert.Contains("скан", await content.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Замена скана убирает прежний файл из хранилища.
    ///
    /// <para>Иначе каждая замена оставляла бы файл, на который никто не ссылается: заметно это стало бы
    /// по счёту за место, а объяснить накопленное было бы нечем. Удаление — ПОСЛЕ сохранения записи о
    /// новом: наоборот, отказ сохранения оставил бы счёт со ссылкой на файл, которого больше нет.</para>
    /// </summary>
    [Fact]
    public async Task Замена_скана_убирает_прежний_файл()
    {
        var (client, _) = await SignInAsync("Supplier");
        var id = (await CreateAsync(client, Requisites(number: "СЧ-26"))).GetProperty("id").GetGuid();

        var first = await AttachAsync(client, id, "Первый.pdf", "старый скан");
        var second = await AttachAsync(client, id, "Второй.pdf", "новый скан");
        Assert.NotEqual(first, second);

        using var scope = host.Services.CreateScope();
        var blobs = scope.ServiceProvider.GetRequiredService<IModuleBlobs>();

        Assert.Null(await blobs.SizeAsync(first));
        Assert.NotNull(await blobs.SizeAsync(second));
    }

    /// <summary>
    /// Ключ, за которым стоит колонка, в <c>data</c> не попадает — у значения один источник.
    ///
    /// <para>Проверяется в БАЗЕ, а не в ответе: ответ собирает тот же код, который раскладывает
    /// значения, и сошёлся бы сам с собой при любой ошибке. Расхождение двух источников тихое —
    /// форма показала бы одно, отбор реестра нашёл бы другое.</para>
    /// </summary>
    [Fact]
    public async Task Колонка_в_данных_схемы_не_дублируется()
    {
        var (client, _) = await SignInAsync("Supplier");
        var id = (await CreateAsync(client, Requisites(number: "СЧ-24", basis: "Договор 7")))
            .GetProperty("id").GetGuid();

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CostsDbContext>();
        var invoice = await db.Invoices.AsNoTracking().SingleAsync(i => i.Id == id);

        Assert.Equal("СЧ-24", invoice.Number);
        Assert.Equal(supplier, invoice.SupplierId);

        var data = invoice.Data.RootElement;
        Assert.False(data.TryGetProperty("Номер", out _));
        Assert.False(data.TryGetProperty("Поставщик", out _));

        // А то, за чем колонки нет, живёт именно здесь: основание переименовать можно безнаказанно —
        // код на него не опирается (ТЗ CORE-15).
        Assert.Equal("Договор 7", data.GetProperty("Основание").GetString());
    }

    /// <summary>
    /// Значение не того вида отвергает ОХРАНА ЗАПИСИ ядра, а не модуль (ТЗ CORE-20).
    ///
    /// <para>Зачем модулю спрашивать то, что ядро проверяет само: ядро проверяет пути, которые ведёт
    /// оно, а запись в таблице модуля оно не сохраняет и проверить не может. Правила при этом те же —
    /// и заказчик вправе дописать в этот тип свои поля (уровень «расширяемый»), значения которых иначе
    /// не проверял бы никто.</para>
    ///
    /// <para>Поле выбрано то, что живёт в схеме, а не в колонке: колонки модуль разбирает сам и своим
    /// отказом, и охрану на них было бы не видно.</para>
    /// </summary>
    [Fact]
    public async Task Значение_не_того_вида_отвергает_охрана_записи()
    {
        var (client, _) = await SignInAsync("Supplier");

        var requisites = new Dictionary<string, object?>
        {
            ["Номер"] = "СЧ-27",
            ["Основание"] = 42,
        };

        var response = await client.PostAsJsonAsync("/api/costs/invoices", new { requisites });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Основание", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Чтение счетов не даёт права их заводить (ТЗ COST-28): у бухгалтера есть
    /// <c>costs.invoice.read</c> и нет <c>costs.invoice.edit</c>.
    ///
    /// <para>Ворота модуля тут ни при чём — они у бухгалтера открыты. Проверяется право НА АДРЕСЕ:
    /// поверь мы воротам, запись была бы открыта каждому, у кого есть модуль.</para>
    /// </summary>
    [Fact]
    public async Task Чтение_счетов_не_даёт_права_их_заводить()
    {
        var (accountant, _) = await SignInAsync("Accountant");

        var read = await accountant.GetAsync("/api/costs/invoices");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        var written = await accountant.PostAsJsonAsync("/api/costs/invoices",
            new { requisites = Requisites(number: "СЧ-25") });
        Assert.Equal(HttpStatusCode.Forbidden, written.StatusCode);
    }

    /// <summary>
    /// Право на накладные счетов не открывает (ТЗ COST-29): роль с одним
    /// <c>costs.waybill.read</c> получает доступ к модулю — и отказ на счетах.
    ///
    /// <para>Роль заводится здесь руками, потому что ни одна системная роль такого состава не имеет:
    /// у снабженца есть и то и другое. А различить «ворота модуля» от «права на адресе» можно только
    /// на том, у кого модуль есть, а права нет.</para>
    /// </summary>
    [Fact]
    public async Task Право_на_накладные_счетов_не_открывает()
    {
        var (admin, _) = await SignInAsync("Admin");
        var created = await admin.PostAsJsonAsync("/api/roles", new
        {
            title = $"Кладовщик {Guid.NewGuid():N}",
            summary = "Только накладные — для проверки COST-29",
            permissions = new[] { "costs.waybill.read" },
        });
        created.EnsureSuccessStatusCode();
        var role = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString()!;

        var (storekeeper, _) = await SignInAsync(role);

        var response = await storekeeper.GetAsync("/api/costs/invoices");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ── Помощники ─────────────────────────────────────────────────────────────

    private object Requisites(string? number = null, decimal? total = null, string? basis = null) =>
        new Dictionary<string, object?>
        {
            ["Номер"] = number,
            ["Дата"] = "2026-09-03",
            ["Поставщик"] = Reference(supplier),
            ["Плательщик"] = Reference(payer),
            ["Итого"] = total,
            ["Основание"] = basis,
        };

    /// <summary>
    /// Ссылка на запись справочника — так её хранит ядро (<c>BaseRefReader</c>). Словарём, а не
    /// анонимным объектом: имени свойства с «$» в C# не бывает, а имя в JSON обязано быть ровно таким.
    /// </summary>
    private static Dictionary<string, object?> Reference(Guid id) =>
        new() { ["$ref"] = "catalog", ["entryId"] = id.ToString() };

    /// <summary>
    /// То же, что вернул сервер, но без полей, которые правкой не задаются: форма их не присылает.
    /// </summary>
    private static Dictionary<string, JsonElement> Writable(JsonElement requisites) =>
        requisites.EnumerateObject()
            .Where(p => p.Name is not ("Состояние" or "СостояниеОплаты" or "Скан"))
            .ToDictionary(p => p.Name, p => p.Value);

    private static async Task<JsonElement> CreateAsync(
        HttpClient client, object requisites, string[]? unconfirmed = null)
    {
        var response = await client.PostAsJsonAsync("/api/costs/invoices",
            new { requisites, unconfirmed });
        await OkAsync(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> UpdateAsync(HttpClient client, Guid id, object requisites)
    {
        var response = await client.PutAsJsonAsync($"/api/costs/invoices/{id}", new { requisites });
        await OkAsync(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Приложить скан и вернуть путь, по которому он лёг в хранилище.</summary>
    private static async Task<string> AttachAsync(
        HttpClient client, Guid id, string fileName, string body)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", fileName);

        var response = await client.PostAsync($"/api/costs/invoices/{id}/scan", form);
        await OkAsync(response);

        var view = await response.Content.ReadFromJsonAsync<JsonElement>();
        return view.GetProperty("requisites").GetProperty("Скан").GetProperty("blobPath").GetString()!;
    }

    /// <summary>
    /// Успех — с ТЕКСТОМ отказа, если его нет. <c>EnsureSuccessStatusCode</c> прячет причину: падение
    /// говорит «400», а что именно не понравилось серверу — приходится выяснять отдельным прогоном.
    /// </summary>
    private static async Task OkAsync(HttpResponseMessage response) =>
        Assert.True(response.IsSuccessStatusCode,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

    private static string[] Unconfirmed(JsonElement view) =>
        [.. view.GetProperty("unconfirmed").EnumerateArray().Select(k => k.GetString()!).Order(StringComparer.Ordinal)];

    private async Task<Guid> OrganizationAsync(string name)
    {
        using var scope = host.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRepository<CatalogEntity>>();

        // Вид записи — КОД ТИПА, как в живой базе («Организация»), а не английское имя: по нему
        // модуль и ищет поставщиков.
        var entity = CatalogEntity.Create(CostsRecordTypes.OrganizationCode, name,
            JsonDocument.Parse($$"""{"Наименование":"{{name}}"}"""));
        await repo.AddAsync(entity);
        await repo.SaveChangesAsync();
        return entity.Id;
    }

    private async Task<(HttpClient Client, Guid Id)> SignInAsync(string role)
    {
        var email = $"inv_{Guid.NewGuid():N}@test.local";
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
/// Хост со включённым модулем счетов и своей базой (задача C1, issue #1076).
///
/// <para>Своя база — по той же причине, что у <see cref="CostsOnlyHost" /> и
/// <see cref="ModulePortsHost" />: состав системных ролей приводится при старте к объявленному, и хост
/// с другим набором модулей менял бы права ролям у соседних классов. Падали бы они, а причину искали
/// бы у них.</para>
/// </summary>
public sealed class InvoiceHost : IntegrationTestFixture
{
    private static string ConnectionString { get; } = Dedicated();

    private static string Dedicated()
    {
        var builder = new NpgsqlConnectionStringBuilder(TestConnectionString);
        builder.Database += "_invoices";
        return builder.ConnectionString;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        var overrides = new Dictionary<string, string?>
        {
            // Оба модуля: счёт ссылается на справочник организаций ЯДРА, а роли и права в базе
            // приводятся к составу поставки — хост без `id` снял бы у ролей права исполнительной
            // документации.
            ["Modules:Enabled"] = "id,costs",
            ["ConnectionStrings:Postgres"] = ConnectionString,
        };

        foreach (var (key, value) in overrides) builder.UseSetting(key, value);
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(overrides));
    }
}
