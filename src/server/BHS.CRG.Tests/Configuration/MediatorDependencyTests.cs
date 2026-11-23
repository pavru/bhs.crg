using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using Mediator;

namespace BHS.CRG.Tests.Configuration;

/// <summary>Не допускает возврата MediatR напрямую или через транзитивную зависимость (#1406).</summary>
public class MediatorDependencyTests
{
    [Fact]
    public void Product_does_not_reference_MediatR()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "BHS.CRG.slnx")))
            root = root.Parent;
        Assert.NotNull(root);
        var references = Directory.EnumerateFiles(root.FullName, "*.csproj", SearchOption.AllDirectories)
            .SelectMany(path => XDocument.Load(path).Descendants("PackageReference"))
            .Select(element => (string?)element.Attribute("Include") ?? (string?)element.Attribute("Update"));
        Assert.DoesNotContain(references, name => name is "MediatR" or "MediatR.Contracts");

        foreach (var path in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.deps.json"))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var libraries = document.RootElement.GetProperty("libraries").EnumerateObject();
            Assert.DoesNotContain(libraries, item => item.Name.StartsWith("MediatR/", StringComparison.OrdinalIgnoreCase) ||
                item.Name.StartsWith("MediatR.Contracts/", StringComparison.OrdinalIgnoreCase));
        }
        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), assembly =>
            assembly.GetName().Name is "MediatR" or "MediatR.Contracts");
        Assert.Equal("Mediator", typeof(IMediator).Namespace);
    }
}
