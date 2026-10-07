using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Objects;
using BHS.CRG.Modules.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Отборы «наведите порядок» в списке страницы ввода счетов и пометки его строк (issue #1186, шаг 3).
/// </summary>
public partial class InvoicePaymentTests
{
    private static async Task<List<JsonElement>> ListedAsync(HttpClient client, string? fix = null)
    {
        var response = await client.GetAsync("/api/costs/invoices" + (fix is null ? "" : $"?fix={fix}"));
        await OkAsync(response);
        return [.. (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray()];
    }

    private static async Task<JsonElement> QueuesAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/costs/invoices/queues");
        await OkAsync(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static JsonElement? Listed(List<JsonElement> list, Guid invoice) =>
        list.Where(i => i.GetProperty("id").GetGuid() == invoice).Select(i => (JsonElement?)i).SingleOrDefault();

    private static (string Kind, int Count)[] Places(JsonElement item, string which) =>
        [.. item.GetProperty("references").GetProperty(which).EnumerateArray()
            .Select(p => (p.GetProperty("kind").GetString()!, p.GetProperty("count").GetInt32()))];

    /// <summary>
    /// Сторож шага: <b>под чипом рейла — столько счетов, сколько на нём написано</b>. Число чип берёт у
    /// готового отбора таблицы счетов, а строки — у списка; посчитай список по своему правилу, «3» стояло
    /// бы над двумя счетами. Заодно — что строка говорит, ГДЕ потеря: в списке виден только поставщик.
    /// </summary>
    [Fact]
    public async Task Список_под_отбором_удалённых_равен_числу_готового_отбора_и_называет_места()
    {
        var (admin, _) = await SignInAsync("Admin");
        var lost = await LostInvoiceAsync(admin);

        var listed = await ListedAsync(admin, "lost");
        var queues = await QueuesAsync(admin);
        // Три числа одно: на чипе рейла, на чипе над реестром и строк под отбором.
        Assert.Equal(listed.Count, queues.GetProperty("lost").GetInt32());
        Assert.Equal(ShortcutCount(await ShortcutsAsync(admin), "lost"), listed.Count);
        Assert.Equal((await TallyAsync(admin, "locked")).Invoices, queues.GetProperty("locked").GetInt32());
        Assert.Equal(JsonValueKind.Null, queues.GetProperty("doubt").ValueKind);

        var references = Listed(listed, lost.Invoice)!.Value.GetProperty("references");
        Assert.Equal("fixable", references.GetProperty("lostState").GetString());
        Assert.True(references.GetProperty("supplierLost").GetBoolean());
        // Разноска — три ссылки на две части: удалённая стройка уносит и раздел.
        Assert.Equal(
            [("supplier", 1), ("payer", 1), ("position", 1), ("allocation", 3)],
            Places(Listed(listed, lost.Invoice)!.Value, "lost"));

        // Пометку несёт и строка списка без отбора: иначе счёт с удалённой позицией выглядел бы чистым.
        Assert.Equal("fixable",
            Listed(await ListedAsync(admin), lost.Invoice)!.Value.GetProperty("references").GetProperty("lostState").GetString());
    }

    /// <summary>
    /// Отбор «в архиве» — только счета в работе, как и число на чипе; у оплаченного пометка остаётся,
    /// но к правке не зовёт. Удалённого у обоих нет — и поставщик без записи не объявлен потерянным.
    /// </summary>
    [Fact]
    public async Task Список_под_отбором_архива_равен_числу_готового_отбора()
    {
        var (admin, _) = await SignInAsync("Admin");
        var today = await TodayAsync();
        var (open, first, _) = await InvoiceWithPositionAsync(admin, "Рейл: архив, счёт в работе");
        var (paid, second, _) = await InvoiceWithPositionAsync(admin, "Рейл: архив, счёт оплачен");
        await PayAsync(admin, paid, today, await PreviewAsync(admin, paid, today), null);
        using (var scope = host.Services.CreateScope())
        {
            var archive = scope.ServiceProvider.GetRequiredService<IRecordArchive>();
            await archive.SetAsync(first, archived: true);
            await archive.SetAsync(second, archived: true);
        }

        var listed = await ListedAsync(admin, "archived");
        Assert.Equal(listed.Count, (await QueuesAsync(admin)).GetProperty("archived").GetInt32());
        Assert.Equal(ShortcutCount(await ShortcutsAsync(admin), "archived"), listed.Count);
        Assert.Null(Listed(listed, paid));

        var mine = Listed(listed, open)!.Value;
        Assert.True(mine.GetProperty("references").GetProperty("archivedCalls").GetBoolean());
        Assert.Equal([("position", 1)], Places(mine, "archived"));
        Assert.Empty(Places(mine, "lost"));
        Assert.Equal(JsonValueKind.Null, mine.GetProperty("references").GetProperty("lostState").ValueKind);
        // Потеря не найдена — и только: «на месте» или «другого вида» список об этом не говорит.
        Assert.False(mine.GetProperty("references").GetProperty("supplierLost").GetBoolean());

        var quiet = Listed(await ListedAsync(admin), paid)!.Value;
        Assert.False(quiet.GetProperty("references").GetProperty("archivedCalls").GetBoolean());
        Assert.Equal([("position", 1)], Places(quiet, "archived"));
    }

    /// <summary>Порт, у которого обратный опрос отказывает.</summary>
    private sealed class BrokenTargets : IModuleReferenceTargets
    {
        public Task<IReadOnlyDictionary<Guid, ReferenceState>> StatesAsync(
            ReferenceTarget target, IReadOnlyCollection<Guid> ids, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, ReferenceState>>(new Dictionary<Guid, ReferenceState>());

        public Task<ReferenceFindings> NotPresentAsync(
            string moduleCode, bool includeArchived, CancellationToken ct = default) =>
            throw new InvalidOperationException("опрос отказал");
    }

    /// <summary>
    /// <b>Отказ опроса не отнимает список счетов</b> (ревью PR #1241): пометки — вспомогательные данные.
    /// Счета приходят без пометок, а вот числа и отбор отвечают отказом — выдать за них «ноль» или
    /// полный список значило бы переодеть отказ в ответ.
    /// </summary>
    [Fact]
    public async Task Отказ_опроса_оставляет_список_счетов_без_пометок_а_числам_и_отбору_отказывает()
    {
        var (admin, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(admin);
        await using var broken = host.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IModuleReferenceTargets>();
            services.AddScoped<IModuleReferenceTargets, BrokenTargets>();
        }));
        var client = broken.CreateClient();
        client.DefaultRequestHeaders.Authorization = admin.DefaultRequestHeaders.Authorization;

        var item = Listed(await ListedAsync(client), invoice)!.Value;
        Assert.Equal(JsonValueKind.Null, item.GetProperty("references").ValueKind);

        Assert.Equal(HttpStatusCode.InternalServerError, (await client.GetAsync("/api/costs/invoices/queues")).StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, (await client.GetAsync("/api/costs/invoices?fix=lost")).StatusCode);
    }

    /// <summary>Неизвестный отбор — отказ, а не полный список: тот выглядел бы ответом на вопрос.</summary>
    [Fact]
    public async Task Неизвестный_отбор_списка_счетов_отказ()
    {
        var (admin, _) = await SignInAsync("Admin");

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/costs/invoices?fix=everything")).StatusCode);
    }
}
