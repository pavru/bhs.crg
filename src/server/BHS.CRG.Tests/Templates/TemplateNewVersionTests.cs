using BHS.CRG.Domain.Templates;

namespace BHS.CRG.Tests.Templates;

/// <summary>
/// <see cref="Template.CreateNewVersion"/> считает по всем версиям шаблона (issue #1920). Здесь —
/// то, чего через обработчик не построить: данные, в которых ошибка уже успела оставить две
/// действующие версии и повторный номер.
/// </summary>
public class TemplateNewVersionTests
{
    private static readonly Guid TypeId = Guid.NewGuid();
    private static readonly DateTimeOffset At = DateTimeOffset.UtcNow.AddDays(-1);

    private static Template V(int version, bool active, bool isDefault = false, string name = "Шаблон")
        => Template.Restore(Guid.NewGuid(), TypeId, name, $"v{version}", version, active, isDefault, At, At);

    [Fact]
    public void AccumulatedSecondActiveVersion_IsDeactivatedToo()
    {
        // Наследство ошибки: от v2 при действующей v4 вышла вторая v3, и она тоже действующая.
        var versions = new[] { V(1, false), V(2, false), V(3, false), V(4, true, isDefault: true), V(3, true) };

        var next = versions[1].CreateNewVersion("от v2", versions);

        Assert.Equal(5, next.Version);
        Assert.True(next.IsActive);
        Assert.True(next.IsDefault);
        Assert.All(versions, v => Assert.False(v.IsActive));
        Assert.All(versions, v => Assert.False(v.IsDefault));
    }

    [Fact]
    public void ContentAndParameters_ComeFromTheSourceVersion()
    {
        var versions = new[] { V(1, false), V(2, true) };

        var next = versions[0].CreateNewVersion("правка v1", versions, "откат");

        Assert.Equal("правка v1", next.Content);
        Assert.Equal("откат", next.Comment);
        Assert.Equal("Шаблон", next.Name);
        Assert.Equal(TypeId, next.DocumentTypeId);
    }

    [Fact]
    public void HistoricalVersionNotDefault_IsNotTouched()
    {
        var versions = new[] { V(1, false), V(2, true) };
        var before = versions[0].UpdatedAt;

        versions[1].CreateNewVersion("v3", versions);

        Assert.Equal(before, versions[0].UpdatedAt);
    }

    [Fact]
    public void SourceMissingFromVersions_StillCountsAndIsDeactivated()
    {
        var source = V(3, true);
        var next = source.CreateNewVersion("x", [V(1, false)]);
        Assert.Equal(4, next.Version);
        Assert.False(source.IsActive);
    }

    [Fact]
    public void VersionOfAnotherTemplate_IsIgnored()
    {
        var source = V(1, true);
        var other = V(9, true, isDefault: true, name: "Другой");

        var next = source.CreateNewVersion("x", [source, other]);

        // Соседний шаблон не гасится, его номер и «по умолчанию» новой версии не достаются.
        Assert.Equal(2, next.Version);
        Assert.False(next.IsDefault);
        Assert.True(other.IsActive);
        Assert.True(other.IsDefault);
    }
}

/// <summary>
/// Переход на адресацию Код.Имя переписывает действующие версии новыми (issue #773). У шаблона с
/// двумя действующими версиями (наследство issue #1920) документы обеих обязаны попасть на версию,
/// которая осталась действующей, — иначе генерация откажет «ни один шаблон не активен».
/// </summary>
public class PrefixedAddressingForkTests
{
    private static readonly Guid TypeId = Guid.NewGuid();
    private static readonly DateTimeOffset At = DateTimeOffset.UtcNow.AddDays(-1);

    private static Template V(int version, bool active, bool isDefault = false, string name = "Шаблон")
        => Template.Restore(Guid.NewGuid(), TypeId, name, $"v{version}", version, active, isDefault, At, At);

    [Fact]
    public void TwoActiveVersions_BothRepinnedToTheSurvivingNewVersion()
    {
        var v4 = V(4, true, isDefault: true);
        var dup3 = V(3, true);
        var all = new[] { V(1, false), V(2, false), V(3, false), v4, dup3 };
        // Порядок словаря — главная первой: функция обязана сама обработать её последней.
        var content = new Dictionary<Guid, string> { [v4.Id] = "v4 новое", [dup3.Id] = "dup3 новое" };

        var (created, replacedBy) = BHS.CRG.Application.Generation.MigrateToPrefixedAddressingHandler
            .ForkVersions(all, content, "переход");

        var survivor = Assert.Single(created, v => v.IsActive);
        Assert.Equal("v4 новое", survivor.Content); // действующей осталась новая версия главной
        Assert.True(survivor.IsDefault);
        Assert.Equal(6, survivor.Version);           // v5 — от дубля, v6 — от главной, без повторов
        Assert.Equal(survivor.Id, replacedBy[v4.Id]);
        Assert.Equal(survivor.Id, replacedBy[dup3.Id]);
        Assert.All(all, v => Assert.False(v.IsActive));
    }

    [Fact]
    public void SeparateTemplates_EachKeepsItsOwnNewVersion()
    {
        var a = V(2, true, name: "Основной");
        var b = V(1, true, name: "Сокращённый");
        var (created, replacedBy) = BHS.CRG.Application.Generation.MigrateToPrefixedAddressingHandler
            .ForkVersions([a, b], new Dictionary<Guid, string> { [a.Id] = "a", [b.Id] = "b" }, "переход");

        Assert.Equal(2, created.Count(v => v.IsActive));
        Assert.Equal("a", created.Single(v => v.Id == replacedBy[a.Id]).Content);
        Assert.Equal("b", created.Single(v => v.Id == replacedBy[b.Id]).Content);
    }
}
