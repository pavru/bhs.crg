using System.Reflection;
using BHS.CRG.Application.Activity;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Tests.Common;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Перепись сворачиваемых видов журнала (ТЗ CORE-28.3): перечень закрыт, и новый сворачиваемый вид не
/// проходит, пока не вписан сюда с причиной.
///
/// <para>Почему сторож, а не проверка каталога. Каталог при старте знает запретные виды ЯДРА — роли,
/// права, состав модулей, закрытие периода. Переходы, оплату и исправление модуля он распознать не
/// может: у вида нет признака «это переход», есть только код и название. Ряд «Счёт оплачен ×3» спрятал
/// бы, что оплату отмечали трижды, — и выглядело бы это как аккуратный журнал. Отличить правку,
/// идущую потоком, от значимого действия может только человек, и сторож заставляет его сказать это
/// словами.</para>
/// </summary>
public class ActivityFoldInventoryTests
{
    /// <summary>
    /// Сворачиваемые виды ядра и всех модулей — и почему. Добавляя строку, вы решаете: записи этого
    /// вида — поток однородных правок, и показать их одним рядом честнее, чем каждую отдельно.
    /// </summary>
    private static readonly Dictionary<string, string> MayFold = new()
    {
        ["costs.invoice.changed"] = "правка шапки черновика: автосохранение пишет её на каждом уходе с поля (COST-6)",
        ["costs.invoice.lines"] = "правка строк черновика: автосохранение пишет её из каждой строки (COST-6)",
        ["costs.waybill.changed"] = "правка шапки черновика накладной — тот же поток, что у счёта (COST-6)",
        ["costs.waybill.lines"] = "правка строк черновика накладной — тот же поток, что у счёта (COST-6)",
    };

    [Fact]
    public void Сворачиваются_только_виды_из_переписи()
    {
        var unnamed = Folded().Where(code => !MayFold.ContainsKey(code)).Order(StringComparer.Ordinal).ToList();

        Assert.True(unnamed.Count == 0,
            "Свёртку объявили виды журнала, которых нет в переписи: " + string.Join(", ", unnamed) + ".\n" +
            "Переходы состояния, оплата и её отмена, исправление и удаление не сворачиваются никогда " +
            "(ТЗ CORE-28.3): ряд «×N» спрятал бы, сколько раз это делали. Если вид — правда поток " +
            "однородных правок, впишите его в MayFold с причиной.");
    }

    /// <summary>Строка переписи, за которой свёртки больше нет, тихо разрешала бы её следующему автору.</summary>
    [Fact]
    public void Перепись_не_хранит_умерших_записей()
    {
        var folded = Folded().ToHashSet(StringComparer.Ordinal);
        var stale = MayFold.Keys.Where(code => !folded.Contains(code)).Order(StringComparer.Ordinal).ToList();

        Assert.True(stale.Count == 0,
            "В переписи виды, которые свёртку больше не объявляют: " + string.Join(", ", stale) + ". Уберите строки.");
    }

    /// <summary>Виды ядра и всех модулей решения, объявившие свёртку.</summary>
    private static IEnumerable<string> Folded() =>
        ActivityActions.All.Where(a => a.Fold is not null).Select(a => a.Code)
            .Concat(SolutionModules.Names
                .Select(name => Assembly.Load(new AssemblyName(name)))
                .SelectMany(assembly => assembly.GetTypes())
                .Where(t => typeof(IModuleActivityActions).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false }
                    && t.GetConstructor(Type.EmptyTypes) is not null)
                .SelectMany(t => ((IModuleActivityActions)Activator.CreateInstance(t)!).Actions)
                .Where(a => a.Fold is not null)
                .Select(a => a.Code));
}
