using System.Net.Http.Json;
using BHS.CRG.Application.Activity;
using BHS.CRG.Modules.Costs.Endpoints;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Правка черновика накладной сворачивается в журнале (ТЗ COST-6, CORE-28.3) — тем же путём, что у
/// счёта: шапка и строки одной группы, через живые адреса. Без этой проверки потеря свёртки накладной
/// выглядела бы как исправный журнал из одиночных строк (ревью PR #1670).
/// </summary>
public partial class WaybillTests
{
    [Fact]
    public async Task Сессия_правки_черновика_накладной_два_ряда()
    {
        var (client, _) = await SignInAsync("Supplier");
        var (site, _) = await SiteAsync("Накладная: журнал правки");
        var id = await DraftAsync(client, site, "2026-09-05", Row(cable, 1m, "м"));
        var header = Header(site, "2026-09-05");

        for (var i = 2; i <= 4; i++)
        {
            header["receivedBy"] = $"Прораб {i}";
            await OkAsync(await client.PutAsJsonAsync($"/api/costs/waybills/{id}", header));
            await OkAsync(await LinesAsync(client, id, Row(cable, i, "м")));
        }

        using var scope = host.Services.CreateScope();
        var rows = (await scope.ServiceProvider.GetRequiredService<IActivityLog>()
            .ReadAsync(0, 200, ActivityVisibility.Whole)).Where(r => r.TargetId == id.ToString()).ToList();

        var edited = Assert.Single(rows, r => r.Action == WaybillActions.Changed.Code);
        var lines = Assert.Single(rows, r => r.Action == WaybillActions.LinesChanged.Code);
        Assert.Equal(3, edited.Count);
        // Строки: первая запись — от заведения черновика, ещё три — правки.
        Assert.Equal(4, lines.Count);
        Assert.Equal(edited.SessionId, lines.SessionId);
        Assert.Equal("строк: 1", lines.After);
    }
}
