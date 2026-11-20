using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Modules.Work.Endpoints;

/// <summary>
/// Состояние связи сотрудника с учётной записью в ответах модуля (ТЗ WORK-47; change <c>work-29-candidate-account</c>):
/// <c>linked</c>, <c>none</c> или <c>deleted</c> — теми же строками, что у связи на карточке сотрудника ядра
/// (<c>EmployeeAccountDto</c>; сверяет <c>AccountStateDtoTests</c>). Один перевод на модуль: выдаче, которой
/// понадобится состояние (кандидат, строка назначения), — отсюда.
/// <c>deleted</c> — не <c>none</c>: удалённую учётную запись никто не отвязывал, и экран, получивший «нет связи»,
/// предложил бы связать заново.
/// </summary>
public sealed record AccountStateDto(string State)
{
    public const string Linked = "linked";
    public const string NoneState = "none";
    public const string Deleted = "deleted";

    /// <remarks>
    /// Новое состояние порта не становится молча «нет связи» — то самое «не знаем», выданное за «пусто»: перевод
    /// обязан назвать каждое, и <c>AccountStateDtoTests</c> краснеет раньше, чем выдача дойдёт до исключения.
    /// </remarks>
    public static AccountStateDto Of(ModuleEmployeeAccount account) => new(account.State switch
    {
        ModuleAccountState.Linked => Linked,
        ModuleAccountState.Deleted => Deleted,
        ModuleAccountState.None => NoneState,
        _ => throw new ArgumentOutOfRangeException(nameof(account), account.State, "Неизвестное состояние связи."),
    });
}
