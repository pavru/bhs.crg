using BHS.CRG.Domain.Jobs;
using BHS.CRG.Domain.Notifications;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Зеркала в портах модулей не расходятся с оригиналами (задача M2 этапа 2, issue #1069).
///
/// <para>Перечисления в контрактах модулей ПОВТОРЯЮТ доменные — по-другому нельзя: ссылок на наши
/// проекты в контрактах нет вовсе, на том и держится правило «модуль знает ядро, ядро о модуле не
/// знает». Плата за это — молчаливое расхождение: новое значение в домене не сломает ни сборку
/// модуля, ни переходник со <c>switch</c> по зеркалу, а значение, доехавшее до модуля через строку
/// статуса, обернётся отказом в бою. Тот же приём и тот же сторож, что у
/// <see cref="ModuleSchemaLevel" /> (<see cref="ModuleRecordTypeTests" />).</para>
/// </summary>
public class ModulePortMirrorTests
{
    /// <summary>Важность уведомления модуля — зеркало доменной.</summary>
    [Fact]
    public void Важность_уведомления_повторяет_доменную()
    {
        var mirrored = Enum.GetNames<ModuleNotificationLevel>().OrderBy(x => x, StringComparer.Ordinal);
        var domain = Enum.GetNames<NotificationSeverity>().OrderBy(x => x, StringComparer.Ordinal);

        Assert.Equal(domain, mirrored);
    }

    /// <summary>
    /// Статус фоновой задачи — зеркало доменного.
    ///
    /// Сверять его особенно важно: статус доезжает до модуля СТРОКОЙ (ядро отдаёт задачу в DTO с
    /// текстовым статусом), и незнакомое имя превращается в отказ «зеркало разошлось» — уже в бою,
    /// на живой задаче.
    /// </summary>
    [Fact]
    public void Статус_задачи_повторяет_доменный()
    {
        var mirrored = Enum.GetNames<ModuleJobStatus>().OrderBy(x => x, StringComparer.Ordinal);
        var domain = Enum.GetNames<JobStatus>().OrderBy(x => x, StringComparer.Ordinal);

        Assert.Equal(domain, mirrored);
    }

    /// <summary>
    /// Действие модуля для журнала объявляется по форме <c>модуль.объект.действие</c> — иначе отказ.
    ///
    /// Форма проверяется в контрактах, префикс — в приложении (см.
    /// <c>Integration.ModulePortsTests.Журнал_отказывает_коду_не_из_модуля</c>): порту не с чем
    /// сверить состав поставки. Одно без другого не работает, поэтому и проверяются они парой.
    /// </summary>
    [Theory]
    [InlineData("costs.invoice.paid", "Счёт оплачен", null)]
    [InlineData("", "Название", "пуст")]
    [InlineData("costs.paid", "Счёт оплачен", "трёх частей")]
    [InlineData("costs.invoice.paid.twice", "Счёт оплачен", "трёх частей")]
    [InlineData("costs..paid", "Счёт оплачен", "трёх частей")]
    [InlineData("costs.invoice.paid", "", "нет названия")]
    public void Действие_журнала_проверяет_свою_форму(string code, string title, string? expected)
    {
        var problem = new ModuleActivityAction(code, title).Validate();

        if (expected is null) Assert.Null(problem);
        else Assert.Contains(expected, problem);
    }
}
