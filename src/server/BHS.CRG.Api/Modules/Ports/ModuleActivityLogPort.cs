using BHS.CRG.Api.Activity;
using BHS.CRG.Application.Activity;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Api.Modules.Ports;

/// <summary>
/// Журнал действий модуля — в журнал ядра (ТЗ CORE-25, CORE-28).
///
/// <para>Переходник, а не второй журнал: запись уходит той же службе, что и действия ядра, поэтому
/// автор, время и экран журнала у них общие.</para>
///
/// <para>⚠️ Пишется только ОБЪЯВЛЕННОЕ действие (<see cref="IModuleActivityActions" />). Проверка
/// именно на объявление, а не на форму кода: название на экране берётся из каталога при чтении, и
/// действие, не попавшее в каталог, читалось бы кодом — то есть название, переданное здесь, не
/// доезжало бы до человека вовсе (поймано ревью PR #1106). Форму кода и префикс модуля каталог
/// проверяет при старте.</para>
/// </summary>
public sealed class ModuleActivityLogPort(IActivityLog log, ActivityActionCatalog catalog) : IModuleActivityLog
{
    public Task RecordAsync(ModuleActivityAction action, string? targetId = null, string? targetLabel = null,
        string? before = null, string? after = null, CancellationToken ct = default)
    {
        if (action.Validate() is { } problem)
            throw new InvalidOperationException(
                $"Действие модуля объявлено негодно: {problem} " +
                "Запись с таким кодом осталась бы в журнале навсегда — переписывать прошлое журнал не умеет.");

        // Незнакомое действие — отказ. Молча записать его значило бы строку журнала, которая на
        // экране читается кодом, и отсутствие этого действия в отборе: и то и другое обнаружилось бы
        // после того, как запись сделана, а переписывать прошлое журнал не умеет.
        if (!catalog.Declares(action.Code))
            throw new InvalidOperationException(
                $"Действие «{action.Code}» не объявлено. Модуль объявляет свои действия журнала через " +
                $"{nameof(IModuleActivityActions)} в RegisterServices — иначе на экране журнала вместо " +
                "названия будет код, а в отборе по действию этой строки не будет вовсе.");

        // Название обязано совпадать с объявленным. Разойдясь, оно молчит: на экран пойдёт
        // объявленное, а автор вызова будет думать, что показывается переданное им, — то есть две
        // копии одного действия разъехались бы, и заметить это было бы нечем.
        if (catalog.Title(action.Code) != action.Title)
            throw new InvalidOperationException(
                $"У действия «{action.Code}» название «{action.Title}», а объявлено " +
                $"«{catalog.Title(action.Code)}». На экране журнала стоит ОБЪЯВЛЕННОЕ название: оно " +
                "берётся из каталога при чтении, чтобы правка названия доезжала и до старых записей. " +
                "Пишите действие тем же объявлением, которым оно объявлено.");

        return log.RecordAsync(new ActivityAction(action.Code, action.Title), targetId, targetLabel, before, after, ct);
    }
}
