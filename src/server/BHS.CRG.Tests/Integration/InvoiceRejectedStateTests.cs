using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Activity;
using BHS.CRG.Domain.Activity;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// «Отклонён»: по счёту не платим (ТЗ COST-9, issue #1284) — переход, возврат в черновик и то, что
/// отклонённый счёт остаётся в реестре, а из очередей уходит.
///
/// <para>До этой задачи состояние было объявлено и учитывалось отборами и отчётами, но перевести счёт
/// в него было нечем: условие «не отклонён» ни разу не срабатывало на живых данных. Отказы оплаченному
/// и запертому счёту — рядом с оплатой, в <see cref="InvoicePaymentTests" />.</para>
/// </summary>
// ⚠️ Собрание указывается КАЖДОМУ классу: атрибут не наследуется от базового.
[Collection("Integration")]
public class InvoiceRejectedStateTests(InvoiceLineHost host) : InvoiceLineTestBase(host)
{
    private static string State(JsonElement view) =>
        view.GetProperty("requisites").GetProperty("Состояние").GetString()!;

    private static async Task<JsonElement> MoveAsync(HttpClient client, Guid invoice, string to)
    {
        var response = await client.PostAsync($"/api/costs/invoices/{invoice}/{to}", null);
        await OkAsync(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<List<ActivityRecord>> JournalAsync(Guid invoice, string action)
    {
        using var scope = host.Services.CreateScope();
        var records = await scope.ServiceProvider.GetRequiredService<IActivityLog>()
            .ReadAsync(0, 200, ActivityVisibility.Whole, action);
        return [.. records.Where(r => r.TargetId == invoice.ToString())];
    }

    private static async Task<List<Guid>> ListedAsync(HttpClient client, string query = "") =>
        [.. (await client.GetFromJsonAsync<JsonElement>("/api/costs/invoices" + query))
            .EnumerateArray().Select(i => i.GetProperty("id").GetGuid())];

    /// <summary>
    /// Черновик со строкой без позиции стоит в очереди «Разобрать». Отклонили — из очереди ушёл, в
    /// реестре остался: это решение о настоящем счёте, а не его удаление. Вернули — снова в очереди.
    /// </summary>
    [Fact]
    public async Task Черновик_отклоняется_остаётся_в_реестре_и_возвращается()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);
        await LinesAsync(client, invoice, [Line(null, quantity: 1, price: 10m, text: "позиции ещё нет")]);
        Assert.Contains(invoice, await ListedAsync(client, "?needsParsing=true"));

        Assert.Equal("Отклонён", State(await MoveAsync(client, invoice, "rejected")));

        Assert.DoesNotContain(invoice, await ListedAsync(client, "?needsParsing=true"));
        var list = await client.GetFromJsonAsync<JsonElement>("/api/costs/invoices");
        Assert.Equal("Отклонён",
            list.EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == invoice).GetProperty("state").GetString());
        // Журнал называет, из какого состояния ушёл счёт, — и не называет суммы (issue #1190).
        var rejected = Assert.Single(await JournalAsync(invoice, "costs.invoice.rejected"));
        Assert.Equal("черновик", rejected.Before);
        Assert.Equal("не платим", rejected.After);

        // Строки при этом целы: отклонённый возвращают в черновик, а не разбирают с нуля.
        var back = await MoveAsync(client, invoice, "draft");
        Assert.Equal("Черновик", State(back));
        Assert.Equal(1, back.GetProperty("lines").GetArrayLength());
        Assert.Contains(invoice, await ListedAsync(client, "?needsParsing=true"));
        Assert.Equal("отклонён", Assert.Single(await JournalAsync(invoice, "costs.invoice.draft")).Before);
    }

    /// <summary>
    /// Разобранный отклоняется тоже: «не платим» от разбора не зависит. Обратно — только в черновик:
    /// «разобран» отклонённому отказывает и называет выход.
    /// </summary>
    [Fact]
    public async Task Разобранный_отклоняется_а_обратно_только_через_черновик()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client, complete: true);
        await AllocatedLinesAsync(client, invoice, [Line(cable, quantity: 1, price: 100m)]);
        Assert.Equal("Разобран", State(await MoveAsync(client, invoice, "parsed")));

        Assert.Equal("Отклонён", State(await MoveAsync(client, invoice, "rejected")));

        var parsed = await client.PostAsync($"/api/costs/invoices/{invoice}/parsed", null);
        Assert.Equal(HttpStatusCode.Conflict, parsed.StatusCode);
        Assert.Contains("Верните счёт в черновик", await parsed.Content.ReadAsStringAsync());

        Assert.Equal("Черновик", State(await MoveAsync(client, invoice, "draft")));
        Assert.Equal("Разобран", State(await MoveAsync(client, invoice, "parsed")));
    }

    /// <summary>
    /// Разобранный счёт, у которого правка строк сняла условие «разобран», уходит в черновик САМ — и
    /// запись об этом называет, откуда он ушёл, как и запись ручного возврата (ревью PR #1290): раньше
    /// под одним действием журнала стояли записи двух видов, с «было» и без.
    /// </summary>
    [Fact]
    public async Task Автоматический_возврат_в_черновик_называет_откуда_ушёл_счёт()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client, complete: true);
        await AllocatedLinesAsync(client, invoice, [Line(cable, quantity: 1, price: 100m)]);
        await MoveAsync(client, invoice, "parsed");

        var edited = await LinesAsync(client, invoice, [Line(null, quantity: 1, price: 100m, text: "позицию сняли")]);

        Assert.Equal("Черновик", State(edited));
        var returned = Assert.Single(await JournalAsync(invoice, "costs.invoice.draft"));
        Assert.Equal("разобран", returned.Before);
        Assert.StartsWith("правка строк:", returned.After);
    }

    /// <summary>
    /// Отклонение уже отклонённого — тем, кто прочитал его отклонённым, — новым решением не становится:
    /// состояние то же, и второй записи в журнале нет.
    ///
    /// <para>⚠️ Это НЕ повтор после оборванного ответа: тот называет версию до отклонения и получает
    /// 409 — см. следующий тест. Помощник тестов подставляет свежую версию сам, и на нём этой разницы
    /// не видно.</para>
    /// </summary>
    [Fact]
    public async Task Повторное_отклонение_журнал_не_пишет()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);
        await MoveAsync(client, invoice, "rejected");

        Assert.Equal("Отклонён", State(await MoveAsync(client, invoice, "rejected")));

        Assert.Single(await JournalAsync(invoice, "costs.invoice.rejected"));
    }

    /// <summary>
    /// Повтор с версией, которую человек видел ДО отклонения (ответ потерялся), — отказ 409, как у любой
    /// правки по устаревшему виду. Сверку версии ради повтора не ослабляли: форма на такой отказ
    /// перечитывает счёт и видит, что он отклонён (ревью PR #1290).
    /// </summary>
    [Fact]
    public async Task Повтор_отклонения_с_прежней_версией_отказ_а_счёт_отклонён()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);
        var seen = (await client.GetFromJsonAsync<JsonElement>($"/api/costs/invoices/{invoice}")).GetProperty("version").GetString()!;
        await MoveAsync(client, invoice, "rejected");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/costs/invoices/{invoice}/rejected");
        request.Headers.TryAddWithoutValidation(SeenInvoiceVersion.Header, seen);
        var repeated = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, repeated.StatusCode);
        Assert.Equal("Отклонён", State(await client.GetFromJsonAsync<JsonElement>($"/api/costs/invoices/{invoice}")));
        Assert.Single(await JournalAsync(invoice, "costs.invoice.rejected"));
    }
}
