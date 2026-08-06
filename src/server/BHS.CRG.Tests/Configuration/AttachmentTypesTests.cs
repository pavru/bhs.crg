using BHS.CRG.Api.Endpoints.Common;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Приём и отдача вложений сверяются по ОДНОЙ таблице (issue #705).
///
/// До этого приём смотрел на заявленный клиентом тип, отдача — на расширение имени, и списки жили
/// порознь. Не-браузерный клиент мог положить <c>evil.svg</c>, объявив его <c>image/png</c>: белый
/// список приёма против такого не давал ничего, и вся защита держалась на карте расширений отдачи —
/// один рубеж вместо двух.
/// </summary>
public class AttachmentTypesTests
{
    [Theory]
    [InlineData("application/pdf")]
    [InlineData("image/png")]
    [InlineData("image/jpeg")]
    [InlineData("application/vnd.ms-excel")]
    public void SupportedType_IsAccepted(string type) => Assert.True(AttachmentTypes.IsAccepted(type));

    /// <summary>Активных форматов во вложениях нет: их открывает не тот, кто загружал.</summary>
    [Theory]
    [InlineData("image/svg+xml")]
    [InlineData("text/html")]
    [InlineData("application/xhtml+xml")]
    [InlineData(null)]
    public void ActiveOrUnknownType_IsRejected(string? type) => Assert.False(AttachmentTypes.IsAccepted(type));

    [Theory]
    [InlineData("scan.pdf", "application/pdf")]
    [InlineData("SCAN.PDF", "application/pdf")]
    [InlineData("photo.jpg", "image/jpeg")]
    [InlineData("photo.jpeg", "image/jpeg")]
    [InlineData("Сертификат ЕАЭС.pdf", "application/pdf")]
    public void NameMatchingDeclaredType_Passes(string fileName, string type)
        => Assert.True(AttachmentTypes.ExtensionMatches(fileName, type));

    /// <summary>Ровно тот случай, ради которого проверка и заведена.</summary>
    [Fact]
    public void SvgDeclaredAsPng_IsRejected()
        => Assert.False(AttachmentTypes.ExtensionMatches("evil.svg", "image/png"));

    [Theory]
    [InlineData("report.xlsx", "application/pdf")]
    [InlineData("photo.png", "image/jpeg")]
    [InlineData("archive.zip", "application/pdf")]
    public void NameContradictingDeclaredType_IsRejected(string fileName, string type)
        => Assert.False(AttachmentTypes.ExtensionMatches(fileName, type));

    /// <summary>
    /// Имя без расширения тоже отвергаем: отдать такой файл заявленным типом всё равно не выйдет —
    /// тип выводится из имени, — и вложение легло бы нечитаемым.
    /// </summary>
    [Theory]
    [InlineData("scan")]
    [InlineData("")]
    [InlineData(null)]
    public void NameWithoutExtension_IsRejected(string? fileName)
        => Assert.False(AttachmentTypes.ExtensionMatches(fileName, "application/pdf"));

    [Theory]
    [InlineData("scan.pdf", "application/pdf")]
    [InlineData("photo.JPEG", "image/jpeg")]
    [InlineData("pic.webp", "image/webp")]
    public void KnownExtension_IsServedWithItsType(string fileName, string expected)
        => Assert.Equal(expected, AttachmentTypes.ServeTypeFor(fileName));

    /// <summary>
    /// Незнакомое расширение отдаётся как поток байтов — в том числе загруженное когда-то, пока
    /// список приёма был шире (SVG принимался до версии 0.88.0).
    /// </summary>
    [Theory]
    [InlineData("old.svg")]
    [InlineData("page.html")]
    [InlineData("noext")]
    public void UnknownExtension_IsServedAsOctetStream(string fileName)
        => Assert.Equal("application/octet-stream", AttachmentTypes.ServeTypeFor(fileName));

    /// <summary>
    /// Две стороны таблицы обязаны сходиться: тип, который принимаем, должен отдаваться собой же по
    /// своему основному расширению. Разойдись они — файл лёг бы, а открылся потоком байтов.
    /// </summary>
    [Theory]
    [InlineData("application/pdf", "f.pdf")]
    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.document", "f.docx")]
    [InlineData("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "f.xlsx")]
    [InlineData("application/vnd.ms-excel", "f.xls")]
    [InlineData("image/png", "f.png")]
    [InlineData("image/jpeg", "f.jpg")]
    [InlineData("image/gif", "f.gif")]
    [InlineData("image/webp", "f.webp")]
    public void AcceptAndServe_AgreeOnEveryType(string type, string fileName)
    {
        Assert.True(AttachmentTypes.IsAccepted(type));
        Assert.True(AttachmentTypes.ExtensionMatches(fileName, type));
        Assert.Equal(type, AttachmentTypes.ServeTypeFor(fileName));
    }

    /// <summary>Поле-изображение берёт из того же списка только растровые.</summary>
    [Theory]
    [InlineData("image/png", true)]
    [InlineData("image/webp", true)]
    [InlineData("application/pdf", false)]
    [InlineData("image/svg+xml", false)]
    public void ImageEndpoint_TakesRasterOnly(string type, bool expected)
        => Assert.Equal(expected, AttachmentTypes.IsImage(type));
}
