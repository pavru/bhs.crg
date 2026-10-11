using BHS.CRG.Modules.Data;

namespace BHS.CRG.Modules.Costs;

/// <summary>
/// Колонки модуля, в которых лежат пути файлов (ТЗ CORE-34, issue #1261).
///
/// <para>Список — не то, чем резервная копия НАХОДИТ файлы: она проходит по строкам модуля сама, и
/// файл из колонки, забытой здесь, в копию всё равно попадёт. Отсюда берутся слова отчёта («сканы
/// счетов») и исключения. Полноту стережёт <c>ModuleFileInventoryTests</c>.</para>
/// </summary>
public static class CostsFiles
{
    /// <summary>
    /// Одни слова на обе колонки скана: распознавание помнит ТОТ ЖЕ файл, что приложен к счёту, и
    /// считать его дважды незачем.
    /// </summary>
    private const string Scans = "файлы счетов";

    public static IReadOnlyList<ModuleFile> All =>
    [
        ModuleFile.Holding("invoices", "scan_blob_path", Scans),
        ModuleFile.Holding("invoice_recognitions", "scan_blob_path", Scans),
        // Образ офисного файла, по которому прочитан счёт (issue #1270). В копию образы не кладутся:
        // после восстановления ядро строит их заново. Путь здесь только СРАВНИВАЕТСЯ с нынешним — по
        // нему ничего не открывают, — поэтому после восстановления он указывает в пустоту безвредно,
        // а пометка «распознано по прежнему виду файла» оказывается правдой.
        ModuleFile.Derived("invoice_recognitions", "image_blob_path",
            "читаемый образ файла счёта — производное: после восстановления строится заново"),
    ];
}
