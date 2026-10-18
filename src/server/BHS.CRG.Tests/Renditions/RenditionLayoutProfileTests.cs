using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Storage;
using BHS.CRG.Infrastructure.Renditions;

namespace BHS.CRG.Tests.Renditions;

/// <summary>Профиль печати в домене (issue #1454): системный неудаляем, отпечаток не зависит от имени.</summary>
public class RenditionLayoutProfileTests
{
    private static readonly PageMarginsMm Five = new(5, 5, 5, 5);

    private static RenditionLayoutProfile Own(string name = "Узкий") =>
        RenditionLayoutProfile.Create(name, PaperFormat.A4, PageOrientation.Portrait, PageFit.Width, Five);

    [Fact]
    public void Системный_не_удаляется_а_свой_удаляется()
    {
        var refused = Assert.Throws<ConflictException>(() => RenditionLayoutProfile.System().EnsureDeletable());
        Assert.Contains("системный", refused.Message);

        Own().EnsureDeletable();
    }

    /// <summary>
    /// Системный — под идентификатором-константой: копия другой установки несёт его под тем же, и
    /// второго системного не появляется.
    /// </summary>
    [Fact]
    public void Системный_всегда_под_одним_идентификатором_и_с_заводскими_параметрами()
    {
        var system = RenditionLayoutProfile.System();

        // Литералом, а не сравнением с собой: случайный идентификатор совпал бы сам с собой в одном
        // процессе, а копию другой установки получил бы вторым системным профилем. Значение — договор
        // с копиями, снятыми раньше: менять его нельзя.
        Assert.Equal(new Guid("6f1d3c2a-0b7e-4c59-9a43-5e2a7d4b1454"), RenditionLayoutProfile.SystemId);
        Assert.Equal(RenditionLayoutProfile.SystemId, system.Id);
        Assert.Equal((RenditionLayoutProfile.SystemName, PaperFormat.A4, PageOrientation.Portrait, PageFit.Width, Five, true),
            (system.Name, system.Paper, system.Orientation, system.Fit, system.Margins, system.IsFactory));
    }

    [Fact]
    public void Переименование_не_меняет_отпечаток_а_параметр_меняет()
    {
        var profile = Own();
        var stamp = profile.Stamp;

        profile.Update("Широкий", PaperFormat.A4, PageOrientation.Portrait, PageFit.Width, Five);
        Assert.Equal(stamp, profile.Stamp);

        profile.Update("Широкий", PaperFormat.A4, PageOrientation.Portrait, PageFit.Width, Five with { Left = 6 });
        Assert.NotEqual(stamp, profile.Stamp);
    }

    /// <summary>Каждый параметр входит в отпечаток: иначе правка его не «старила» бы виды.</summary>
    [Fact]
    public void Отпечаток_различает_каждый_параметр()
    {
        RenditionLayoutProfile With(PaperFormat paper = PaperFormat.A4, PageOrientation orientation = PageOrientation.Portrait,
            PageFit fit = PageFit.Width, PageMarginsMm? margins = null) =>
            RenditionLayoutProfile.Create("Проба", paper, orientation, fit, margins ?? Five);

        string[] stamps =
        [
            With().Stamp, With(paper: PaperFormat.A3).Stamp, With(orientation: PageOrientation.Landscape).Stamp,
            With(fit: PageFit.None).Stamp, With(margins: Five with { Top = 6 }).Stamp,
            With(margins: Five with { Bottom = 6 }).Stamp, With(margins: Five with { Left = 6 }).Stamp,
            With(margins: Five with { Right = 6 }).Stamp,
        ];
        Assert.Equal(stamps.Length, stamps.Distinct().Count());
    }

    [Fact]
    public void Системный_правится_но_не_переименовывается()
    {
        var system = RenditionLayoutProfile.System();

        system.Update(RenditionLayoutProfile.SystemName, PaperFormat.A3, PageOrientation.Landscape, PageFit.None, Five);
        Assert.Equal((PaperFormat.A3, false), (system.Paper, system.IsFactory));

        Assert.Throws<PrintProfileFieldException>(() =>
            system.Update("Мой", PaperFormat.A4, PageOrientation.Portrait, PageFit.Width, Five));
    }

    [Fact]
    public void Сброс_к_заводским_только_у_системного()
    {
        var system = RenditionLayoutProfile.System();
        system.Update(RenditionLayoutProfile.SystemName, PaperFormat.A5, PageOrientation.Portrait, PageFit.None, new(20, 20, 10, 10));

        system.ResetToFactory();

        Assert.True(system.IsFactory);
        Assert.Throws<ConflictException>(() => Own().ResetToFactory());
    }

    [Theory]
    [InlineData("", PaperFormat.A4, PageOrientation.Portrait, 5, 5)]
    [InlineData("Проба", PaperFormat.A4, PageOrientation.Portrait, 41, 5)]
    [InlineData("Проба", PaperFormat.A4, PageOrientation.Portrait, -1, 5)]
    // A5 альбомная: высота 148, с полями 25 + 25 остаётся 98 мм — меньше ста.
    [InlineData("Проба", PaperFormat.A5, PageOrientation.Landscape, 25, 5)]
    // A5 книжная: ширина 148, с полями 25 + 25 — те же 98.
    [InlineData("Проба", PaperFormat.A5, PageOrientation.Portrait, 5, 25)]
    public void Негодные_параметры_получают_отказ(string name, PaperFormat paper, PageOrientation orientation, int vertical, int horizontal)
        => Assert.Throws<PrintProfileFieldException>(() => RenditionLayoutProfile.Create(
            name, paper, orientation, PageFit.Width, new(vertical, vertical, horizontal, horizontal)));

    [Fact]
    public void Название_длиннее_предела_получает_отказ()
    {
        Assert.Throws<PrintProfileFieldException>(() => Own(new string('я', RenditionLayoutProfile.NameMax + 1)));
        Assert.Equal(RenditionLayoutProfile.NameMax, Own(new string('я', RenditionLayoutProfile.NameMax)).Name.Length);
    }

    /// <summary>Ровно сто миллиметров полезного места — ещё можно: предел включительный.</summary>
    [Fact]
    public void Сто_миллиметров_полезного_места_допустимы() =>
        RenditionLayoutProfile.Create("A5", PaperFormat.A5, PageOrientation.Landscape, PageFit.Width, new(24, 24, 40, 40));

    [Fact]
    public void Профиль_раскладки_снимается_с_параметров_профиля()
    {
        var page = PageProfile.From(RenditionLayoutProfile.Create(
            "Альбомный A3", PaperFormat.A3, PageOrientation.Landscape, PageFit.None, new(10, 15, 7, 3)));

        Assert.Equal(new PageProfile("Альбомный A3", 8, "landscape", false, 10, 15, 7, 3), page);
        Assert.Equal(11, PageProfile.From(RenditionLayoutProfile.Create(
            "A5", PaperFormat.A5, PageOrientation.Portrait, PageFit.Width, Five)).PaperSize);
        Assert.Equal(new PageProfile(RenditionLayoutProfile.SystemName, 9, "portrait", true, 5, 5, 5, 5), PageProfile.Default);
    }
}
