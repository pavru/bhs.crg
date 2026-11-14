namespace BHS.CRG.Domain.Templates;

/// <summary>
/// Поля версии шаблона, по которым версии упорядочиваются (порядок запасного выбора, [ID-3]). Общий
/// у версии с текстом (<see cref="Template"/>) и без него (сводка списка, issue #1972) — одно правило
/// порядка на оба вида.
/// </summary>
public interface ITemplateVersionKey
{
    Guid Id { get; }
    Guid DocumentTypeId { get; }
    string Name { get; }
    int Version { get; }
    DateTimeOffset CreatedAt { get; }
}
