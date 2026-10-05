using System.Linq.Expressions;
using BHS.CRG.Domain.Activity;

namespace BHS.CRG.Application.Activity;

/// <summary>
/// Какие записи журнала действий вправе увидеть читающий (задача H1 этапа 2, issue #1104).
///
/// <para><b>Зачем.</b> Журнал в продукте один, и пишут в него и ядро, и модули. Читался он по одному
/// <c>core.audit.read</c> — то есть человек без модуля счетов видел, какой счёт какого поставщика
/// завели, разобрали и оплатили. Сумм в записях нет (issue #1190), но «кто, кому и когда платит» —
/// те же данные модуля, только без цифры.</para>
///
/// <para><b>Правило.</b> Запись видна, если её владелец открыт читающему: ядро — всякому, кто вошёл в
/// журнал, модуль — тому, у кого есть хотя бы одно его право. Сверх того действие вправе назвать своё
/// право чтения, и тогда запись видна только с ним.</para>
///
/// <para>⚠️ Владелец — первая часть кода действия, и перечень ОТКРЫТЫХ владельцев положительный. Запись
/// модуля, которого на экземпляре сейчас нет, в него не попадает и не видна никому: его прав никому не
/// выдать, и «не знаю, чья запись» обязано значить «закрыто», а не «покажу всем». Запись не пропадает —
/// она вернётся на экран вместе с модулем и едет в резервной копии.</para>
///
/// <para>⚠️ У чтения журнала значения по умолчанию НЕТ нарочно: забытый отбор означал бы весь журнал
/// любому вошедшему в него, и выглядело бы это как обычный ответ. Служебное чтение называет
/// <see cref="Whole" /> словом.</para>
/// </summary>
public sealed class ActivityVisibility
{
    private readonly string[]? _owners;
    private readonly HashSet<string> _closed;

    private ActivityVisibility(string[]? owners, IEnumerable<string> closed)
    {
        _owners = owners;
        _closed = closed.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Весь журнал, без отбора: служебное чтение (сверка состояния, тесты, перенос), а не ответ
    /// пользователю. Ответ пользователю собирает каталог действий по его правам.
    /// </summary>
    public static ActivityVisibility Whole { get; } = new(null, []);

    /// <param name="openOwners">Владельцы, записи которых читающему открыты: «core» и коды модулей.</param>
    /// <param name="closedActions">Действия открытых владельцев, требующие права, которого у читающего
    /// нет.</param>
    public static ActivityVisibility Of(IEnumerable<string> openOwners, IEnumerable<string> closedActions) =>
        new([.. openOwners.Distinct(StringComparer.Ordinal)], closedActions);

    /// <summary>Видна ли запись с таким кодом действия. То же правило, что и <see cref="Filter" />.</summary>
    public bool Shows(string action) =>
        !_closed.Contains(action) &&
        (_owners is null || _owners.Any(owner => action.StartsWith(owner + ".", StringComparison.Ordinal)));

    /// <summary>
    /// Отбор для запроса; <c>null</c> — отбирать нечего. Строится деревом, а не <c>Any</c> по списку:
    /// отбор обязан выполниться в базе (от него зависят и страница, и общее число), а перечень
    /// владельцев — две-три строки.
    /// </summary>
    public Expression<Func<ActivityRecord, bool>>? Filter()
    {
        if (_owners is null && _closed.Count == 0) return null;

        var record = Expression.Parameter(typeof(ActivityRecord), "r");
        var action = Expression.Property(record, nameof(ActivityRecord.Action));
        Expression? body = null;

        if (_owners is not null)
        {
            var startsWith = typeof(string).GetMethod(nameof(string.StartsWith), [typeof(string)])!;
            // Без единого открытого владельца не видно ничего — а не «отбора нет».
            body = _owners
                .Select(owner => (Expression)Expression.Call(action, startsWith, Expression.Constant(owner + ".")))
                .DefaultIfEmpty(Expression.Constant(false))
                .Aggregate(Expression.OrElse);
        }

        if (_closed.Count > 0)
        {
            var contains = typeof(Enumerable).GetMethods()
                .Single(m => m.Name == nameof(Enumerable.Contains) && m.GetParameters().Length == 2)
                .MakeGenericMethod(typeof(string));
            var open = Expression.Not(Expression.Call(contains, Expression.Constant(_closed.ToArray()), action));
            body = body is null ? open : Expression.AndAlso(body, open);
        }

        return Expression.Lambda<Func<ActivityRecord, bool>>(body!, record);
    }
}
