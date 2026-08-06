using BHS.CRG.Api.Endpoints.Common;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Приём и отдача вложений судят по ОДНОЙ таблице, и решает в ней расширение (issue #705).
///
/// До этого приём смотрел на заявленный клиентом тип, отдача — на расширение имени, и списки жили
/// порознь. Не-браузерный клиент мог положить <c>evil.svg</c>, объявив его <c>image/png</c>: белый
/// список приёма против такого не давал ничего, и вся защита держалась на карте расширений отдачи —
/// один рубеж вместо двух.
/// </summary>
public class AttachmentTypesTests
{
    [Theory]
    [InlineData("scan.pdf")]
    [InlineData("SCAN.PDF")]
    [InlineData("photo.jpg")]
    [InlineData("photo.jpeg")]
    [InlineData("смета.xlsx")]
    [InlineData("Сертификат ЕАЭС RU С-RU.АТ21.pdf")]
    public void SupportedExtension_IsAccepted(string fileName)
        => Assert.True(AttachmentTypes.IsAcceptedName(fileName));

    /// <summary>Активных форматов во вложениях нет: их открывает не тот, кто загружал.</summary>
    [Theory]
    [InlineData("evil.svg")]
    [InlineData("page.html")]
    [InlineData("script.js")]
    [InlineData("archive.zip")]
    public void ActiveOrUnknownExtension_IsRejected(string fileName)
        => Assert.False(AttachmentTypes.IsAcceptedName(fileName));

    /// <summary>
    /// Имя без расширения отвергаем: тип отдачи выводится из имени, и такой файл лёг бы нечитаемым.
    /// </summary>
    [Theory]
    [InlineData("scan")]
    [InlineData("")]
    [InlineData(null)]
    public void NameWithoutExtension_IsRejected(string? fileName)
        => Assert.False(AttachmentTypes.IsAcceptedName(fileName));

    /// <summary>
    /// Заявленный клиентом тип на решение НЕ влияет — и это не упущение.
    ///
    /// Он берётся из реестра ОС и врёт: на машине без Office Windows объявляет <c>.xlsx</c> как
    /// <c>application/vnd.ms-excel</c>. Сверка заявленного типа с расширением отвергала бы такую
    /// загрузку без всякой причины, тогда как судьбу файла решает расширение: им же выводится тип
    /// отдачи. Проверка по расширению и строже, и без ложных отказов.
    /// </summary>
    [Fact]
    public void DeclaredTypeIsIrrelevant_ExtensionDecides()
    {
        // Ровно тот случай, ради которого проверка заведена: заявить можно что угодно,
        // но .svg не пройдёт.
        Assert.False(AttachmentTypes.IsAcceptedName("evil.svg"));
        // И тот, который прежняя сверка ломала: имя годное, а заявленный тип у клиента свой.
        Assert.True(AttachmentTypes.IsAcceptedName("смета.xlsx"));
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            AttachmentTypes.ServeTypeFor("смета.xlsx"));
    }

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
    /// Приём и отдача обязаны сходиться на каждом расширении: принятое имя должно отдаваться
    /// осмысленным типом, а не потоком байтов. Разойдись они — файл лёг бы и не открылся.
    /// </summary>
    [Theory]
    [InlineData("f.pdf")]
    [InlineData("f.docx")]
    [InlineData("f.xlsx")]
    [InlineData("f.xls")]
    [InlineData("f.png")]
    [InlineData("f.jpg")]
    [InlineData("f.jpeg")]
    [InlineData("f.gif")]
    [InlineData("f.webp")]
    public void AcceptAndServe_AgreeOnEveryExtension(string fileName)
    {
        Assert.True(AttachmentTypes.IsAcceptedName(fileName));
        Assert.NotEqual("application/octet-stream", AttachmentTypes.ServeTypeFor(fileName));
    }

    /// <summary>Поле-изображение берёт из того же списка только растровые.</summary>
    [Theory]
    [InlineData("f.png", true)]
    [InlineData("f.webp", true)]
    [InlineData("f.jpeg", true)]
    [InlineData("f.pdf", false)]
    [InlineData("f.svg", false)]
    [InlineData("noext", false)]
    public void ImageEndpoint_TakesRasterOnly(string fileName, bool expected)
        => Assert.Equal(expected, AttachmentTypes.IsImageName(fileName));

    /// <summary>Текст отказа перечисляет расширения: имя файла человек видит, MIME — нет.</summary>
    [Fact]
    public void AcceptedExtensions_ListsWhatUserSees()
    {
        Assert.Contains(".pdf", AttachmentTypes.AcceptedExtensions);
        Assert.Contains(".xlsx", AttachmentTypes.AcceptedExtensions);
        Assert.DoesNotContain("application/", AttachmentTypes.AcceptedExtensions);
    }
}
