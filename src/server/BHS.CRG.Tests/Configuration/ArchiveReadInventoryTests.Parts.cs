namespace BHS.CRG.Tests.Configuration;

/// <content>
/// Зрение самой переписи: вызов порта из части partial-типа, где порт не объявлен. Проверяется на
/// выдуманных исходниках — настоящий код такого места может и не содержать, а зрение обязано быть.
/// Настоящее место тоже есть: названия организаций в <c>InvoiceTableRows.Prepare.cs</c> — порт там
/// объявлен в <c>InvoiceTable.cs</c>, и строка переписи держится только на этой связи.
/// </content>
public partial class ArchiveReadInventoryTests
{
    private const string Read = "var names = await catalog.ListAsync(type, RecordsFor.Display, ct);";
    private const string Calling = "Stand/Rows.Prepare.cs";

    private static Source Src(string path, string text, string project = "Stand") => new(project, path, text + "\n");

    private static string In(string ns, string body) => $"namespace {ns};\n\n{body}";

    private static string Body(string line) => $"\n{{\n    async Task PrepareAsync()\n    {{\n        {line}\n    }}\n}}";

    private static Source Declares(string declaration = "public sealed partial class Rows(IModuleCatalog catalog)\n{\n}") =>
        Src("Stand/Rows.cs", In("Stand.Tables", declaration));

    private static Source Calls(string declaration, string ns = "Stand.Tables", string project = "Stand", string line = Read) =>
        Src(Calling, In(ns, declaration + Body(line)), project);

    [Fact]
    public void Вызов_порта_виден_из_части_класса_где_порт_не_объявлен()
    {
        var found = FindReads([Declares(), Calls("public sealed partial class Rows")]);

        Assert.Equal([$"{Calling}|{Read}"], found.Keys);
    }

    /// <summary>Репозиторий объектов узнаётся своим объявлением — и связь частей у него своя.</summary>
    [Fact]
    public void Вызов_репозитория_объектов_виден_из_соседней_части_так_же()
    {
        const string line = "var entry = await objects.FindAsync(id, ct);";

        var found = FindReads([
            Declares("public sealed partial class Rows(IDomainObjectRepository objects)\n{\n}"),
            Calls("public sealed partial class Rows", line: line),
        ]);

        Assert.Equal([$"{Calling}|{line}"], found.Keys);
    }

    [Theory]
    [InlineData("record")]
    [InlineData("record class")]
    [InlineData("record struct")]
    [InlineData("struct")]
    public void Часть_записи_и_структуры_связана_так_же(string kind)
    {
        // У записи без тела объявление кончается «;» — порт стоит до неё.
        var found = FindReads([Declares($"public partial {kind} Rows(IModuleCatalog catalog);"), Calls($"public partial {kind} Rows")]);

        Assert.Equal([$"{Calling}|{Read}"], found.Keys);
    }

    /// <summary>
    /// Имя несёт только общий тип. Иначе «catalog» из одного класса открывал бы вызовы порта ДРУГОГО
    /// типа под тем же именем во всём проекте — и первый тест проходил бы по той же причине, ничего не
    /// говоря о частях класса.
    /// </summary>
    [Theory]
    [InlineData("public sealed partial class Other", "Stand.Tables", "Stand")]
    [InlineData("public sealed class Rows", "Stand.Tables", "Stand")]
    [InlineData("public sealed partial class Rows<T>", "Stand.Tables", "Stand")]
    [InlineData("public sealed partial class Rows", "Stand.Other", "Stand")]
    [InlineData("public sealed partial class Rows", "Stand.Tables", "Neighbour")]
    public void Одно_имя_в_чужом_типе_чтением_не_считается(string declaration, string ns, string project)
    {
        var found = FindReads([Declares(), Calls(declaration, ns, project)]);

        Assert.Empty(found);
    }

    /// <summary>
    /// Порт постороннего класса, лежащего в одном файле с частью типа, остальным частям не уходит:
    /// в <c>InvoiceTable.cs</c> классов несколько, и имя любого из них иначе разошлось бы по всем
    /// частям соседа.
    /// </summary>
    [Theory]
    [InlineData("public sealed partial class Rows(IClock clock)\n{\n}\n\npublic sealed class Other(IModuleCatalog catalog)\n{\n}")]
    [InlineData("public sealed class Other(IModuleCatalog catalog)\n{\n}\n\npublic sealed partial class Rows(IClock clock)\n{\n}")]
    [InlineData("public partial record Rows(IClock Clock);\n\npublic sealed class Other(IModuleCatalog catalog)\n{\n}")]
    public void Порт_соседнего_класса_того_же_файла_частям_не_передаётся(string declarations)
    {
        var found = FindReads([Declares(declarations), Calls("public sealed partial class Rows")]);

        Assert.Empty(found);
    }

    [Fact]
    public void Тип_названный_в_комментарии_файл_частью_не_делает()
    {
        var found = FindReads([
            Declares(),
            Calls("/// <summary>Не часть: см. partial class Rows.</summary>\npublic sealed class Helper"),
        ]);

        Assert.Empty(found);
    }

    /// <summary>Пространство имён части — ближайшее выше неё, а не первое в файле.</summary>
    [Fact]
    public void Часть_во_втором_пространстве_имён_файла_связана_со_своим_типом()
    {
        var declaring = Src("Stand/Rows.cs",
            "namespace Stand.Other\n{\n    public sealed class Unrelated\n    {\n    }\n}\n\n" +
            "namespace Stand.Tables\n{\n    public sealed partial class Rows(IModuleCatalog catalog)\n    {\n    }\n}");

        var found = FindReads([declaring, Calls("public sealed partial class Rows")]);

        Assert.Equal([$"{Calling}|{Read}"], found.Keys);
    }
}
