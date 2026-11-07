using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BHS.CRG.Modules;
using BHS.CRG.Tests.Support;
using Microsoft.AspNetCore.Authorization;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Сознательный повтор файла (COST-6.7, лист «Ф» 27.10.2026): человек видел окно повтора и выбрал
/// «Завести ещё один черновик». Сервер помечает это у счёта — отпечатком файла, а не флажком, — и пункт
/// «тот же файл» у такого счёта стоит «к сведению». Оговорка остаётся оговоркой: черновик заводится всегда.
/// </summary>
public sealed partial class InvoiceSameFileTests
{
    private const string FromScan = "/api/costs/invoices/from-scan";

    private static MultipartFormDataContent RepeatForm(string content, string fileName, string? acceptRepeat)
    {
        var form = Form(content, fileName);
        if (acceptRepeat is not null) form.Add(new StringContent(acceptRepeat, Encoding.UTF8), "acceptRepeat");
        return form;
    }

    private async Task<JsonElement> RepeatAsync(HttpClient client, string content, string fileName, string? acceptRepeat)
    {
        var created = await client.PostAsync(FromScan, RepeatForm(content, fileName, acceptRepeat));
        // Заведение не спрашивает версию: If-Match у адресов /{id}/…, а у счёта до заведения версии нет.
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("invoice");
    }

    private static bool Accepted(JsonElement view) => view.GetProperty("scanRepeatAccepted").GetBoolean();

    [Fact]
    public async Task Завести_ещё_один_черновик_помечает_повтор_принятым()
    {
        var (client, _) = await SignInAsync("Admin");
        var content = Pdf();
        var first = await FromScanAsync(client, content, "Счёт.pdf");
        Assert.False(Accepted(first));

        var second = await RepeatAsync(client, content, "Счёт (ещё раз).pdf", "true");

        Assert.True(Accepted(second));
        Assert.Equal([Id(first)], SameFile(second));
        // Признак — у счёта, а не у ответа: чтение заново его помнит. Двойник своего повтора не принимал.
        Assert.True(Accepted(await ReadAsync(client, Id(second))));
        Assert.False(Accepted(await ReadAsync(client, Id(first))));
    }

    /// <summary>Двойник исчез до заведения (гонка, удалён) — поле молча не действует, черновик заводится.</summary>
    [Fact]
    public async Task Без_двойников_повтор_не_помечается_и_отказа_нет()
    {
        var (client, _) = await SignInAsync("Admin");

        var lone = await RepeatAsync(client, Pdf(), "Один.pdf", "true");

        Assert.False(Accepted(lone));
        Assert.Empty(SameFile(lone));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public async Task Без_согласия_повтор_не_помечается(string? acceptRepeat)
    {
        var (client, _) = await SignInAsync("Admin");
        var content = Pdf();
        var first = await FromScanAsync(client, content, "Дважды.pdf");

        var second = await RepeatAsync(client, content, "Дважды.pdf", acceptRepeat);

        Assert.False(Accepted(second));
        Assert.Equal([Id(first)], SameFile(second));
    }

    /// <summary>Признак относится к ФАЙЛУ: замена файла его гасит — у нового файла другой отпечаток.</summary>
    [Fact]
    public async Task Замена_файла_гасит_принятый_повтор()
    {
        var (client, _) = await SignInAsync("Admin");
        var content = Pdf();
        await FromScanAsync(client, content, "Первый.pdf");
        var repeat = await RepeatAsync(client, content, "Повтор.pdf", "true");
        Assert.True(Accepted(repeat));

        var replaced = await AttachAsync(client, Id(repeat), Pdf(), "Другой.pdf");

        Assert.False(Accepted(replaced));
        Assert.False(Accepted(await ReadAsync(client, Id(repeat))));

        // Тот же файл приложен снова — новый вопрос, а не прежнее согласие: принять повтор может только заведение.
        var back = await AttachAsync(client, Id(repeat), content, "Повтор.pdf");
        Assert.NotEmpty(SameFile(back));
        Assert.False(Accepted(back));
    }

    /// <summary>Двойник удалён — принимать больше нечего: признак ложен, а не висит над пустой оговоркой.</summary>
    [Fact]
    public async Task Удалённый_двойник_гасит_принятый_повтор()
    {
        var (client, _) = await SignInAsync("Admin");
        var content = Pdf();
        var first = await FromScanAsync(client, content, "Первый.pdf");
        var repeat = await RepeatAsync(client, content, "Повтор.pdf", "true");
        Assert.True(Accepted(repeat));

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/costs/invoices/{Id(first)}")).StatusCode);

        var read = await ReadAsync(client, Id(repeat));
        Assert.Empty(SameFile(read));
        Assert.False(Accepted(read));
    }

    /// <summary>Значение не из двух — отказ словами, а не безымянный 400 привязки; черновика нет.</summary>
    [Fact]
    public async Task Непонятное_согласие_отказ_400_со_словами()
    {
        var (client, _) = await SignInAsync("Admin");
        var content = Pdf();

        var refused = await client.PostAsync(FromScan, RepeatForm(content, "Счёт.pdf", "1"));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("acceptRepeat", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Empty((await AskAsync(client, Print(content)))[0].GetProperty("twins").EnumerateArray());
    }

    /// <summary>Чтение счетов заводить их не даёт — и с согласием на повтор тоже: черновика нет.</summary>
    [Fact]
    public async Task Без_права_правки_отказ_403_и_черновика_нет()
    {
        var (admin, _) = await SignInAsync("Admin");
        var (reader, _) = await SignInAsync(await RoleAsync("costs.invoice.read"));
        var content = Pdf();
        await FromScanAsync(admin, content, "Есть.pdf");

        var refused = await reader.PostAsync(FromScan, RepeatForm(content, "Повтор.pdf", "true"));

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Single((await AskAsync(admin, Print(content)))[0].GetProperty("twins").EnumerateArray());
    }

    [Fact]
    public async Task Без_прав_модуля_отказ_и_заведение_за_воротами_модуля()
    {
        var (stranger, _) = await SignInAsync(await RoleAsync("core.catalog.read"));

        var refused = await stranger.PostAsync(FromScan, RepeatForm(Pdf(), "Чужой.pdf", "true"));

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var route = EndpointInventory.Routes(host.Services)
            .Single(e => EndpointInventory.Route(e) == FromScan && EndpointInventory.Verbs(e) == "POST");
        var policies = route.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy).ToList();
        Assert.Contains(AppPolicies.Module(BHS.CRG.Modules.Costs.CostsModule.ModuleCode), policies);
        Assert.Contains(AppPolicies.Permission(BHS.CRG.Modules.Costs.CostsModule.InvoiceEdit), policies);
    }
}
