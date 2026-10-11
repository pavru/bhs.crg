using System.Text.Json;
using BHS.CRG.Api.Modules;
using BHS.CRG.Application.Backup;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Costs;
using BHS.CRG.Modules.Costs.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Перепись колонок модуля с путём файла (issue #1261): каждая названа в <see cref="IAppModule.Files" />.
///
/// <para>Копии это НЕ нужно — файл из забытой колонки она возьмёт всё равно: находит обход строк, а
/// не объявление. Нужно человеку: о забытой колонке отчёт скажет «invoices.scan_blob_path — 3 из 10»
/// вместо «файлы счетов», а копия при каждом снятии будет предупреждать о необъявленной колонке.
/// Перепись ловит забытое здесь, а не в паспорте копии у заказчика.</para>
///
/// <para>⚠️ Колонка опознаётся по ИМЕНИ, и это слабый признак: путь в колонке с другим именем перепись
/// не увидит. Вторая половина сторожа поэтому поведенческая — копия, нашедшая путь в необъявленной
/// колонке, говорит об этом, и круг «снял — восстановил» на настоящем счёте требует, чтобы таких
/// слов не было (<c>InvoiceBackupRoundTripTests</c>).</para>
/// </summary>
public class ModuleFileInventoryTests
{
    /// <summary>Модули сборки со своей схемой и модель каждого. Новый модуль вписывается сюда.</summary>
    private static IEnumerable<(IAppModule Module, IModel Model)> Modules()
    {
        var options = new DbContextOptionsBuilder<CostsDbContext>();
        CostsDbContext.Configure(options, "Host=127.0.0.1;Database=never");
        using var costs = new CostsDbContext(options.Options);
        yield return (new CostsModule(), costs.Model);
    }

    private static HashSet<(string Table, string Column)> Columns(IModel model) =>
        model.GetEntityTypes()
            .SelectMany(e => e.GetProperties().Select(p => (e.GetTableName()!, p.GetColumnName())))
            .ToHashSet();

    [Fact]
    public void Каждая_колонка_с_путём_файла_объявлена()
    {
        var forgotten = new List<string>();
        foreach (var (module, model) in Modules())
        foreach (var (table, column) in Columns(model))
            if (column.Contains("blob_path", StringComparison.OrdinalIgnoreCase)
                && !module.Files.Any(f => f.Table == table && f.Column == column))
                forgotten.Add($"{module.Code}: {table}.{column}");

        Assert.True(forgotten.Count == 0,
            "Колонки модуля с путём файла не объявлены:\n" + string.Join("\n", forgotten) + "\n\n" +
            "В резервную копию файл из такой колонки попадёт и без объявления — его найдёт обход строк. " +
            "Но отчёты назовут колонку адресом, а копия будет предупреждать о ней при каждом снятии. " +
            "Объявите её в Files модуля: ModuleFile.Holding — что это за файлы, или Derived — почему " +
            "файл в копию не едет.");
    }

    /// <summary>Объявление о колонке, которой в модели нет, — след переименования: слова отвалятся молча.</summary>
    [Fact]
    public void Объявления_называют_существующие_колонки_и_не_повторяются()
    {
        foreach (var (module, model) in Modules())
        {
            var columns = Columns(model);
            foreach (var file in module.Files)
            {
                Assert.True(columns.Contains((file.Table, file.Column)),
                    $"{module.Code}: {file.Table}.{file.Column} — такой колонки в модели модуля нет.");
                Assert.False(string.IsNullOrWhiteSpace(file.What),
                    $"{module.Code}: {file.Table}.{file.Column} — не сказано, что это за файлы или почему они не едут.");
            }

            Assert.Equal(module.Files.Count, module.Files.Select(f => (f.Table, f.Column)).Distinct().Count());
        }
    }

    // ── Само правило обхода: без базы и без хранилища ────────────────────────────────────────────

    private const string Scan = "bhs-crg/2026/10/09/3f2504e0-4f89-11d3-9a0c-0305e82c3301_Счёт 31.pdf";
    private const string Image = "bhs-crg/2026/10/10/9b2504e0-4f89-11d3-9a0c-0305e82c3302_Счёт 31.pdf";

    private static BackupModuleTable Table(string name, params object[] rows) =>
        new(name, [.. rows.Select(r => JsonSerializer.SerializeToElement(r))]);

    [Fact]
    public void Файл_счёта_берётся_один_раз_а_образ_не_берётся()
    {
        var found = ModuleFileScan.Scan(
        [
            Table("invoices", new { id = 1, number = "31", scan_blob_path = Scan }),
            Table("invoice_recognitions", new { invoice_id = 1, scan_blob_path = Scan, image_blob_path = Image }),
        ], CostsFiles.All);

        Assert.Equal([Scan], found.Paths);
        Assert.Equal(["файлы счетов"], found.Groups.Keys);
        Assert.Empty(found.Undeclared);
        Assert.Empty(found.Embedded);
    }

    /// <summary>
    /// Направление отказа, ради которого выбран обход: модуль о колонке промолчал — файл ВЗЯТ. Массив
    /// и вложенный JSON — потому что модуль вправе хранить путь как угодно.
    /// </summary>
    [Fact]
    public void Путь_в_необъявленной_колонке_взят_и_назван_адресом()
    {
        var second = Scan.Replace("3301", "3309");
        var found = ModuleFileScan.Scan(
        [
            Table("waybills", new { id = 1, files = new[] { Scan }, extra = new { attachment = new { path = second } } }),
        ], CostsFiles.All);

        Assert.Equal(new[] { Scan, second }.Order(StringComparer.Ordinal), found.Paths);
        Assert.Equal(["waybills.extra", "waybills.files"], found.Undeclared);
    }

    /// <summary>
    /// Путь внутри текста найден, но не взят: где кончается имя файла, по тексту не понять. Молчать об
    /// этом нельзя — отсюда отдельный перечень, из которого копия делает предупреждение.
    /// </summary>
    [Fact]
    public void Путь_внутри_текста_не_берётся_но_называется()
    {
        var found = ModuleFileScan.Scan(
        [
            Table("exports", new { id = 1, note = $"см. http://storage/{Scan} и ещё {Image}" }),
        ], CostsFiles.All);

        Assert.Empty(found.Paths);
        Assert.Equal(2, found.Embedded["exports.note"]);
    }

    /// <summary>
    /// Текст, который НАЧИНАЕТСЯ не с пути, точным путём не считается (ревью PR #1289): раньше «всё, что
    /// не слэш» перед датой принималось за имя корзины, и JSON, записанный строкой, уходил в копию
    /// «путём» целиком — без файла и без предупреждения.
    /// </summary>
    [Theory]
    [InlineData("{\"scan\":\"" + Scan + "\"}")]
    [InlineData("см. " + Scan)]
    [InlineData(Scan + "; " + Image)]
    public void Путь_с_текстом_вокруг_точным_не_считается(string text)
    {
        var found = ModuleFileScan.Scan([Table("exports", new { id = 1, note = text })], CostsFiles.All);

        Assert.Empty(found.Paths);
        Assert.True(found.Embedded["exports.note"] >= 1);
    }

    /// <summary>
    /// Распознавание помнит путь файла, который читало, — и после замены файла этот путь указывает на
    /// удалённый файл. В копию он не едет: файл держит счёт.
    /// </summary>
    [Fact]
    public void Путь_который_помнит_распознавание_в_копию_не_едет()
    {
        var found = ModuleFileScan.Scan(
            [Table("invoice_recognitions", new { invoice_id = 1, scan_blob_path = Scan })], CostsFiles.All);

        Assert.Empty(found.Paths);
        Assert.Empty(found.Undeclared);
    }

    [Fact]
    public void Обычный_текст_файлом_не_считается()
    {
        var found = ModuleFileScan.Scan(
        [
            Table("invoices", new { id = 1, number = "2026/10/09", comment = "оплатить до 2026.10.09", scan_blob_path = (string?)null }),
        ], CostsFiles.All);

        Assert.Empty(found.Paths);
        Assert.Empty(found.Embedded);
    }
}
