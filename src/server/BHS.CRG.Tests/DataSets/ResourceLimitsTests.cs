using System.IO.Compression;
using System.Text;
using BHS.CRG.Application.Generation;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Infrastructure.DataSets;
using BHS.CRG.Infrastructure.Generation;
using BHS.CRG.Infrastructure.Scripting;
using Jint;
using Jint.Runtime;

namespace BHS.CRG.Tests.DataSets;

/// <summary>
/// Пределы на пользовательских входах (раздел 7 аудита). Все четыре входа доступны обычному
/// пользователю, и у каждого отказ должен быть ОТКАЗОМ, а не падением процесса: сервер один на всех,
/// и уронивший его уносит с собой чужие генерации.
/// </summary>
public class ResourceLimitsTests
{
    // ── Выражения (Jint) ────────────────────────────────────────────────────────

    /// <summary>
    /// Экспоненциальный рост строки. Таймаута мало: гигабайты выделяются ВНУТРИ отведённой секунды,
    /// а OutOfMemoryException процесс уже не переживёт.
    /// </summary>
    [Fact]
    public void Expression_GrowingStringHitsMemoryLimit_Throws()
    {
        var engine = JintSandbox.Create();

        // Тип ожидаем КОНКРЕТНЫЙ: на «любом исключении» тест прошёл бы и на прежнем конфиге, где
        // ограничения памяти не было вовсе — остановил бы таймаут, и проверка ничего не значила бы.
        // Строка удваивается, до предела в 16 МБ доходит за пару десятков шагов, то есть за
        // микросекунды: с секундным таймаутом это не гонка.
        Assert.Throws<MemoryLimitExceededException>(
            () => engine.Evaluate("var s='x'; while(true){ s = s + s; } s"));
    }

    /// <summary>
    /// Зацикливание без роста памяти ловится пределом числа инструкций — он срабатывает раньше
    /// таймаута и не зависит от того, насколько занята машина.
    /// </summary>
    [Fact]
    public void Expression_InfiniteLoop_HitsStatementLimit()
    {
        var engine = JintSandbox.Create();
        Assert.Throws<StatementsCountOverflowException>(
            () => engine.Evaluate("var i=0; while(true){ i++; } i"));
    }

    /// <summary>Обычное выражение пределами не задевается — иначе защита сломала бы работу.</summary>
    [Fact]
    public void Expression_OrdinaryOne_Evaluates()
    {
        var engine = JintSandbox.Create();
        engine.SetValue("get", new Func<string, string?>(_ => "17"));
        Assert.Equal(34d, engine.Evaluate("parseInt(get('кол-во')) * 2").AsNumber());
    }

    // ── Архивы ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Архив-бомба: несколько килобайт сжатых нулей разворачиваются в сотни мегабайт. Раньше запись
    /// читалась в память целиком, а буфер выделялся по ЗАЯВЛЕННОМУ в заголовке размеру.
    /// </summary>
    [Fact]
    public async Task Zip_OversizedEntry_IsRefusedNotRead()
    {
        var bomb = BuildZip(entries: [("bomb.csv", 300 * 1024 * 1024)]);
        var parser = new ZipDataSetParser(new EmptyServices());

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => parser.DetectSourcesAsync(bomb, CancellationToken.None));
        Assert.Contains("bomb.csv", ex.Message);
    }

    /// <summary>
    /// Пределы должны СКЛАДЫВАТЬСЯ: две тысячи записей по потолку каждая — это сотня гигабайт
    /// распаковки в одном запросе. Памяти это не съедает (запись за раз одна), но поток занят
    /// часами, а входной архив при хорошей сжимаемости весит единицы мегабайт.
    /// </summary>
    [Fact]
    public async Task Zip_TotalUnpackedSize_IsCapped()
    {
        // Двенадцать записей по 60 МБ: каждая по отдельности в потолок укладывается, вместе — нет.
        var zip = BuildZip([.. Enumerable.Range(0, 12).Select(i => ($"f{i}.csv", 60 * 1024 * 1024))]);
        var parser = new ZipDataSetParser(new CsvOnlyServices());

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => parser.DetectSourcesAsync(zip, CancellationToken.None));
        Assert.Contains("Суммарный размер", ex.Message);
    }

    [Fact]
    public async Task Zip_TooManyEntries_IsRefused()
    {
        var many = BuildZip([.. Enumerable.Range(0, 2_100).Select(i => ($"f{i}.csv", 1))]);
        var parser = new ZipDataSetParser(new EmptyServices());

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => parser.DetectSourcesAsync(many, CancellationToken.None));
        Assert.Contains("слишком много файлов", ex.Message);
    }

    /// <summary>Пустые нули жмутся почти в ничто — ровно то, на чём построена архив-бомба.</summary>
    private static byte[] BuildZip(IReadOnlyList<(string Name, int Size)> entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var chunk = new byte[1024 * 1024];
            foreach (var (name, size) in entries)
            {
                using var s = zip.CreateEntry(name).Open();
                for (var written = 0; written < size; written += chunk.Length)
                    s.Write(chunk, 0, Math.Min(chunk.Length, size - written));
            }
        }
        return ms.ToArray();
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    /// <summary>
    /// Провайдер с настоящей фабрикой парсеров — нужен там, где отказ ожидается НЕ на первой записи:
    /// до неё архив должен успеть разобрать предыдущие.
    /// </summary>
    private sealed class CsvOnlyServices : IServiceProvider
    {
        private readonly DataSetParserFactory _factory = new([new CsvDataSetParser()]);
        public object? GetService(Type serviceType) =>
            serviceType == typeof(DataSetParserFactory) ? _factory : null;
    }

    // ── Вычисляемые колонки ─────────────────────────────────────────────────────

    /// <summary>
    /// Пределы песочницы действуют на ОДНО вычисление, а строк бывают сотни тысяч. Выражение с
    /// бесконечным циклом должно бросаться целиком после нескольких упираний в предел, а не
    /// упираться на каждой строке заново: отказ проглатывается, и такой источник жёг бы процессор
    /// часами молча.
    /// </summary>
    [Fact]
    public void ComputedColumn_LoopingExpression_IsAbandonedAfterFewRows()
    {
        var rows = Enumerable.Range(0, 400)
            .Select(i => (IReadOnlyDictionary<string, string?>)new Dictionary<string, string?> { ["n"] = i.ToString() })
            .ToList();

        var started = DateTime.UtcNow;
        var result = DataSetComputedColumnExecutor.Apply("[{\"alias\":\"x\",\"expr\":\"var i=0; while(true){i++;} i\"}]", rows);
        var elapsed = DateTime.UtcNow - started;

        Assert.Equal(400, result.Count);
        Assert.All(result, r => Assert.Null(r["x"]));
        // Четыреста строк по пределу инструкций заняли бы десятки секунд; пять — доли секунды.
        Assert.True(elapsed < TimeSpan.FromSeconds(10), $"колонка считалась {elapsed.TotalSeconds:0.0} с — похоже, не брошена");
    }

    /// <summary>
    /// А вот выражение, честно падающее на отдельных строках с негодными данными, бросать нельзя:
    /// это обычное дело, и остальные строки считаются как считались.
    /// </summary>
    [Fact]
    public void ComputedColumn_FailingOnSomeRows_KeepsComputingOthers()
    {
        var rows = Enumerable.Range(0, 50)
            .Select(i => (IReadOnlyDictionary<string, string?>)new Dictionary<string, string?>
            {
                ["v"] = i % 2 == 0 ? null : i.ToString(),
            })
            .ToList();

        // На чётных строках get('v') вернёт пустую строку → обращение к .нет.нет бросит TypeError.
        var result = DataSetComputedColumnExecutor.Apply(
            "[{\"alias\":\"x\",\"expr\":\"get('v').length > 0 ? get('v') : null.boom\"}]", rows);

        Assert.Equal(50, result.Count);
        Assert.Contains(result, r => r["x"] is not null);   // нечётные посчитались
    }

    // ── XML ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// «Billion laughs»: файл в пару килобайт разворачивается в гигабайты. Умолчание фреймворка
    /// такое НЕ закрывало — оно закрывало только внешние сущности.
    /// </summary>
    [Fact]
    public async Task Xml_EntityExpansionBomb_IsRefused()
    {
        var xml = new StringBuilder()
            .Append("<?xml version=\"1.0\"?><!DOCTYPE r [")
            .Append("<!ENTITY a \"aaaaaaaaaa\">")
            .Append("<!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;\">")
            .Append("<!ENTITY c \"&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;\">")
            .Append("<!ENTITY d \"&c;&c;&c;&c;&c;&c;&c;&c;&c;&c;\">")
            .Append("<!ENTITY e \"&d;&d;&d;&d;&d;&d;&d;&d;&d;&d;\">")
            .Append("<!ENTITY f \"&e;&e;&e;&e;&e;&e;&e;&e;&e;&e;\">")   // 10 млн символов из ~400 байт
            .Append("]><r><row><v>&f;</v></row></r>")
            .ToString();

        var parser = new XmlDataSetParser();
        // Отказ любой — лишь бы это был отказ разбора, а не съеденная память.
        await Assert.ThrowsAnyAsync<Exception>(
            () => parser.ParseAsync(Encoding.UTF8.GetBytes(xml), "/r/row", null, CancellationToken.None));
    }

    /// <summary>
    /// Файл, где DOCTYPE просто ОБЪЯВЛЕН и ничего не разворачивает, разбираться обязан: такие
    /// выгрузки встречаются, и отказывать в них было бы платой без выгоды.
    /// </summary>
    [Fact]
    public async Task Xml_HarmlessDoctype_IsStillParsed()
    {
        const string xml = "<?xml version=\"1.0\"?><!DOCTYPE r SYSTEM \"r.dtd\"><r><row><v>7</v></row></r>";
        var parser = new XmlDataSetParser();

        var result = await parser.ParseAsync(Encoding.UTF8.GetBytes(xml), "/r/row", null, CancellationToken.None);

        Assert.Single(result.Rows);
        Assert.Equal("7", result.Rows[0]["v"]);
    }

    /// <summary>
    /// Файл, который сущность объявляет И ИСПОЛЬЗУЕТ, но безобидного размера, разбираться обязан.
    ///
    /// Это ровно та граница, на которой запрет DTD целиком (Prohibit) и пропуск объявлений (Ignore)
    /// одинаково отказывают законному файлу: при Ignore он падает с «ссылка на необъявленную
    /// сущность». Потолок на объём раскрытия таких файлов не трогает.
    /// </summary>
    [Fact]
    public async Task Xml_SmallInternalEntity_IsStillParsed()
    {
        const string xml = "<?xml version=\"1.0\"?><!DOCTYPE r [<!ENTITY org \"ООО Ромашка\">]>" +
                           "<r><row><v>&org;</v></row></r>";
        var parser = new XmlDataSetParser();

        var result = await parser.ParseAsync(Encoding.UTF8.GetBytes(xml), "/r/row", null, CancellationToken.None);

        Assert.Single(result.Rows);
        Assert.Equal("ООО Ромашка", result.Rows[0]["v"]);
    }

    // ── Процесс Typst ───────────────────────────────────────────────────────────

    /// <summary>
    /// Срок на процесс: раньше ожидание шло без него, и шаблон с бесконечным циклом занимал ядро
    /// навсегда. Берём заведомо долгую команду вместо самого Typst — проверяется обвязка, а не он.
    /// </summary>
    [Fact]
    public async Task TypstProcess_ExceedingDeadline_IsKilled()
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "sh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // На Windows именно ping, а не timeout: у timeout при перенаправленном stdin ввод не
        // поддерживается, команда падает мгновенно — и «уложился в срок» получалось бы даром.
        foreach (var a in OperatingSystem.IsWindows()
                     ? new[] { "/c", "ping -n 31 127.0.0.1" }
                     : ["-c", "sleep 30"])
            psi.ArgumentList.Add(a);

        var started = DateTime.UtcNow;
        await Assert.ThrowsAsync<TypstTimeoutException>(
            () => TypstProcess.RunAsync(psi, CancellationToken.None, TimeSpan.FromSeconds(2)));

        // Именно остановлен, а не «дождались своей смерти»: иначе прошло бы тридцать секунд.
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(15));
    }

    /// <summary>Уложившийся процесс отдаёт код возврата и stderr как обычно.</summary>
    [Fact]
    public async Task TypstProcess_FinishingInTime_ReturnsExitCode()
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "sh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in OperatingSystem.IsWindows() ? new[] { "/c", "exit 3" } : ["-c", "exit 3"])
            psi.ArgumentList.Add(a);

        var (exitCode, _) = await TypstProcess.RunAsync(psi, CancellationToken.None, TimeSpan.FromSeconds(30));
        Assert.Equal(3, exitCode);
    }
}
