using BHS.CRG.Application.Activity;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Api.Modules.Ports;

/// <summary>
/// Журнал действий модуля — в журнал ядра (ТЗ CORE-25, CORE-28).
///
/// <para>Переходник, а не второй журнал: запись уходит той же службе, что и действия ядра, поэтому
/// автор, время и экран журнала у них общие. Своей логики здесь ровно одна — проверка объявления, и
/// она здесь потому, что порт её закончить не может: форму кода он проверяет сам, а про состав
/// поставки знает только приложение.</para>
/// </summary>
public sealed class ModuleActivityLogPort(IActivityLog log, ModuleRegistry registry) : IModuleActivityLog
{
    public Task RecordAsync(ModuleActivityAction action, string? targetId = null, string? targetLabel = null,
        string? before = null, string? after = null, CancellationToken ct = default)
    {
        if (action.Validate() is { } problem)
            throw new InvalidOperationException(
                $"Действие модуля объявлено негодно: {problem} " +
                "Запись с таким кодом осталась бы в журнале навсегда — переписывать прошлое журнал не умеет.");

        // Префикс обязан быть кодом ВКЛЮЧЁННОГО модуля. Иначе в журнале появились бы действия от
        // имени того, кто их не делал: `core.user.deleted`, написанное модулем, на экране не
        // отличить от записи ядра — там есть автор, но нет «чьё это действие по смыслу».
        //
        // Проверяется включённость, а не просто наличие кода в сборке: писать в журнал может только
        // работающий модуль, а выключенный служб не регистрирует вовсе — то есть код, дошедший сюда
        // с чужим префиксом, выполняется не тем, за кого себя выдаёт.
        var module = action.Code.Split('.')[0];
        if (!registry.IsEnabled(module))
            throw new InvalidOperationException(
                $"Действие «{action.Code}» начинается с «{module}», а модуля с таким кодом на этом " +
                $"экземпляре нет. Включены: {string.Join(", ", registry.Codes)}. Код действия модуля " +
                "обязан начинаться с его кода — иначе запись в журнале приписана не тому.");

        return log.RecordAsync(new ActivityAction(action.Code, action.Title), targetId, targetLabel, before, after, ct);
    }
}
