using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Data;

namespace BHS.CRG.Tests.Work;

/// <summary>
/// Правила отчёта и строки (ТЗ WORK-5, WORK-7, WORK-25, WORK-49, WORK-50, CORE-9; change <c>work-13-report-model</c>)
/// — без хоста: правила — чистые функции над тем, что ответили порты. По тесту на правило, и у каждого отказа —
/// поле в словах: строку правит человек на телефоне.
/// </summary>
public class ReportRulesTests
{
    private static readonly Guid CableId = Guid.NewGuid(), TrayId = Guid.NewGuid(), Unit = Guid.NewGuid();
    private static readonly ModuleWorkVariant InTray = new(Guid.NewGuid(), CableId, "в лотке", Archived: false);
    private static readonly ModuleWorkVariant OnTrayMount = new(Guid.NewGuid(), TrayId, "на стене", Archived: false);

    private static readonly ModuleWorkType Cable = WorkType(CableId, "Прокладка кабеля", [InTray]);
    private static readonly ModuleSection Eom = new(Guid.NewGuid(), "ЭОМ-1");
    private static readonly ModuleConstruction SiteA = new(Guid.NewGuid(), "Стройка А", [Eom], "Europe/Moscow", false);
    private static readonly ModuleConstruction SiteB = new(Guid.NewGuid(), "Стройка Б", [new(Guid.NewGuid(), "СС-1")], "Europe/Moscow", false);

    private static ModuleWorkType WorkType(Guid id, string name, IReadOnlyList<ModuleWorkVariant> variants) =>
        new(id, "01.02", name, name, Unit, "м", false, false, false, false, variants);

    private static ReportLineValues Line(
        Guid? variant = null, Guid? section = null, bool crew = false, IReadOnlyList<Guid>? participants = null,
        IReadOnlyList<ReportMaterialValues>? materials = null) =>
        new(Guid.NewGuid(), CableId, variant, 120m, Unit, section, null, null, crew, null,
            materials ?? [], participants ?? []);

    private static string Refusal(Action act) => Assert.Throws<InvalidRequestException>(act).Message;

    [Fact]
    public void Годная_строка_проходит()
    {
        ReportRules.EnsureLine(0, Line(InTray.Id, Eom.Id, materials: [new(Guid.NewGuid(), "остаток бухты")]), Cable, SiteA);
        ReportRules.EnsureLine(0, Line(crew: true, participants: [Guid.NewGuid(), Guid.NewGuid()]), Cable, SiteA);
    }

    /// <summary>Вариант чужого вида — отказ с названием вида (ТЗ WORK-49).</summary>
    [Fact]
    public void Вариант_чужого_вида_отвергается()
    {
        var text = Refusal(() => ReportRules.EnsureLine(1, Line(OnTrayMount.Id), Cable, SiteA));

        Assert.Contains("Строка 2", text);
        Assert.Contains("variantId", text);
        Assert.Contains("Прокладка кабеля", text);
    }

    /// <summary>Вид без вариантов принимает строку без варианта и отвергает любой вариант.</summary>
    [Fact]
    public void Вид_без_вариантов_принимает_строку_без_варианта()
    {
        var bare = WorkType(CableId, "Прокладка кабеля", []);

        ReportRules.EnsureLine(0, Line(), bare, SiteA);
        Assert.Contains("вариантов нет", Refusal(() => ReportRules.EnsureLine(0, Line(InTray.Id), bare, SiteA)));
    }

    /// <summary>Раздел чужой стройки — отказ с названием стройки отчёта.</summary>
    [Fact]
    public void Раздел_чужой_стройки_отвергается()
    {
        var text = Refusal(() => ReportRules.EnsureLine(0, Line(section: SiteB.Sections[0].Id), Cable, SiteA));

        Assert.Contains("sectionId", text);
        Assert.Contains("Стройка А", text);
    }

    /// <summary>Материал без ссылки на номенклатуру — отказ: свободный текст только в примечании (ТЗ WORK-50).</summary>
    [Fact]
    public void Материал_свободным_текстом_отвергается()
    {
        var text = Refusal(() => ReportRules.EnsureLine(0, Line(materials: [new(null, "кабель ВВГнг 3х2.5")]), Cable, SiteA));

        Assert.Contains("материал 1", text);
        Assert.Contains("nomenclatureId", text);
        Assert.Contains("примечании", text);
    }

    /// <summary>Участники у строки без признака бригады — отказ с названием поля.</summary>
    [Fact]
    public void Участники_без_признака_бригады_отвергаются()
    {
        var text = Refusal(() => ReportRules.EnsureLine(0, Line(participants: [Guid.NewGuid()]), Cable, SiteA));

        Assert.Contains("participants", text);
        Assert.Contains("isCrew", text);
    }

    [Fact]
    public void Вида_работы_нет_отказ()
    {
        Assert.Contains("workTypeId", Refusal(() => ReportRules.EnsureLine(0, Line(), null, SiteA)));
    }

    /// <summary>Часы — от 0 до 24; ноль — сведение, а не пропуск (ТЗ WORK-7).</summary>
    [Theory]
    [InlineData(0, true)]
    [InlineData(8.5, true)]
    [InlineData(24, true)]
    [InlineData(-1, false)]
    [InlineData(24.5, false)]
    public void Часы_от_нуля_до_суток(double hours, bool accepted)
    {
        var act = () => ReportRules.EnsureHours((decimal)hours);
        if (accepted) act();
        else Assert.Contains("hours", Refusal(act));
    }

    /// <summary>Поле часов в строке не принимается молча — отказ с его названием (ТЗ WORK-7).</summary>
    [Theory]
    [InlineData("hours")]
    [InlineData("Hours")]
    [InlineData("часы")]
    public void Часы_в_строке_отвергаются(string key)
    {
        var text = Refusal(() => ReportRules.EnsureNoLineHours(2, ["workTypeId", key, "volume"]));

        Assert.Contains("Строка 3", text);
        Assert.Contains(key, text);
        ReportRules.EnsureNoLineHours(0, ["workTypeId", "volume", "details"]);
    }

    /// <summary>Отчёт без стройки — отказ с названием поля (ТЗ WORK-58); смена по умолчанию — дневная (ТЗ MOB-35).</summary>
    [Fact]
    public void Стройка_обязательна_смена_по_умолчанию_дневная()
    {
        var text = Refusal(() => Report.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, new DateOnly(2026, 11, 16),
            ReportShift.Day, 8, null, 1, DateTimeOffset.UtcNow));

        Assert.Contains("constructionId", text);
        Assert.Equal(ReportShift.Day, Report.ParseShift(null));
        Assert.Equal(ReportShift.Night, Report.ParseShift(" Night "));
        Assert.Contains("shift", Refusal(() => Report.ParseShift("evening")));
    }
}
