using System.Text.Json;
using BHS.CRG.Modules.Costs.Endpoints;
using Microsoft.AspNetCore.Http;

namespace BHS.CRG.Tests.Costs;

/// <summary>
/// Отпечаток файла — общий набор примеров с браузером (COST-6.7).
///
/// <para>Отпечаток считается в двух местах: сервером при приёме (<see cref="InvoiceSameFiles.FingerprintAsync" />,
/// он ложится в счёт) и браузером до выгрузки — чтобы спросить, нет ли уже счёта с этим файлом. Разойдись
/// они, вопрос отвечал бы «двойников нет» про файл, у которого они есть, и никто бы не заметил. Поэтому
/// примеры одни, в файле рядом с клиентской утилитой, и сверяют их оба теста: отставший краснеет.</para>
/// </summary>
public class FileFingerprintCasesTests
{
    private static readonly string CasesFile =
        RepoPaths.File("src", "client", "src", "shared", "utils", "fileFingerprintCases.json");

    public static TheoryData<string> Names() => [.. Cases().Select(c => c.GetProperty("name").GetString()!)];

    [Theory]
    [MemberData(nameof(Names))]
    public async Task Сервер_считает_отпечаток_как_набор(string name)
    {
        var c = Cases().Single(x => x.GetProperty("name").GetString() == name);
        var content = Content(c);
        using var stream = new MemoryStream(content);
        var file = new FormFile(stream, 0, content.Length, "file", "пример.bin");

        Assert.Equal(c.GetProperty("sha256").GetString(), await InvoiceSameFiles.FingerprintAsync(file, CancellationToken.None));
    }

    /// <summary>Набор обязан держать границы блока SHA-256 и большой файл — иначе он сверяет только лёгкое.</summary>
    [Fact]
    public void Набор_покрывает_границы_блока_и_большой_файл()
    {
        var lengths = Cases().Where(c => c.TryGetProperty("length", out _))
            .Select(c => c.GetProperty("length").GetInt32()).ToHashSet();

        Assert.Superset(new HashSet<int> { 0, 1, 55, 56, 63, 64, 65 }, lengths);
        Assert.Contains(lengths, l => l > 4 * 1024 * 1024);
    }

    /// <summary>Байт i — (i × 31 + 7) mod 256; так же их строит клиентский тест (см. <c>_generator</c> в файле).</summary>
    private static byte[] Content(JsonElement c)
    {
        if (c.TryGetProperty("hex", out var hex)) return Convert.FromHexString(hex.GetString()!);

        var bytes = new byte[c.GetProperty("length").GetInt32()];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = unchecked((byte)(i * 31 + 7));
        return bytes;
    }

    private static IEnumerable<JsonElement> Cases()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(CasesFile));
        return [.. document.RootElement.GetProperty("cases").EnumerateArray().Select(c => c.Clone())];
    }
}
