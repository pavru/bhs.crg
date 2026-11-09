using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Plan.Distribution;

namespace BHS.CRG.Tests.Plan;

/// <summary>
/// Перенос распределения при повторном импорте (change <c>plan-10-distribution</c>, задача 1.5, ТЗ PLAN-5.1,
/// COST-13): раздел ЛСР — по названию, строка — по шифру и порядку его вхождения в разделе; категории отчёта.
/// Сопоставление — без базы; выбор источника и запись — <see cref="DistributionCarryOverStoreTests" />.
/// </summary>
public class DistributionCarryOverTests
{
    private static readonly Guid K1 = Guid.NewGuid();
    private static readonly Guid K2 = Guid.NewGuid();

    private const string Cable = "ГЭСНм08-02-148-01";
    private const string Box = "ГЭСНм08-03-591-02";
    private const string Other = "ГЭСНм10-01-001-01";

    /// <summary>Строки редакции: (номер главы, название главы, шифр, объём) — номер в разделе и порядок по месту.</summary>
    private static List<CarryRow> Rows(params (string Code, string Name, string Rate, decimal Volume)[] rows)
    {
        var inSection = new Dictionary<string, int>();
        return
        [
            .. rows.Select((r, i) =>
            {
                inSection[r.Code] = inSection.GetValueOrDefault(r.Code) + 1;
                var n = inSection[r.Code].ToString();
                return new CarryRow(Guid.NewGuid(), i + 1, r.Code, r.Name, r.Rate, n, n, $"Позиция {r.Rate}", r.Volume, "100 м");
            }),
        ];
    }

    private static CarrySource Source(
        List<CarryRow> rows, Dictionary<string, DistributionTarget>? rules = null,
        params (CarryRow Row, CarriedPart[] Parts)[] parts) =>
        new(rows, (rules ?? []).ToDictionary(r => new LsrKey(r.Key), r => r.Value),
            parts.ToDictionary(p => p.Row.Id, p => (IReadOnlyList<CarriedPart>)p.Parts));

    private static CarriedPart[] Split(decimal k1, decimal k2) => [new(K1, k1), new(K2, k2)];

    private static IReadOnlyList<CarriedPart> PartsOf(CarryPlan plan, Guid rowId) => Assert.Single(plan.Parts, p => p.RowId == rowId).Parts;

    [Fact]
    public void Как_есть_всё_перенесено_без_отчёта()
    {
        var from = Rows(("1", "Кабельные линии", Cable, 12m), ("1", "Кабельные линии", Box, 3m));
        var to = Rows(("1", "Кабельные линии", Cable, 12m), ("1", "Кабельные линии", Box, 3m));
        var plan = DistributionCarryOverPlan.Build(
            Source(from, new() { ["1"] = DistributionTarget.Section(K1) }, (from[0], Split(7m, 5m))), to);

        Assert.Equal(1, plan.Carried);
        Assert.Empty(plan.Items);
        Assert.Equal(Split(7m, 5m), PartsOf(plan, to[0].Id));
        Assert.Equal((new LsrKey("1"), DistributionTarget.Section(K1)), Assert.Single(plan.Rules));
    }

    [Fact]
    public void Шифр_в_двух_разделах_переносится_по_разделам()
    {
        var from = Rows(("1", "Корпус А", Cable, 12m), ("2", "Корпус Б", Cable, 10m));
        var to = Rows(("1", "Корпус А", Cable, 12m), ("2", "Корпус Б", Cable, 10m));
        var plan = DistributionCarryOverPlan.Build(
            Source(from, null, (from[0], Split(7m, 5m)), (from[1], [new(K2, 10m)])), to);

        Assert.Equal(Split(7m, 5m), PartsOf(plan, to[0].Id));
        Assert.Equal([new CarriedPart(K2, 10m)], PartsOf(plan, to[1].Id));
        Assert.Equal(2, plan.Carried);
    }

    [Fact]
    public void Вставка_другого_шифра_в_начало_раздела_с_повтором_шифра_каждая_на_свою_строку()
    {
        var from = Rows(("1", "Кабельные линии", Cable, 12m), ("1", "Кабельные линии", Cable, 8m));
        var to = Rows(("1", "Кабельные линии", Other, 1m), ("1", "Кабельные линии", Cable, 12m), ("1", "Кабельные линии", Cable, 8m));
        var plan = DistributionCarryOverPlan.Build(
            Source(from, null, (from[0], Split(7m, 5m)), (from[1], [new(K1, 8m)])), to);

        Assert.Equal(Split(7m, 5m), PartsOf(plan, to[1].Id));
        Assert.Equal([new CarriedPart(K1, 8m)], PartsOf(plan, to[2].Id));
        Assert.Equal(2, plan.Count(CarryOverCategory.Shifted));
        Assert.Equal([to[1].Id, to[2].Id], plan.Items.Select(i => i.RowId));
        Assert.Equal(0, plan.Carried);
    }

    [Fact]
    public void Две_строки_с_шифром_стали_тремя_неоднозначно_с_тремя_кандидатами()
    {
        var from = Rows(("1", "Кабельные линии", Cable, 12m), ("1", "Кабельные линии", Cable, 8m));
        var to = Rows(("1", "Кабельные линии", Cable, 12m), ("1", "Кабельные линии", Cable, 8m), ("1", "Кабельные линии", Cable, 4m));
        var plan = DistributionCarryOverPlan.Build(
            Source(from, null, (from[0], Split(7m, 5m)), (from[1], [new(K1, 8m)])), to);

        Assert.Empty(plan.Parts);
        Assert.Equal(2, plan.Items.Count);
        Assert.All(plan.Items, i =>
        {
            Assert.Equal(CarryOverCategory.Ambiguous, i.Category);
            Assert.Equal(to.Select(r => r.Id), i.Candidates);
        });
    }

    [Fact]
    public void Вставленная_перед_разделом_глава_правило_уходит_на_раздел_с_тем_же_названием()
    {
        var from = Rows(("1", "Освещение", Box, 3m), ("2", "Кабельные линии 0,4 кВ", Cable, 12m));
        var to = Rows(("1", "Демонтаж", Other, 1m), ("2", "Освещение", Box, 3m), ("3", "Кабельные линии 0,4 кВ", Cable, 12m));
        var plan = DistributionCarryOverPlan.Build(
            Source(from, new() { ["2"] = DistributionTarget.Section(K2), ["1"] = DistributionTarget.WholeConstruction },
                (from[1], Split(7m, 5m))), to);

        Assert.Contains((new LsrKey("3"), DistributionTarget.Section(K2)), plan.Rules);
        Assert.Contains((new LsrKey("2"), DistributionTarget.WholeConstruction), plan.Rules);
        Assert.DoesNotContain(plan.Rules, r => r.Section == new LsrKey("1"));
        Assert.Equal(Split(7m, 5m), PartsOf(plan, to[2].Id));
        Assert.Empty(plan.Items);
    }

    [Fact]
    public void Раздел_без_названия_по_номеру_главы_с_пометкой()
    {
        var from = Rows(("1", "", Cable, 12m));
        var to = Rows(("1", "", Cable, 12m));
        var plan = DistributionCarryOverPlan.Build(
            Source(from, new() { ["1"] = DistributionTarget.Section(K1) }, (from[0], Split(7m, 5m))), to);

        Assert.Equal(Split(7m, 5m), PartsOf(plan, to[0].Id));
        Assert.Equal(2, plan.Count(CarryOverCategory.Shifted));
        Assert.Contains(plan.Items, i => i.Subject == CarryOverSubject.Rule);
    }

    [Fact]
    public void Глава_с_номером_занята_названием_другого_раздела_источника_не_сопоставлена()
    {
        // Раздел источника 1 без названия; в новой редакции глава 1 называется как раздел источника 2.
        var from = Rows(("1", "", Box, 3m), ("2", "Кабельные линии", Cable, 12m));
        var to = Rows(("1", "Кабельные линии", Cable, 12m));
        var plan = DistributionCarryOverPlan.Build(
            Source(from, new() { ["1"] = DistributionTarget.Section(K1) }, (from[0], [new(K1, 3m)])), to);

        Assert.Empty(plan.Rules);
        Assert.Equal(2, plan.Count(CarryOverCategory.Lost));
    }

    [Fact]
    public void Объём_12_стал_10_при_частях_7_и_5_не_помещается()
    {
        var from = Rows(("1", "Кабельные линии", Cable, 12m));
        var to = Rows(("1", "Кабельные линии", Cable, 10m));
        var plan = DistributionCarryOverPlan.Build(Source(from, null, (from[0], Split(7m, 5m))), to);

        Assert.Empty(plan.Parts);
        var item = Assert.Single(plan.Items);
        Assert.Equal(CarryOverCategory.Overflow, item.Category);
        Assert.Equal(to[0].Id, item.RowId);
        Assert.Equal(10m, item.NewVolume);
        Assert.Equal(Split(7m, 5m), item.Parts);
    }

    [Fact]
    public void Пропавшая_строка_в_lost_с_прежними_частями()
    {
        var from = Rows(("1", "Кабельные линии", Cable, 12m), ("1", "Кабельные линии", Box, 3m));
        var to = Rows(("1", "Кабельные линии", Box, 3m));
        var plan = DistributionCarryOverPlan.Build(Source(from, null, (from[0], Split(7m, 5m))), to);

        var item = Assert.Single(plan.Items);
        Assert.Equal(CarryOverCategory.Lost, item.Category);
        Assert.Equal(Cable, item.Source.RateCode);
        Assert.Equal(Split(7m, 5m), item.Parts);
        Assert.Empty(plan.Parts);
    }

    [Fact]
    public void Раздел_пропал_правило_и_части_в_lost()
    {
        var from = Rows(("1", "Кабельные линии", Cable, 12m));
        var to = Rows(("1", "Освещение", Box, 3m), ("2", "Слаботочка", Other, 1m));
        var plan = DistributionCarryOverPlan.Build(
            Source(from, new() { ["1"] = DistributionTarget.Section(K1) }, (from[0], Split(7m, 5m))), to);

        Assert.Empty(plan.Rules);
        Assert.Equal([CarryOverSubject.Rule, CarryOverSubject.Row], plan.Items.Select(i => i.Subject));
        Assert.All(plan.Items, i => Assert.Equal(CarryOverCategory.Lost, i.Category));
    }

    /// <summary>Каждая строка новой редакции получает не больше одной строки источника.</summary>
    [Fact]
    public void Одной_строке_не_больше_одной_строки_источника()
    {
        var from = Rows(("1", "А", Cable, 5m), ("1", "А", Cable, 5m), ("2", "Б", Cable, 5m));
        var to = Rows(("1", "А", Cable, 5m), ("1", "А", Cable, 5m), ("2", "Б", Cable, 5m));
        var plan = DistributionCarryOverPlan.Build(
            Source(from, null, (from[0], [new(K1, 5m)]), (from[1], [new(K2, 5m)]), (from[2], [new(K1, 1m)])), to);

        Assert.Equal(plan.Parts.Count, plan.Parts.Select(p => p.RowId).Distinct().Count());
        Assert.Equal(3, plan.Parts.Count);
    }

    [Fact]
    public void Строки_вне_разделов_ЛСР_переносятся_как_есть_без_пометки()
    {
        static List<CarryRow> Outside() =>
        [
            new(Guid.NewGuid(), 1, null, null, Cable, "1", "1", "Кабель", 12m, "100 м"),
            new(Guid.NewGuid(), 2, null, null, Box, "2", "2", "Короб", 3m, "100 м"),
        ];
        var from = Outside();
        var to = Outside();
        var plan = DistributionCarryOverPlan.Build(new CarrySource(from,
                new Dictionary<LsrKey, DistributionTarget> { [new LsrKey(null)] = DistributionTarget.WholeConstruction },
                new Dictionary<Guid, IReadOnlyList<CarriedPart>> { [from[0].Id] = Split(7m, 5m) }), to);

        Assert.Equal(1, plan.Carried);
        Assert.Empty(plan.Items);
        Assert.Equal((new LsrKey(null), DistributionTarget.WholeConstruction), Assert.Single(plan.Rules));
        Assert.Equal(Split(7m, 5m), PartsOf(plan, to[0].Id));
    }

    [Fact]
    public void Единица_строки_сменилась_части_не_переносятся_overflow()
    {
        var from = Rows(("1", "Кабельные линии", Cable, 12m));
        var to = Rows(("1", "Кабельные линии", Cable, 1200m));
        to[0] = to[0] with { SourceUnit = "м" };
        var plan = DistributionCarryOverPlan.Build(Source(from, null, (from[0], Split(7m, 5m))), to);

        Assert.Empty(plan.Parts);
        var item = Assert.Single(plan.Items);
        Assert.Equal(CarryOverCategory.Overflow, item.Category);
        Assert.Equal(to[0].Id, item.RowId);
        Assert.Equal(1200m, item.NewVolume);
    }
}
