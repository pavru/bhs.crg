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
            JsonDocument.Parse(schema.Replace('\'', '"')), TypeOwner.Core, TypeVisibility.Shared);

    private static JsonDocument Schema(string json) => JsonDocument.Parse(json.Replace('\'', '"'));

    /// <summary>Реестр экземпляра без модулей — тэги ядра; их ограничений эти проверки и касаются.</summary>
    private static readonly TagCatalog Catalog = TagCatalog.Build(TagRegistry.Core, []);

    [Fact]
    public void SecondBearer_IsBlocked_AndListsExisting()
    {
        var a = Type("Профиль-А", "{'tags':['profile.construction'],'fields':[]}");
        var incoming = Schema("{'tags':['profile.construction'],'fields':[]}");

        var v = TagRestrictionValidator.Validate(Catalog, incoming, Guid.Empty, "Новый", [a]);

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
        var v = TagRestrictionValidator.Validate(Catalog, incoming, a.Id, "Профиль-А", [a]);

        Assert.Empty(v);
    }

    [Fact]
    public void InheritedTag_IsNotCounted_AsBearer()
    {
        var parent = Type("Профиль-Родитель", "{'tags':['profile.construction'],'fields':[]}");
        var child = Type("Дочерний", "{'fields':[]}", parentId: parent.Id); // наследует тэг, но НЕ несёт own

        // Новый тип с тэгом: носители — только parent (own), child (inherited) НЕ считается → total 2 > 1.
        var incoming = Schema("{'tags':['profile.construction'],'fields':[]}");
        var v = TagRestrictionValidator.Validate(Catalog, incoming, Guid.Empty, "Новый", [parent, child]);

        Assert.Single(v);
        var msg = v[0].Describe();
        Assert.Contains("Профиль-Родитель", msg);
        Assert.DoesNotContain("Дочерний", msg); // унаследованный не носитель
    }

    [Fact]
    public void SingleBearer_NoViolation()
    {
        var incoming = Schema("{'tags':['profile.construction'],'fields':[]}");
        var v = TagRestrictionValidator.Validate(Catalog, incoming, Guid.Empty, "Единственный", []);
        Assert.Empty(v);
    }

    [Fact]
    public void UnrestrictedTags_NeverViolate()
    {
        // Тэг БЕЗ Restriction — сколько угодно носителей во всей системе.
        //
        // ⚠️ Раньше здесь стоял `type.qualityDocument`, и тест стал ХОЛОСТЫМ, когда тэг уехал к
        // модулю исполнительной документации: каталог теста собран без модулей, тэга в нём нет, и
        // цикл по реестру до проверки просто не доходил — «пусто» получалось само собой (поймано
        // ревью PR #1012). Поэтому тэг берётся ИЗ САМОГО каталога, а не по памяти, и отбирается по
        // признаку, ради которого тест написан.
        var unrestricted = Catalog.All.First(t => t.Scope == TagScope.Type && t.Restriction is null);
        var a = Type("К1", $"{{'tags':['{unrestricted.Code}'],'fields':[]}}");
        var b = Type("К2", $"{{'tags':['{unrestricted.Code}'],'fields':[]}}");
        var incoming = Schema($"{{'tags':['{unrestricted.Code}'],'fields':[]}}");
        var v = TagRestrictionValidator.Validate(Catalog, incoming, Guid.Empty, "К3", [a, b]);
        Assert.Empty(v);
    }
}
