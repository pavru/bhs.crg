using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Activity;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Objects;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Activity;
using BHS.CRG.Domain.Catalog;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Действие «в архив» / «вернуть» у записи справочника — адресами, как его зовёт экран
/// (issue #1185, ТЗ CORE-34.4).
///
/// <para>Что служба делает с признаком, проверено у неё самой (<c>BackupServiceTests.Archive</c>).
/// Здесь — то, что добавляет действие: кому оно отказывает, что пишет в журнал и чем отвечает
/// удаление занятой записи.</para>
/// </summary>
[Collection("Integration")]
public class RecordArchiveActionTests(InvoiceLineHost host) : InvoiceLineTestBase(host)
{
    private readonly InvoiceLineHost host = host;

    [Fact]
    public async Task В_архив_и_обратно_запись_уходит_из_выбора_и_возвращается_а_журнал_помнит_оба_шага()
    {
        var (client, _) = await SignInAsync("Admin");
        var (type, id) = await RecordAsync();
        var archivedBefore = await CountAsync(ActivityActions.RecordArchived);
        var returnedBefore = await CountAsync(ActivityActions.RecordUnarchived);

        var archived = await PostAsync(client, id, "archive");
        Assert.True(archived.GetProperty("archived").GetBoolean());
        Assert.True(archived.GetProperty("changed").GetBoolean());
        Assert.DoesNotContain(id, await ChoiceAsync(type));

        var last = await LastAsync(ActivityActions.RecordArchived);
        Assert.Equal(id.ToString(), last!.TargetId);
        Assert.Contains("Запись действия", last.TargetLabel);
        Assert.Equal(archivedBefore + 1, await CountAsync(ActivityActions.RecordArchived));

        var returned = await PostAsync(client, id, "unarchive");
        Assert.False(returned.GetProperty("archived").GetBoolean());
        Assert.True(returned.GetProperty("changed").GetBoolean());
        Assert.Contains(id, await ChoiceAsync(type));
        Assert.Equal(returnedBefore + 1, await CountAsync(ActivityActions.RecordUnarchived));
    }

    /// <summary>
    /// Повтор — двойное нажатие, вторая вкладка — не ошибка и не событие: ответ тот же, а в журнале
    /// одна запись. Иначе журнал свидетельствовал бы о действии, которого не было.
    /// </summary>
    [Fact]
    public async Task Повтор_отвечает_успехом_но_в_журнал_не_идёт()
    {
        var (client, _) = await SignInAsync("Admin");
        var (_, id) = await RecordAsync();

        await PostAsync(client, id, "archive");
        var before = await CountAsync(ActivityActions.RecordArchived);
        var again = await PostAsync(client, id, "archive");

        Assert.True(again.GetProperty("archived").GetBoolean());
        Assert.False(again.GetProperty("changed").GetBoolean());
        Assert.Equal(before, await CountAsync(ActivityActions.RecordArchived));
    }

    [Fact]
    public async Task Записи_которой_нет_отвечают_что_её_нет()
    {
        var (client, _) = await SignInAsync("Admin");

        var response = await client.PostAsync($"/api/common-data/{Guid.NewGuid()}/archive", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Справочник модуля общим адресом в архив не уходит: модуль об архиве пока не знает, и статья
    /// осталась бы в его выборе — «убрал», а её предлагают. Отказ называет причину, а не роняет 500.
    /// </summary>
    [Fact]
    public async Task Запись_справочника_модуля_общим_адресом_в_архив_не_уходит()
    {
        var (client, _) = await SignInAsync("Admin");
        var (article, _) = await ArticleAsync(client, $"Статья архива {Guid.NewGuid().ToString("N")[..6]}");

        var response = await client.PostAsync($"/api/common-data/{article}/archive", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();
        Assert.Contains("модуль", error);
        Assert.False(await SendAsync(new CanArchiveRecordQuery(article)));
    }

    /// <summary>
    /// Отказ удалить занятую запись говорит ПОЛЕМ, что выход — архив; у записи, которая уже в
    /// архиве, поля нет — предлагать ей архив значило бы звать туда, где человек уже стоит.
    /// </summary>
    [Fact]
    public async Task Отказ_удалить_занятую_запись_предлагает_архив_пока_она_не_в_архиве()
    {
        var (client, _) = await SignInAsync("Admin");
        var (type, id) = await RecordAsync();
        // Занятость — базовым экземпляром: вторая запись того же типа стоит на первой.
        await SendAsync(new CreateCommonDataEntryCommand("Держатель", type,
            JsonDocument.Parse($$"""{"_baseRef":"{{id}}"}"""), CatalogScope.System, null));

        var refused = await client.DeleteAsync($"/api/common-data/{id}");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.True((await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("canArchive").GetBoolean());

        await PostAsync(client, id, "archive");

        var again = await client.DeleteAsync($"/api/common-data/{id}");
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.False((await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("canArchive").GetBoolean());
    }

    /// <summary>
    /// Вернуть из архива можно ЛЮБУЮ запись (ревью PR #1226): признак мог оказаться на записи модуля
    /// из копии или после смены владельца типа, и запрет на этом пути оставил бы её в архиве навсегда.
    /// </summary>
    [Fact]
    public async Task Вернуть_из_архива_можно_и_запись_справочника_модуля()
    {
        var (client, _) = await SignInAsync("Admin");
        var (article, _) = await ArticleAsync(client, $"Статья возврата {Guid.NewGuid().ToString("N")[..6]}");
        using (var scope = host.Services.CreateScope())
            Assert.Equal(ArchiveOutcome.Changed,
                await scope.ServiceProvider.GetRequiredService<IRecordArchive>().SetAsync(article, archived: true));

        var returned = await PostAsync(client, article, "unarchive");

        Assert.False(returned.GetProperty("archived").GetBoolean());
        Assert.True(returned.GetProperty("changed").GetBoolean());
    }

    /// <summary>
    /// Запись, которую держит перечень работ: отказ в удалении сам говорит «запись останется на
    /// месте, сообщите о находке» — и кнопка «в архив» под ним была бы вторым, обратным указанием.
    /// </summary>
    [Fact]
    public async Task Отказ_из_за_перечня_работ_архив_не_предлагает()
    {
        var (client, _) = await SignInAsync("Admin");
        var (type, work) = await RecordAsync();
        var unit = await EntryAsync(type, $"Единица {Guid.NewGuid().ToString("N")[..6]}");
        var (site, _) = await SiteAsync($"Перечень {Guid.NewGuid().ToString("N")[..6]}", "Раздел");
        using (var scope = host.Services.CreateScope())
        {
            var plan = scope.ServiceProvider.GetRequiredService<IRepository<WorkPlanItem>>();
            await plan.AddAsync(WorkPlanItem.Create(work, site, null, unit));
            await plan.SaveChangesAsync();
        }

        var refused = await client.DeleteAsync($"/api/common-data/{work}");

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.False((await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("canArchive").GetBoolean());
    }

    // ── Помощники ─────────────────────────────────────────────────────────────

    private async Task<(Guid Type, Guid Id)> RecordAsync()
    {
        var mark = Guid.NewGuid().ToString("N")[..8];
        var type = await TypeAsync($"Действие{mark}", $"Действие {mark}");
        return (type, await EntryAsync(type, $"Запись действия {mark}"));
    }

    private static async Task<JsonElement> PostAsync(HttpClient client, Guid id, string action)
    {
        var response = await client.PostAsync($"/api/common-data/{id}/{action}", null);
        await OkAsync(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<Guid[]> ChoiceAsync(Guid type) =>
        [.. (await SendAsync(new ListCommonDataEntriesQuery(RecordsFor.Choice, CompositeTypeId: type))).Select(e => e.Id)];

    private async Task<int> CountAsync(ActivityAction action)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IActivityLog>()
            .CountAsync(ActivityVisibility.Whole, action.Code);
    }

    private async Task<ActivityRecord?> LastAsync(ActivityAction action)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IActivityLog>().LastAsync(action);
    }

    private async Task<T> SendAsync<T>(IRequest<T> request)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(request);
    }
}
