using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Costs.Endpoints;
using BHS.CRG.Tests.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Вопрос «нет ли уже счёта с этим файлом» ДО выгрузки (COST-6.7, лист «Ф» 27.10.2026): браузер
/// спрашивает по отпечаткам, сервер отвечает двойниками — тем же поиском и тем же правилом доверия, что
/// оговорка <c>sameFile</c> у счёта. И двойник называет, что это за счёт: поставщик, состояние, оплата,
/// кто завёл.
/// </summary>
public sealed partial class InvoiceSameFileTests
{
    private const string Ask = "/api/costs/invoices/same-file";

    /// <summary>Отпечаток так, как его посчитает браузер: SHA-256 тех же байтов, шестнадцатеричной строкой.</summary>
    private static string Print(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static string Query(params string[] prints) =>
        Ask + "?" + string.Join("&", prints.Select(p => "fingerprint=" + Uri.EscapeDataString(p)));

    private static async Task<JsonElement[]> AskAsync(HttpClient client, params string[] prints)
    {
        var answer = await client.GetAsync(Query(prints));
        await OkAsync(answer);
        return [.. (await answer.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray()];
    }

    private static Guid[] Twins(JsonElement answer) =>
        [.. answer.GetProperty("twins").EnumerateArray().Select(t => t.GetProperty("id").GetGuid())];

    [Fact]
    public async Task Пакет_из_трёх_файлов_получает_три_записи_в_порядке_вопроса()
    {
        var (client, _) = await SignInAsync("Admin");
        var (twice, once, fresh) = (Pdf(), Pdf(), Pdf());
        var first = await FromScanAsync(client, twice, "Дважды 1.pdf");
        var second = await FromScanAsync(client, twice, "Дважды 2.pdf");
        var single = await FromScanAsync(client, once, "Однажды.pdf");

        // Верхний регистр — тоже отпечаток: браузер вправе прислать и так.
        var answers = await AskAsync(client, Print(fresh), Print(twice).ToUpperInvariant(), Print(once));

        Assert.Equal([Print(fresh), Print(twice), Print(once)],
            answers.Select(a => a.GetProperty("fingerprint").GetString()));
        Assert.Empty(Twins(answers[0]));
        Assert.Equal([Id(first), Id(second)], Twins(answers[1]));
        Assert.Equal([Id(single)], Twins(answers[2]));
    }

    /// <summary>Повтор отпечатка в вопросе — одна запись: пакет, в котором файл выбран дважды, спрашивает о нём раз.</summary>
    [Fact]
    public async Task Повтор_отпечатка_в_вопросе_схлопывается()
    {
        var (client, _) = await SignInAsync("Admin");
        var print = Print(Pdf());

        var answers = await AskAsync(client, print, print.ToUpperInvariant(), print);

        Assert.Single(answers);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(InvoiceSameFiles.MaxFingerprints + 1)]
    public async Task Пустой_или_слишком_длинный_перечень_отказ_400(int count)
    {
        var (client, _) = await SignInAsync("Admin");

        var answer = await client.GetAsync(count == 0 ? Ask : Query([.. Enumerable.Range(0, count).Select(_ => Print(Pdf()))]));

        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
        Assert.Contains("отпечат", await answer.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("не-отпечаток")]
    [InlineData("zz")]
    // 64 знака, но не шестнадцатеричных.
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    // Шестнадцатеричные, но 63 знака.
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task Не_отпечаток_отказ_400_со_словами(string bad)
    {
        var (client, _) = await SignInAsync("Admin");

        var answer = await client.GetAsync(Query(Print(Pdf()), bad));

        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
        Assert.Contains("шестнадцатеричных", await answer.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Пятьдесят_отпечатков_принимаются()
    {
        var (client, _) = await SignInAsync("Admin");

        var answers = await AskAsync(client, [.. Enumerable.Range(0, InvoiceSameFiles.MaxFingerprints).Select(_ => Print(Pdf()))]);

        Assert.Equal(InvoiceSameFiles.MaxFingerprints, answers.Length);
    }

    /// <summary>
    /// Недоверенная печать (путь сменился в обход приёма) — двойника нет НИГДЕ: ни в оговорке счёта, ни в
    /// ответе по отпечатку. Один поиск — одно правило доверия.
    /// </summary>
    [Fact]
    public async Task Недоверенный_отпечаток_не_называется_ни_в_оговорке_ни_в_ответе()
    {
        var (client, _) = await SignInAsync("Admin");
        var content = Pdf();
        var first = await FromScanAsync(client, content, "Копия.pdf");
        var second = await FromScanAsync(client, content, "Копия.pdf");
        Assert.Equal([Id(first), Id(second)], Twins((await AskAsync(client, Print(content)))[0]));

        await SqlAsync($"UPDATE costs.invoices SET scan_blob_path = scan_blob_path || '.прежний' WHERE id = '{Id(first)}'");

        Assert.Equal([Id(second)], Twins((await AskAsync(client, Print(content)))[0]));
        Assert.Empty(SameFile(await ReadAsync(client, Id(second))));
    }

    /// <summary>Предел двойников — на КАЖДЫЙ отпечаток: бланк, загруженный много раз, не вытесняет соседа по пакету.</summary>
    [Fact]
    public async Task Предел_двойников_считается_на_каждый_отпечаток()
    {
        var (client, _) = await SignInAsync("Admin");
        var (blank, other) = (Pdf(), Pdf());
        for (var i = 0; i < InvoiceSameFiles.Limit + 2; i++)
            await FromScanAsync(client, blank, $"Бланк {i}.pdf");
        var single = await FromScanAsync(client, other, "Другой.pdf");

        var answers = await AskAsync(client, Print(blank), Print(other));

        Assert.Equal(InvoiceSameFiles.Limit, Twins(answers[0]).Length);
        Assert.Equal([Id(single)], Twins(answers[1]));
    }

    /// <summary>
    /// Двойник, заведённый другим и с тех пор разобранный и оплаченный, называет поставщика, состояние,
    /// оплату и заведшего — именем на момент заведения: переименование автора его не меняет.
    /// </summary>
    [Fact]
    public async Task Двойник_разобран_и_оплачен_другим_и_назван_полностью()
    {
        var (petrov, petrovId) = await SignInAsync("Admin");
        var (me, _) = await SignInAsync("Admin");
        var content = Pdf();
        var twin = await FromScanAsync(petrov, content, "Счёт Петрова.pdf");
        var name = await UserNameAsync(petrovId);
        await SqlAsync(
            $"UPDATE costs.invoices SET state = 'Parsed', payment = 'Paid', paid_on = '2026-10-01', " +
            $"supplier_id = '{supplier}' WHERE id = '{Id(twin)}'");
        await RenameAsync(petrovId, $"Петров {Guid.NewGuid():N}");

        var found = (await AskAsync(me, Print(content)))[0].GetProperty("twins").EnumerateArray().Single();

        Assert.Equal(supplier, found.GetProperty("supplierId").GetGuid());
        Assert.Equal("ООО «Кабель-Торг»", found.GetProperty("supplierName").GetString());
        Assert.False(found.GetProperty("supplierArchived").GetBoolean());
        Assert.Equal(InvoiceRequisites.Label(InvoiceState.Parsed), found.GetProperty("state").GetString());
        Assert.Equal(InvoiceRequisites.Label(InvoicePaymentState.Paid), found.GetProperty("payment").GetString());
        Assert.Equal(name, found.GetProperty("createdByName").GetString());
        Assert.False(found.GetProperty("mine").GetBoolean());
        Assert.Equal("Счёт Петрова.pdf", found.GetProperty("scanFileName").GetString());
    }

    /// <summary>
    /// Ответ на заведение потерялся, и тот же человек спрашивает о файле: двойник — его черновик минутной
    /// давности, «это вы». Та же запись — в оговорке <c>sameFile</c> у счёта.
    /// </summary>
    [Fact]
    public async Task Свой_черновик_назван_своим()
    {
        var (client, id) = await SignInAsync("Admin");
        var content = Pdf();
        var mine = await FromScanAsync(client, content, "Мой.pdf");

        var found = (await AskAsync(client, Print(content)))[0].GetProperty("twins").EnumerateArray().Single();

        Assert.Equal(Id(mine), found.GetProperty("id").GetGuid());
        Assert.True(found.GetProperty("mine").GetBoolean());
        Assert.Equal(await UserNameAsync(id), found.GetProperty("createdByName").GetString());
        Assert.Equal(InvoiceRequisites.Label(InvoiceState.Draft), found.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, found.GetProperty("supplierId").ValueKind);

        var again = await FromScanAsync(client, content, "Мой.pdf");
        var named = again.GetProperty("sameFile").EnumerateArray().Single();
        Assert.True(named.GetProperty("mine").GetBoolean());
        Assert.Equal(await UserNameAsync(id), named.GetProperty("createdByName").GetString());
    }

    /// <summary>
    /// Счёт, заведённый ВРУЧНУЮ и получивший файл потом, называет заведшего тоже: снимок имени ставят оба
    /// пути заведения, а не только «из файла».
    /// </summary>
    [Fact]
    public async Task Ручное_заведение_тоже_помнит_имя_заведшего()
    {
        var (author, authorId) = await SignInAsync("Admin");
        var (me, _) = await SignInAsync("Admin");
        var content = Pdf();
        var invoice = await CreateAsync(author);
        await AttachAsync(author, invoice, content, "Приложен потом.pdf");

        var found = (await AskAsync(me, Print(content)))[0].GetProperty("twins").EnumerateArray().Single();

        Assert.Equal(invoice, found.GetProperty("id").GetGuid());
        Assert.Equal(await UserNameAsync(authorId), found.GetProperty("createdByName").GetString());
        Assert.False(found.GetProperty("mine").GetBoolean());
    }

    /// <summary>
    /// Счёт, заведённый до снимка имени: имени нет, а «это вы» всё равно верно — по учётной записи.
    /// </summary>
    [Fact]
    public async Task Прежний_счёт_без_имени_но_со_своей_учётной_записью()
    {
        var (client, _) = await SignInAsync("Admin");
        var content = Pdf();
        var old = await FromScanAsync(client, content, "Прежний.pdf");
        await SqlAsync($"UPDATE costs.invoices SET created_by_name = NULL WHERE id = '{Id(old)}'");

        var found = (await AskAsync(client, Print(content)))[0].GetProperty("twins").EnumerateArray().Single();

        Assert.Equal(JsonValueKind.Null, found.GetProperty("createdByName").ValueKind);
        Assert.True(found.GetProperty("mine").GetBoolean());
    }

    /// <summary>Справочник не прочитан — идентификатор поставщика без названия: «не знаем», а не «без поставщика».</summary>
    [Fact]
    public async Task Справочник_не_ответил_поставщик_без_названия()
    {
        var (client, _) = await SignInAsync("Admin");
        var content = Pdf();
        var twin = await FromScanAsync(client, content, "Без справочника.pdf");
        await SqlAsync($"UPDATE costs.invoices SET supplier_id = '{supplier}' WHERE id = '{Id(twin)}'");

        host.OrganizationRefsUnread = true;
        try
        {
            var found = (await AskAsync(client, Print(content)))[0].GetProperty("twins").EnumerateArray().Single();

            Assert.Equal(supplier, found.GetProperty("supplierId").GetGuid());
            Assert.Equal(JsonValueKind.Null, found.GetProperty("supplierName").ValueKind);
        }
        finally
        {
            host.OrganizationRefsUnread = false;
        }
    }

    /// <summary>Без двойников справочник ядра не зовётся вовсе — ни вопросом, ни чтением счёта.</summary>
    [Fact]
    public async Task Без_двойников_справочник_не_зовётся()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await FromScanAsync(client, Pdf(), "Один.pdf");
        var before = host.OrganizationRefsCalls;

        await AskAsync(client, Print(Pdf()), Print(Pdf()));
        await ReadAsync(client, Id(invoice));

        Assert.Equal(before, host.OrganizationRefsCalls);
    }

    /// <summary>Право на накладные вопроса о счетах не открывает: ответ раскрыл бы, какие счета несут файл.</summary>
    [Fact]
    public async Task Без_права_чтения_счетов_отказ_403()
    {
        var (storekeeper, _) = await SignInAsync(await RoleAsync("costs.waybill.read"));

        var answer = await storekeeper.GetAsync(Query(Print(Pdf())));

        Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
    }

    [Fact]
    public async Task Без_прав_модуля_отказ_и_адрес_за_воротами_модуля()
    {
        var (stranger, _) = await SignInAsync(await RoleAsync("core.catalog.read"));

        var answer = await stranger.GetAsync(Query(Print(Pdf())));

        Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
        var route = EndpointInventory.Routes(host.Services)
            .Single(e => EndpointInventory.Route(e) == Ask && EndpointInventory.Verbs(e) == "GET");
        var policies = route.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy).ToList();
        Assert.Contains(AppPolicies.Module(BHS.CRG.Modules.Costs.CostsModule.ModuleCode), policies);
        Assert.Contains(AppPolicies.Permission("costs.invoice.read"), policies);
    }

    private async Task SqlAsync(string sql)
    {
        using var scope = host.Services.CreateScope();
#pragma warning disable EF1002 // строки теста — свои идентификаторы, не ввод
        await scope.ServiceProvider.GetRequiredService<CostsDbContext>().Database.ExecuteSqlRawAsync(sql);
#pragma warning restore EF1002
    }

    private async Task<string> UserNameAsync(Guid id)
    {
        using var scope = host.Services.CreateScope();
        var user = await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByIdAsync(id.ToString());
        return string.IsNullOrWhiteSpace(user!.DisplayName) ? user.Email! : user.DisplayName;
    }

    private async Task RenameAsync(Guid id, string name)
    {
        using var scope = host.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByIdAsync(id.ToString());
        user!.DisplayName = name;
        Assert.True((await users.UpdateAsync(user)).Succeeded);
    }

    /// <summary>Своя роль с названными правами — системной «только накладные» нет.</summary>
    private async Task<string> RoleAsync(params string[] permissions)
    {
        var name = $"SameFile_{Guid.NewGuid():N}";
        using var scope = host.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var role = new IdentityRole<Guid>(name);
        Assert.True((await roles.CreateAsync(role)).Succeeded);
        foreach (var permission in permissions)
            Assert.True((await roles.AddClaimAsync(role, new Claim(RoleSynchronizer.PermissionClaim, permission))).Succeeded);
        return name;
    }
}
