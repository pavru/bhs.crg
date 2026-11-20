using BHS.CRG.Api.Endpoints.Core;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Endpoints;

namespace BHS.CRG.Tests.Work;

/// <summary>
/// Состояние связи в выдаче модуля <c>work</c> (ТЗ WORK-47; change <c>work-29-candidate-account</c>) — теми же строками,
/// что у связи на карточке сотрудника ядра (<see cref="EmployeeAccountDto" />), и у каждого состояния порта своё.
///
/// <para>Строки модуля — копия: ядро модулю недоступно (<c>ModuleBoundaryTests</c>). Переименуй значение в ядре —
/// <c>GET /api/employees</c> и кандидаты назначения разойдутся молча, а клиентский <c>EmployeeAccount['state']</c>,
/// общий для обоих, будет врать одному из них. Новое состояние порта без перевода — красный тест здесь, а не 500 на
/// весь список кандидатов.</para>
/// </summary>
public class AccountStateDtoTests
{
    [Fact]
    public void Строки_совпадают_с_ядром()
    {
        Assert.Equal(EmployeeAccountDto.Linked, AccountStateDto.Linked);
        Assert.Equal(EmployeeAccountDto.NoneState, AccountStateDto.NoneState);
        Assert.Equal(EmployeeAccountDto.Deleted, AccountStateDto.Deleted);
    }

    [Fact]
    public void Каждое_состояние_порта_переведено_в_своё()
    {
        var states = Enum.GetValues<ModuleAccountState>()
            .Select(s => AccountStateDto.Of(new ModuleEmployeeAccount(s, null, null)).State)
            .ToList();

        Assert.Equal(states.Count, states.Distinct().Count());
        Assert.Equal(
            [AccountStateDto.Deleted, AccountStateDto.Linked, AccountStateDto.NoneState],
            states.Order(StringComparer.Ordinal));
    }
}
