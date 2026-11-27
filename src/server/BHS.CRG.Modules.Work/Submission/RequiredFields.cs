using System.Text.Json;
using System.Text.Json.Serialization;
using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Data;

namespace BHS.CRG.Modules.Work.Submission;

/// <summary>Незаполненное обязательное поле строки: номер строки с единицы, её идентификатор и код поля.</summary>
/// <param name="Field"><c>location</c> — место работ, <c>materials</c> — материалы.</param>
public sealed record MissingField(int Line, Guid LineId, string Field)
{
    public const string Location = "location";
    public const string Materials = "materials";

    /// <summary>Поле словами — для текста отказа.</summary>
    [JsonIgnore]
    public string Title => Field == Location ? "место работ" : "материалы";
}

[Flags]
public enum RequiredGaps { None = 0, Location = 1, Materials = 2 }

/// <summary>
/// Ручная подача без обязательных полей (ТЗ WORK-6): 422 с перечнем строк и полей. Черновик остаётся черновиком —
/// незаполненное сохраняется, отказывает только переход «подан».
/// </summary>
public sealed class RequiredMissingException(IReadOnlyList<MissingField> missing) : DomainException(
    "Отчёт не подан — не заполнено: " +
    string.Join("; ", missing.Select(m => $"строка {m.Line}, {m.Title}")) +
    ". Заполните и подайте снова: черновик сохранён.")
{
    public IReadOnlyList<MissingField> Missing => missing;
}

/// <summary>
/// Обязательность места и материалов по признакам вида работы (ТЗ WORK-6; change <c>work-15-submission</c>) —
/// проверяется на переходе «подан», а не при сохранении: черновик с телефона приходит частями.
/// Признаки — у вида работы в порту <see cref="IModuleWorkTypes" /> (<c>work.requiresLocation</c>,
/// <c>work.requiresMaterials</c>).
/// </summary>
public sealed class RequiredFields(IModuleWorkTypes workTypes)
{
    /// <summary>
    /// Что не заполнено, по порядку строк. Справочник видов работ не заведён — <c>null</c>: требований узнать не у
    /// кого (ручная подача отвечает на это отказом, автоподача подаёт без перечня).
    /// </summary>
    public async Task<IReadOnlyList<MissingField>?> OfAsync(Report report, CancellationToken ct)
    {
        var lines = report.Lines.OrderBy(l => l.Position).ToList();
        if (lines.Count == 0) return [];
        var known = await workTypes.GetAsync([.. lines.Select(l => l.WorkTypeId).Distinct()], ct);
        if (known is null) return null;
        var types = known.ToDictionary(type => type.Id);
        var missing = new List<MissingField>();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var gaps = Check(types.GetValueOrDefault(line.WorkTypeId), line.Location, line.Materials.Count > 0);
            missing.AddRange(ForLine(gaps, i + 1, line.Id));
        }
        return missing;
    }

    public static RequiredGaps Check(ModuleWorkType? type, string? location, bool hasMaterials)
    {
        var gaps = RequiredGaps.None;
        if (type?.RequiresLocation == true && string.IsNullOrWhiteSpace(location)) gaps |= RequiredGaps.Location;
        if (type?.RequiresMaterials == true && !hasMaterials) gaps |= RequiredGaps.Materials;
        return gaps;
    }

    public static IEnumerable<MissingField> ForLine(RequiredGaps gaps, int number, Guid id)
    {
        if (gaps.HasFlag(RequiredGaps.Location)) yield return new(number, id, MissingField.Location);
        if (gaps.HasFlag(RequiredGaps.Materials)) yield return new(number, id, MissingField.Materials);
    }

    /// <summary>Перечень колонкой <c>required_missing</c>: пустой — <c>null</c>.</summary>
    public static string? Json(IReadOnlyList<MissingField>? missing) =>
        missing is not { Count: > 0 }
            ? null
            : JsonSerializer.Serialize(missing.Select(m => new { line = m.Line, lineId = m.LineId, field = m.Field }));
}
