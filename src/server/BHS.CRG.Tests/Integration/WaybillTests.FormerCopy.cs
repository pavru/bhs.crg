using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using BHS.CRG.Application.Backup;
using BHS.CRG.Infrastructure.Backup;
using BHS.CRG.Modules.Costs.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Contour = BHS.CRG.Domain.Periods.PeriodContour;

namespace BHS.CRG.Tests.Integration;

// Накладная и резервная копия, снятая до появления учётной даты (issue #1285, ревью PR #1296).
public partial class WaybillTests
{
    /// <summary>
    /// Копия, снятая до issue #1285, колонки <c>accounting_on</c> не знает. Восстановление обязано её
    /// принять — первая редакция PR несла ограничение базы «у проведённой есть учётная дата», и такая
    /// копия на нём падала бы, — а проведённая накладная после него обязана остаться под замком периода:
    /// по дате отпуска, раз другой даты у неё нет.
    ///
    /// <para>Копия подделана из нынешней: у строк накладных убрано поле колонки — так строку и записала
    /// бы прежняя версия.</para>
    /// </summary>
    [Fact]
    public async Task Проведённая_накладная_возвращается_из_копии_снятой_до_учётной_даты()
    {
        var (client, _) = await SignInAsync("Supplier");
        var (site, _) = await SiteAsync("Накладная: копия до учётной даты");
        var id = (await PostedAsync(client, site, "2026-06-09", Row(cable, 10m, "м"))).GetProperty("id").GetGuid();

        var former = await FormerCopyAsync();
        await using (var scope = host.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<CostsDbContext>().Database
                .ExecuteSqlRawAsync($"TRUNCATE {CostsDbContext.SchemaName}.\"waybills\" CASCADE");

        RestoreReport report;
        using (var scope = host.Services.CreateScope())
            report = await scope.ServiceProvider.GetRequiredService<BackupService>().ImportAsync(former);
        Assert.True(report.Success, string.Join("; ", report.Warnings));

        var restored = await WaybillAsync(client, id);
        Assert.Equal("Posted", restored.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, restored.GetProperty("accountingOn").ValueKind);
        Assert.Equal(1, restored.GetProperty("lines").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, restored.GetProperty("locked").ValueKind);

        await CloseAsync(Contour.Construction(site), new DateOnly(2026, 6, 30));
        Assert.Contains("период закрыт по 30.06.2026", (await WaybillAsync(client, id)).GetProperty("locked").GetString());
    }

    /// <summary>Нынешняя копия, из строк накладных которой убрана учётная дата.</summary>
    private async Task<MemoryStream> FormerCopyAsync()
    {
        BackupManifest manifest;
        using (var scope = host.Services.CreateScope())
        {
            var (zip, _) = await scope.ServiceProvider.GetRequiredService<BackupService>().ExportAsync(BackupScope.Full);
            await using var handle = zip;
            using var reader = new ZipArchive(zip, ZipArchiveMode.Read);
            using var entry = reader.GetEntry("manifest.json")!.Open();
            manifest = JsonSerializer.Deserialize<BackupManifest>(entry)!;
        }

        var stripped = 0;
        var former = manifest with
        {
            ModuleData = [.. manifest.ModuleData!.Select(m => m with
            {
                Tables = [.. m.Tables.Select(t => t.Table != "waybills" ? t : t with
                {
                    Rows = [.. t.Rows.Select(row =>
                    {
                        var node = JsonNode.Parse(row.GetRawText())!.AsObject();
                        if (node.Remove("accounting_on")) stripped++;
                        return JsonSerializer.SerializeToElement(node);
                    })],
                })],
            })],
        };
        // Подделка, которая ничего не убрала, проверяла бы обычную копию и молчала об этом.
        Assert.True(stripped > 0, "В строках накладных копии нет поля accounting_on — подделка прежней копии ослепла.");

        var archive = new MemoryStream();
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
        using (var entry = zip.CreateEntry("manifest.json").Open())
            JsonSerializer.Serialize(entry, former);
        archive.Position = 0;
        return archive;
    }
}
