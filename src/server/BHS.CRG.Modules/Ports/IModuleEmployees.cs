namespace BHS.CRG.Modules.Ports;

/// <summary>Сотрудник справочника ядра (ТЗ CORE-7) — как его видит модуль.</summary>
/// <param name="FullName">ФИО — название карточки. Тэга у ФИО нет: оно приходит от «Персоны», схему
/// которой ведёт заказчик, и карточку человек называет им же.</param>
/// <param name="PersonnelNumber">Табельный номер — по тэгу <c>employee.personnelNumber</c>; <c>null</c> — не заполнен.</param>
/// <param name="EmployedFrom">«Принят с» — по тэгу <c>employee.employedFrom</c>.</param>
/// <param name="EmployedTo">«Уволен с» — по тэгу <c>employee.employedTo</c>: первый день, когда сотрудник уже
/// не работает.</param>
/// <param name="Archived">Карточка в архиве (ТЗ CORE-34.4). Архив — не увольнение: дат он не трогает.</param>
public sealed record ModuleEmployee(
    Guid Id, string FullName, string? PersonnelNumber, DateOnly? EmployedFrom, DateOnly? EmployedTo, bool Archived)
{
    /// <summary>
    /// Пометка для выбора на дату: «уволен с …» или «принят с …» — либо <c>null</c>, если в этот день
    /// сотрудник работает. Поиск на дату такого сотрудника не прячет, а помечает: пропавший молча
    /// человек выглядел бы ошибкой справочника.
    /// </summary>
    public string? NotWorkingOn(DateOnly date) =>
        EmployedTo is { } to && date >= to ? $"уволен с {to:dd.MM.yyyy}"
        : EmployedFrom is { } from && date < from ? $"принят с {from:dd.MM.yyyy}"
        : null;
}

/// <summary>
/// Справочник сотрудников ядра — ЧТЕНИЕ (ТЗ PLAN-16, CORE-7; change <c>plan-03-work-types-door</c>):
/// исполнители работ графика. Связь с учётной записью сюда не входит — её строит этап 4 (<c>work-01</c>).
///
/// <para>Поля — по тэгам <c>employee.*</c>, а не по ключам: ключи заказчик вправе переименовать.</para>
///
/// <para><b><c>null</c> — справочник не заведён</b> (нет «Персоны», от которой он производен), а не
/// «сотрудников нет». Модуль говорит это словами <see cref="NotSetUp" />.</para>
///
/// <para>⚠️ Правами не отбирается — как и остальные справочники ядра (issue #675): модуль получает
/// сотрудников от имени ядра.</para>
/// </summary>
public interface IModuleEmployees
{
    /// <summary>Что сказать человеку, когда порт ответил <c>null</c>.</summary>
    const string NotSetUp =
        "Справочник «Сотрудник» не заведён: на экземпляре нет справочника лиц («Персона»), от которого он " +
        "производен. Его заводит администратор в общих данных.";

    /// <summary>Сотрудники по идентификаторам — для показа: архивные и уволенные на месте.</summary>
    Task<IReadOnlyList<ModuleEmployee>?> GetAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);

    /// <summary>
    /// Поиск на выбор по части ФИО или табельного номера: живые карточки, не больше
    /// <paramref name="limit" />, и число архивных под тот же запрос. Не работающие на
    /// <paramref name="onDate" /> в выдаче остаются — после работающих, с пометкой
    /// (<see cref="ModuleEmployee.NotWorkingOn" />).
    /// </summary>
    Task<ModuleChoice<ModuleEmployee>?> SearchAsync(string? query, DateOnly onDate, int limit, CancellationToken ct = default);
}
