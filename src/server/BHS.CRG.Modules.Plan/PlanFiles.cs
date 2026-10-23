using BHS.CRG.Modules.Data;

namespace BHS.CRG.Modules.Plan;

/// <summary>
/// Колонки модуля, в которых лежат пути файлов (ТЗ CORE-34, issue #1261). Список — не то, чем
/// резервная копия НАХОДИТ файлы: она проходит по строкам модуля сама. Отсюда — слова отчёта. Полноту
/// стережёт <c>ModuleFileInventoryTests</c>.
/// </summary>
public static class PlanFiles
{
    public static IReadOnlyList<ModuleFile> All =>
    [
        // Файл, из которого импортирована редакция (plan-07): по нему разбирают спор «что было в смете».
        ModuleFile.Holding("estimate_revisions", "source_blob_path", "файлы смет, из которых импортированы редакции"),
    ];
}
