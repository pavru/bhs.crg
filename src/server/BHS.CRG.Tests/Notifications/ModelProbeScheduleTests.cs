using BHS.CRG.Api.Notifications;
using BHS.CRG.Application.Settings;

namespace BHS.CRG.Tests.Notifications;

/// <summary>
/// Когда мониторинг платит за пробу модели (issue #921). Ошибка здесь не видна ни на одном экране —
/// только в счёте у поставщика: раньше на процесс уходило до 480 оплаченных запросов в сутки.
/// </summary>
public class ModelProbeScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Первый_круг_процесса_пробует_если_вердикта_нет()
    {
        // В том числе после перезапуска с восстановленным отказом: вердикта в кэше нет, и молчаливое
        // «здоров» было бы ложью.
        Assert.Equal(ModelProbe.IfUnknown, ModelProbeSchedule.Decide(null, configChanged: false, announcedDown: true, Now));
        Assert.Equal(ModelProbe.IfUnknown, ModelProbeSchedule.Decide(null, configChanged: false, announcedDown: false, Now));
    }

    [Fact]
    public void Новая_конфигурация_пробуется_сразу()
    {
        var justNow = Now.AddMinutes(-1);
        Assert.Equal(ModelProbe.IfUnknown, ModelProbeSchedule.Decide(justNow, configChanged: true, announcedDown: false, Now));
    }

    [Fact]
    public void Между_пересмотрами_мониторинг_не_платит()
    {
        var recently = Now - ModelProbeSchedule.WhileUp + TimeSpan.FromMinutes(1);
        Assert.Equal(ModelProbe.CacheOnly, ModelProbeSchedule.Decide(recently, false, announcedDown: false, Now));
    }

    [Fact]
    public void В_норме_вердикт_пересматривается_по_длинному_интервалу()
    {
        Assert.Equal(ModelProbe.Refresh,
            ModelProbeSchedule.Decide(Now - ModelProbeSchedule.WhileUp, false, announcedDown: false, Now));
    }

    [Fact]
    public void В_отказе_вердикт_пересматривается_чаще()
    {
        // Без этого круг замкнулся бы: «модель снята» сама не протухает, и выйти из отказа нечем.
        var twoHoursAgo = Now.AddHours(-2);
        Assert.Equal(ModelProbe.Refresh, ModelProbeSchedule.Decide(twoHoursAgo, false, announcedDown: true, Now));
        Assert.Equal(ModelProbe.CacheOnly, ModelProbeSchedule.Decide(twoHoursAgo, false, announcedDown: false, Now));
    }

    [Fact]
    public void Сутки_в_норме_стоят_единиц_запросов_а_не_сотен()
    {
        // Сорокапятисекундный круг на сутки: сколько раз расписание разрешит платить.
        DateTimeOffset? paidAt = null;
        var paid = 0;
        for (var t = Now; t < Now.AddDays(1); t += TimeSpan.FromSeconds(45))
        {
            if (ModelProbeSchedule.Decide(paidAt, false, announcedDown: false, t) == ModelProbe.CacheOnly) continue;
            paid++;
            paidAt = t;
        }

        Assert.InRange(paid, 1, 5);
    }
}
