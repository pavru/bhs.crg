using BHS.CRG.Application.Backup;
using BHS.CRG.Infrastructure.Backup;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Слепое пятно обхода файлов копии — JSON, который едет СТРОКОЙ (issue #1422).
///
/// <para>Обход манифеста (<see cref="CoreFileScan" />) берёт точный путь из любого поля, а поле-JSON
/// (<c>JsonElement</c>) разбирает на любую глубину. Но у части сущностей колонка <c>jsonb</c> едет в
/// манифесте строкой — разбиение набора, кэш строк источника, состояние представления. Для обхода
/// это текст: путь внутри него найден будет, взят — нет, и копия только предупредит. Поэтому у
/// каждой такой строки решение названо здесь: либо её пути берёт список мест копии (и чем), либо
/// путей в ней не бывает, и если появятся — копия скажет «внутри текста». Новая jsonb-колонка в
/// переносимой сущности, едущая строкой, — красный в день появления, а не предупреждение у
/// заказчика. Эта карта — основа объявлений шага 2 (обход как единственный источник).</para>
///
/// <para>Поля-JSON (<c>JsonElement</c>) сюда не входят: решение у них всегда одно — обход.</para>
/// </summary>
[Collection("Integration")]
public class BackupManifestFileHoldersTests(IntegrationTestFixture fixture)
{
    private const string ByList = "пути берёт список мест копии: ";
    private const string NoFiles = "файлов не держит; путь внутри — предупреждение копии";

    /// <summary>Строка с JSON в записи манифеста → как копия обходится с файлами в ней.</summary>
    private static readonly Dictionary<string, string> Decided = new()
    {
        // ── Пути есть: берёт список мест (issue #1399) ──────────────────────────────────────────
        [$"{nameof(BackupDataSetFile)}.{nameof(BackupDataSetFile.Grouping)}"] =
            ByList + "части альбома по ГОСТ — CollectGroupingParts",
        [$"{nameof(BackupDataSetSource)}.{nameof(BackupDataSetSource.CachedData)}"] =
            ByList + "колонка ФайлПуть реестра «Документы» — CollectRowFilePaths. Кэш прочих источников — " +
            "текст файла пользователя или производное системного набора: путь внутри — предупреждение (#1422)",
        [$"{nameof(BackupDataSetSource)}.{nameof(BackupDataSetSource.CachedSchema)}"] =
            ByList + "образцы значений колонок — подмножество CachedData, их ключи взяты тем же сбором",

        // ── Путей не бывает ─────────────────────────────────────────────────────────────────────
        [$"{nameof(BackupDataSetFile)}.{nameof(BackupDataSetFile.InvoiceRawData)}"] = NoFiles + " (распознанные поля счёта)",
        [$"{nameof(BackupDataSetFile)}.{nameof(BackupDataSetFile.RecognitionProfiles)}"] = NoFiles + " (коды профилей)",
        [$"{nameof(BackupDataSetSource)}.{nameof(BackupDataSetSource.ColumnExpressions)}"] = NoFiles + " (выражения колонок)",
        [$"{nameof(BackupDataSetSource)}.{nameof(BackupDataSetSource.ComputedColumns)}"] = NoFiles + " (скрипты колонок)",
        [$"{nameof(BackupDataSetSource)}.{nameof(BackupDataSetSource.RowFilter)}"] = NoFiles + " (дерево фильтра)",
        [$"{nameof(BackupDataSetSource)}.{nameof(BackupDataSetSource.SortSpec)}"] = NoFiles + " (порядок строк)",
        [$"{nameof(BackupDataSetSource)}.{nameof(BackupDataSetSource.Tags)}"] = NoFiles + " (функциональные тэги)",
        [$"{nameof(BackupDataSetSource)}.{nameof(BackupDataSetSource.MaterializeMapping)}"] = NoFiles + " (маппинг колонок)",
        [$"{nameof(BackupDataSetSource)}.{nameof(BackupDataSetSource.MaterializeDiscriminator)}"] = NoFiles + " (правило вида записи)",
        [$"{nameof(BackupDataSetProcessingTemplate)}.{nameof(BackupDataSetProcessingTemplate.ColumnExpressions)}"] = NoFiles + " (выражения колонок)",
        [$"{nameof(BackupDataSetProcessingTemplate)}.{nameof(BackupDataSetProcessingTemplate.ComputedColumns)}"] = NoFiles + " (скрипты колонок)",
        [$"{nameof(BackupDataSetProcessingTemplate)}.{nameof(BackupDataSetProcessingTemplate.RowFilter)}"] = NoFiles + " (дерево фильтра)",
        [$"{nameof(BackupDataSetProcessingTemplate)}.{nameof(BackupDataSetProcessingTemplate.SortSpec)}"] = NoFiles + " (порядок строк)",
        [$"{nameof(BackupDataSetBinding)}.{nameof(BackupDataSetBinding.Mapping)}"] = NoFiles + " (поле ← колонка)",
        [$"{nameof(BackupDataSetBindingTemplate)}.{nameof(BackupDataSetBindingTemplate.ColumnMappings)}"] = NoFiles + " (поле ← колонка)",
        [$"{nameof(BackupDocument)}.{nameof(BackupDocument.TemplateIds)}"] = NoFiles + " (идентификаторы шаблонов)",
        [$"{nameof(BackupDocument)}.{nameof(BackupDocument.TemplateParams)}"] = NoFiles + " (значения параметров: строка, число, признак)",
        [$"{nameof(BackupTemplate)}.{nameof(BackupTemplate.Parameters)}"] = NoFiles + " (объявления параметров)",
        [$"{nameof(BackupPeriodClosure)}.{nameof(BackupPeriodClosure.Report)}"] = NoFiles + " (перечень диалога закрытия; Link — адрес экрана)",
        [$"{nameof(BackupTableView)}.{nameof(BackupTableView.State)}"] = NoFiles + " (состояние экрана таблицы)",
    };

    private List<string> JsonStrings()
    {
        using var scope = fixture.Services.CreateScope();
        return BackupManifestFieldMap.Of(scope.ServiceProvider.GetRequiredService<AppDbContext>())
            .Where(f => f.InRecord?.PropertyType == typeof(string) && f.Property.GetColumnType() == "jsonb")
            .Select(f => $"{f.Record.Name}.{f.InRecord!.Name}")
            .Distinct().OrderBy(k => k, StringComparer.Ordinal).ToList();
    }

    [Fact]
    public void EveryJsonString_HasFileDecision()
    {
        var undecided = JsonStrings().Where(k => !Decided.ContainsKey(k)).ToList();

        Assert.True(undecided.Count == 0,
            "Колонки jsonb, которые едут в манифесте копии строкой, без решения о файлах:\n  " +
            string.Join("\n  ", undecided) + "\n" +
            "Обход копии видит такую строку текстом: точный путь из неё не берётся, только " +
            "предупреждение. Если в колонке могут лежать пути файлов — научите список мест копии " +
            "(BackupService.ExtractBlobPaths) их брать и назовите чем; если нет — так и запишите в Decided.");
    }

    [Fact]
    public void Inventory_HoldsNoDeadRows()
    {
        var known = JsonStrings().ToHashSet(StringComparer.Ordinal);
        var dead = Decided.Keys.Where(k => !known.Contains(k)).OrderBy(k => k).ToList();

        Assert.True(dead.Count == 0, "В переписи строк-JSON копии — поля, которых больше нет:\n  " + string.Join("\n  ", dead));
    }
}
