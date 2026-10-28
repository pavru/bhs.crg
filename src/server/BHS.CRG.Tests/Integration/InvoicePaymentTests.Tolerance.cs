using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Modules;
using BHS.CRG.Application.Activity;
using BHS.CRG.Application.Backup;
using BHS.CRG.Application.Settings;
using BHS.CRG.Infrastructure.Backup;
using BHS.CRG.Modules.Costs;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Costs.Endpoints;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Смена допуска пишет книгу (E2, issue #1100, PR 5, spec v3.3 п. 1.9): затронутым счетам — открытие числами
/// старого допуска и выравнивание по правилу (б′), день остатка — в колонку, версия формы — дальше.
/// Сценарии S05, S29, S31 и S32 пятого прогона — на живой базе, через адрес настроек.
///
/// <para>Счёт везде — <c>TwoSitesAsync</c>: строка 1 — 40 000 на стройку А, строка 2 — 60 000 на стройку Б,
/// последняя часть разноски — у Б; оплата 15 дней назад. Сумма к оплате даёт расхождение в допуске 1,00.</para>
/// </summary>
public partial class InvoicePaymentTests
{
    /// <summary>
    /// S05: смена туда и обратно у счёта, чья последняя часть лежит в закрытом дне. Закрытый день стройки Б
    /// правится днём смены, открытый день остатка — своим; обратная смена кладёт зеркальные строки. «На момент
    /// закрытия» Б за день оплаты всё время 59 999,20.
    /// </summary>
    [Fact]
    public async Task S05_смена_допуска_пишет_книгу_запертому_счёту_туда_и_обратно()
    {
        var (admin, _) = await SignInAsync("Admin");
        var today = await TodayAsync();
        var paidOn = today.AddDays(-15);
        var (invoice, a, b) = await TwoSitesAsync(admin, total: 99_999.20m);
        await PayAsync(admin, invoice, paidOn, await PreviewAsync(admin, invoice, paidOn), null);
        await CloseAsync(b, today.AddDays(-10));
        var version = (await ReadAsync(admin, invoice)).GetProperty("version").GetString();
        try
        {
            await ToleranceAsync(admin, "0.50");

            var book = await RecordsAsync(invoice);
            Assert.Equal([InvoiceBookKind.Opening, InvoiceBookKind.Alignment], book.Select(r => r.Kind));
            Assert.Equal([1.00m, 0.50m], book.Select(r => r.Tolerance));
            Assert.All(book, r => Assert.Equal(today, r.Day));
            Assert.Equal([(a, paidOn, 40_000m), (b, paidOn, 59_999.20m)], Rows(book[0]));
            Assert.Equal([(b, today, 0.80m), (null, paidOn, -0.80m)], Rows(book[1]));
            Assert.Equal([null, "допуск 1,00 → 0,50"], await ReasonsAsync(invoice));
            // Остаток появился сменой — день самой поздней части с деньгами, в колонке; версия формы сдвинута.
            Assert.Equal(paidOn, await RemainderAsync(invoice));
            Assert.NotEqual(version, (await ReadAsync(admin, invoice)).GetProperty("version").GetString());
            var record = (await ToleranceEventsAsync(invoice)).Single();
            Assert.Equal(("1,00", "0,50"), (record.Before, record.After));
        }
        finally
        {
            await ToleranceAsync(admin, null);
        }

        var back = await RecordsAsync(invoice);
        Assert.Equal(3, back.Count);
        Assert.Equal([(b, today, -0.80m), (null, paidOn, 0.80m)], Rows(back[2]));
        Assert.Null(await RemainderAsync(invoice));
        Assert.Equal(59_999.20m, back.SelectMany(r => r.Lines).Where(l => l.Cell.Target?.ConstructionId == b && l.Day == paidOn).Sum(l => l.Gross));
    }

    /// <summary>
    /// S31: предупреждение называет два числа, а книгу получают только запертые. С1 (−0,80) и С2 (+0,50)
    /// заперты, С3 — нет: его деньги пересчитываются, как до E2, без книги и без штампа остатка.
    /// </summary>
    [Fact]
    public async Task S31_книгу_получают_только_запертые_а_предупреждение_считает_всех()
    {
        var (admin, _) = await SignInAsync("Admin");
        var today = await TodayAsync();
        var paidOn = today.AddDays(-15);
        // Счета соседних тестов на общей базе тоже считаются — сверяется прирост.
        var (changed, recorded) = await ImpactAsync(admin, "0.40");

        var c1 = await PaidOffAsync(admin, 99_999.20m, paidOn);
        var c2 = await PaidOffAsync(admin, 100_000.50m, paidOn);
        var c3 = await PaidOffAsync(admin, 99_999.20m, paidOn);
        await CloseAsync(c1.B, today.AddDays(-10));
        await CloseAsync(c2.B, today.AddDays(-10));

        Assert.Equal((changed + 3, recorded + 2), await ImpactAsync(admin, "0.40"));
        try
        {
            await ToleranceAsync(admin, "0.40");

            Assert.Equal([(c1.B, today, 0.80m), (null, paidOn, -0.80m)], Rows((await RecordsAsync(c1.Invoice))[1]));
            Assert.Equal([(c2.B, today, -0.50m), (null, paidOn, 0.50m)], Rows((await RecordsAsync(c2.Invoice))[1]));
            Assert.Equal((paidOn, paidOn), (await RemainderAsync(c1.Invoice), await RemainderAsync(c2.Invoice)));
            Assert.Equal((0, 0), await BookAsync(c3.Invoice));
            Assert.Null(await RemainderAsync(c3.Invoice));
        }
        finally
        {
            await ToleranceAsync(admin, null);
        }

        Assert.Equal(3, (await RecordsAsync(c1.Invoice)).Count);
        Assert.Equal(3, (await RecordsAsync(c2.Invoice)).Count);
        Assert.Equal((0, 0), await BookAsync(c3.Invoice));

        // Предпросмотр проверяет значения, как запись: негодное — отказ у своего поля.
        var refused = await admin.PostAsJsonAsync("/api/settings/modules/costs/preview",
            new { values = new Dictionary<string, string?> { [ToleranceKey] = "много" } });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    /// <summary>
    /// S29: остаток +0,50 рождён понижением допуска у счёта, закрытого периодом компании. Закрыто всё —
    /// поэтому обе строки выравнивания днём смены; день остатка в колонке — день оплаты, как его читают.
    /// Исправление «только текст» после смены ничего не проводит: книга уже при нынешнем допуске.
    /// </summary>
    [Fact]
    public async Task S29_остаток_от_смены_допуска_в_закрытом_периоде_компании()
    {
        var (admin, _) = await SignInAsync("Admin");
        var today = await TodayAsync();
        var paidOn = today.AddDays(-15);
        var (invoice, a, b) = await TwoSitesAsync(admin, total: 100_000.50m);
        await PayAsync(admin, invoice, paidOn, await PreviewAsync(admin, invoice, paidOn), null);
        await CloseCompanyAsync(today.AddDays(-10));
        try
        {
            await ToleranceAsync(admin, "0.40");
            var book = await RecordsAsync(invoice);
            Assert.Equal([(a, paidOn, 40_000m), (b, paidOn, 60_000.50m)], Rows(book[0]));
            Assert.Equal([(b, today, -0.50m), (null, today, 0.50m)], Rows(book[1]));
            Assert.Equal(paidOn, await RemainderAsync(invoice));

            var view = await ReadAsync(admin, invoice);
            var body = Correction(view, "опечатка в наименовании", lines => lines[0]["supplierText"] = "Кабель ВВГнг 3×2,5");
            var preview = await CorrectionPreviewAsync(admin, invoice, view, body);
            var records = preview.GetProperty("records").EnumerateArray().ToList();
            Assert.Equal("correction", records.Single().GetProperty("kind").GetString());
            Assert.Empty(Lines(records[0]));

            await CorrectAsync(admin, invoice, view, body with { Stamp = preview.GetProperty("stamp").GetString() });
            Assert.Equal((3, 1), await BookAsync(invoice));
            Assert.Equal(paidOn, await RemainderAsync(invoice));
        }
        finally
        {
            await ToleranceAsync(admin, null);
            await ReopenCompanyAsync(today.AddDays(-10));
        }
    }

    /// <summary>
    /// S32 (а): между предпросмотром исправления и записью сменили допуск. Смена записала счёту книгу и
    /// сдвинула версию формы — запись отказывает «счёт тем временем изменили». Перечитанный и пересчитанный —
    /// проходит, и исправление ложится после выравнивания.
    /// </summary>
    [Fact]
    public async Task S32а_смена_допуска_после_предпросмотра_исправления_устаревшая_форма()
    {
        var (admin, _) = await SignInAsync("Admin");
        var today = await TodayAsync();
        // Строки на 100 000, к оплате 100 000,80: расхождение 0,80 при допуске 1,00 уходит в последнюю долю.
        var (invoice, _, _) = await LockedAsync(admin, today, total: 100_000.80m);
        try
        {
            var view = await ReadAsync(admin, invoice);
            var body = Correction(view, "опечатка в наименовании", lines => lines[0]["supplierText"] = "Кабель ВВГнг 3×2,5");
            var seen = await CorrectionPreviewAsync(admin, invoice, view, body);

            await ToleranceAsync(admin, "0.50");
            Assert.Equal((2, 0), await BookAsync(invoice));
            var stale = await CorrectionRawAsync(admin, invoice, "", view.GetProperty("version").GetString(),
                body with { Stamp = seen.GetProperty("stamp").GetString() });
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            Assert.Contains("тем временем изменили", await ErrorAsync(stale));

            // При 0,50 расхождение 0,80 сверх допуска — но оно было и до исправления: «не хуже» пропускает.
            view = await ReadAsync(admin, invoice);
            var fresh = await CorrectionPreviewAsync(admin, invoice, view, body);
            await CorrectAsync(admin, invoice, view, body with { Stamp = fresh.GetProperty("stamp").GetString() });
            Assert.Equal(
                [InvoiceBookKind.Opening, InvoiceBookKind.Alignment, InvoiceBookKind.Correction],
                (await RecordsAsync(invoice)).Select(r => r.Kind));
        }
        finally
        {
            await ToleranceAsync(admin, null);
        }
    }

    /// <summary>
    /// S32 (б): исправление пришло между двумя фиксациями смены — модуль уже записал книгу при 0,50, а ядро
    /// ещё не записало значение, и исправление читает 1,00. Страховка (п. 1.3 шаг 2) выравнивает книгу обратно
    /// перед его записью; выравнивание после фиксации ядра снова приводит её к 0,50.
    /// </summary>
    [Fact]
    public async Task S32б_исправление_между_фиксациями_смены_выравнивает_страховка()
    {
        var (admin, _) = await SignInAsync("Admin");
        var today = await TodayAsync();
        var paidOn = today.AddDays(-15);
        var (invoice, _, b) = await TwoSitesAsync(admin, total: 99_999.20m);
        await PayAsync(admin, invoice, paidOn, await PreviewAsync(admin, invoice, paidOn), null);
        await CloseAsync(b, today.AddDays(-10));
        try
        {
            await ModuleCommitsAsync("0.50");

            var view = await ReadAsync(admin, invoice);
            var body = Correction(view, "опечатка в наименовании", lines => lines[0]["supplierText"] = "Кабель ВВГнг 3×2,5");
            var preview = await CorrectionPreviewAsync(admin, invoice, view, body);
            await CorrectAsync(admin, invoice, view, body with { Stamp = preview.GetProperty("stamp").GetString() });

            var book = await RecordsAsync(invoice);
            Assert.Equal(
                [InvoiceBookKind.Opening, InvoiceBookKind.Alignment, InvoiceBookKind.Alignment, InvoiceBookKind.Correction],
                book.Select(r => r.Kind));
            Assert.Equal([1.00m, 0.50m, 1.00m, 1.00m], book.Select(r => r.Tolerance));
            Assert.Equal([(b, today, -0.80m), (null, paidOn, 0.80m)], Rows(book[2]));

            // Ядро зафиксировало значение — и выровняло книгу к нему.
            using (var scope = host.Services.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<IAppSettingsStore>().SetAsync(ToleranceKey, "0.50");
                await scope.ServiceProvider.GetRequiredService<ModuleSettingWriter>().AlignAllAsync();
            }
            book = await RecordsAsync(invoice);
            Assert.Equal(InvoiceBookKind.Alignment, book[4].Kind);
            Assert.Equal([(b, today, 0.80m), (null, paidOn, -0.80m)], Rows(book[4]));
            Assert.Equal(paidOn, await RemainderAsync(invoice));
        }
        finally
        {
            await ToleranceAsync(admin, null);
        }
    }

    /// <summary>
    /// Сбой между фиксациями: модуль записал книгу, ядро значение не записало (I5). Выравнивание при старте
    /// возвращает книгу к действующему допуску — закрытые дни целы, документ и книга снова сходятся. Второй
    /// старт не пишет ничего.
    /// </summary>
    [Fact]
    public async Task Сбой_между_фиксациями_смены_чинит_выравнивание_при_старте()
    {
        var (admin, _) = await SignInAsync("Admin");
        var today = await TodayAsync();
        var paidOn = today.AddDays(-15);
        var (invoice, _, b) = await TwoSitesAsync(admin, total: 99_999.20m);
        await PayAsync(admin, invoice, paidOn, await PreviewAsync(admin, invoice, paidOn), null);
        await CloseAsync(b, today.AddDays(-10));

        await ModuleCommitsAsync("0.50");
        Assert.Equal(paidOn, await RemainderAsync(invoice));

        for (var start = 0; start < 2; start++)
        {
            using var scope = host.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ModuleSettingWriter>().AlignAllAsync();
        }

        var book = await RecordsAsync(invoice);
        Assert.Equal([1.00m, 0.50m, 1.00m], book.Select(r => r.Tolerance));
        Assert.Equal([(b, today, -0.80m), (null, paidOn, 0.80m)], Rows(book[2]));
        Assert.Equal(["допуск 0,50 → 1,00"], (await ReasonsAsync(invoice)).Skip(2));
        Assert.Null(await RemainderAsync(invoice));
    }

    /// <summary>
    /// Предупреждение согласует со счётом всё сказуемое, а не одно существительное: «1 счёт заперт … или уже
    /// исправлялся». Окно (0,98; 0,99] — расхождение 0,99 только у этого счёта: у соседей по общей базе
    /// (и у <see cref="LockedGapAsync" />) расхождения другие.
    /// </summary>
    [Fact]
    public async Task Предупреждение_согласует_сказуемое_с_числом_счетов()
    {
        var (admin, _) = await SignInAsync("Admin");
        await LockedGapAsync(admin, total: 99_999.01m);

        var text = (await ImpactOfAsync(admin, "0.98")).GetProperty("text").GetString();
        Assert.Contains("Изменятся суммы 1 оплаченного счёта", text);
        Assert.Contains("1 счёт заперт закрытым периодом или уже исправлялся: смена запишет книгу", text);
    }

    /// <summary>
    /// Книга, последняя запись которой посчитана другим допуском, но деньги при нынешнем — те же, выравнивания не
    /// ждёт: иначе такой счёт перечитывался бы под замками после каждой смены и при каждом старте, кончаясь ничем.
    /// </summary>
    [Fact]
    public async Task Отставшая_книга_без_разницы_в_деньгах_выравнивания_не_ждёт()
    {
        var (admin, _) = await SignInAsync("Admin");
        var invoice = await LockedGapAsync(admin);
        try
        {
            await ToleranceAsync(admin, OddTolerance);
            Assert.Equal([1.00m, 0.97m], (await RecordsAsync(invoice)).Select(r => r.Tolerance));

            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CostsDbContext>();
            // Расхождение 0,98 сверх и 0,97, и 0,50 — деньги одни; в допуске 1,00 — другие.
            Assert.DoesNotContain(invoice, await InvoiceBook.BehindAsync(db, 0.50m, default));
            Assert.Contains(invoice, await InvoiceBook.BehindAsync(db, 1.00m, default));
        }
        finally
        {
            await ToleranceAsync(admin, null);
        }
    }

    /// <summary>
    /// Копия без данных модуля сменила бы допуск над здешними счетами — обычная смена, но без книги, и закрытый
    /// месяц запертого счёта сдвинулся бы молча. Восстановление такую смену не проводит и говорит почему.
    /// </summary>
    [Fact]
    public async Task Копия_без_данных_модуля_допуск_над_запертыми_счетами_не_меняет()
    {
        var (admin, _) = await SignInAsync("Admin");
        var invoice = await LockedGapAsync(admin);

        RestoreReport report;
        using (var scope = host.Services.CreateScope())
        {
            var backup = scope.ServiceProvider.GetRequiredService<BackupService>();
            var (zip, _) = await backup.ExportAsync(BackupScope.Full);
            BackupManifest manifest;
            await using (zip)
            using (var reader = new ZipArchive(zip, ZipArchiveMode.Read))
            using (var entry = reader.GetEntry("manifest.json")!.Open())
                manifest = JsonSerializer.Deserialize<BackupManifest>(entry)!;

            // Конфигурационная: данных модулей нет, а допуск — другой.
            var copy = new MemoryStream();
            using (var writer = new ZipArchive(copy, ZipArchiveMode.Create, leaveOpen: true))
            using (var entry = writer.CreateEntry("manifest.json").Open())
                JsonSerializer.Serialize(entry, manifest with
                {
                    ModuleData = null,
                    AppSettings = [.. (manifest.AppSettings ?? []).Where(a => a.Key != ToleranceKey),
                        new BackupAppSetting(ToleranceKey, OddTolerance, DateTimeOffset.UtcNow)],
                });
            copy.Position = 0;
            report = await backup.ImportAsync(copy);
        }

        Assert.True(report.Success, string.Join("; ", report.Warnings));
        Assert.Contains(report.Warnings, w => w.Contains("Значение НЕ изменено") && w.Contains("закрытым периодом"));
        using (var scope = host.Services.CreateScope())
            Assert.Null(await scope.ServiceProvider.GetRequiredService<IAppSettingsStore>().GetAsync(ToleranceKey));
        Assert.Equal((0, 0), await BookAsync(invoice));
    }

    // ── Помощники ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Строки записи книги: стройка (у остатка — <c>null</c>), день, сумма с НДС. Стройки — первыми и по сумме:
    /// книга упорядочивает строки по идентификатору стройки, а он у каждого прогона свой.
    /// </summary>
    private static List<(Guid? Site, DateOnly Day, decimal Gross)> Rows(BookRecord record) =>
        [.. record.Lines.Select(l => (l.Cell.Target?.ConstructionId, l.Day, l.Gross)).OrderBy(l => l.Item1 is null).ThenBy(l => l.Gross)];

    /// <summary>Оплаченный счёт с расхождением в допуске 1,00 — без закрытий.</summary>
    private async Task<(Guid Invoice, Guid B)> PaidOffAsync(HttpClient admin, decimal total, DateOnly paidOn)
    {
        var (invoice, _, b) = await TwoSitesAsync(admin, total);
        await PayAsync(admin, invoice, paidOn, await PreviewAsync(admin, invoice, paidOn), null);
        return (invoice, b);
    }

    /// <summary>Предупреждение смены допуска с умолчания на <paramref name="value" />: два числа.</summary>
    private static async Task<(int Changed, int Recorded)> ImpactAsync(HttpClient admin, string value)
    {
        var impact = await ImpactOfAsync(admin, value);
        Assert.False(string.IsNullOrWhiteSpace(impact.GetProperty("text").GetString()));
        return (impact.GetProperty("changed").GetInt32(), impact.GetProperty("recorded").GetInt32());
    }

    private static async Task<JsonElement> ImpactOfAsync(HttpClient admin, string value)
    {
        var response = await admin.PostAsJsonAsync("/api/settings/modules/costs/preview",
            new { values = new Dictionary<string, string?> { [ToleranceKey] = value } });
        await OkAsync(response);
        var impact = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("impacts").EnumerateArray().Single();
        Assert.Equal(ToleranceKey, impact.GetProperty("key").GetString());
        return impact;
    }

    /// <summary>
    /// Допуск, при котором деньги меняются только у счёта <see cref="LockedGapAsync" />: его расхождение 0,98 —
    /// между 0,97 и умолчанием, а у счетов соседних тестов расхождения другие.
    /// </summary>
    private const string OddTolerance = "0.97";

    /// <summary>Оплаченный и запертый закрытием стройки Б счёт — по умолчанию с расхождением 0,98.</summary>
    private async Task<Guid> LockedGapAsync(HttpClient admin, decimal total = 99_999.02m)
    {
        var today = await TodayAsync();
        var (invoice, b) = await PaidOffAsync(admin, total, today.AddDays(-15));
        await CloseAsync(b, today.AddDays(-10));
        return invoice;
    }

    /// <summary>
    /// Первая фиксация смены с умолчания на <paramref name="value" /> — модуль записал и зафиксировал, а
    /// значение в хранилище прежнее: ядро ещё не записало (или сорвалось).
    /// </summary>
    private async Task ModuleCommitsAsync(string value)
    {
        using var scope = host.Services.CreateScope();
        var handler = scope.ServiceProvider.GetServices<IModuleSettingChange>().Single(h => h.Setting.Key == ToleranceKey);
        var boundaries = await scope.ServiceProvider.GetRequiredService<IModulePeriods>().BoundariesAsync();
        await handler.ApplyAsync(new ModuleSettingChange(null, value, boundaries));
    }

    /// <summary>События «книга выровнена сменой допуска» у счёта.</summary>
    private async Task<List<BHS.CRG.Application.Activity.ActivityRow>> ToleranceEventsAsync(Guid invoice)
    {
        using var scope = host.Services.CreateScope();
        return [.. (await scope.ServiceProvider.GetRequiredService<IActivityLog>()
                .ReadAsync(0, 50, ActivityVisibility.Whole, InvoiceActions.ToleranceAligned.Code))
            .Where(r => r.TargetId == invoice.ToString())];
    }
}
