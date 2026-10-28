using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Activity;
using BHS.CRG.Domain.Activity;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Costs.Endpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Удаление накладной (issue #1283): удаляется только черновик — насовсем, вместе со строками.
///
/// <para><b>Сторож задачи</b> — отказы: проведённая и человек без права правки здесь; устаревшую и
/// неназванную версию, а с ней и накладную закрытого периода новому адресу проверяют переписи адресов
/// рядом (<c>WaybillTests.ClosedPeriod</c>) — они находят его сами. Ломается он снятием проверки состояния: тогда удалением убирали бы материалы, уже выданные
/// на стройку, — и перечень отпущенного менялся бы без возврата в черновик, то есть без следа.</para>
/// </summary>
public partial class WaybillTests
{
    private async Task<List<BHS.CRG.Application.Activity.ActivityRow>> JournalAsync(Guid waybill, string action)
    {
        using var scope = host.Services.CreateScope();
        var records = await scope.ServiceProvider.GetRequiredService<IActivityLog>()
            .ReadAsync(0, 200, ActivityVisibility.Whole, action);
        return [.. records.Where(r => r.TargetId == waybill.ToString())];
    }

    /// <summary>
    /// Главный путь: заведённый по ошибке черновик удаляется — его нет ни в списке, ни в базе вместе со
    /// строками, а в журнале осталась запись, по которой удалённое можно опознать.
    /// </summary>
    [Fact]
    public async Task Черновик_накладной_удаляется_насовсем_вместе_со_строками()
    {
        var (client, _) = await SignInAsync("Supplier");
        var (site, _) = await SiteAsync("Накладная: удаление");
        var id = await DraftAsync(client, site, "2026-09-14", Row(cable, 3m, "м"), Row(null, 2m, "шт", text: "Хомут"));
        var number = (await WaybillAsync(client, id)).GetProperty("number").GetString()!;

        var deleted = await client.DeleteAsync($"/api/costs/waybills/{id}");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/costs/waybills/{id}")).StatusCode);
        Assert.DoesNotContain(await ListAsync(client, ""), w => w.GetProperty("id").GetGuid() == id);

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CostsDbContext>();
            Assert.False(await db.Waybills.AnyAsync(w => w.Id == id));
            Assert.False(await db.WaybillLines.AnyAsync(l => l.WaybillId == id));
        }

        var record = Assert.Single(await JournalAsync(id, WaybillActions.Deleted.Code));
        Assert.Contains(number, record.TargetLabel);
        Assert.Contains("14.09.2026", record.TargetLabel);
        Assert.Contains("склад: Основной склад", record.Before);
        Assert.Contains("получатель: ", record.Before);
        Assert.Contains("Прораб Иванов", record.Before);
        Assert.Contains("строк: 2", record.Before);
        // История удалённой накладной находится по тому же targetId: запись о создании на месте.
        Assert.Single(await JournalAsync(id, WaybillActions.Created.Code));

        // Повтор отвечает «нет такой» — клиент читает это как «уже удалена».
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/costs/waybills/{id}")).StatusCode);
    }

    /// <summary>
    /// Проведённая не удаляется: отказ называет выход — вернуть в черновик. Возвращённая и удалённая не
    /// оставляет строк в «Материалах на объекте».
    /// </summary>
    [Fact]
    public async Task Проведённая_накладная_удаляется_только_через_возврат_в_черновик()
    {
        var (client, _) = await SignInAsync("Supplier");
        var (site, _) = await SiteAsync("Накладная: удаление проведённой");
        var id = (await PostedAsync(client, site, "2026-09-15", Row(cable, 10m, "м"))).GetProperty("id").GetGuid();

        var refused = await client.DeleteAsync($"/api/costs/waybills/{id}");

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("Верните накладную в черновик", await refused.Content.ReadAsStringAsync());
        Assert.Single((await MaterialsAsync(client, site)).GetProperty("items").EnumerateArray());
        Assert.Empty(await JournalAsync(id, WaybillActions.Deleted.Code));

        await OkAsync(await client.PostAsync($"/api/costs/waybills/{id}/draft", null));
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/costs/waybills/{id}")).StatusCode);

        var materials = await MaterialsAsync(client, site);
        Assert.Empty(materials.GetProperty("items").EnumerateArray());
        Assert.Equal(0, materials.GetProperty("unmatchedLines").GetInt32());
    }

    /// <summary>Отдельного права на удаление нет: удаляет тот, кто правит накладные. Читающий — не удаляет.</summary>
    [Fact]
    public async Task Без_права_правки_накладную_не_удалить()
    {
        var (editor, _) = await SignInAsync("Supplier");
        var (site, _) = await SiteAsync("Накладная: удаление без права");
        var id = await DraftAsync(editor, site, "2026-09-16");
        var reader = await WithOnlyAsync("costs.waybill.read");

        var refused = await reader.DeleteAsync($"/api/costs/waybills/{id}");

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await editor.GetAsync($"/api/costs/waybills/{id}")).StatusCode);
    }
}
