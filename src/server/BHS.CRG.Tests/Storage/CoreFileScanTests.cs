using System.Text.Json;
using BHS.CRG.Application.Backup;
using BHS.CRG.Infrastructure.Backup;

namespace BHS.CRG.Tests.Storage;

/// <summary>
/// Обход манифеста ядра по форме пути (issue #1422) — без базы и хранилища: что найдено, под каким
/// адресом и чего обход не смотрит.
/// </summary>
public class CoreFileScanTests
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private static string PathOf(int n) => $"bhs-crg/2026.10.17/{new Guid(n, 0, 0, new byte[8])}_файл {n}.pdf";

    private static BackupManifest Manifest(
        BackupCommonDataEntry[]? entries = null, BackupDataSetFile[]? files = null,
        BackupActivityRecord[]? journal = null, BackupModuleSchema[]? modules = null) =>
        new(SchemaVersion: BackupService.CurrentSchemaVersion, AppVersion: "test",
            CreatedAt: DateTimeOffset.UnixEpoch, DocumentTypes: [], Templates: [], CatalogEntities: [],
            CommonDataEntries: entries ?? [], DataSetFiles: files, ActivityLog: journal, ModuleData: modules);

    private static BackupCommonDataEntry Entry(Guid id, object data) =>
        new(id, "Запись", Guid.Empty, JsonSerializer.SerializeToElement(data), "System", null,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    /// <summary>
    /// Путь лежит строкой вне объекта <c>{$type: file}</c>, — список мест такой не знает, обход
    /// находит. Адрес обрывается на поле-JSON: ключи схемы пользователя суть данные. Запись названа.
    /// </summary>
    [Fact]
    public void Путь_в_данных_записи_найден_с_адресом_поля_и_записью()
    {
        var id = Guid.NewGuid();
        var found = CoreFileScan.Scan(Manifest(entries: [Entry(id, new
        {
            Скан = new { type = "file", blobPath = PathOf(1) },
            Подпись = PathOf(2),
            Примечание = "см. файл " + PathOf(3),
        })]), Options);

        Assert.Equal([PathOf(1), PathOf(2)], found.Exact.Select(h => h.Text));
        Assert.All(found.Exact, h => Assert.Equal("CommonDataEntries[].Data", h.Address));
        Assert.All(found.Exact, h => Assert.Equal(id, h.RecordId));
        var inText = Assert.Single(found.InText);
        Assert.Equal("CommonDataEntries[].Data", inText.Address);
    }

    /// <summary>Строка с JSON внутри (разбиение набора) — для обхода текст: ключи есть, точного пути нет.</summary>
    [Fact]
    public void JSON_строкой_даёт_ключи_внутри_текста()
    {
        var grouping = JsonSerializer.Serialize(new { Groups = new[] { new { BlobPath = PathOf(4) } } });
        var file = new BackupDataSetFile(Guid.NewGuid(), "Альбом", "Pdf", PathOf(5), "System", null,
            null, grouping, null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

        var found = CoreFileScan.Scan(Manifest(files: [file]), Options);

        Assert.Equal("DataSetFiles[].BlobPath", Assert.Single(found.Exact).Address);
        var inText = Assert.Single(found.InText);
        Assert.Equal("DataSetFiles[].Grouping", inText.Address);
        Assert.Single(inText.Hit.Keys);
    }

    /// <summary>
    /// Журнал — снимок истории, его пути могут вести к удалённым файлам; строки модулей обходит сам
    /// модуль. Ни то, ни другое обход ядра не смотрит.
    /// </summary>
    [Fact]
    public void Журнал_и_данные_модулей_не_обходятся()
    {
        var journal = new BackupActivityRecord(Guid.NewGuid(), DateTimeOffset.UnixEpoch, "x", null, "Тест",
            null, null, null, PathOf(6));
        var module = new BackupModuleSchema("costs", "costs",
            [new BackupModuleTable("invoices", [JsonSerializer.SerializeToElement(new { scan = PathOf(7) })])]);

        var found = CoreFileScan.Scan(Manifest(journal: [journal], modules: [module]), Options);

        Assert.Empty(found.Exact);
        Assert.Empty(found.InText);
    }

    /// <summary>Порядок — по адресу: текст паспорта копии и её оценки обязан совпасть байт в байт.</summary>
    [Fact]
    public void Находки_упорядочены_по_адресу()
    {
        var file = new BackupDataSetFile(Guid.NewGuid(), "Набор", "Pdf", PathOf(8), "System", null,
            null, null, null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var found = CoreFileScan.Scan(
            Manifest(entries: [Entry(Guid.NewGuid(), new { p = PathOf(9) })], files: [file]), Options);

        Assert.Equal(["CommonDataEntries[].Data", "DataSetFiles[].BlobPath"], found.Exact.Select(h => h.Address));
    }

    /// <summary>
    /// Сторож контракта: обход знает вид каждого листа манифеста. Новое поле словарём или типом со
    /// своим конвертером отказывает здесь — а не молча выпадает из обхода в копии у заказчика.
    /// </summary>
    [Fact]
    public void Контракт_манифеста_весь_знаком_обходу()
    {
        var fields = BackupService.ManifestFields();

        Assert.Contains(fields, f => f is { Address: "Documents[].Data", Kind: CoreFieldKind.Json });
        Assert.Contains(fields, f => f is { Address: "DataSetFiles[].Grouping", Kind: CoreFieldKind.Text });
        Assert.Contains(fields, f => f is { Address: "Documents[].GeneratedFiles[].BlobPath", Kind: CoreFieldKind.Text });
        // Не обходимое и в перечне полей отсутствует — вместе со всем, что под ним.
        foreach (var skipped in CoreFileScan.NotWalked.Keys)
            Assert.DoesNotContain(fields, f => f.Address == skipped || f.Address.StartsWith(skipped + "[", StringComparison.Ordinal));
    }
}
