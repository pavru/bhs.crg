using BHS.CRG.Infrastructure.Renditions;

namespace BHS.CRG.Tests.Renditions;

/// <summary>
/// Пометка о раскладке по профилю узнаётся среди прочих (issue #1495): порт видов отделяет её от списка
/// пометок, а экран показывает у выбора профиля. Имя профиля стоит посередине пометки, и узнавание не
/// должно от него зависеть.
/// </summary>
public class ExcelPageLayoutNoteTests
{
    /// <summary>Имена, на которых сломался бы разбор по кавычкам и двоеточию.</summary>
    public static TheoryData<string> Names => ["По ширине", "Кв. «А»: 1", "»: в Excel", ""];

    [Theory]
    [MemberData(nameof(Names))]
    public void Каждая_форма_пометки_о_раскладке_узнаётся(string name)
    {
        foreach (var fitWidth in new[] { true, false })
        foreach (var sheets in new[] { 1, 3, 12 })
        {
            var note = ExcelPageLayout.AppliedNote(new PageProfile(name, 9, "portrait", fitWidth, 5, 5, 5, 5), sheets);
            Assert.True(ExcelPageLayout.IsLayoutNote(note), note);
        }
    }

    [Fact]
    public void Пометка_о_неудаче_раскладки_узнаётся() =>
        Assert.True(ExcelPageLayout.IsLayoutNote(ExcelPageLayout.FailedNote));

    /// <summary>Пометки другого рода остаются строками — их узнавать нельзя.</summary>
    [Theory]
    [InlineData(OfficeRenditionBuilder.SmallTextNote)]
    [InlineData(OfficeRenditionBuilder.UncheckedNote)]
    [InlineData("Листов с данными: 2 — «Счёт», «Параметры»; каждый начинается с новой страницы.")]
    [InlineData(RenditionPostconditions.LostTextNote + ": 3 из 120 строк.")]
    [InlineData("Листы () вписаны в ширину страницы по профилю печати «А»: в Excel они печатаются иначе.")]
    [InlineData("Лист вписан в ширину страницы по профилю печати «А»")]
    [InlineData("")]
    public void Чужая_пометка_не_узнаётся(string note) =>
        Assert.False(ExcelPageLayout.IsLayoutNote(note));
}
