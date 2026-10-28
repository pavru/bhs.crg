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
///
/// <para>⚠️ Свёртка вида (ТЗ CORE-28.3) проверяется здесь же: группа своего модуля, одно право чтения
/// на группу и ни одного вида ядра из <see cref="NeverFolded" />. Перечень сворачиваемых видов модулей
/// держит сторож <c>ActivityFoldInventoryTests</c>: переходы и оплату модуля каталог распознать не
/// может, а человек, вписывающий вид в перечень, — может.</para>
/// </summary>
public sealed class ActivityActionCatalog : IActivityActionLookup
{
    /// <summary>Владелец действий ядра — первая часть их кода.</summary>
    public const string CoreOwner = "core";

    private readonly Dictionary<string, ActivityAction> _byCode;
    // Право чтения записи — только у действий, которые его назвали.
    private readonly Dictionary<string, string> _readPermission = new(StringComparer.Ordinal);
    private readonly ModuleRegistry _modules;

    /// <summary>
    /// Виды ядра, которые не сворачиваются никогда (ТЗ CORE-28.3): пользователи, роли и права, состав
    /// модулей, закрытие периода, удаление и архив. Каждая такая запись — отдельное свидетельство, и
    /// ряд «×3» спрятал бы, что право выдавали трижды.
    /// </summary>
    public static readonly IReadOnlySet<string> NeverFolded = new HashSet<string>(StringComparer.Ordinal)
    {
        ActivityActions.UserCreated.Code, ActivityActions.UserRoleChanged.Code, ActivityActions.UserDeleted.Code,
        ActivityActions.RoleCreated.Code, ActivityActions.RolePermissionsChanged.Code,
        ActivityActions.RoleRenamed.Code, ActivityActions.RoleDeleted.Code,
        ActivityActions.ModulesChanged.Code, ActivityActions.PeriodClosed.Code, ActivityActions.PeriodReopened.Code,
        ActivityActions.RecordArchived.Code, ActivityActions.RecordUnarchived.Code,
        ActivityActions.RecordPurged.Code, ActivityActions.ScopePurged.Code, ActivityActions.TableViewDeleted.Code,
    };

    public ActivityActionCatalog(IEnumerable<IModuleActivityActions> declarations, ModuleRegistry modules,
        PermissionCatalog permissions)
    {
        _modules = modules;
        CheckCoreFolds(ActivityActions.All);
        _byCode =ActivityActions.All.ToDictionary(a => a.Code, StringComparer.OrdinalIgnoreCase);

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

            if (declared.ReadPermission is { } permission)
            {
                // Чужое или необъявленное право — отказ старта: такую запись не увидел бы никто, и
                // выглядело бы это как «действий не было».
                if (!permission.StartsWith(module + ".", StringComparison.Ordinal) || !permissions.Declares(permission))
                    throw new InvalidOperationException(
                        $"Действие журнала «{declared.Code}» закрыто правом «{permission}», а модуль " +
                        $"«{module}» такого права не объявляет. Запись с таким действием не увидел бы " +
                        "никто: право нельзя выдать ни одной роли.");

                _readPermission[declared.Code] = permission;
            }

            if (declared.Fold is { } fold && !fold.Group.StartsWith(module + ".", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Действие журнала «{declared.Code}» сворачивается в группу «{fold.Group}», а группа " +
                    $"обязана начинаться с кода своего модуля «{module}.». Чужая группа склеила бы правку " +
                    "одного модуля с правкой другого в один ряд.");

            _byCode[declared.Code] = new ActivityAction(declared.Code, declared.Title, Fold(declared.Fold));
        }

        CheckGroupPermissions();
    }

    /// <summary>
    /// Свёртка у вида ядра из запретного перечня или с чужой группой — отказ старта (ТЗ CORE-28.3).
    /// Отдельным методом, потому что каталог ядра — поля-константы, и проверить отказ иначе как
    /// подменой перечня нечем.
    /// </summary>
    public static void CheckCoreFolds(IEnumerable<ActivityAction> core)
    {
        foreach (var action in core)
        {
            if (action.Fold is not { } fold) continue;

            if (NeverFolded.Contains(action.Code))
                throw new InvalidOperationException(
                    $"Действие журнала «{action.Code}» объявило свёртку, а оно из тех, что не сворачиваются " +
                    "никогда: пользователи, роли и права, состав модулей, закрытие периода, удаление и архив. " +
                    "Каждая такая запись — отдельное свидетельство, и ряд «×N» спрятал бы, сколько их было.");

            if (!fold.Group.StartsWith(CoreOwner + ".", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Действие журнала «{action.Code}» сворачивается в группу «{fold.Group}», а группа " +
                    $"ядра обязана начинаться с «{CoreOwner}.».");
        }
    }

    // Виды одной группы закрыты одним правом: иначе один читатель видел бы серию, а другой — её
    // обрывки, и одна сессия у разных читателей распадалась бы по-разному.
    private void CheckGroupPermissions()
    {
        var split = _byCode.Values
            .Where(a => a.Fold is not null)
            .GroupBy(a => a.Fold!.Group, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Select(a => _readPermission.GetValueOrDefault(a.Code)).Distinct().Count() > 1);

        if (split is not null)
            throw new InvalidOperationException(
                $"Виды журнала группы свёртки «{split.Key}» закрыты разными правами чтения: " +
                string.Join(", ", split.Select(a =>
                    $"{a.Code} — {_readPermission.GetValueOrDefault(a.Code) ?? "без права"}")) +
                ". Один читатель видел бы серию целиком, а другой — её обрывки.");
    }

    private static ActivityFold? Fold(ModuleActivityFold? fold) =>
        fold is null ? null : new ActivityFold(fold.Group, Rule(fold.Before), Rule(fold.After));

    // По имени, а не по числу: порядок значений двух перечислений никто не сверяет, имена сверяет
    // ModulePortMirrorTests.
    private static FoldRule Rule(ModuleFoldRule rule) => Enum.Parse<FoldRule>(rule.ToString());

    /// <summary>
    /// Вид по коду — вместе со свёрткой. Порт модуля пишет вид ИЗ КАТАЛОГА: собранный заново из
    /// объявления, он потерял бы свёртку молча.
    /// </summary>
    public ActivityAction? Find(string code) => _byCode.GetValueOrDefault(code);

    /// <summary>
    /// Что из журнала видно обладателю этих прав (issue #1104): ядро, модули, которые ему открыты, — и
    /// без действий, чьего права чтения у него нет. Сюда приходит уже вошедший в журнал: само право на
    /// журнал проверяют ворота адреса.
    /// </summary>
    public ActivityVisibility VisibleTo(IReadOnlyCollection<string> granted)
    {
        var owners = _modules.Enabled.Select(m => m.Code)
            .Where(code => ModuleAccess.IsOpen(code, granted))
            .Prepend(CoreOwner);
        var closed = _readPermission
            .Where(p => !granted.Contains(p.Value, StringComparer.OrdinalIgnoreCase))
            .Select(p => p.Key);

        return ActivityVisibility.Of(owners, closed);
    }

    /// <summary>Объявлено ли действие — по нему порт журнала и отказывает незнакомому коду.</summary>
    public bool Declares(string code) => _byCode.ContainsKey(code);

    /// <summary>
    /// Название для экрана. Незнакомый код отдаётся как есть: так читаются записи действий, которые
    /// переименовали или которые оставил выключенный с тех пор модуль, — журнал обязан показать
    /// строку, а не спрятать её.
    /// </summary>
    public string Title(string code) => _byCode.GetValueOrDefault(code)?.Title ?? code;

    /// <summary>Каталог целиком: ядро и включённые модули, по названию.</summary>
    public IReadOnlyList<ActivityAction> All =>
        [.. _byCode.Values.OrderBy(a => a.Title, StringComparer.CurrentCulture)];

    /// <summary>
    /// Каталог для отбора на экране — только то, что читающему видно. Иначе отбор сам сообщал бы,
    /// какие действия ведёт закрытый от него модуль, и предлагал бы строку, по которой ничего не
    /// находится.
    /// </summary>
    public IReadOnlyList<ActivityAction> Shown(ActivityVisibility visible) =>
        [.. All.Where(a => visible.Shows(a.Code))];
}
