using BHS.CRG.Application.Activity;
using BHS.CRG.Modules;

namespace BHS.CRG.Api.Activity;

/// <summary>
/// Состав включённых модулей в журнале действий (ТЗ CORE-28: «включение и выключение модулей»).
///
/// Модули включает ОПЕРАТОР ПОСТАВКИ переменной окружения <c>Modules__Enabled</c> (AUTH-17), а не
/// администратор кнопкой, — значит, и события «нажали включить» не существует. Поэтому смену
/// замечает старт: нынешний состав сверяется с тем, что записано последней записью журнала.
///
/// Журнал здесь заодно и хранилище прежнего состояния: отдельная строка в настройках «какой состав
/// был в прошлый раз» разошлась бы с журналом при первой же правке базы руками, и тогда одно из
/// двух показывало бы неправду.
///
/// ⚠️ Первый старт после появления журнала записывает состав БЕЗ прежнего значения. Это не «модули
/// включили сегодня» — это начало отсчёта, и <c>Before</c> в такой записи пуст именно поэтому.
/// </summary>
public static class ModuleCompositionJournal
{
    public static async Task RecordIfChangedAsync(IActivityLog log, ModuleRegistry modules,
        CancellationToken ct = default)
    {
        var now = Describe(modules.Enabled.Select(m => m.Code));
        var last = await log.LastAsync(ActivityActions.ModulesChanged, ct);

        // Порядок в переменной окружения ничего не значит, поэтому сравниваем нормализованную
        // запись: перестановка «id,costs» → «costs,id» не событие, и записью о ней журнал
        // заполнился бы шумом при каждом редактировании .env.
        if (last?.After == now) return;

        await log.RecordAsync(ActivityActions.ModulesChanged,
            targetLabel: "Состав модулей экземпляра", before: last?.After, after: now, ct: ct);
    }

    private static string Describe(IEnumerable<string> codes)
    {
        var list = codes.Select(c => c.Trim()).Where(c => c.Length > 0)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
        return list.Count == 0 ? "модулей нет" : string.Join(", ", list);
    }
}
