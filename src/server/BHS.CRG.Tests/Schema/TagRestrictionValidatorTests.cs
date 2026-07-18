using System.Text.Json;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Schema;

namespace BHS.CRG.Tests.Schema;

public class TagRestrictionValidatorTests
{
    private const string Tag = FunctionalTag.ProfileConstruction; // Type-scope, MaxBearers=1

    private static DocumentType Type(string name, string schema, Guid? parentId = null) =>
        DocumentType.Create(name, name, DocumentTypeKind.Composite, parentId,
            JsonDocument.Parse(schema.Replace('\'', '"')));

    private static JsonDocument Schema(string json) => JsonDocument.Parse(json.Replace('\'', '"'));

    [Fact]
    public void SecondBearer_IsBlocked_AndListsExisting()
    {
        var a = Type("Профиль-А", "{'tags':['profile.construction'],'fields':[]}");
        var incoming = Schema("{'tags':['profile.construction'],'fields':[]}");

        var v = TagRestrictionValidator.Validate(incoming, Guid.Empty, "Новый", [a]);

        Assert.Single(v);
        Assert.Equal(1, v[0].MaxBearers);
        Assert.Contains("Профиль-А", v[0].Describe());
    }

    [Fact]
    public void ReSavingTheOnlyBearer_IsAllowed()
    {
        var a = Type("Профиль-А", "{'tags':['profile.construction'],'fields':[]}");
        var incoming = Schema("{'tags':['profile.construction'],'fields':[]}");

        // Сохраняем сам тип A (savingId = a.Id) — не считаем против себя.
        var v = TagRestrictionValidator.Validate(incoming, a.Id, "Профиль-А", [a]);

        Assert.Empty(v);
    }

    [Fact]
    public void InheritedTag_IsNotCounted_AsBearer()
    {
        var parent = Type("Профиль-Родитель", "{'tags':['profile.construction'],'fields':[]}");
        var child = Type("Дочерний", "{'fields':[]}", parentId: parent.Id); // наследует тэг, но НЕ несёт own

        // Новый тип с тэгом: носители — только parent (own), child (inherited) НЕ считается → total 2 > 1.
        var incoming = Schema("{'tags':['profile.construction'],'fields':[]}");
        var v = TagRestrictionValidator.Validate(incoming, Guid.Empty, "Новый", [parent, child]);

        Assert.Single(v);
        var msg = v[0].Describe();
        Assert.Contains("Профиль-Родитель", msg);
        Assert.DoesNotContain("Дочерний", msg); // унаследованный не носитель
    }

    [Fact]
    public void SingleBearer_NoViolation()
    {
        var incoming = Schema("{'tags':['profile.construction'],'fields':[]}");
        var v = TagRestrictionValidator.Validate(incoming, Guid.Empty, "Единственный", []);
        Assert.Empty(v);
    }

    [Fact]
    public void UnrestrictedTags_NeverViolate()
    {
        // type.qualityDocument без Restriction — сколько угодно носителей.
        var a = Type("К1", "{'tags':['type.qualityDocument'],'fields':[]}");
        var b = Type("К2", "{'tags':['type.qualityDocument'],'fields':[]}");
        var incoming = Schema("{'tags':['type.qualityDocument'],'fields':[]}");
        var v = TagRestrictionValidator.Validate(incoming, Guid.Empty, "К3", [a, b]);
        Assert.Empty(v);
    }
}
