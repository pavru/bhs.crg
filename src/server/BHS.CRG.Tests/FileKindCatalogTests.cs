using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using BHS.CRG.Infrastructure.Recognition;
using BHS.CRG.Modules.Files;
using BHS.CRG.Tests.Common;

namespace BHS.CRG.Tests;

/// <summary>
/// Реестр видов файлов (issue #1266) — один источник для ядра, модуля счетов и экрана.
///
/// <para>Два сторожа задачи. (а) Всё, что реестр называет читаемым «как есть», принимает КАЖДЫЙ
/// движок распознавания: иначе вид был бы читаемым на одном экземпляре и отказом на другом. (б) В
/// модулях и на экране счетов нет своих перечней типов: перечень, записанный рядом, разойдётся
/// с реестром на первой же правке, и молча.</para>
/// </summary>
public class FileKindCatalogTests
{
    private static readonly Type[] Engines =
    [
        .. typeof(IRecognizerEngine).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(IRecognizerEngine).IsAssignableFrom(type)),
    ];

    /// <summary>
    /// Движки берутся перебором сборки, а не списком: новый движок попадает под сторожа сам. Ответ
    /// спрашивается у объекта без конструктора — он не зависит ни от настроек, ни от сети.
    /// </summary>
    [Fact]
    public void Каждый_движок_принимает_всё_что_реестр_читает_как_есть()
    {
        Assert.True(Engines.Length >= 3, "Движков распознавания найдено меньше трёх — перебор сборки сломан.");
        Assert.NotEmpty(FileKindCatalog.Recognized);

        var refused =
            from engine in Engines
            let probe = (IRecognizerEngine)RuntimeHelpers.GetUninitializedObject(engine)
            from kind in FileKindCatalog.Recognized
            where !probe.Accepts(kind.Mime)
            select $"{engine.Name} не принимает {kind.Label} ({kind.Mime})";

        Assert.Empty(refused);
    }

    /// <summary>И обратное: сторож выше не был бы сторожем, если бы движки соглашались на что угодно.</summary>
    [Fact]
    public void Движок_отказывается_от_вида_которого_не_читает()
    {
        foreach (var engine in Engines)
        {
            var probe = (IRecognizerEngine)RuntimeHelpers.GetUninitializedObject(engine);
            Assert.False(probe.Accepts(FileKinds.Xlsx), $"{engine.Name} согласился на таблицу Excel");
            Assert.False(probe.Accepts(FileKinds.Unknown), $"{engine.Name} согласился на файл неизвестного вида");
        }
    }

    /// <summary>
    /// Реестр и определение вида по содержимому знают одни и те же виды. Вид, который определяется,
    /// но в реестре не описан, отдавался бы с типом, о котором экран ничего не знает.
    /// </summary>
    [Fact]
    public void В_реестре_описан_каждый_вид_который_сервер_умеет_определить()
    {
        var detected = typeof(FileKinds).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field is { IsLiteral: true } && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .Where(mime => mime != FileKinds.Unknown);

        Assert.Equal(detected.Order(), FileKindCatalog.All.Select(kind => kind.Mime).Order());
        Assert.Null(FileKindCatalog.Find(FileKinds.Unknown));
    }

    [Fact]
    public void Расширения_записаны_с_точкой_строчными_и_не_повторяются()
    {
        var extensions = FileKindCatalog.All.SelectMany(kind => kind.Extensions).ToList();

        Assert.All(extensions, extension => Assert.Matches("^\\.[a-z0-9]+$", extension));
        Assert.Equal(extensions.Count, extensions.Distinct().Count());
        Assert.All(FileKindCatalog.All, kind => Assert.NotEmpty(kind.Extensions));
    }

    /// <summary>
    /// Читаемый образ ещё не строится (issue #1268–#1270): офисный файл реестр знает, но читаемым
    /// не называет. Тест снимается вместе с этим ограничением.
    /// </summary>
    [Fact]
    public void Офисный_файл_известен_но_пока_не_распознаётся()
    {
        foreach (var mime in new[] { FileKinds.Xlsx, FileKinds.Xls, FileKinds.Docx })
        {
            Assert.Equal(FileReading.Rendition, FileKindCatalog.Find(mime)!.Reading);
            Assert.False(FileKindCatalog.IsRecognized(mime));
        }
    }

    [Fact]
    public void Перечень_словами_собирается_из_названий_без_повторов()
    {
        Assert.Equal("PDF, PNG и JPEG", FileKindCatalog.Words(FileKindCatalog.Recognized));
        Assert.Equal("PDF, PNG или JPEG", FileKindCatalog.Words(FileKindCatalog.Recognized, "или"));
        Assert.Equal("Excel и Word", FileKindCatalog.Words(
            FileKindCatalog.All.Where(kind => kind.Reading == FileReading.Rendition)));
        Assert.Equal("PDF", FileKindCatalog.Words([FileKindCatalog.Find(FileKinds.Pdf)!]));
        Assert.Equal("", FileKindCatalog.Words([]));
    }

    [Theory]
    [InlineData("IMAGE/PNG", true)]
    [InlineData(" application/pdf ", true)]
    [InlineData("image/webp", false)]
    [InlineData("text/html", false)]
    [InlineData(null, false)]
    public void Читаемость_спрашивается_по_типу_без_учёта_регистра(string? mime, bool recognized)
        => Assert.Equal(recognized, FileKindCatalog.IsRecognized(mime));

    // ── (б) своих перечней типов нет ───────────────────────────────────────────────────────

    /// <summary>Тип в кавычках: «application/pdf», 'image/png', `text/html`.</summary>
    private static readonly Regex MimeLiteral = new(
        """["'`](application|image|text|audio|video)/[A-Za-z0-9.+*-]+["'`]""", RegexOptions.Compiled);

    /// <summary>Файл → почему тип в нём записан литералом. Пусто: исключений нет.</summary>
    private static readonly Dictionary<string, string> Deliberate = new();

    private static IEnumerable<string> Guarded()
    {
        // Все проекты модулей, а не один по имени: следующий модуль попадает под сторожа сам.
        foreach (var project in SolutionModules.Names)
            foreach (var file in SourceTree.Files(project))
                yield return file;

        var screen = Path.GetFullPath(Path.Combine(SourceTree.SolutionDir, "..", "client", "src", "features", "costs"));
        Assert.True(Directory.Exists(screen), $"Не найден каталог экрана модуля счетов: {screen}");
        foreach (var file in Directory.EnumerateFiles(screen, "*.ts*", SearchOption.AllDirectories))
            // Тесты называют типы нарочно: ими они проверяют, что экран спрашивает реестр.
            if (!file.Contains(".test.")) yield return file;
    }

    [Fact]
    public void В_модулях_и_на_экране_счетов_нет_литералов_типа_файла()
    {
        var found = new List<string>();
        var scanned = 0;
        foreach (var file in Guarded())
        {
            scanned++;
            var relative = SourceTree.Relative(file);
            if (Deliberate.ContainsKey(relative)) continue;
            var text = File.ReadAllText(file);
            foreach (Match match in MimeLiteral.Matches(text))
                found.Add($"{relative}:{SourceTree.LineOf(text, match.Index)}  {match.Value}");
        }

        Assert.True(scanned > 50, $"Просмотрено файлов: {scanned} — обход исходников сломан.");
        Assert.True(found.Count == 0,
            "Тип файла записан литералом — это свой перечень рядом с реестром (FileKindCatalog):\n" +
            string.Join("\n", found) + "\n\n" +
            "На сервере спросите реестр (FileKinds / FileKindCatalog), на экране — useFileKinds. " +
            "Если литерал нужен — впишите файл в Deliberate с причиной.");
    }

    [Fact]
    public void В_исключениях_нет_устаревших_записей()
    {
        var guarded = Guarded().Select(SourceTree.Relative).ToHashSet();
        var stale = Deliberate.Keys
            .Where(file => !guarded.Contains(file)
                || !MimeLiteral.IsMatch(File.ReadAllText(Path.Combine(SourceTree.SolutionDir, file))))
            .ToList();

        Assert.True(stale.Count == 0, "В исключениях файлы, где литералов типа больше нет: " + string.Join(", ", stale));
    }
}
