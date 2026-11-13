using BHS.CRG.Api.Auth;
using BHS.CRG.Modules;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Коды в составах системных ролей — той же формы, что объявленные права (ТЗ AUTH-1), даже если модуля,
/// который их объявит, в сборке ещё нет (ревью PR #1932).
///
/// <para><b>От чего это.</b> Права модулей, которых нет, состав роли перечислять вправе: при наполнении
/// незнакомый код пропускается и приходит вместе с модулем (<see cref="RoleDefinition" />). Поэтому код,
/// который объявить НЕЛЬЗЯ, пропускался молча и навсегда: <c>work.assign</c> из двух частей стоял в
/// составе «Менеджера проекта» с этапа 1 и обнаружился только на каркасе <c>work</c>, когда
/// <c>AppPermission.Validate</c> остановил старт. Так же вышло у счетов с <c>costs.allocate</c>.</para>
///
/// <para>Проверка — та же, что у объявления (<see cref="AppPermission.Validate" />), а не своё выражение:
/// два правила одной формы однажды разошлись бы.</para>
/// </summary>
public class SystemRoleCodesTests
{
    /// <summary>
    /// Известные коды неверной формы — храповик: каждый ждёт решения своего этапа, и запись уходит, как
    /// только код в составе роли приведён к форме (иначе тест потребует убрать её).
    /// </summary>
    private static readonly Dictionary<string, string> Pending = new()
    {
        // Этап 7 (ОЖР). Таблица прав ТЗ (60-ожр.md) сама в двух частях — ozhr.read, ozhr.edit, ozhr.sign,
        // — и с составом роли не совпадает; имена решит нарезка этапа, а не каркас другого модуля.
        ["ozhr.release"] = "имя права выпуска листа на подпись — нарезка этапа 7 (ТЗ OZHR, таблица прав)",
    };

    [Fact]
    public void Every_code_in_system_roles_has_the_declarable_form()
    {
        var malformed = SystemRoles.All
            .SelectMany(r => r.Permissions.Select(code => (r.Name, Code: code)))
            .Where(p => p.Code != PermissionCatalog.ReadAllCode)
            .Select(p => (p.Name, p.Code, Fault: new AppPermission(p.Code, "проверка", "проверка").Validate()))
            .Where(p => p.Fault is not null)
            .ToList();

        var bad = malformed.Where(p => !Pending.ContainsKey(p.Code)).Select(p => $"{p.Name}: {p.Fault}").ToList();
        Assert.True(bad.Count == 0,
            "В составе системной роли код, который модуль объявить не сможет, — при наполнении он молча " +
            "пропускается, и роль не получит права никогда. Приведите код к виду «модуль.объект.действие»:\n  " +
            string.Join("\n  ", bad));

        var stale = Pending.Keys.Where(c => malformed.All(p => p.Code != c)).ToList();
        Assert.True(stale.Count == 0,
            "Эти коды уже приведены к форме или убраны из составов — уберите записи из Pending:\n  " +
            string.Join("\n  ", stale));
    }

    /// <summary>Проверка обязана доходить до дела: код неверной формы она ловит.</summary>
    [Theory]
    [InlineData("work.assign")]
    [InlineData("work.report.read.all")]
    public void The_check_catches_a_malformed_code(string code) =>
        Assert.NotNull(new AppPermission(code, "проверка", "проверка").Validate());
}
