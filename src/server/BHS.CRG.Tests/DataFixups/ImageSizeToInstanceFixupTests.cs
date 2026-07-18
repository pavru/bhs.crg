using System.Text.Json;
using BHS.CRG.Application.Schema;
using BHS.CRG.Infrastructure.DataFixups;

namespace BHS.CRG.Tests.DataFixups;

public class ImageSizeToInstanceFixupTests
{
    private const string Png = "data:image/png;base64,AAAA";

    private static readonly IReadOnlyDictionary<string, ImageRenderOptions> Opts =
        new Dictionary<string, ImageRenderOptions>
        {
            ["Логотип"] = new("4cm", null, "center", null),
            ["Фото"] = new(null, "3cm", null, "contain"),
        };

    [Fact]
    public void MigrateDataJson_WrapsBareDataUri_WithSchemaSize()
    {
        var json = $$"""{"Логотип":"{{Png}}","Имя":"ООО"}""";
        var result = ImageSizeToInstanceFixup.MigrateDataJson(json, Opts);
        Assert.NotNull(result);
        var root = JsonDocument.Parse(result!).RootElement;
        var logo = root.GetProperty("Логотип");
        Assert.Equal(Png, logo.GetProperty("src").GetString());
        Assert.Equal("4cm", logo.GetProperty("width").GetString());
        Assert.Equal("center", logo.GetProperty("align").GetString());
        Assert.False(logo.TryGetProperty("height", out _)); // пустые опции не пишем
        Assert.Equal("ООО", root.GetProperty("Имя").GetString());
    }

    [Fact]
    public void MigrateDataJson_MigratesNestedObjectFields()
    {
        var json = "{\"Орг\":{\"Фото\":\"" + Png + "\",\"Имя\":\"X\"}}";
        var result = ImageSizeToInstanceFixup.MigrateDataJson(json, Opts);
        Assert.NotNull(result);
        var foto = JsonDocument.Parse(result!).RootElement.GetProperty("Орг").GetProperty("Фото");
        Assert.Equal("3cm", foto.GetProperty("height").GetString());
        Assert.Equal("contain", foto.GetProperty("fit").GetString());
    }

    [Fact]
    public void MigrateDataJson_ArrayElementImages_HaveNoKey_NotSized_ButUnchangedStaysBare()
    {
        // Элемент массива под ключом "Фото" внутри объекта строки — ключ есть → мигрируется.
        var json = $$"""{"Материалы":[{"Фото":"{{Png}}"}]}""";
        var result = ImageSizeToInstanceFixup.MigrateDataJson(json, Opts);
        Assert.NotNull(result);
        var foto = JsonDocument.Parse(result!).RootElement.GetProperty("Материалы")[0].GetProperty("Фото");
        Assert.Equal(Png, foto.GetProperty("src").GetString());
        Assert.Equal("3cm", foto.GetProperty("height").GetString());
    }

    [Fact]
    public void MigrateDataJson_UnknownKey_LeavesBareString()
    {
        var json = $$"""{"Прочее":"{{Png}}"}""";
        // "Прочее" нет в карте → не мигрируем → изменений нет.
        Assert.Null(ImageSizeToInstanceFixup.MigrateDataJson(json, Opts));
    }

    [Fact]
    public void MigrateDataJson_AlreadyObjectValue_Idempotent()
    {
        var json = "{\"Логотип\":{\"src\":\"" + Png + "\",\"width\":\"4cm\"}}";
        // Уже объект-значение → не трогаем, повторная миграция ничего не меняет.
        Assert.Null(ImageSizeToInstanceFixup.MigrateDataJson(json, Opts));
    }

    [Fact]
    public void StripImageFromSchema_RemovesImageBlocks()
    {
        var schema = """
        {"fields":[
          {"key":"Логотип","type":"image","image":{"width":"4cm"}},
          {"key":"Имя","type":"string"}
        ]}
        """;
        var result = ImageSizeToInstanceFixup.StripImageFromSchema(schema);
        Assert.NotNull(result);
        var fields = JsonDocument.Parse(result!).RootElement.GetProperty("fields");
        Assert.False(fields[0].TryGetProperty("image", out _));
        Assert.Equal("image", fields[0].GetProperty("type").GetString()); // тип поля не трогаем
    }

    [Fact]
    public void StripImageFromSchema_NoImage_ReturnsNull()
    {
        Assert.Null(ImageSizeToInstanceFixup.StripImageFromSchema("""{"fields":[{"key":"Имя","type":"string"}]}"""));
        Assert.Null(ImageSizeToInstanceFixup.StripImageFromSchema("""{"noFields":true}"""));
    }
}
