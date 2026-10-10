using System.Text;
using BHS.CRG.Infrastructure.Renditions;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using static BHS.CRG.Tests.Renditions.RenditionFixtures;

namespace BHS.CRG.Tests.Renditions;

/// <summary>
/// Постусловия на готовый PDF (issue #1268) — против отказа, переодетого в результат.
///
/// <para>Конвертер отвечает успехом и тогда, когда успеха нет: пустая книга даёт код 200 и страницу
/// без единой буквы. Каждый тест здесь ломает настоящий вход — ответ конвертера — и проверяет, что
/// служба ответила отказом или пометкой, а не образом «как будто всё хорошо».</para>
/// </summary>
public class RenditionPostconditionTests
{
    /// <summary>Двадцать разных слов в двадцати ячейках: доля найденных считается на глаз.</summary>
    private static readonly string[] Words =
    [
        "alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india", "juliet",
        "kilo", "lima", "mike", "november", "oscar", "papa", "quebec", "romeo", "sierra", "tango",
    ];

    private static Task<Rendition> BuildFrom(byte[] converterAnswer) =>
        Service(Returns(converterAnswer)).BuildAsync(Workbook(Words));

    private static string First(int count) => string.Join(" ", Words.Take(count));

    [Fact]
    public async Task Пустой_PDF_вместо_образа_книги_это_отказ_а_не_образ()
    {
        var refused = Assert.IsType<Rendition.Refused>(await BuildFrom(Pdf("")));

        Assert.Equal(RenditionRefusal.Failed, refused.Kind);
        Assert.Contains("нет текста", refused.Reason);
    }

    [Fact]
    public async Task Ответ_который_не_открывается_как_PDF_это_отказ()
    {
        var refused = Assert.IsType<Rendition.Refused>(await BuildFrom(Encoding.ASCII.GetBytes("%PDF-1.7 and nothing else")));

        Assert.Equal(RenditionRefusal.Failed, refused.Kind);
    }

    /// <summary>У документа Word сверять не с чем: он мог быть пуст и сам — или нести один вставленный скан.</summary>
    [Fact]
    public async Task Документ_без_текста_пуст()
    {
        var refused = Assert.IsType<Rendition.Refused>(await Service(Returns(Pdf(""))).BuildAsync(Document()));

        Assert.Equal(RenditionRefusal.Empty, refused.Kind);
    }

    [Fact]
    public async Task Документ_с_текстом_превращается_в_образ()
    {
        string? sentAs = null;
        var service = Service((request, _) =>
        {
            sentAs = SentName(request);
            return Task.FromResult(PdfReply(Pdf("Invoice text", "Second page")));
        });

        var built = Assert.IsType<Rendition.Built>(await service.BuildAsync(Document()));

        Assert.Equal(2, built.Pages);
        Assert.Equal("file.docx", sentAs);
    }

    [Fact]
    public async Task Весь_текст_ячеек_на_странице_образ_без_пометок()
    {
        var built = Assert.IsType<Rendition.Built>(await BuildFrom(Pdf(First(20))));

        Assert.Empty(built.Notes);
    }

    /// <summary>Слова в PDF стоят не так, как в ячейках: перенос и разрядка — дело вёрстки, а не потеря.</summary>
    [Fact]
    public async Task Перенос_и_регистр_потерей_не_считаются()
    {
        var typeset = string.Join(" ", Words.Select(word => word.ToUpperInvariant()));

        var built = Assert.IsType<Rendition.Built>(await BuildFrom(Pdf(typeset[..40], typeset[40..])));

        Assert.Empty(built.Notes);
    }

    /// <summary>Между порогами: образ годен, но человек обязан узнать, что он неполон.</summary>
    [Theory]
    [InlineData(18, "90")]
    [InlineData(10, "50")]
    public async Task Часть_текста_потеряна_образ_с_пометкой(int found, string percent)
    {
        var built = Assert.IsType<Rendition.Built>(await BuildFrom(Pdf(First(found))));

        var note = Assert.Single(built.Notes);
        Assert.StartsWith("Часть текста не попала на страницу", note);
        Assert.Contains(percent, note);
    }

    [Fact]
    public async Task Потеряно_больше_половины_текста_это_отказ()
    {
        var refused = Assert.IsType<Rendition.Refused>(await BuildFrom(Pdf(First(9))));

        Assert.Equal(RenditionRefusal.Failed, refused.Kind);
        Assert.Contains("меньше половины", refused.Reason);
    }

    /// <summary>
    /// Порог пометки стоит вплотную к здоровым счетам: 49 слов из 50 — это 98 %, образ чист; 48 —
    /// уже пометка. Обрезанный счёт из пробы давал 94 %, и порог в 95 его едва замечал.
    /// </summary>
    [Theory]
    [InlineData(49, 0)]
    [InlineData(48, 1)]
    public async Task Порог_пометки_98_процентов(int found, int notes)
    {
        var fifty = Enumerable.Range(0, 50).Select(index => $"token{index:00}end").ToArray();
        var service = Service(Returns(Pdf(string.Join(" ", fifty.Take(found)))));

        var built = Assert.IsType<Rendition.Built>(await service.BuildAsync(Workbook(fifty)));

        Assert.Equal(notes, built.Notes.Count);
    }

    /// <summary>
    /// Доля в пометке — вниз до целого: 48 слов из 49 — это 97,96 %, и «найдено 98 %» рядом с
    /// предупреждением о потере читалось бы как здоровое число.
    /// </summary>
    [Fact]
    public async Task Доля_в_пометке_не_округляется_до_порога()
    {
        var words = Enumerable.Range(0, 49).Select(index => $"token{index:00}end").ToArray();
        var service = Service(Returns(Pdf(string.Join(" ", words.Take(48)))));

        var built = Assert.IsType<Rendition.Built>(await service.BuildAsync(Workbook(words)));

        Assert.Contains("найдено 97 %", Assert.Single(built.Notes));
    }

    /// <summary>
    /// Прайс-лист на десятки тысяч разных слов: каждое ищется проходом по всему тексту, и без
    /// предела на число искомых слов сверка шла бы минутами на потоке запроса.
    /// </summary>
    [Fact]
    public async Task Большая_книга_сверяется_быстро_и_повторы_слов_считаются()
    {
        var cells = Enumerable.Range(0, 30_000).Select(index => $"item{index:00000}name repeated").ToArray();
        var service = Service(Returns(Pdf(string.Join(" ", cells.Select(cell => cell.Split(' ')[0])) + " repeated")));

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var built = Assert.IsType<Rendition.Built>(await service.BuildAsync(Workbook(cells)));

        Assert.Empty(built.Notes);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"сверка шла {clock.Elapsed}");
    }

    /// <summary>
    /// Слово, стоящее в тридцати ячейках, весит в доле тридцать, а не один: иначе два потерянных
    /// редких слова против одного частого давали бы «найдена треть» — и отказ здоровой книге.
    /// </summary>
    [Fact]
    public async Task Повторы_слова_весят_в_доле_столько_сколько_их_в_книге()
    {
        string[] cells = [.. Enumerable.Repeat("common", 30), "lostalpha", "lostbravo"];

        var built = Assert.IsType<Rendition.Built>(await Service(Returns(Pdf("common"))).BuildAsync(Workbook(cells)));

        Assert.Contains("найдено 93 %", Assert.Single(built.Notes));
    }

    /// <summary>
    /// В ячейках одни прочерки и галочки: букв нет ни в книге, ни в её PDF. Это пустой файл, а не
    /// «текст потерян по дороге».
    /// </summary>
    [Fact]
    public async Task Книга_из_одних_знаков_пуста_а_не_испорчена_конвертером()
    {
        var refused = Assert.IsType<Rendition.Refused>(
            await Service(Returns(Pdf(""))).BuildAsync(Workbook("—", "***", "✓")));

        Assert.Equal(RenditionRefusal.Empty, refused.Kind);
    }

    /// <summary>
    /// Буква, записанная в PDF, ещё не напечатана. Ячейку, не поместившуюся внизу листа, LibreOffice
    /// дописывает НИЖЕ края страницы: в файле текст есть, на листе его нет. Сверка обязана считать
    /// его потерянным — иначе она находит то, чего не видит никто.
    /// </summary>
    [Fact]
    public async Task Текст_за_краем_листа_на_странице_не_считается()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);
        page.AddText(First(15), 8, new PdfPoint(20, 800), font);
        page.AddText(string.Join(" ", Words.Skip(15)), 8, new PdfPoint(20, -17), font);

        var built = Assert.IsType<Rendition.Built>(await BuildFrom(builder.Build()));

        Assert.Contains("75", Assert.Single(built.Notes));
    }

    /// <summary>
    /// Страницы без текста не выбрасываются, а называются: старый Excel даёт лишние пустые листы,
    /// но на такой же странице может стоять печать картинкой — по текстовому слою их не различить.
    /// </summary>
    [Fact]
    public async Task Страницы_без_текста_названы_пометкой()
    {
        var built = Assert.IsType<Rendition.Built>(await BuildFrom(Pdf(First(20), "", "")));

        Assert.Equal(3, built.Pages);
        Assert.Equal("Страниц: 3, из них без текста: 2.", Assert.Single(built.Notes));
    }

    [Fact]
    public async Task Образ_длиннее_предела_страниц_слишком_велик()
    {
        var pages = Enumerable.Repeat(First(20), RenditionLimits.MaxPages + 1).ToArray();

        var refused = Assert.IsType<Rendition.Refused>(await BuildFrom(Pdf(pages)));

        Assert.Equal(RenditionRefusal.TooLarge, refused.Kind);
        Assert.Contains($"получилось {RenditionLimits.MaxPages + 1} страниц", refused.Reason);
    }

    /// <summary>Числа и даты со страницей не сверяются: на ней они записаны по формату ячейки.</summary>
    [Fact]
    public async Task Книга_из_одних_чисел_проходит_по_тексту_страницы()
    {
        var numbers = Workbook(book =>
        {
            var row = book.CreateSheet("Sheet").CreateRow(0);
            row.CreateCell(0).SetCellValue(3834.16);
            row.CreateCell(1).SetCellValue(new DateTime(2026, 2, 20));
        });

        var built = Assert.IsType<Rendition.Built>(await Service(Returns(Pdf("3 834,16 20.02.2026"))).BuildAsync(numbers));

        Assert.Empty(built.Notes);
    }
}
