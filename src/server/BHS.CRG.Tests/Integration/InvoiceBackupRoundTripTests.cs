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
    /// Файл счёта уезжает в копию ФАЙЛОМ и открывается после восстановления (issue #1261).
    ///
    /// <para>Строка счёта со ссылкой на файл в копию попадала всегда (секция модуля), а без файла рядом
    /// она — обещание, которое не исполнится: счёт восстановлен, файл «приложен», открыть нечего.
    /// Потеря здесь двойная, как в жизни: нет ни данных модуля, ни файла в хранилище.</para>
    /// </summary>
    [Fact]
    public async Task Файл_счёта_уезжает_в_копию_файлом_и_открывается_после_восстановления()
    {
        var (client, _) = await SignInAsync("Admin");
        await ForgetInvoicesAsync();
        var invoice = await CreateAsync(client);
        var path = await AttachScanAsync(client, invoice, "Счёт 31.pdf", "%PDF-1.4 скан для копии");

        var (archive, manifest) = await ExportAsync();

        // Ссылка в копии есть — иначе отсутствие файла было бы следствием отсутствия строки.
        var section = Assert.Single(manifest.ModuleData!);
        Assert.Contains(section.Tables.Single(t => t.Table == "invoices").Rows,
            r => r.GetProperty("scan_blob_path").GetString() == path);
        // Не «ровно он»: счета соседних тестов класса живут в той же базе, и их файлы копия берёт тоже.
        Assert.Contains(path, section.Files!);

        BackupSummary summary;
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true))
        {
            Assert.True(zip.GetEntry($"blobs/{path}") is not null,
                $"файла счёта нет в архиве; файлы в архиве: [{string.Join(", ",
                    zip.Entries.Where(e => e.FullName.StartsWith("blobs/", StringComparison.Ordinal)).Select(e => e.FullName))}]");
            using var passport = zip.GetEntry("summary.json")!.Open();
            summary = JsonSerializer.Deserialize<BackupSummary>(passport, JsonSerializerOptions.Web)!;
        }
        archive.Position = 0;

        // Паспорт называет файлы модулей числом — по нему список копий отличит прежнюю копию.
        Assert.Equal(section.Files!.Length, summary.ModuleBlobCount);
        // Вторая половина инвентарного сторожа: модуль назвал каждую колонку, где копия нашла путь.
        Assert.DoesNotContain(summary.Warnings ?? [], w => w.Contains("не сообщил") || w.Contains("внутри текста"));

        await LoseAsync(path);

        var report = await ImportAsync(archive);
        Assert.True(report.Success, string.Join("; ", report.Warnings));
        Assert.DoesNotContain(report.Warnings, w => w.Contains(Missing));

        var content = await client.GetAsync($"/api/costs/invoices/{invoice}/scan");
        Assert.True(content.IsSuccessStatusCode, $"файл после восстановления не открылся: {(int)content.StatusCode}");
        Assert.Contains("скан для копии", await content.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>Оценка веса копии считает файл счёта: она берёт пути из того же места, что и архив.</summary>
    [Fact]
    public async Task Оценка_веса_копии_считает_файл_счёта()
    {
        var (client, _) = await SignInAsync("Admin");
        await ForgetInvoicesAsync();
        var invoice = await CreateAsync(client);
        var body = "%PDF-1.4 " + new string('я', 4000);
        await AttachScanAsync(client, invoice, "Счёт 32.pdf", body);

        var (archive, _) = await ExportAsync();
        using var scope = _host.Services.CreateScope();
        var estimate = await scope.ServiceProvider.GetRequiredService<BackupService>()
            .EstimateSizeAsync(long.MaxValue, BackupScope.Full);

        Assert.True(estimate.Variant.BlobBytes >= System.Text.Encoding.UTF8.GetByteCount(body),
            $"файл счёта в оценку не вошёл: файлов {estimate.Variant.BlobCount}, байт {estimate.Variant.BlobBytes}");
        Assert.InRange(Math.Abs(estimate.Variant.TotalBytes - archive.Length), 0, 1024);
    }

    /// <summary>
    /// Копия, снятая до исправления: строка счёта есть, файла нет. Восстановление обязано СКАЗАТЬ об
    /// этом — раньше оно отвечало успехом без единого слова, и про пустой файл узнавали, когда он
    /// понадобился. Копия подделана из нынешней: убран файл и перечень файлов модуля.
    /// </summary>
    [Fact]
    public async Task Восстановление_из_прежней_копии_называет_файлы_которых_нет()
    {
        var (client, _) = await SignInAsync("Admin");
        await ForgetInvoicesAsync();
        var invoice = await CreateAsync(client);
        var path = await AttachScanAsync(client, invoice, "Счёт 33.pdf", "%PDF-1.4 скан");

        var (archive, manifest) = await ExportAsync();
        var former = FormerCopy(manifest);

        // Файл на месте — говорить не о чем: предупреждение, звучащее всегда, перестаёт значить что-либо.
        var calm = await ImportAsync(former);
        Assert.DoesNotContain(calm.Warnings, w => w.Contains(Missing));

        await LoseAsync(path);
        former.Position = 0;
        var report = await ImportAsync(former);

        Assert.True(report.Success, string.Join("; ", report.Warnings));
        var warning = Assert.Single(report.Warnings, w => w.Contains(Missing));
        // Пропал ровно один: файлы счетов соседних тестов на месте, и в счёт потерь они не идут.
        Assert.Matches(@"файлы счетов — 1 из \d+\.", warning);
        Assert.Contains("файлы модулей не брала", warning);

        // И нынешняя копия без файла в хранилище говорит то же, но без совета «снимите заново».
        archive.Position = 0;
        var stripped = WithoutBlobs(archive);
        await LoseAsync(path);
        var plain = Assert.Single((await ImportAsync(stripped)).Warnings, w => w.Contains(Missing));
        Assert.DoesNotContain("не брала", plain);
    }

    /// <summary>
    /// Файл счёта заменили после распознавания (ревью PR #1289). Прежний файл удалён нарочно, а
    /// распознавание помнит его путь до следующего прогона — и копия не должна звать это потерей: ни
    /// при снятии («файлов не оказалось в хранилище»), ни при восстановлении на другой сервер.
    /// </summary>
    [Fact]
    public async Task Заменённый_файл_счёта_копия_потерей_не_зовёт()
    {
        var (client, _) = await SignInAsync("Admin");
        await ForgetInvoicesAsync();
        var invoice = await CreateAsync(client);
        var former = await AttachScanAsync(client, invoice, "Счёт 34.pdf", "%PDF-1.4 прежний");

        // Распознавание запомнило прежний файл; строка пишется прямо — движка у теста нет.
        await ExecuteAsync(
            $"INSERT INTO {CostsDbContext.SchemaName}.invoice_recognitions " +
            "(invoice_id, scan_blob_path, outcome, notes, started_at) " +
            $"VALUES ('{invoice}', '{former.Replace("'", "''")}', 'Failed', '{{}}', now())");
        var current = await AttachScanAsync(client, invoice, "Счёт 34 (верный).pdf", "%PDF-1.4 нынешний");
        Assert.NotEqual(former, current);

        var (archive, manifest) = await ExportAsync();
        var section = Assert.Single(manifest.ModuleData!);
        Assert.Contains(section.Tables.Single(t => t.Table == "invoice_recognitions").Rows,
            r => r.GetProperty("scan_blob_path").GetString() == former);
        Assert.Equal([current], section.Files);

        await LoseAsync(current);
        var report = await ImportAsync(archive);
        Assert.True(report.Success, string.Join("; ", report.Warnings));
        Assert.DoesNotContain(report.Warnings, w => w.Contains(Missing));
    }

    /// <summary>Слова отчёта о файлах, которых нет ни в копии, ни в хранилище.</summary>
    private const string Missing = "в хранилище они не найдены";

    /// <summary>
    /// Счета соседних тестов класса — долой: база у класса одна, а счёт, файл которого сосед нарочно
    /// потерял, восстановление справедливо назвало бы и здесь.
    /// </summary>
    private Task ForgetInvoicesAsync() =>
        ExecuteAsync($"TRUNCATE {CostsDbContext.SchemaName}.\"invoices\" CASCADE");

    /// <summary>Потеря, ради которой копия существует: нет ни строк счетов, ни файла.</summary>
    private async Task LoseAsync(string path)
    {
        await ExecuteAsync($"TRUNCATE {CostsDbContext.SchemaName}.\"invoices\" CASCADE");
        using var scope = _host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IBlobStorage>().DeleteAsync(path);
    }

    private async Task<RestoreReport> ImportAsync(Stream archive)
    {
        using var scope = _host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<BackupService>().ImportAsync(archive);
    }

    /// <summary>Архив, каким его сняла бы версия до issue #1261: без файлов модулей и без их перечня.</summary>
    private static MemoryStream FormerCopy(BackupManifest manifest)
    {
        var former = manifest with { ModuleData = [.. manifest.ModuleData!.Select(m => m with { Files = null })] };
        var archive = new MemoryStream();
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
        using (var entry = zip.CreateEntry("manifest.json").Open())
            JsonSerializer.Serialize(entry, former);
        archive.Position = 0;
        return archive;
    }

    /// <summary>Тот же архив без единого файла — так выглядит копия, у которой файл пропал при снятии.</summary>
    private static MemoryStream WithoutBlobs(MemoryStream source)
    {
        var archive = new MemoryStream();
        using (var from = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true))
        using (var to = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var entry in from.Entries.Where(e => !e.FullName.StartsWith("blobs/", StringComparison.Ordinal)))
            {
                using var input = entry.Open();
                using var output = to.CreateEntry(entry.FullName).Open();
                input.CopyTo(output);
            }
        archive.Position = 0;
        return archive;
    }

    /// <summary>Приложить файл и вернуть путь, по которому он лёг в хранилище.</summary>
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
