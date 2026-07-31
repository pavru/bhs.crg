using System.Text.Json;
using BHS.CRG.Application.Documents;

namespace BHS.CRG.Tests.Documents;

/// <summary>
/// Отсечение тяжёлых листьев в списочных ответах (issue #520). Главное требование — не потерять
/// КЛЮЧИ: по ним считается покрытие базовым экземпляром, и без них поля начали бы требовать
/// заполнения вручную.
///
/// Проверяем структуру, а не подстроки: <c>JsonNode.ToJsonString()</c> экранирует кириллицу
/// (<c>П</c>), и сравнение по тексту падало бы на ровном месте, ничего не говоря о поведении.
/// </summary>
public class HeavyLeafElisionTests
{
    private static JsonElement Elide(string json) =>
        HeavyLeafElision.WithoutHeavyLeaves(JsonDocument.Parse(json)).RootElement;

    private static bool IsElided(JsonElement el) =>
        el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty(HeavyLeafElision.ElidedMarker, out var m)
        && m.GetString() == HeavyLeafElision.ImageKind;

    [Fact]
    public void KeepsKeyAndOptions_DropsPayload()
    {
        var root = Elide("""
            {"Печать":{"src":"data:image/png;base64,iVBORw0KGgo=","width":"4cm","align":"center"}}
            """);

        var stamp = root.GetProperty("Печать");           // ключ на месте
        Assert.True(IsElided(stamp));
        Assert.False(stamp.TryGetProperty("src", out _)); // полезной нагрузки нет
        Assert.Equal("4cm", stamp.GetProperty("width").GetString());
        Assert.Equal("center", stamp.GetProperty("align").GetString());
    }

    /// <summary>Легаси-форма значения — голая строка data-URI; ключ обязан уцелеть так же.</summary>
    [Fact]
    public void LegacyBareStringIsElidedToo()
    {
        var root = Elide("""{"Факсимиле":"data:image/png;base64,iVBORw0KGgo="}""");
        Assert.True(IsElided(root.GetProperty("Факсимиле")));
    }

    [Fact]
    public void FindsImagesInNestedObjectsAndArrays()
    {
        var root = Elide("""
            {"Орг":{"Скан":{"src":"data:image/png;base64,AAAA"}},
             "Материалы":[{"Фото":{"src":"data:image/jpeg;base64,BBBB"}}]}
            """);

        Assert.True(IsElided(root.GetProperty("Орг").GetProperty("Скан")));
        Assert.True(IsElided(root.GetProperty("Материалы")[0].GetProperty("Фото")));
    }

    /// <summary>Скаляры — то, ради чего список и грузит Data, — не трогаем.</summary>
    [Fact]
    public void ScalarsUntouched()
    {
        const string json = """{"ИНН":"7701234567","Дом":12,"Активна":true,"_baseRef":"abc"}""";
        var root = Elide(json);
        Assert.Equal("7701234567", root.GetProperty("ИНН").GetString());
        Assert.Equal(12, root.GetProperty("Дом").GetInt32());
        Assert.True(root.GetProperty("Активна").GetBoolean());
        Assert.Equal("abc", root.GetProperty("_baseRef").GetString());
    }

    /// <summary>
    /// Резать нечего — возвращаем ТОТ ЖЕ экземпляр: картинок нет у подавляющего большинства записей,
    /// и платить за них копированием документа незачем.
    /// </summary>
    [Fact]
    public void ReturnsSameInstanceWhenNothingToElide()
    {
        var doc = JsonDocument.Parse("""{"ИНН":"7701234567"}""");
        Assert.Same(doc, HeavyLeafElision.WithoutHeavyLeaves(doc));
    }

    /// <summary>Ссылка на файл-вложение живёт в блобе, а не в JSON — её трогать не за что.</summary>
    [Fact]
    public void FileAttachmentNodeUntouched()
    {
        var root = Elide("""{"Файл":{"$type":"file","blobPath":"attachments/x.pdf","fileName":"x.pdf"}}""");
        var file = root.GetProperty("Файл");
        Assert.Equal("file", file.GetProperty("$type").GetString());
        Assert.Equal("attachments/x.pdf", file.GetProperty("blobPath").GetString());
        Assert.False(IsElided(file));
    }
}
