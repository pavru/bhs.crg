using BHS.CRG.Application.Activity;
using BHS.CRG.Infrastructure.Updates;
using BHS.CRG.Modules;

namespace BHS.CRG.Api.Activity;

/// <summary>Прежний состав модулей — след службы, а не настройка: его записал старт, не человек.</summary>
public sealed class ModuleCompositionState
{
    /// <summary>Нормализованный состав, записанный прошлым стартом. null — отсчёт ещё не начат.</summary>
    public string? Composition { get; set; }
}

/// <summary>
/// Состав включённых модулей в журнале действий (ТЗ CORE-28: «включение и выключение модулей»).
///
/// Модули включает ОПЕРАТОР ПОСТАВКИ переменной окружения <c>Modules__Enabled</c> (AUTH-17), а не
/// администратор кнопкой, — значит, и события «нажали включить» не существует. Поэтому смену
/// замечает старт: нынешний состав сверяется с тем, что записал прошлый.
///
/// ⚠️ Прежнее состояние лежит в <c>service_state</c>, а НЕ в самом журнале (issue #980). Журнал
/// хранилищем состояния быть не может по одной причине, и она не про чистоту: журнал входит в
/// резервную копию, а состав модулей — свойство ЭТОЙ установки. Восстановили копию с установки,
/// где включены <c>id,costs</c>, на установку с одним <c>id</c> — и старт, сверяясь с журналом,
/// записывал «costs, id → id»: смену, которой не было, в журнал, который не правят.
/// <c>service_state</c> в копию не входит именно потому, что переносить его между установками
/// нечего, — то есть восстановление копии его не трогает, и сверять после него по-прежнему есть с чем.
///
/// ⚠️ Первый старт БЕЗ записанного состояния — начало отсчёта: <c>Before</c> в такой записи пуст
/// именно поэтому, и это не «модули включили сегодня».
/// </summary>
public static class ModuleCompositionJournal
{
    /// <summary>Ключ строки состояния. Менять нельзя: со сменой ключа прежний состав теряется, и
    /// следующий старт запишет начало отсчёта заново.</summary>
    private const string StateKey = "module-composition";

    public static async Task RecordIfChangedAsync(IActivityLog log, ModuleRegistry modules,
        ServiceStateStore state, CancellationToken ct = default)
    {
        var now = Describe(modules.Enabled.Select(m => m.Code));
        var saved = await state.LoadAsync<ModuleCompositionState>(StateKey, ct);

        // Переход со старого механизма: до #980 прежний состав держал сам журнал, и на установке,
        // которая обновляется, строки состояния ещё нет. Один раз — и только один — берём прежнее
        // значение оттуда, иначе обновление выглядело бы как «модули включили сегодня».
        //
        // ⚠️ Остаточное окно названо вслух: если копию восстановили ПОСЛЕ обновления, но ДО первого
        // старта новой версии, прежним значением окажется чужое. Окно — ровно один старт, после
        // которого состояние записано и журнала больше не спрашивают; прежнее поведение повторялось
        // при каждом восстановлении и не кончалось никогда.
        var previous = saved.Composition ?? (await log.LastAsync(ActivityActions.ModulesChanged, ct))?.After;

        // Порядок в переменной окружения ничего не значит, поэтому сравниваем нормализованную
        // запись: перестановка «id,costs» → «costs,id» не событие, и записью о ней журнал
        // заполнился бы шумом при каждом редактировании .env.
        if (previous != now)
            await log.RecordAsync(ActivityActions.ModulesChanged,
                targetLabel: "Состав модулей экземпляра", before: previous, after: now, ct: ct);

        // Состояние пишется и тогда, когда записи не было: иначе строка так и не появится, а
        // прежним значением каждый старт служил бы журнал — то есть лечение не наступило бы вовсе.
        if (saved.Composition != now)
            await state.SaveAsync(StateKey, new ModuleCompositionState { Composition = now }, ct);
    }

    private static string Describe(IEnumerable<string> codes)
    {
        var list = codes.Select(c => c.Trim()).Where(c => c.Length > 0)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
        return list.Count == 0 ? "модулей нет" : string.Join(", ", list);
    }
}
