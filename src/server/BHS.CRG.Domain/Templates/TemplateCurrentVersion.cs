namespace BHS.CRG.Domain.Templates;

/// <summary>Признаки версии шаблона, по которым выбирается текущая (<see cref="TemplateCurrentVersion" />).</summary>
public sealed record TemplateVersionFacts(
    Guid Id, bool IsActive, bool IsDefault, int Version, DateTimeOffset UpdatedAt);

/// <summary>
/// Текущая версия шаблона — та, на которую ведут «Недавние» (shell-09, Р-5 — Б): по правилу авто-выбора
/// страницы шаблонов, но детерминированно. Действующая по умолчанию → действующая → любая; среди равных —
/// больший номер, затем поздняя правка, затем идентификатор.
///
/// <para><b>Зачем порядок среди равных</b>, если с #1920 новая версия гасит все прежние действующие.
/// Уникального индекса нет — гонка двух форков (#1922) и данные, накопленные до #1921, дают две
/// действующие, и тогда каждое чтение обязано вести на одну и ту же из них.</para>
/// </summary>
public static class TemplateCurrentVersion
{
    /// <summary>Текущая из версий одного шаблона; пустой список — <c>null</c>.</summary>
    public static TemplateVersionFacts? Of(IEnumerable<TemplateVersionFacts> versions) =>
        versions
            .OrderByDescending(v => v.IsActive && v.IsDefault)
            .ThenByDescending(v => v.IsActive)
            .ThenByDescending(v => v.Version)
            .ThenByDescending(v => v.UpdatedAt)
            .ThenByDescending(v => v.Id)
            .FirstOrDefault();

    /// <summary>Признаки версии — для выбора среди загруженных строк.</summary>
    public static TemplateVersionFacts Facts(this Template t) =>
        new(t.Id, t.IsActive, t.IsDefault, t.Version, t.UpdatedAt);
}
