using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Api.Modules.Tables;
using BHS.CRG.Application.Activity;
using BHS.CRG.Modules.Costs.Data;
using Microsoft.Extensions.DependencyInjection;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Выгрузка таблицы в XLSX — «то, что на экране» (задача G3b, issue #1096; ТЗ CORE-33).
///
/// <para>Сторож задачи — <see cref="Закрытая_колонка_в_файл_не_идёт_и_названа_в_шапке" />: выгрузка
/// читает строки службой таблиц, и вычистку закрытого обойти ей нечем. Заведи она своё чтение —
/// сумма ушла бы в файл человеку без права на суммы.</para>
/// </summary>
[Collection("Integration")]
public sealed class TableExportTests(InvoiceLineHost host) : ModuleTableSeededTests(host)
{
    private const string Export = $"/api/tables/{Address}/export";

    private readonly InvoiceLineHost host = host;

    private static string Mine(Seed seed) =>
        JsonSerializer.Serialize(new { type = "condition", column = InvoiceRequisites.NumberKey, op = "starts_with", value = seed.Tag });

    [Fact]
    public async Task В_файл_идёт_весь_отбор_значениями_своего_вида_с_итогом_под_таблицей()
    {
        var (admin, _) = await SignInAsync("Admin");
        var seed = await SeedAsync(admin);

        var response = await admin.PostAsJsonAsync(Export, new
        {
            columns = new[] { InvoiceRequisites.NumberKey, InvoiceRequisites.TotalKey, InvoiceRequisites.DueDateKey },
            filter = Mine(seed),
            sort = new[] { new { column = InvoiceRequisites.NumberKey, descending = false } },
            totals = new[] { new { column = InvoiceRequisites.TotalKey, aggregate = "sum" } },
            view = "Мои счета",
        });
        await OkAsync(response);
        Assert.Contains(".xlsx", response.Content.Headers.ContentDisposition!.FileNameStar);
        Assert.Contains("Мои счета", response.Content.Headers.ContentDisposition.FileNameStar);

        var sheet = await SheetAsync(response);
        var preamble = Lines(sheet);
        Assert.Contains(preamble, l => l.EndsWith("— Мои счета"));
        // Отбор словами собран сервером из того же дерева, что отобрало строки.
        Assert.Contains(preamble, l => l.StartsWith("Отбор: Номер") && l.EndsWith($"начинается с «{seed.Tag}»"));
        Assert.Contains("Строк: 8", preamble);
        Assert.Contains(preamble, l => l.StartsWith("Суммы — в валюте учёта: "));

        var header = HeaderRow(sheet);
        Assert.StartsWith("Номер", sheet.GetRow(header).GetCell(0).StringCellValue);

        // Все восемь строк, по порядку, без страницы.
        var first = sheet.GetRow(header + 1);
        Assert.Equal($"{seed.Tag}-1", first.GetCell(0).StringCellValue);
        Assert.Equal($"{seed.Tag}-8", sheet.GetRow(header + 8).GetCell(0).StringCellValue);
        Assert.Null(sheet.GetRow(header + 9));

        // Сумма — числом в формате денег, срок — датой, отсутствующее — пустой клеткой.
        Assert.Equal(CellType.Numeric, first.GetCell(1).CellType);
        Assert.Equal(110.00, first.GetCell(1).NumericCellValue);
        Assert.Equal("#,##0.00", first.GetCell(1).CellStyle.GetDataFormatString());
        Assert.True(DateUtil.IsCellDateFormatted(first.GetCell(2)));
        Assert.Equal(new DateTime(2026, 5, 15), first.GetCell(2).DateCellValue);
        Assert.Equal(CellType.Blank, sheet.GetRow(header + 8).GetCell(1).CellType);

        // Итог — по всему отбору, через пустую строку, подписью под первой колонкой.
        var total = sheet.GetRow(header + 10);
        Assert.Equal("Сумма", total.GetCell(0).StringCellValue);
        Assert.Equal(6869.99, total.GetCell(1).NumericCellValue, 2);
    }

    [Fact]
    public async Task Закрытая_колонка_в_файл_не_идёт_и_названа_в_шапке()
    {
        var (admin, _) = await SignInAsync("Admin");
        var seed = await SeedAsync(admin);
        var (narrow, _) = await SignInAsync(await RoleAsync("costs.waybill.read"));

        var response = await narrow.PostAsJsonAsync(Export, new
        {
            columns = new[] { InvoiceRequisites.NumberKey, InvoiceRequisites.TotalKey },
            filter = Mine(seed),
            totals = new[] { new { column = InvoiceRequisites.TotalKey, aggregate = "sum" } },
        });
        await OkAsync(response);
        var sheet = await SheetAsync(response);

        var header = sheet.GetRow(HeaderRow(sheet));
        Assert.Equal(1, header.LastCellNum);
        Assert.Contains(Lines(sheet), l => l.StartsWith("Не выгружено — нет права на"));
        // Ни одной суммы — ни в клетках, ни итогом, ни строкой о валюте.
        for (var r = 0; r <= sheet.LastRowNum; r++)
            foreach (var cell in sheet.GetRow(r)?.Cells ?? [])
                Assert.NotEqual(CellType.Numeric, cell.CellType);
        Assert.DoesNotContain(Lines(sheet), l => l.StartsWith("Суммы —"));
    }

    [Fact]
    public async Task Сверх_потолка_строк_отказ_а_не_обрезанный_файл()
    {
        var (admin, adminId) = await SignInAsync("Admin");
        var seed = await SeedAsync(admin);

        using var scope = host.Services.CreateScope();
        var access = await scope.ServiceProvider.GetRequiredService<DataAccessResolver>().ForUserAsync(adminId, default);
        var export = scope.ServiceProvider.GetRequiredService<ModuleTableExport>();

        var (file, refusal) = await export.ExportAsync(Address, access,
            new TableExportBody(null, Mine(seed), null, null), maxRows: 7, default);
        Assert.Null(file);
        Assert.Equal(409, refusal!.Status);
        Assert.Contains("под отбором 8 строк", refusal.Error);
        Assert.Contains("Сузьте отбор", refusal.Error);

        var (exact, _) = await export.ExportAsync(Address, access,
            new TableExportBody(null, Mine(seed), null, null), maxRows: 8, default);
        Assert.NotNull(exact);
    }

    [Fact]
    public async Task Отказ_отбора_доходит_до_экрана_как_есть()
    {
        var (admin, _) = await SignInAsync("Admin");
        var filter = JsonSerializer.Serialize(new { type = "condition", column = "НетТакой", op = "eq", value = "1" });

        var response = await admin.PostAsJsonAsync(Export, new { filter });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("НетТакой", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Без_ключа_таблицы_выгрузки_нет()
    {
        var (installer, _) = await SignInAsync("Installer");

        Assert.Equal(HttpStatusCode.Forbidden, (await installer.PostAsJsonAsync(Export, new { })).StatusCode);
    }

    /// <summary>В журнал — кто, какая таблица и сколько строк; ни сумм, ни условий отбора.</summary>
    [Fact]
    public async Task В_журнал_пишется_таблица_и_число_строк_без_сумм_и_отбора()
    {
        var (admin, _) = await SignInAsync("Admin");
        var seed = await SeedAsync(admin);

        await OkAsync(await admin.PostAsJsonAsync(Export, new
        {
            columns = new[] { InvoiceRequisites.NumberKey, InvoiceRequisites.TotalKey },
            filter = Mine(seed),
            totals = new[] { new { column = InvoiceRequisites.TotalKey, aggregate = "sum" } },
        }));

        using var scope = host.Services.CreateScope();
        var record = await scope.ServiceProvider.GetRequiredService<IActivityLog>().LastAsync(ActivityActions.TableExported);
        Assert.Equal(Address, record!.TargetId);
        Assert.Equal("строк: 8", record.After);
        Assert.DoesNotContain(seed.Tag, $"{record.TargetLabel} {record.Before} {record.After}");
        Assert.DoesNotContain("6869", $"{record.TargetLabel} {record.Before} {record.After}");
    }

    private static async Task<ISheet> SheetAsync(HttpResponseMessage response) =>
        new XSSFWorkbook(new MemoryStream(await response.Content.ReadAsByteArrayAsync())).GetSheetAt(0);

    /// <summary>Строки шапки — до первой пустой строки.</summary>
    private static List<string> Lines(ISheet sheet)
    {
        var lines = new List<string>();
        for (var r = 0; sheet.GetRow(r) is { } row && row.GetCell(0) is { CellType: CellType.String } cell; r++)
            lines.Add(cell.StringCellValue);
        return lines;
    }

    /// <summary>Строка заголовка колонок — первая после пустой строки под шапкой.</summary>
    private static int HeaderRow(ISheet sheet) => Lines(sheet).Count + 1;
}
