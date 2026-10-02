using System.IO.Compression;
using System.Text.Json;
using BHS.CRG.Application.Backup;
using BHS.CRG.Infrastructure.Backup;
using BHS.CRG.Modules.Costs.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Круг «снял — восстановил» на НАСТОЯЩИХ таблицах модуля счетов (issue #1158).
///
/// <para>Копия схемы модуля проверялась на поддельном модуле — и поначалу иначе было нельзя: у
/// <c>costs</c> не было ни одной таблицы. Таблицы появились (счёт, строки, разноска), а проверка осталась
/// на подделке: что настоящий счёт уезжает в копию и возвращается из неё тем же, не утверждал никто.
/// Механизм копии состав модуля спрашивает у базы, поэтому каждая новая колонка и каждое ограничение
/// модуля попадают в него без правки — и без проверки. Здесь она есть.</para>
///
/// <para>Сравнивается секция модуля ЦЕЛИКОМ, строка в строку: до потери и после восстановления. Так
/// проверка не знает состава таблиц и не устареет с первой же миграцией модуля.</para>
/// </summary>
// ⚠️ Собрание указывается КАЖДОМУ классу: атрибут не наследуется от базового.
[Collection("Integration")]
public class InvoiceBackupRoundTripTests(InvoiceLineHost host) : InvoiceLineTestBase(host)
{
    private readonly InvoiceLineHost _host = host;

    [Fact]
    public async Task Счёт_со_строками_и_разноской_возвращается_из_копии_тем_же()
    {
        var (client, _) = await SignInAsync("Admin");
        var (site, sections) = await SiteAsync("Комарова 36", "4 эт.");
        var invoice = await CreateAsync(client, complete: true);
        var view = await LinesAsync(client, invoice, [Line(cable, quantity: 10, price: 5m)]);
        await AllocateAsync(client, invoice, LineId(view, 1), [Part(site, quantity: 10, section: sections[0])]);

        var (archive, before) = await ExportAsync();
        var section = Assert.Single(before.ModuleData!);
        Assert.Equal(CostsDbContext.SchemaName, section.Schema);
        foreach (var table in (string[])["invoices", "invoice_lines", "invoice_allocations"])
            Assert.NotEmpty(section.Tables.Single(t => t.Table == table).Rows);

        // Потеря, ради которой копия существует: данных модуля нет. Ядро на месте.
        await ExecuteAsync("TRUNCATE " + string.Join(", ",
            section.Tables.Select(t => $"{CostsDbContext.SchemaName}.\"{t.Table}\"")) + " CASCADE");

        RestoreReport report;
        using (var scope = _host.Services.CreateScope())
            report = await scope.ServiceProvider.GetRequiredService<BackupService>().ImportAsync(archive);
        Assert.True(report.Success, string.Join("; ", report.Warnings));

        var (_, after) = await ExportAsync();
        var restored = Assert.Single(after.ModuleData!);
        Assert.Equal(section.Tables.Select(t => t.Table), restored.Tables.Select(t => t.Table));
        foreach (var (was, now) in section.Tables.Zip(restored.Tables))
            Assert.Equal(was.Rows.Select(r => r.GetRawText()), now.Rows.Select(r => r.GetRawText()));

        // И счёт читается обычной дорогой — со строкой и её разноской.
        var read = await ReadAsync(client, invoice);
        Assert.Equal(1, read.GetProperty("lines").GetArrayLength());
        Assert.True(read.GetProperty("allocation").GetProperty("allocated").GetBoolean());
    }

    private async Task<(MemoryStream Archive, BackupManifest Manifest)> ExportAsync()
    {
        using var scope = _host.Services.CreateScope();
        var (zip, _) = await scope.ServiceProvider.GetRequiredService<BackupService>()
            .ExportAsync(BackupScope.Full);
        await using var handle = zip;

        var archive = new MemoryStream();
        await zip.CopyToAsync(archive);
        archive.Position = 0;

        BackupManifest manifest;
        using (var reader = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true))
        using (var entry = reader.GetEntry("manifest.json")!.Open())
            manifest = JsonSerializer.Deserialize<BackupManifest>(entry)!;
        archive.Position = 0;

        return (archive, manifest);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(
            _host.Services.GetRequiredService<IConfiguration>().GetConnectionString("Postgres"));
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
