using BHS.CRG.Application.Activity;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Api.Activity;

/// <summary>
/// Каталог действий журнала: действия ядра плюс действия ВКЛЮЧЁННЫХ модулей (ТЗ CORE-28, задача M2
/// этапа 2, issue #1069).
///
/// <para><b>Зачем он появился.</b> В записи журнала лежит только код действия — название берётся из
/// каталога при чтении, чтобы правка названия доезжала и до старых записей. Каталог ядра закрыт
/// (<see cref="ActivityActions" />, поля-константы), и для кода модуля он возвращал сам код: строка на
/// экране читалась бы «costs.invoice.paid», а в отборе по действию модульных действий не было бы
/// вовсе — отбор строится из каталога, а не из того, что записано. То есть название, которое модуль
/// передаёт при записи, не доезжало до человека НИКАК (поймано ревью PR #1106).</para>
///
/// <para>⚠️ Объявления проверяются здесь, на сборке каталога, а каталог разрешается при старте
/// (<c>StartupTasks</c>): негодный код или два одинаковых обязаны ронять старт, а не всплывать
/// строкой без названия — тогда, когда запись уже сделана.</para>
///
/// <para>⚠️ Префикс кода обязан быть кодом включённого модуля. Иначе модуль объявил бы
/// <c>core.user.deleted</c> и писал бы действия от имени ядра: на экране журнала такую строку не
/// отличить от настоящей.</para>
/// </summary>
public sealed class ActivityActionCatalog
{
    private readonly Dictionary<string, ActivityAction> _byCode;

    public ActivityActionCatalog(IEnumerable<IModuleActivityActions> declarations, ModuleRegistry modules)
    {
        _byCode = ActivityActions.All.ToDictionary(a => a.Code, StringComparer.OrdinalIgnoreCase);

        foreach (var declared in declarations.SelectMany(d => d.Actions))
        {
            if (declared.Validate() is { } problem)
                throw new InvalidOperationException(
                    $"Модуль объявил негодное действие журнала: {problem} " +
                    "Запись с таким кодом осталась бы в журнале навсегда — переписывать прошлое журнал не умеет.");

            var module = declared.Code.Split('.')[0];
            if (!modules.IsEnabled(module))
                throw new InvalidOperationException(
                    $"Действие журнала «{declared.Code}» начинается с «{module}», а модуля с таким кодом " +
                    $"на этом экземпляре нет. Включены: {string.Join(", ", modules.Codes)}. Код действия " +
                    "модуля обязан начинаться с его кода — иначе запись в журнале приписана не тому.");

            if (_byCode.TryGetValue(declared.Code, out var taken))
                throw new InvalidOperationException(
                    $"Действие журнала «{declared.Code}» объявлено дважды: «{taken.Title}» и " +
                    $"«{declared.Title}». Название берётся из каталога при чтении, поэтому второе " +
                    "объявление молча решало бы, как читается уже записанное.");

            _byCode[declared.Code] = new ActivityAction(declared.Code, declared.Title);
        }
    }

    /// <summary>Объявлено ли действие — по нему порт журнала и отказывает незнакомому коду.</summary>
    public bool Declares(string code) => _byCode.ContainsKey(code);

    /// <summary>
    /// Название для экрана. Незнакомый код отдаётся как есть: так читаются записи действий, которые
    /// переименовали или которые оставил выключенный с тех пор модуль, — журнал обязан показать
    /// строку, а не спрятать её.
    /// </summary>
    public string Title(string code) => _byCode.GetValueOrDefault(code)?.Title ?? code;

    /// <summary>Каталог для отбора на экране: ядро и включённые модули, по названию.</summary>
    public IReadOnlyList<ActivityAction> All =>
        [.. _byCode.Values.OrderBy(a => a.Title, StringComparer.CurrentCulture)];
}
