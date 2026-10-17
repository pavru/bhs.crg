using System.Text.Json;
using BHS.CRG.Infrastructure.Storage;

namespace BHS.CRG.Tests.Storage;

/// <summary>
/// Правило «точный путь» — одно на копию ядра и копию модулей (issue #1422). Прежде оно жило в
/// <c>ModuleFileScan</c>, а сбор строк реестра «Документы» проверял значения широкой формой
/// (<see cref="BlobPathShape.Pattern" />), то есть «путём» становился и текст перед датой.
/// </summary>
public class BlobPathShapeInspectTests
{
    private const string Path = "bhs-crg/2026.10.17/3f2504e0-4f89-11d3-9a0c-0305e82c3301_Счёт 31.pdf";
    private const string Key = "2026.10.17/3f2504e0-4f89-11d3-9a0c-0305e82c3301_";

    [Fact]
    public void Путь_целиком_точный_и_даёт_свой_ключ()
    {
        var hit = BlobPathShape.Inspect(Path);

        Assert.NotNull(hit);
        Assert.True(hit.Exact);
        Assert.Equal([Key], hit.Keys);
        Assert.True(BlobPathShape.IsExactPath(Path));
    }

    /// <summary>Слова перед путём, JSON строкой, два пути через разделитель — ключи есть, точного пути нет.</summary>
    [Theory]
    [InlineData("Лист 3 см. " + Path)]
    [InlineData("""{"Groups":[{"BlobPath":"bhs-crg/2026.10.17/3f2504e0-4f89-11d3-9a0c-0305e82c3301_Счёт 31.pdf"}]}""")]
    [InlineData(Path + ";" + Path)]
    public void Путь_внутри_текста_не_точный(string text)
    {
        var hit = BlobPathShape.Inspect(text);

        Assert.NotNull(hit);
        Assert.False(hit.Exact);
        Assert.All(hit.Keys, k => Assert.Equal(Key, k));
        Assert.False(BlobPathShape.IsExactPath(text));
    }

    /// <summary>JSON, записанный текстом с «\/»: ключ найден и очищен от обратных черт.</summary>
    [Fact]
    public void Экранированный_слэш_ключу_не_мешает()
    {
        var hit = BlobPathShape.Inspect("""{"p":"bhs-crg\/2026.10.17\/3f2504e0-4f89-11d3-9a0c-0305e82c3301_a.pdf"}""");

        Assert.NotNull(hit);
        Assert.Equal([Key], hit.Keys);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Лист 3, шифр 01-ЭМ/2026.10.17")]
    [InlineData(@"C:\Проект\2026.10.17\3f2504e0-4f89-11d3-9a0c-0305e82c3301_a.pdf")]
    public void Без_ключа_пути_ничего(string text) => Assert.Null(BlobPathShape.Inspect(text));

    /// <summary>
    /// Картинка в base64 полна «/», но «_» в её алфавите нет: отбор по байтам отсекает её до разбора, и
    /// обход не разворачивает мегабайты в строки на каждой копии.
    /// </summary>
    [Fact]
    public void Base64_картинки_отсекается_до_разбора()
    {
        var bytes = new byte[64 * 1024];
        new Random(1422).NextBytes(bytes);
        var picture = Convert.ToBase64String(bytes);
        Assert.Contains('/', picture);

        var seen = new List<string>();
        BlobPathShape.VisitStrings(JsonSerializer.SerializeToElement(new { picture, path = Path }), seen.Add);

        Assert.Equal([Path], seen);
    }

    /// <summary>Обход JSON доходит до строк на любой глубине.</summary>
    [Fact]
    public void Обход_JSON_находит_строки_в_массивах_и_объектах()
    {
        var seen = new List<string>();
        BlobPathShape.VisitStrings(
            JsonSerializer.SerializeToElement(new { a = new[] { new { b = new { c = Path } } }, n = 5, s = "коротко" }),
            seen.Add);

        Assert.Equal([Path], seen);
    }
}
