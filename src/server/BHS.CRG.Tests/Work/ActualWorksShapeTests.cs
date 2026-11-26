using System.Reflection;
using BHS.CRG.Modules.Exchange;

namespace BHS.CRG.Tests.Work;

public class ActualWorksShapeTests
{
    private static readonly IReadOnlyDictionary<string, Type> SafeProperties = new Dictionary<string, Type>
    {
        ["Items"] = typeof(IReadOnlyList<ActualWork>),
        ["Diagnostics"] = typeof(ActualWorksDiagnostics),
        ["PlanItemId"] = typeof(Guid),
        ["ConstructionId"] = typeof(Guid),
        ["SectionId"] = typeof(Guid?),
        ["Date"] = typeof(DateOnly),
        ["From"] = typeof(DateOnly?),
        ["To"] = typeof(DateOnly?),
        ["Shift"] = typeof(string),
        ["Volume"] = typeof(decimal),
        ["Scope"] = typeof(ActualWorksScope),
        ["AwaitingAcceptance"] = typeof(int),
        ["ExcludedCrewGroups"] = typeof(int),
        ["CrewExclusions"] = typeof(IReadOnlyList<CrewFactExclusionCount>),
        ["Reason"] = typeof(CrewFactExclusion),
        ["Groups"] = typeof(int),
        ["WithoutPlanItem"] = typeof(int),
        ["Unsubmitted"] = typeof(int),
    };

    [Fact]
    public void ActualWorks_answer_only_contains_safe_properties_at_every_level() =>
        Assert.Empty(Violations(typeof(ActualWorksAnswer), "Answer"));

    [Theory]
    [InlineData(typeof(HoursLeak), "Hours")]
    [InlineData(typeof(EmployeeLeak), "EmployeeId")]
    [InlineData(typeof(AuthorLeak), "Author")]
    [InlineData(typeof(AccountLeak), "AccountId")]
    [InlineData(typeof(ReportLeak), "ReportId")]
    [InlineData(typeof(ParticipantLeak), "Unsubmitted")]
    public void ActualWorks_shape_finds_nested_forbidden_fields(Type leak, string field)
    {
        var type = typeof(IReadOnlyList<>).MakeGenericType(leak);
        Assert.Contains("Answer[]." + field, Violations(type, "Answer"));
    }

    private static IEnumerable<string> Violations(Type type, string path)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.IsPrimitive || type.IsEnum || type == typeof(Guid) || type == typeof(DateOnly)
            || type == typeof(string) || type == typeof(decimal)) yield break;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
        {
            foreach (var nested in Violations(type.GetGenericArguments()[0], path + "[]")) yield return nested;
            yield break;
        }
        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            var member = path + "." + property.Name;
            if (!SafeProperties.TryGetValue(property.Name, out var allowed) || allowed != property.PropertyType)
                yield return member;
            else
                foreach (var nested in Violations(property.PropertyType, member)) yield return nested;
        }
    }

    public sealed record HoursLeak(decimal Hours);
    public sealed record EmployeeLeak(Guid EmployeeId);
    public sealed record AuthorLeak(string Author);
    public sealed record AccountLeak(Guid AccountId);
    public sealed record ReportLeak(Guid ReportId);
    public sealed record ParticipantLeak(IReadOnlyList<Guid> Unsubmitted);
}
