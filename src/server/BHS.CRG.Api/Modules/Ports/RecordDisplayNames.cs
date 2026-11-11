using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Modules.Ports;
using MediatR;
using CoreRecordsFor = BHS.CRG.Application.Documents.RecordsFor;

namespace BHS.CRG.Api.Modules.Ports;

/// <summary>
/// Запись справочника для показа — названием из поля с тэгом названия (<see cref="TagDefinition.Names" />),
/// если оно заполнено: организация — сокращённым названием (ТЗ CORE-6, CORE-20.4, issue #1473).
///
/// <para><b>Одна дверь для всех модулей.</b> Правило живёт в порте ядра, а не в местах модуля: счёт
/// показывает организацию в пятнадцати местах, и каждое новое (а с ним модули <c>plan</c> и
/// <c>work</c>) забывало бы его. Порт называет запись так в КАЖДОМ чтении — список, выбор, ссылки,
/// значения поля, карточка, заведение из файла, — а полное название кладёт рядом
/// (<see cref="ModuleCatalogRef.FullName" />) для мест, где похожие надо различать.</para>
///
/// <para><b>Сокращённое находится только по тэгу</b> <c>org.shortName</c> — на поле справочника или на
/// части его составного поля названия (<see cref="RoleResolution.Field" />, тот же расчёт, что у
/// экрана сопоставления). Тэга нет или поле не годится — организация называется названием записи, как
/// прежде: угадывать по названиям полей нельзя (TYPE-2).</para>
///
/// <para>Значение читается с наследованием <c>_baseRef</c> — тем же запросом, что ИНН для
/// сопоставления: запись-роль, хранящая только ссылку на основу, называется сокращённым названием
/// основы. Неразрешимая основа — не ошибка показа: запись называется своим названием.</para>
/// </summary>
internal sealed class RecordDisplayNames
{
    private static readonly RecordDisplayNames None = new(new Dictionary<Guid, string>());

    private readonly IReadOnlyDictionary<Guid, string> _short;

    private RecordDisplayNames(IReadOnlyDictionary<Guid, string> shortNames) => _short = shortNames;

    /// <summary>
    /// Названия для показа записей справочника роли. У роли нет тэга названия, роль не обеспечена или
    /// тэг не сопоставлен — пусто, и записи называются как прежде.
    /// </summary>
    /// <param name="ids">Только эти записи — у тех, кому нужно назвать одну-две стоящие ссылки (ревью
    /// PR #1898): иначе каждое открытие счёта читало бы поле у всего справочника. <c>null</c> — все.</param>
    public static async Task<RecordDisplayNames> LoadAsync(
        IMediator mediator, TagCatalog tags, ResolvedRole role, IReadOnlyList<DocumentType> all, CancellationToken ct,
        IReadOnlyCollection<Guid>? ids = null)
    {
        if (role.Root is null || ids is { Count: 0 }
            || tags.All.FirstOrDefault(t => t.Names && t.Role == role.Role) is not { } tag
            || RoleResolution.Field(role, tag, all) is not { Key: { } key } field)
            return None;

        var values = await mediator.Send(
            new CommonDataFieldValuesQuery([.. role.Family.Keys], key, CoreRecordsFor.Display, field.Part?.Key, ids), ct);
        return new(values
            .Where(v => !string.IsNullOrWhiteSpace(v.Value))
            .ToDictionary(v => v.Record.Id, v => v.Value!.Trim()));
    }

    /// <summary>
    /// Справочник с тэгом названия, в семью которого входит тип <paramref name="typeId" />, — для
    /// чтения записи по идентификатору, где роль не названа; <c>null</c> — такого нет.
    /// </summary>
    public static ResolvedRole? DirectoryOf(TagCatalog tags, Guid typeId, IReadOnlyList<DocumentType> all) =>
        tags.All.Where(t => t.Names && t.Role is not null)
            .Select(t => RoleResolution.Role(t.Role!, all))
            .FirstOrDefault(role => role.Family.ContainsKey(typeId));

    /// <summary>Записи, чьё название для показа содержит набранное, — без учёта регистра.</summary>
    public IEnumerable<Guid> Matching(string needle) =>
        _short.Where(n => n.Value.Contains(needle, StringComparison.CurrentCultureIgnoreCase)).Select(n => n.Key);

    /// <summary>Ссылка — сокращённым названием, если оно есть и отличается от названия записи.</summary>
    public ModuleCatalogRef Apply(ModuleCatalogRef record) =>
        Shown(record.Id, record.DisplayName) is { } shortName
            ? record with { DisplayName = shortName, FullName = record.DisplayName }
            : record;

    /// <summary>Запись — так же, как ссылка.</summary>
    public ModuleCatalogEntry Apply(ModuleCatalogEntry entry) =>
        Shown(entry.Id, entry.DisplayName) is { } shortName
            ? entry with { DisplayName = shortName, FullName = entry.DisplayName }
            : entry;

    /// <summary>Название для показа, когда оно другое, чем у записи; иначе <c>null</c>. Одно правило на оба вида.</summary>
    private string? Shown(Guid id, string? recordName) =>
        _short.TryGetValue(id, out var shortName) && shortName != recordName ? shortName : null;
}
