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
    public void VersionsWithoutTheSource_Refused()
        => Assert.Throws<ArgumentException>(() => V(1, true).CreateNewVersion("x", [V(2, true)]));

    [Fact]
    public void VersionOfAnotherTemplate_Refused()
    {
        var source = V(1, true);
        Assert.Throws<ArgumentException>(() => source.CreateNewVersion("x", [source, V(1, true, name: "Другой")]));
    }
}
