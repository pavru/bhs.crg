using System.Text.Json;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Tests.Schema;

/// <summary>Чтение справочников ядра по смыслу (<see cref="CoreDirectories" />): тэг и ссылка.</summary>
public class CoreDirectoriesTests
{
    private static DocumentType Type(string schemaJson, Guid? parentId = null) =>
        DocumentType.Create("T", "C", DocumentTypeKind.Composite, parentId, JsonDocument.Parse(schemaJson),
            TypeOwner.Core, TypeVisibility.Shared);

    /// <summary>
    /// Поле, которое подтип исключил, у подтипа не находится, хотя тэг у предка стоит: в форме его нет,
    /// и порт, читавший его, отдавал бы значение, которого человек не видит и не правит.
    /// </summary>
    [Fact]
    public void TaggedKey_не_находит_поле_исключённое_подтипом()
    {
        var parent = Type("""{"fields":[{"key":"Табельный","tags":["employee.personnelNumber"]}]}""");
        var child = Type("""{"fields":[],"excludedFields":["Табельный"]}""", parent.Id);

        Assert.Equal("Табельный", CoreDirectories.TaggedKey(parent, [parent, child], "employee.personnelNumber"));
        Assert.Null(CoreDirectories.TaggedKey(child, [parent, child], "employee.personnelNumber"));
    }

    [Fact]
    public void Reference_читает_только_ссылку_на_запись()
    {
        var id = Guid.NewGuid();
        var data = JsonDocument.Parse(
            "{\"ед\":{\"$ref\":\"catalog\",\"entryId\":\"" + id + "\"},\"вписано\":{\"Наименование\":\"м\"}," +
            "\"док\":{\"$ref\":\"document\",\"entryId\":\"" + id + "\"}}").RootElement;

        Assert.Equal(id, CoreDirectories.Reference(data, "ед"));
        Assert.Null(CoreDirectories.Reference(data, "вписано"));
        Assert.Null(CoreDirectories.Reference(data, "док"));
        Assert.Null(CoreDirectories.Reference(data, "нет"));
    }
}
