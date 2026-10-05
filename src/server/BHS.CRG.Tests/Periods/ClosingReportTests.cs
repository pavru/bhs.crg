using BHS.CRG.Domain.Periods;
using BHS.CRG.Modules.Ports;
using Contour = BHS.CRG.Domain.Periods.PeriodContour;

namespace BHS.CRG.Tests.Periods;

/// <summary>
/// Перечень диалога закрытия — чистые правила (задача E1b, issue #1099; ревью PR #1201): что считается
/// годной записью и какие дни закрытие закрывает впервые.
/// </summary>
public class ClosingReportTests
{
    private static readonly Guid SiteA = Guid.NewGuid();
    private static readonly Guid SiteB = Guid.NewGuid();

    private static DateOnly D(int month, int day) => new(2026, month, day);

    private static PeriodClosure Close(Contour contour, DateOnly from, DateOnly through) =>
        PeriodClosure.Close(contour, from, through, null, "Бухгалтер", null, DateTimeOffset.UtcNow, ClosingReport.Empty);

    /// <summary>
    /// «Разобралось» — не «годно». Каждый из этих перечней десериализуется без ошибки, а потом роняет
    /// «Историю» на первом же обращении к разделу или к единице счёта — целиком, на весь список.
    /// </summary>
    [Theory]
    [InlineData("""{"sections":[{}]}""")]
    [InlineData("""{"sections":[null]}""")]
    [InlineData("""{"sections":[{"module":"costs","title":"Счета","dateRule":"…","unfinished":[],"frozen":null}]}""")]
    [InlineData("""{"sections":[{"module":"costs","title":"Счета","dateRule":"…","unfinished":[null],"frozen":[]}]}""")]
    [InlineData("""{"sections":[{"module":"costs","title":"Счета","dateRule":"…","unfinished":[{"key":"k","text":"т","count":1}],"frozen":[]}]}""")]
    [InlineData("""{"sections":[{"module":"costs","title":"Счета","dateRule":"…","unfinished":[],"frozen":[{"key":"k","text":"т","count":1,"unit":{"one":"счёт"}}]}]}""")]
    [InlineData("""{"other":1}""")]
    [InlineData("не JSON")]
    public void Перечень_разобранный_наполовину_не_считается_перечнем(string json)
    {
        Assert.Null(ClosingReport.FromJson(json));
        // И в запись из копии такой не ложится: запись неизменяема, мусор остался бы навсегда.
        Assert.Null(PeriodClosure.Restore(
            Guid.NewGuid(), PeriodClosureKind.Close, PeriodContourKind.Company, null, D(9, 1), D(9, 30),
            DateTimeOffset.UtcNow, null, "Бухгалтер", null, null, json).Report);
    }

    [Fact]
    public void Годный_перечень_переживает_запись_и_чтение()
    {
        var unit = new ClosingUnit("счёт", "счёта", "счетов");
        var report = new ClosingReport([new("costs", "Счета и накладные", "По учётному периоду оплаты.",
            [new("unsettled", "Не разобраны", 2, unit, 1500.5m, "costs.report.read", "Пояснение")],
            [new("locked", "Запрутся целиком", 3, unit, null, null, null)])]);

        var read = ClosingReport.FromJson(report.ToJson());

        Assert.NotNull(read);
        Assert.Equal(report.ToJson(), read.ToJson());
        Assert.Equal("Не завершено: Счета и накладные — не разобраны: 2 счёта", read.JournalText());
    }

    /// <summary>
    /// Закрытие компании закрывает впервые не все дни своего отрезка: стройка, закрытая своим закрытием
    /// дальше, часть из них уже держит. Названа она — с днём, по который закрыта; стройка, чьё закрытие
    /// до отрезка не дотягивается, не названа.
    /// </summary>
    [Fact]
    public void Стройки_закрытые_дальше_компании_названы_с_их_границей()
    {
        var ledger = PeriodLedger.From(
        [
            Close(Contour.Company, D(8, 1), D(8, 31)),
            Close(Contour.Construction(SiteA), D(9, 1), D(9, 20)),   // держит часть сентября
            Close(Contour.Construction(SiteB), D(6, 1), D(6, 30)),   // раньше — перекрыта компанией
        ]);

        var ahead = ledger.ClosedAheadOfCompany(ledger.ExpectedFrom(Contour.Company));
        Assert.Equal(new Dictionary<Guid, DateOnly> { [SiteA] = D(9, 20) }, ahead);

        // У компании не закрыто ничего — впервые закрывается всё, и своё закрытие любой стройки значимо.
        var first = PeriodLedger.From([Close(Contour.Construction(SiteB), D(6, 1), D(6, 30))]);
        Assert.Equal(new Dictionary<Guid, DateOnly> { [SiteB] = D(6, 30) }, first.ClosedAheadOfCompany(null));
    }

    /// <summary>
    /// Ссылка строки — только путь внутри приложения. Табуляцию и перевод строки браузер из адреса
    /// выбрасывает, и «/⇥/example.org» открыл бы чужой сайт.
    /// </summary>
    [Theory]
    [InlineData("/tables/costs.invoices/registry#filter=%7B%7D", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("/", false)]
    [InlineData("https://example.org/x", false)]
    [InlineData("//example.org/x", false)]
    [InlineData("/\\example.org", false)]
    [InlineData("/\t/example.org/x", false)]
    [InlineData("/\n/example.org/x", false)]
    [InlineData("/\r/example.org/x", false)]
    [InlineData("/ /example.org/x", false)]
    [InlineData("javascript:alert(1)", false)]
    public void Ссылка_строки_только_путь_внутри_приложения(string? link, bool local) =>
        Assert.Equal(local, ClosingReport.IsLocalLink(link));

    /// <summary>«Закрывается впервые» — на границах: день включительно с обеих сторон.</summary>
    [Fact]
    public void День_закрывается_впервые_только_в_отрезке_и_не_у_стройки_закрытой_дальше()
    {
        var scope = new ModuleClosingScope(null, D(9, 1), D(9, 30), new Dictionary<Guid, DateOnly> { [SiteA] = D(9, 20) });

        Assert.False(scope.ClosesAnew(null, D(8, 31)));
        Assert.True(scope.ClosesAnew(null, D(9, 1)));
        Assert.True(scope.ClosesAnew(null, D(9, 30)));
        Assert.False(scope.ClosesAnew(null, D(10, 1)));

        // Стройка А закрыта по 20-е: её дни по 20-е уже заперты, с 21-го — закрываются впервые.
        Assert.False(scope.ClosesAnew(SiteA, D(9, 20)));
        Assert.True(scope.ClosesAnew(SiteA, D(9, 21)));
        // Чужая стройка и «не на стройку» в тот же день — впервые.
        Assert.True(scope.ClosesAnew(SiteB, D(9, 20)));
        Assert.True(scope.ClosesAnew(null, D(9, 20)));

        // Первое закрытие контура: начала нет, закрывается всё по дату.
        var whole = new ModuleClosingScope(null, null, D(9, 30), new Dictionary<Guid, DateOnly>());
        Assert.True(whole.ClosesAnew(null, D(1, 1)));
        Assert.False(whole.ClosesAnew(null, D(10, 1)));
    }
}
