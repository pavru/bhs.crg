using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using BHS.CRG.Tests.Support;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Отладочный пакет воспроизводит вход генерации (ТЗ CORE-21, CORE-22; change
/// core-generation-input-one-path, D6). Сторож на настоящем Typst: документ выпускается, каталог,
/// который генерация отдала компилятору, снимается снаружи (<see cref="CompileDirectoryWatch" />), и
/// пакет того же экземпляра сравнивается с ним.
///
/// <para>Прежде пакет собирал вход своей копией кода, и за копией не смотрел ни один тест: на
/// репетиции выпуска 2.105.0 сравнение пакета с выпущенным PDF было главным доводом «данные не
/// поехали», а держалось на копии.</para>
/// </summary>
[Collection("Integration")]
public class GenerationDebugBundleParityTests(IntegrationTestFixture fixture)
{
    /// <summary>
    /// Пакет — тот же каталог: тот же набор файлов (README — только пакету), тот же разобранный
    /// <c>data.json</c>, и внешний Typst получает из него тот же документ — по тексту и числу листов.
    /// </summary>
    [Fact]
    public async Task Bundle_IsTheCompileDirectoryOfGeneration()
    {
        TypstRequired.Ensure();
        var host = await GenerationParityHost.ForAsync(fixture);
        host.Spy.Reset();
        var seed = await host.SeedAsync();

        var (response, dir) = await host.GenerateAsync(seed);
        await using var _ = dir;
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var issued = await host.DownloadPdfAsync(seed);

        var bundle = await host.BundleAsync(seed);

        Assert.Equal(dir.Paths.Order(StringComparer.Ordinal),
            bundle.Keys.Where(k => k != "README.txt").Order(StringComparer.Ordinal));
        Assert.Contains("fonts/font_0.ttf", dir.Paths);   // сид проверяет то, что обещает
        Assert.Contains("assets/logo.png", dir.Paths);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(dir.DataJson), JsonNode.Parse(bundle["data.json"])),
            "data.json пакета по содержанию не совпал с data.json генерации");

        var rebuilt = await GenerationParityHost.CompileBundleAsync(bundle);
        Assert.Equal(GenerationParityHost.PageTexts(issued), GenerationParityHost.PageTexts(rebuilt));

        // Документ выпускается — перечня причин отказа нет.
        Assert.DoesNotContain("откажет", GenerationParityHost.Readme(bundle) ?? "");
    }

    /// <summary>Ассет больше предела генерация не видит — значит, и в пакете его быть не должно.</summary>
    [Fact]
    public async Task OversizeAsset_IsAbsentFromBoth()
    {
        TypstRequired.Ensure();
        var host = await GenerationParityHost.ForAsync(fixture);
        host.Spy.Reset();
        var seed = await host.SeedAsync();
        host.Spy.Oversize[seed.Blobs.Spare] = true;
        try
        {
            var (response, dir) = await host.GenerateAsync(seed);
            await using var _ = dir;
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            Assert.DoesNotContain("assets/spare.png", dir.Paths);

            var bundle = await host.BundleAsync(seed);
            Assert.False(bundle.ContainsKey("assets/spare.png"),
                "Ассет больше предела (100 МБ) попал в пакет, хотя генерация его пропустила: внешний Typst " +
                "отладил бы не тот вход, что видит сервер.");
        }
        finally { host.Spy.Reset(); }
    }

    /// <summary>
    /// Шрифт объявлен, но хранилище его не отдало: генерация компилирует без <c>--font-path</c>, и
    /// пакет этого ключа не предлагает — иначе внешняя компиляция шла бы иначе, чем на сервере.
    /// </summary>
    [Fact]
    public async Task FontNotDownloaded_ReadmeDoesNotOfferFontPath()
    {
        TypstRequired.Ensure();
        var host = await GenerationParityHost.ForAsync(fixture);
        host.Spy.Reset();
        var seed = await host.SeedAsync();
        host.Spy.Refuse[seed.Blobs.Font] = true;
        try
        {
            var (response, dir) = await host.GenerateAsync(seed);
            await using var _ = dir;
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            Assert.DoesNotContain(dir.Paths, p => p.StartsWith("fonts/", StringComparison.Ordinal));

            var bundle = await host.BundleAsync(seed);
            Assert.False(GenerationParityHost.Readme(bundle)?.Contains("--font-path", StringComparison.Ordinal) == true,
                "README пакета предлагает --font-path, хотя шрифт не скачался и генерация компилирует без ключа:\n" +
                GenerationParityHost.Readme(bundle));
        }
        finally { host.Spy.Reset(); }
    }

    /// <summary>
    /// Запрос пакета отменён, пока скачиваются ассеты шаблона: сборка прерывается и за следующими
    /// файлами не идёт. Считаются ПОПЫТКИ чтения — отменённая, но продолжившаяся сборка до хранилища
    /// бы не дошла (её остановил бы реестр), а время и запросы к базе потратила бы.
    /// </summary>
    [Fact]
    public async Task CancelledBundle_StopsReadingFiles()
    {
        TypstRequired.Ensure();
        var host = await GenerationParityHost.ForAsync(fixture);
        host.Spy.Reset();
        var seed = await host.SeedAsync();
        using var cts = new CancellationTokenSource();
        var cancelledAt = -1;
        host.Spy.BeforeRead[seed.Blobs.Logo] = () =>
        {
            cancelledAt = host.Spy.Reads.Count;
            cts.Cancel();
        };
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.BundleAsync(seed, cts.Token));
            Assert.True(cancelledAt > 0, "Отмена не сработала: пакет не дошёл до ассета шаблона.");

            // Сервер дорабатывает запрос уже без клиента: даём ему время показать, пойдёт ли он дальше.
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline && host.Spy.Reads.Count == cancelledAt)
                await Task.Delay(50);

            var after = host.Spy.Reads.Skip(cancelledAt).ToList();
            Assert.True(after.Count == 0,
                "После отмены запроса пакет продолжил загрузку файлов: " + string.Join(", ", after));
        }
        finally { host.Spy.Reset(); }
    }

    /// <summary>
    /// Генерация откажет на незаполненном обязательном поле — пакет всё равно отдаётся (отлаживают
    /// как раз такие документы), а README называет код и путь поля.
    /// </summary>
    [Fact]
    public async Task MissingRequired_BundleNamesTheRefusal()
    {
        var host = await GenerationParityHost.ForAsync(fixture);
        host.Spy.Reset();
        var seed = await host.SeedAsync(fillRequired: false);

        var generated = await host.GenerateWithoutWatchAsync(seed);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, generated.StatusCode);
        Assert.Contains("missing-required", await generated.Content.ReadAsStringAsync());

        var readme = GenerationParityHost.Readme(await host.BundleAsync(seed)) ?? "";
        Assert.True(readme.Contains("missing-required", StringComparison.Ordinal) && readme.Contains("Номер", StringComparison.Ordinal),
            "README пакета не называет причину, по которой генерация откажет (missing-required, поле «Номер»):\n" + readme);
    }

    /// <summary>
    /// Картинку поля хранилище не отдало — генерация откажет, назвав поле (CORE-37.2); пакет
    /// отдаётся и называет то же поле.
    /// </summary>
    [Fact]
    public async Task UnreadableImage_BundleNamesTheField()
    {
        var host = await GenerationParityHost.ForAsync(fixture);
        host.Spy.Reset();
        var seed = await host.SeedAsync();
        host.Spy.Refuse[seed.Blobs.Signature] = true;
        try
        {
            var generated = await host.GenerateWithoutWatchAsync(seed);
            Assert.False(generated.IsSuccessStatusCode, "Генерация выпустила документ с непрочитанной картинкой");
            Assert.Contains("Подпись", await generated.Content.ReadAsStringAsync());

            var readme = GenerationParityHost.Readme(await host.BundleAsync(seed)) ?? "";
            Assert.True(readme.Contains("Подпись", StringComparison.Ordinal),
                "README пакета не называет поле с непрочитанной картинкой, на котором генерация откажет:\n" + readme);
        }
        finally { host.Spy.Reset(); }
    }
}
