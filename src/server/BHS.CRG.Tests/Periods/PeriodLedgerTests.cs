using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Periods;
using BHS.CRG.Modules.Ports;
using Contour = BHS.CRG.Domain.Periods.PeriodContour;

namespace BHS.CRG.Tests.Periods;

/// <summary>
/// Границы закрытия периода — чистые функции (ТЗ CORE-35; задача E1a, issue #1081).
///
/// <para>Всё, что здесь проверяется, ошибается на один день и только на границе: «по дату
/// включительно», «следующий период — с завтрашнего дня», «стройка закрыта позднейшей из двух
/// границ». Поэтому случаи — таблицей вокруг границы, а не «закрыли и проверили».</para>
/// </summary>
public class PeriodLedgerTests
{
    private static readonly Guid SiteA = Guid.NewGuid();
    private static readonly Guid SiteB = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 10, 4);

    private static DateOnly D(int month, int day) => new(2026, month, day);

    private static PeriodClosure Close(Contour contour, DateOnly from, DateOnly through) =>
        PeriodClosure.Close(contour, from, through, null, "Бухгалтер", null, DateTimeOffset.UtcNow, ClosingReport.Empty);

    private static PeriodClosure Reopen(PeriodClosure closure) =>
        PeriodClosure.Reopen(closure, null, "Бухгалтер", "закрыли не тот месяц", DateTimeOffset.UtcNow);

    [Fact]
    public void Без_закрытий_не_закрыто_ничего()
    {
        var ledger = PeriodLedger.Empty;

        Assert.Null(ledger.ClosedThrough(Contour.Company));
        Assert.Null(ledger.ClosedThrough(Contour.Construction(SiteA)));
        Assert.False(ledger.IsClosed(D(1, 1), Contour.Company));
        Assert.Null(ledger.ExpectedFrom(Contour.Company));
    }

    /// <summary>Закрыто — префикс: дни ДО начала первого закрытия закрыты тоже.</summary>
    [Theory]
    [InlineData(1, 1, true)]    // задолго до начала отрезка
    [InlineData(8, 31, true)]   // последний закрытый день
    [InlineData(9, 1, false)]   // первый открытый
    public void Граница_компании_включает_свой_день(int month, int day, bool closed)
    {
        var ledger = PeriodLedger.From([Close(Contour.Company, D(8, 1), D(8, 31))]);

        Assert.Equal(closed, ledger.IsClosed(D(month, day), Contour.Company));
        Assert.Equal(D(9, 1), ledger.ExpectedFrom(Contour.Company));
    }

    /// <summary>
    /// Стройка без своих закрытий закрыта границей компании; со своим — позднейшей из двух, в какую
    /// бы сторону они ни расходились.
    /// </summary>
    [Fact]
    public void Действующая_граница_стройки_позднейшая_из_своей_и_компании()
    {
        var ledger = PeriodLedger.From(
        [
            Close(Contour.Company, D(8, 1), D(8, 31)),
            Close(Contour.Construction(SiteA), D(9, 1), D(9, 30)),
        ]);

        Assert.Equal(D(9, 30), ledger.ClosedThrough(Contour.Construction(SiteA)));
        Assert.Equal(D(8, 31), ledger.ClosedThrough(Contour.Construction(SiteB)));
        Assert.Null(ledger.Own(Contour.Construction(SiteB)));

        // Компания ушла дальше стройки: своя граница стройки осталась, действует граница компании.
        var later = PeriodLedger.From(
        [
            Close(Contour.Construction(SiteA), D(7, 1), D(7, 31)),
            Close(Contour.Company, D(8, 1), D(8, 31)),
        ]);

        Assert.Equal(D(8, 31), later.ClosedThrough(Contour.Construction(SiteA)));
        Assert.Equal(D(7, 31), later.Own(Contour.Construction(SiteA)));
        Assert.Equal(D(9, 1), later.ExpectedFrom(Contour.Construction(SiteA)));
    }

    [Fact]
    public void Закрыть_можно_только_прошедшее()
    {
        var ledger = PeriodLedger.Empty;

        ledger.EnsureCanClose(Contour.Company, D(9, 1), Today.AddDays(-1), Today);

        var today = Assert.Throws<InvalidRequestException>(
            () => ledger.EnsureCanClose(Contour.Company, D(9, 1), Today, Today));
        Assert.Contains("04.10.2026", today.Message);
        Assert.Throws<InvalidRequestException>(
            () => ledger.EnsureCanClose(Contour.Company, D(9, 1), Today.AddDays(1), Today));
        Assert.Throws<InvalidRequestException>(
            () => ledger.EnsureCanClose(Contour.Company, D(9, 2), D(9, 1), Today));
    }

    /// <summary>Отрезок с дырой — отказ, называющий ожидаемое начало. Начало первого — любое.</summary>
    [Fact]
    public void Периоды_закрываются_подряд_и_отказ_называет_начало()
    {
        PeriodLedger.Empty.EnsureCanClose(Contour.Company, D(3, 15), D(8, 31), Today);

        var ledger = PeriodLedger.From([Close(Contour.Company, D(8, 1), D(8, 31))]);

        ledger.EnsureCanClose(Contour.Company, D(9, 1), D(9, 30), Today);

        var gap = Assert.Throws<InvalidRequestException>(
            () => ledger.EnsureCanClose(Contour.Company, D(9, 2), D(9, 30), Today));
        Assert.Contains("начинается 01.09.2026", gap.Message);

        var overlap = Assert.Throws<InvalidRequestException>(
            () => ledger.EnsureCanClose(Contour.Company, D(8, 15), D(9, 30), Today));
        Assert.Contains("начинается 01.09.2026", overlap.Message);

        Assert.Throws<ConflictException>(
            () => ledger.EnsureCanClose(Contour.Company, D(8, 1), D(8, 31), Today));
    }

    /// <summary>
    /// Начало закрытия стройки — после ПОЗДНЕЙШЕЙ из границ: иначе запись стройки заново
    /// «закрывала» бы то, что уже закрыто компанией.
    /// </summary>
    [Fact]
    public void Начало_закрытия_стройки_считается_от_позднейшей_границы()
    {
        var ledger = PeriodLedger.From(
        [
            Close(Contour.Construction(SiteA), D(7, 1), D(7, 31)),
            Close(Contour.Company, D(8, 1), D(8, 31)),
        ]);
        var site = Contour.Construction(SiteA);

        ledger.EnsureCanClose(site, D(9, 1), D(9, 30), Today);

        var fromOwn = Assert.Throws<InvalidRequestException>(
            () => ledger.EnsureCanClose(site, D(8, 1), D(9, 30), Today));
        Assert.Contains("начинается 01.09.2026", fromOwn.Message);
    }

    [Fact]
    public void Отмена_возвращает_прежнюю_границу_и_отменить_можно_только_последнее()
    {
        var august = Close(Contour.Company, D(8, 1), D(8, 31));
        var september = Close(Contour.Company, D(9, 1), D(9, 30));
        var ledger = PeriodLedger.From([august, september]);

        Assert.Same(september, ledger.Reopenable(Contour.Company));

        var reopened = PeriodLedger.From([august, september, Reopen(september)]);
        Assert.Equal(D(8, 31), reopened.ClosedThrough(Contour.Company));
        Assert.Same(august, reopened.Reopenable(Contour.Company));

        // Отменённый месяц закрывается заново — новой записью, с того же дня.
        reopened.EnsureCanClose(Contour.Company, D(9, 1), D(9, 30), Today);

        var both = PeriodLedger.From([august, september, Reopen(september), Reopen(august)]);
        Assert.Null(both.ClosedThrough(Contour.Company));
        Assert.Throws<ConflictException>(() => both.Reopenable(Contour.Company));
    }

    /// <summary>
    /// Отмена закрытия стройки, перекрытого закрытием компании, не открыла бы ни дня — такую
    /// отклоняем с объяснением, а не записываем «успех», после которого ничего не изменилось.
    /// </summary>
    [Fact]
    public void Отмена_закрытия_стройки_под_закрытием_компании_отклоняется()
    {
        var site = Contour.Construction(SiteA);
        var ledger = PeriodLedger.From(
        [
            Close(site, D(7, 1), D(7, 31)),
            Close(Contour.Company, D(8, 1), D(8, 31)),
        ]);

        var refusal = Assert.Throws<ConflictException>(() => ledger.Reopenable(site));
        Assert.Contains("перекрыто закрытием компании", refusal.Message);
        Assert.Null(ledger.ReopenableOrNull(site));

        Assert.Throws<ConflictException>(() => ledger.Reopenable(Contour.Construction(SiteB)));
    }

    /// <summary>
    /// Отмена закрытия компании не открывает стройку, закрывшуюся после него своим закрытием:
    /// закрыто — префикс, и её собственная граница держит и отменяемые дни. Реестр обязан такие
    /// стройки НАЗВАТЬ — иначе диалог обещал бы «дни откроются» там, где они не откроются.
    /// </summary>
    [Fact]
    public void Отмена_закрытия_компании_называет_стройки_закрытые_своим_закрытием()
    {
        var october = Close(Contour.Company, D(8, 1), D(8, 31));
        var ledger = PeriodLedger.From(
        [
            Close(Contour.Construction(SiteB), D(6, 1), D(6, 30)),   // раньше отменяемых дней — откроется
            october,
            Close(Contour.Construction(SiteA), D(9, 1), D(9, 30)),   // дальше компании — останется закрыта
        ]);

        Assert.Equal([SiteA], ledger.KeptClosedByOwn(ledger.Reopenable(Contour.Company)));

        var reopened = PeriodLedger.From(
        [
            october, Reopen(october), Close(Contour.Construction(SiteA), D(9, 1), D(9, 30)),
        ]);
        Assert.True(reopened.IsClosed(D(8, 15), Contour.Construction(SiteA)));
        Assert.False(reopened.IsClosed(D(8, 15), Contour.Company));
    }

    [Fact]
    public void Отмена_требует_причину()
    {
        var closure = Close(Contour.Company, D(8, 1), D(8, 31));

        Assert.Throws<InvalidRequestException>(
            () => PeriodClosure.Reopen(closure, null, "Бухгалтер", "  ", DateTimeOffset.UtcNow));
    }

    // ── Снимок для модулей ────────────────────────────────────────────────────

    /// <summary>
    /// Учётный день (ТЗ COST-16): сама дата, если открыта, иначе первый открытый день контура.
    /// Таблица — вокруг границы, для компании и для стройки без своих закрытий.
    /// </summary>
    [Theory]
    [InlineData(8, 30, 9, 1)]   // закрытый день → первый открытый
    [InlineData(8, 31, 9, 1)]   // последний закрытый → первый открытый
    [InlineData(9, 1, 9, 1)]    // первый открытый — сам
    [InlineData(9, 2, 9, 2)]
    public void Учётный_день_на_границе(int month, int day, int toMonth, int toDay)
    {
        var boundaries = new PeriodBoundaries(D(8, 31), new Dictionary<Guid, DateOnly>());
        var date = D(month, day);

        Assert.Equal(D(toMonth, toDay), boundaries.AccountingDate(date, new Modules.Ports.PeriodContour.Company()));
        Assert.Equal(D(toMonth, toDay),
            boundaries.AccountingDate(date, new Modules.Ports.PeriodContour.Construction(SiteB)));
    }

    [Fact]
    public void Учётный_день_стройки_со_своим_закрытием_дальше_чем_у_компании()
    {
        var boundaries = new PeriodBoundaries(D(8, 31), new Dictionary<Guid, DateOnly> { [SiteA] = D(9, 30) });
        var site = new Modules.Ports.PeriodContour.Construction(SiteA);

        Assert.Equal(D(10, 1), boundaries.AccountingDate(D(9, 15), site));
        Assert.Equal(D(9, 15), boundaries.AccountingDate(D(9, 15), new Modules.Ports.PeriodContour.Company()));
        Assert.True(boundaries.IsClosed(D(9, 30), site));
        Assert.False(boundaries.IsClosed(D(10, 1), site));
    }

    /// <summary>
    /// Ничего не закрыто — учётный день всегда сама дата.
    /// </summary>
    [Fact]
    public void Без_закрытий_учётный_день_сама_дата()
    {
        Assert.Equal(D(1, 1), PeriodBoundaries.None.AccountingDate(D(1, 1), new Modules.Ports.PeriodContour.Company()));
        Assert.Null(PeriodBoundaries.None.ClosedThrough(new Modules.Ports.PeriodContour.Construction(SiteA)));
    }

    /// <summary>
    /// Формул границы две — у ядра (<see cref="PeriodLedger" />) и у снимка контрактов
    /// (<see cref="PeriodBoundaries" />): контракты на домен не ссылаются. Здесь они обязаны
    /// отвечать одинаково на одних и тех же закрытиях — в том числе когда словарь снимка несёт
    /// СОБСТВЕННУЮ границу стройки, а компания ушла дальше.
    /// </summary>
    [Fact]
    public void Снимок_контрактов_отвечает_так_же_как_ядро()
    {
        var ledger = PeriodLedger.From(
        [
            Close(Contour.Construction(SiteA), D(7, 1), D(7, 31)),
            Close(Contour.Company, D(8, 1), D(8, 31)),
            Close(Contour.Construction(SiteB), D(9, 1), D(9, 30)),
        ]);
        var boundaries = new PeriodBoundaries(ledger.Company, ledger.ConstructionsWithOwn.ToDictionary(
            id => id, id => ledger.Own(Contour.Construction(id))!.Value));

        foreach (var site in new[] { SiteA, SiteB, Guid.NewGuid() })
        {
            Assert.Equal(
                ledger.ClosedThrough(Contour.Construction(site)),
                boundaries.ClosedThrough(new Modules.Ports.PeriodContour.Construction(site)));
            foreach (var date in new[] { D(7, 31), D(8, 31), D(9, 1), D(9, 30), D(10, 1) })
                Assert.Equal(
                    ledger.IsClosed(date, Contour.Construction(site)),
                    boundaries.IsClosed(date, new Modules.Ports.PeriodContour.Construction(site)));
        }

        Assert.Equal(ledger.Company, boundaries.ClosedThrough(new Modules.Ports.PeriodContour.Company()));
    }
}
