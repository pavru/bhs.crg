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
        // Файл импорта до решения человека; из импорта, ставшего редакцией, тот же путь держит и она.
        ModuleFile.Holding("estimate_imports", "blob_path", "файлы смет, загруженные для импорта"),
        // Приложения к смете (plan-11): файл переходит к ним от принятого импорта.
        ModuleFile.Holding("conjuncture_analyses", "blob_path", "файлы конъюнктурных анализов, приложенных к сметам"),
        ModuleFile.Holding("volume_statements", "blob_path", "файлы ведомостей объёмов работ, приложенных к сметам"),
    ];
}
