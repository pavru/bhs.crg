using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Activity;
using BHS.CRG.Application.Objects;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Условия поставщика — отсрочка платежа (ТЗ COST-9.2; задача C4, issue #1080, часть 2) и отметки срока
/// в списке счетов (ТЗ COST-9.1).
///
/// <para>Сторожа задачи: новый счёт получает срок от отсрочки поставщика; отсрочку, вписанную человеком
/// или прочитанную из бумаги, условия не перекрывают; смена условий заведённые счета не трогает;
/// «просрочен» не хранится — его считает сервер от сегодня.</para>
///
/// <para>У каждого теста СВОЙ поставщик: условия на общем поставщике посева подставили бы отсрочку в
/// счета соседних классов, и те проверяли бы уже не то, что написано.</para>
/// </summary>
public class SupplierTermsTests(InvoiceLineHost host) : InvoiceLineTestBase(host)
{
    [Fact]
    public async Task Новый_счёт_получает_отсрочку_из_условий_и_срок_по_правилу()
    {
        var (client, _) = await SignInAsync("Admin");
        var vendor = await VendorAsync();
        await SetAsync(client, vendor, 30);

        var view = await CreateAsync(client, vendor, shipped: "2026-10-01", deferral: null);

        Assert.Equal(30, Deferral(view));
        Assert.Equal("2026-10-30", Due(view));
        Assert.Equal(30, view.GetProperty("due").GetProperty("supplierDeferral").GetInt32());
    }

    /// <summary>
    /// Главный путь экрана: «Новый счёт» заводит пустой черновик, и поставщика выбирают ПРАВКОЙ. Отсрочка
    /// приходит со сменой поставщика — и снимает с поля метку «распознано», если та стояла.
    /// </summary>
    [Fact]
    public async Task Выбор_поставщика_в_черновике_приносит_отсрочку_и_журнал_её_называет()
    {
        var (client, _) = await SignInAsync("Admin");
        var vendor = await VendorAsync();
        await SetAsync(client, vendor, 14);
        var id = Id(await CreateAsync(client, supplierId: null, shipped: "2026-10-01", deferral: null));

        var view = await UpdateAsync(client, id, "Поставщик", Reference(vendor));

        Assert.Equal(14, Deferral(view));
        Assert.Equal("2026-10-14", Due(view));
        var changed = Assert.Single(await RecordsAsync(id, "costs.invoice.changed"));
        Assert.Contains("Отсрочка", changed.Before);
    }

    [Fact]
    public async Task Отсрочку_из_бумаги_и_вписанную_рукой_условия_не_перекрывают()
    {
        var (client, _) = await SignInAsync("Admin");
        var vendor = await VendorAsync();
        await SetAsync(client, vendor, 30);

        // Из бумаги: пришла вместе с поставщиком.
        Assert.Equal(45, Deferral(await CreateAsync(client, vendor, shipped: null, deferral: 45)));

        // Рукой: вписана раньше, чем выбран поставщик.
        var id = Id(await CreateAsync(client, supplierId: null, shipped: null, deferral: 7));
        Assert.Equal(7, Deferral(await UpdateAsync(client, id, "Поставщик", Reference(vendor))));
    }

    /// <summary>
    /// Смена поставщика переносит отсрочку, взятую из условий прежнего, на условия нового. Вписанную —
    /// не трогает. У нового условий нет — отсрочка остаётся: стереть её значило бы молча снять и срок.
    /// </summary>
    [Fact]
    public async Task Смена_поставщика_меняет_отсрочку_из_условий_прежнего()
    {
        var (client, _) = await SignInAsync("Admin");
        var (first, second, bare) = (await VendorAsync(), await VendorAsync(), await VendorAsync());
        await SetAsync(client, first, 10);
        await SetAsync(client, second, 20);
        var id = Id(await CreateAsync(client, first, shipped: "2026-10-01", deferral: null));

        Assert.Equal(20, Deferral(await UpdateAsync(client, id, "Поставщик", Reference(second))));
        Assert.Equal(20, Deferral(await UpdateAsync(client, id, "Поставщик", Reference(bare))));
    }

    /// <summary>
    /// Происхождение отсрочки — признаком, а не совпадением с условиями (ревью PR #1329): вписанные рукой
    /// «30» у поставщика с условиями «30» при смене поставщика остаются, а подставленные из условий
    /// меняются, даже если условия прежнего поставщика с тех пор сменили.
    /// </summary>
    [Fact]
    public async Task Происхождение_отсрочки_решает_признак_а_не_совпадение_с_условиями()
    {
        var (client, _) = await SignInAsync("Admin");
        var (first, second) = (await VendorAsync(), await VendorAsync());
        var firstTerms = await SetAsync(client, first, 30);
        await SetAsync(client, second, 45);

        // Вписано рукой то же число, что в условиях первого.
        var typed = Id(await CreateAsync(client, supplierId: null, shipped: null, deferral: 30));
        await UpdateAsync(client, typed, "Поставщик", Reference(first));
        Assert.Equal(30, Deferral(await UpdateAsync(client, typed, "Поставщик", Reference(second))));

        // Подставлено из условий первого, затем его условия сменили — при смене поставщика отсрочка
        // всё равно из условий и уступает условиям второго.
        var termed = Id(await CreateAsync(client, first, shipped: null, deferral: null));
        await SetAsync(client, first, 60, firstTerms.GetProperty("version").GetString());
        Assert.Equal(45, Deferral(await UpdateAsync(client, termed, "Поставщик", Reference(second))));

        // Правка отсрочки рукой снимает признак: дальше условия её не трогают.
        var edited = Id(await CreateAsync(client, first, shipped: null, deferral: null));
        await UpdateAsync(client, edited, "Отсрочка", 12);
        Assert.Equal(12, Deferral(await UpdateAsync(client, edited, "Поставщик", Reference(second))));
    }

    [Fact]
    public async Task Правка_условий_заведённые_счета_не_трогает()
    {
        var (client, _) = await SignInAsync("Admin");
        var vendor = await VendorAsync();
        var set = await SetAsync(client, vendor, 30);
        var id = Id(await CreateAsync(client, vendor, shipped: "2026-10-01", deferral: null));

        await SetAsync(client, vendor, 60, set.GetProperty("version").GetString());

        var view = await ReadAsync(client, id);
        Assert.Equal(30, Deferral(view));
        Assert.Equal("2026-10-30", Due(view));
        // А подсказка формы говорит уже по новым условиям: «в условиях поставщика — 60 дн.».
        Assert.Equal(60, view.GetProperty("due").GetProperty("supplierDeferral").GetInt32());
    }

    [Fact]
    public async Task Условия_заводят_правят_и_снимают_по_версии_и_пишут_в_журнал()
    {
        var (client, _) = await SignInAsync("Admin");
        var vendor = await VendorAsync();

        var created = await SetAsync(client, vendor, 30);
        var listed = (await client.GetFromJsonAsync<JsonElement>("/api/costs/supplier-terms"))
            .EnumerateArray().Single(t => t.GetProperty("supplierId").GetGuid() == vendor);
        Assert.Equal(30, listed.GetProperty("deferralDays").GetInt32());
        Assert.False(listed.GetProperty("supplierLost").GetBoolean());

        // Заведение поверх заведённого — отказ, а не тихая перезапись.
        Assert.Equal(HttpStatusCode.Conflict, (await PutAsync(client, vendor, 40, version: null)).StatusCode);

        var version = created.GetProperty("version").GetString();
        var changed = await SetAsync(client, vendor, 45, version);
        // Устаревшая версия — отказ: окно открыто долго, и правка поверх чужой затёрла бы её.
        Assert.Equal(HttpStatusCode.Conflict, (await PutAsync(client, vendor, 50, version)).StatusCode);

        // Условия держат поставщика от удаления — снимают их в окне, куда ведут слова отказа.
        var held = await client.DeleteAsync($"/api/common-data/{vendor}");
        Assert.Equal(HttpStatusCode.Conflict, held.StatusCode);
        Assert.Contains("Условия поставщиков", await held.Content.ReadAsStringAsync());

        using (var bare = new HttpRequestMessage(HttpMethod.Delete, $"/api/costs/supplier-terms/{vendor}"))
            Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(bare)).StatusCode);
        using (var remove = new HttpRequestMessage(HttpMethod.Delete, $"/api/costs/supplier-terms/{vendor}"))
        {
            remove.Headers.TryAddWithoutValidation("If-Match", changed.GetProperty("version").GetString());
            Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(remove)).StatusCode);
        }

        var set = await RecordsAsync(vendor, "costs.supplier-terms.set");
        Assert.Contains(set, r => r.Before is null && r.After == "30 дн.");
        Assert.Contains(set, r => r.Before == "30 дн." && r.After == "45 дн.");
        Assert.Equal("45 дн.", Assert.Single(await RecordsAsync(vendor, "costs.supplier-terms.removed")).Before);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/common-data/{vendor}")).StatusCode);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3651)]
    public async Task Отсрочка_вне_пределов_отказ(int days)
    {
        var (client, _) = await SignInAsync("Admin");
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(client, await VendorAsync(), days, version: null)).StatusCode);
    }

    [Fact]
    public async Task Новые_условия_архивной_или_несуществующей_организации_отказ()
    {
        var (client, _) = await SignInAsync("Admin");
        var archived = await VendorAsync();
        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IRecordArchive>().SetAsync(archived, archived: true);

        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(client, archived, 10, version: null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(client, Guid.NewGuid(), 10, version: null)).StatusCode);
    }

    [Fact]
    public async Task Условия_ведёт_тот_кто_правит_счета()
    {
        // Руководитель читает счета и не правит их. (Бухгалтеру с E2 правка выдана — вместе с исправлением.)
        var (accountant, _) = await SignInAsync("Executive");
        Assert.Equal(HttpStatusCode.Forbidden, (await accountant.GetAsync("/api/costs/supplier-terms")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(accountant, Guid.NewGuid(), 10, version: null)).StatusCode);
    }

    /// <summary>
    /// «Просрочен» и «осталось дней» в списке считает сервер от сегодня — сдвиг срока меняет отметку без
    /// какой-либо записи признака. У отклонённого счёта (оплаты не ждёт) отметок нет.
    /// </summary>
    [Fact]
    public async Task Список_помечает_просроченный_и_считает_дни_до_срока()
    {
        var (client, _) = await SignInAsync("Admin");
        var today = DateOnly.FromDateTime(DateTime.Today);
        // С запасом в дни: пояс компании и пояс машины в полночь могут разойтись на сутки.
        var late = Id(await CreateAsync(client, supplierId: null, shipped: null, deferral: null,
            due: today.AddDays(-5).ToString("yyyy-MM-dd")));
        var soon = Id(await CreateAsync(client, supplierId: null, shipped: null, deferral: null,
            due: today.AddDays(10).ToString("yyyy-MM-dd")));
        var rejected = Id(await CreateAsync(client, supplierId: null, shipped: null, deferral: null,
            due: today.AddDays(-5).ToString("yyyy-MM-dd")));
        await OkAsync(await client.PostAsync($"/api/costs/invoices/{rejected}/rejected", null));

        var list = await client.GetFromJsonAsync<JsonElement>("/api/costs/invoices");
        JsonElement Row(Guid id) => list.EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == id);

        Assert.True(Row(late).GetProperty("overdue").GetBoolean());
        Assert.InRange(Row(late).GetProperty("daysLeft").GetInt32(), -6, -4);
        Assert.False(Row(soon).GetProperty("overdue").GetBoolean());
        Assert.InRange(Row(soon).GetProperty("daysLeft").GetInt32(), 9, 11);
        Assert.False(Row(rejected).GetProperty("overdue").GetBoolean());
        Assert.Equal(JsonValueKind.Null, Row(rejected).GetProperty("daysLeft").ValueKind);

        // Сдвинули срок — сдвинулась и отметка: признак не лежит нигде, его считают на чтении.
        await UpdateAsync(client, late, "Срок", today.AddDays(5).ToString("yyyy-MM-dd"));
        list = await client.GetFromJsonAsync<JsonElement>("/api/costs/invoices");
        Assert.False(Row(late).GetProperty("overdue").GetBoolean());
    }

    // ── Помощники ─────────────────────────────────────────────────────────────

    /// <summary>Свой поставщик теста — см. описание класса.</summary>
    private async Task<Guid> VendorAsync() =>
        await EntryAsync(await TypeAsync(TestDirectories.OrganizationCode, "Организация"),
            $"ООО «Условия {Guid.NewGuid().ToString()[..6]}»");

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, Guid vendor, int days, string? version)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/costs/supplier-terms/{vendor}")
        {
            Content = JsonContent.Create(new { deferralDays = days }),
        };
        if (version is not null) request.Headers.TryAddWithoutValidation("If-Match", version);
        return client.SendAsync(request);
    }

    private static async Task<JsonElement> SetAsync(HttpClient client, Guid vendor, int days, string? version = null)
    {
        var response = await PutAsync(client, vendor, days, version);
        await OkAsync(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> CreateAsync(
        HttpClient client, Guid? supplierId, string? shipped, int? deferral, string? due = null)
    {
        var requisites = new Dictionary<string, object?>
        {
            ["Номер"] = $"УП-{Guid.NewGuid().ToString()[..6]}",
            ["Дата"] = "2026-09-29",
            ["Поставщик"] = supplierId is { } id ? Reference(id) : null,
            ["ДатаОтгрузки"] = shipped,
            ["Отсрочка"] = deferral,
            ["Срок"] = due,
        };
        var response = await client.PostAsJsonAsync("/api/costs/invoices", new { requisites });
        await OkAsync(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Правка одного поля — как её присылает форма: все реквизиты, одно изменено.</summary>
    private static async Task<JsonElement> UpdateAsync(HttpClient client, Guid id, string key, object? value)
    {
        var requisites = JsonSerializer.Deserialize<Dictionary<string, object?>>(
            (await ReadAsync(client, id)).GetProperty("requisites").GetRawText())!;
        requisites[key] = value;
        var response = await client.PutAsJsonAsync($"/api/costs/invoices/{id}", new { requisites });
        await OkAsync(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<List<BHS.CRG.Application.Activity.ActivityRow>> RecordsAsync(Guid target, string action)
    {
        using var scope = host.Services.CreateScope();
        var records = await scope.ServiceProvider.GetRequiredService<IActivityLog>()
            .ReadAsync(0, 200, ActivityVisibility.Whole, action);
        return [.. records.Where(r => r.TargetId == target.ToString())];
    }

    private static Guid Id(JsonElement view) => view.GetProperty("id").GetGuid();

    private static int? Deferral(JsonElement view) =>
        view.GetProperty("requisites").TryGetProperty("Отсрочка", out var days) && days.ValueKind == JsonValueKind.Number
            ? days.GetInt32()
            : null;

    private static string? Due(JsonElement view) =>
        view.GetProperty("requisites").GetProperty("Срок").GetString();
}
