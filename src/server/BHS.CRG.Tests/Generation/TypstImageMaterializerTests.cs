using System.Text.Json;
using BHS.CRG.Application.Schema;
using BHS.CRG.Infrastructure.Generation;

namespace BHS.CRG.Tests.Generation;

public class TypstImageMaterializerTests
{
    // 1×1 прозрачный PNG
    private const string Png =
        "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    private static JsonElement El(object v) => JsonSerializer.SerializeToElement(v);

    [Fact]
    public void Materialize_WritesFiles_AndReplacesWithPaths_IncludingNested()
    {
        var dir = Path.Combine(Path.GetTempPath(), "matz-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var data = new Dictionary<string, object?>
            {
                ["Логотип"] = El(Png),
                ["Орг"] = El(new { СканПечати = Png, Имя = "ООО" }),
                ["Материалы"] = El(new[] { new { Фото = Png }, new { Фото = "нет" } }),
                ["Текст"] = El("обычная строка"),
            };

            var opts = new Dictionary<string, ImageRenderOptions>
            {
                ["Логотип"] = new("4cm", null, "center", null),
            };
            var json = TypstImageMaterializer.Materialize(data, dir, imageOptions: opts);

            // записаны три файла-изображения
            var files = Directory.GetFiles(Path.Combine(dir, "assets"));
            Assert.Equal(3, files.Length);
            Assert.All(files, f => Assert.EndsWith(".png", f));

            // в JSON больше нет data-URI; изображение — объект {src, width, ...}
            Assert.DoesNotContain("data:image", json);
            var root = JsonDocument.Parse(json).RootElement;

            var logo = root.GetProperty("Логотип");
            Assert.StartsWith("assets/img_", logo.GetProperty("src").GetString());
            Assert.Equal("4cm", logo.GetProperty("width").GetString());
            Assert.Equal("center", logo.GetProperty("align").GetString());
            Assert.Equal(JsonValueKind.Null, logo.GetProperty("height").ValueKind);

            Assert.StartsWith("assets/img_", root.GetProperty("Орг").GetProperty("СканПечати").GetProperty("src").GetString());
            Assert.StartsWith("assets/img_", root.GetProperty("Материалы")[0].GetProperty("Фото").GetProperty("src").GetString());
            // не-картиночные строки не тронуты
            Assert.Equal("ООО", root.GetProperty("Орг").GetProperty("Имя").GetString());
            Assert.Equal("нет", root.GetProperty("Материалы")[1].GetProperty("Фото").GetString());
            Assert.Equal("обычная строка", root.GetProperty("Текст").GetString());
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Materialize_NoImages_NoAssetsDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "matz-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var json = TypstImageMaterializer.Materialize(
                new Dictionary<string, object?> { ["A"] = El("x"), ["N"] = El(5) }, dir);
            Assert.False(Directory.Exists(Path.Combine(dir, "assets")));
            Assert.Contains("\"A\"", json);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
