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

    private static Source Part(string path, string body, string ns = "Stand.Tables", string project = "Stand") =>
        new(project, path, $"namespace {ns};\n\n{body}\n");

    private static Source Declaring(string type = "Rows") =>
        Part("Stand/Rows.cs", $"public sealed partial class {type}(IModuleCatalog catalog)\n{{\n}}");

    private static Source Calling(string declaration, string ns = "Stand.Tables", string project = "Stand") =>
        Part("Stand/Rows.Prepare.cs", $"{declaration}\n{{\n    async Task PrepareAsync()\n    {{\n        {Read}\n    }}\n}}", ns, project);

    [Fact]
    public void Вызов_порта_виден_из_части_класса_где_порт_не_объявлен()
    {
        var found = FindReads([Declaring(), Calling("public sealed partial class Rows")]);

        Assert.Equal([$"Stand/Rows.Prepare.cs|{Read}"], found.Keys);
    }

    /// <summary>
    /// Имя несёт только общий тип. Иначе «catalog» из одного класса открывал бы вызовы порта ДРУГОГО
    /// типа под тем же именем во всём проекте — и предыдущий тест проходил бы по той же причине,
    /// ничего не говоря о частях класса.
    /// </summary>
    [Theory]
    [InlineData("public sealed partial class Other", "Stand.Tables", "Stand")]
    [InlineData("public sealed class Rows", "Stand.Tables", "Stand")]
    [InlineData("public sealed partial class Rows", "Stand.Other", "Stand")]
    [InlineData("public sealed partial class Rows", "Stand.Tables", "Neighbour")]
    public void Одно_имя_в_чужом_типе_чтением_не_считается(string declaration, string ns, string project)
    {
        var found = FindReads([Declaring(), Calling(declaration, ns, project)]);

        Assert.Empty(found);
    }

    [Fact]
    public void Часть_записи_и_структуры_связана_так_же()
    {
        foreach (var kind in new[] { "record", "record struct", "struct" })
        {
            var found = FindReads([
                Part("Stand/Rows.cs", $"public partial {kind} Rows(IModuleCatalog catalog);"),
                Calling($"public partial {kind} Rows"),
            ]);

            Assert.True(found.ContainsKey($"Stand/Rows.Prepare.cs|{Read}"), kind);
        }
    }
}
