using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Backup;
using BHS.CRG.Application.Common;
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

    /// <summary>
    /// Скан счёта уезжает в копию ФАЙЛОМ и открывается после восстановления.
    ///
    /// <para>Строка счёта со ссылкой на скан в копию попадает (секция модуля), и без файла рядом она —
    /// обещание, которое не исполнится: счёт восстановлен, скан «приложен», а открыть нечего. Потеря
    /// здесь двойная, как в жизни: нет ни данных модуля, ни файла в хранилище.</para>
    /// </summary>
    [Fact]
    public async Task Скан_счёта_уезжает_в_копию_файлом_и_открывается_после_восстановления()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);
        var path = await AttachScanAsync(client, invoice, "Счёт 31.pdf", "%PDF-1.4 скан для копии");

        var (archive, manifest) = await ExportAsync();

        // Ссылка в копии есть — иначе отсутствие файла было бы следствием отсутствия строки.
        var rows = Assert.Single(manifest.ModuleData!).Tables.Single(t => t.Table == "invoices").Rows;
        Assert.Contains(rows, r => r.GetProperty("scan_blob_path").GetString() == path);

        using (var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true))
            Assert.True(zip.GetEntry($"blobs/{path}") is not null,
                $"файла скана нет в архиве; файлы в архиве: [{string.Join(", ",
                    zip.Entries.Where(e => e.FullName.StartsWith("blobs/", StringComparison.Ordinal)).Select(e => e.FullName))}]");
        archive.Position = 0;

        await ExecuteAsync($"TRUNCATE {CostsDbContext.SchemaName}.\"invoices\" CASCADE");
        using (var scope = _host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IBlobStorage>().DeleteAsync(path);

        RestoreReport report;
        using (var scope = _host.Services.CreateScope())
            report = await scope.ServiceProvider.GetRequiredService<BackupService>().ImportAsync(archive);
        Assert.True(report.Success, string.Join("; ", report.Warnings));

        var content = await client.GetAsync($"/api/costs/invoices/{invoice}/scan");
        Assert.True(content.IsSuccessStatusCode, $"скан после восстановления не открылся: {(int)content.StatusCode}");
        Assert.Contains("скан для копии", await content.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>Приложить скан и вернуть путь, по которому он лёг в хранилище.</summary>
    private static async Task<string> AttachScanAsync(HttpClient client, Guid id, string fileName, string body)
    {
        var version = (await ReadAsync(client, id)).GetProperty("version").GetString()!;
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(body));
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", fileName);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/costs/invoices/{id}/scan") { Content = form };
        request.Headers.TryAddWithoutValidation("If-Match", version);

        var response = await client.SendAsync(request);
        await OkAsync(response);
        var view = await response.Content.ReadFromJsonAsync<JsonElement>();
        return view.GetProperty("requisites").GetProperty("Скан").GetProperty("blobPath").GetString()!;
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
