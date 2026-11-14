using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Domain.Templates;

namespace BHS.CRG.Application.Generation;

/// <summary>
/// Шаблоны, которыми выпускается документ, — одно правило для генерации, предпросмотра и отладочного
/// пакета (issue #1401, change id-debug-bundle-template-choice, [ID-3]). Прежде правило было написано
/// трижды, копии разошлись, и пакет после восстановления копии отдавал не тот шаблон, которым
/// выпускает генерация.
/// <list type="number">
/// <item>Выбранный набор <c>TemplateIds</c> — его действующие версии <b>в порядке выбора</b> (D3).
/// Выбран, но ни один не действует — отказ (D5): молча взять другой шаблон значило бы показать
/// документ, которого генерация не выпустит.</item>
/// <item>Явный <c>TemplateId</c> — шаблон этого типа, в том числе историческая версия: так
/// закрепляют документ на версии (D4). Ссылка не на шаблон типа — как если бы её не было.</item>
/// <item>Шаблон по умолчанию (действующая версия).</item>
/// <item>Действующая версия самого раннего шаблона (D2).</item>
/// </list>
/// Порядок кандидатов из базы на ответ не влияет: «первый» определяет <see cref="Order"/>, а не
/// физический порядок строк, который восстановление копии меняет.
/// </summary>
public static class TemplateChoice
{
    /// <summary>Отказ «выбран, но не действует» — один у генерации, предпросмотра и пакета.</summary>
    public const string NoneSelectedActive = "Ни один из выбранных шаблонов не активен.";

    /// <summary>
    /// Шаблоны выпуска документа по порядку. Пусто — у типа нет ни одного действующего шаблона
    /// (что ответить, решает вызывающий: генерация отказывает, предпросмотр говорит «нет шаблона»).
    /// </summary>
    /// <param name="candidates">Шаблоны типа документа; чужие отбрасываются здесь же.</param>
    /// <exception cref="ConflictException">Набор выбран, но ни один из выбранных не действует.</exception>
    public static IReadOnlyList<Template> For(DomainObject document, IEnumerable<Template> candidates)
        => For(document.CompositeTypeId, document.TemplateId, document.TemplateIds, candidates);

    /// <inheritdoc cref="For(DomainObject, IEnumerable{Template})"/>
    public static IReadOnlyList<Template> For(
        Guid documentTypeId, Guid? templateId, string? templateIds, IEnumerable<Template> candidates)
    {
        var own = candidates.Where(t => t.DocumentTypeId == documentTypeId).ToList();

        var selected = TemplateRefJson.SelectionIds(templateIds);
        if (selected.Count > 0)
        {
            var byId = own.ToDictionary(t => t.Id);
            var chosen = selected
                .Select(id => byId.GetValueOrDefault(id))
                .OfType<Template>()
                .Where(t => t.IsActive)
                .ToList();
            if (chosen.Count == 0) throw new ConflictException(NoneSelectedActive);
            return chosen;
        }

        var pinned = templateId is { } pin ? own.FirstOrDefault(t => t.Id == pin) : null;
        var single = pinned ?? Fallback(own);
        return single is null ? [] : [single];
    }

    /// <summary>
    /// То же, что <see cref="For(DomainObject, IEnumerable{Template})"/>, но пустой ответ — отказ:
    /// выпускать нечем. Один текст у генерации и отладочного пакета.
    /// </summary>
    /// <exception cref="ConflictException">Набор выбран, но не действует, или действующих шаблонов нет.</exception>
    public static IReadOnlyList<Template> Require(DomainObject document, IEnumerable<Template> candidates)
    {
        var chosen = For(document, candidates);
        return chosen.Count > 0
            ? chosen
            : throw new ConflictException($"No active template for DocumentType {document.CompositeTypeId}");
    }

    /// <summary>
    /// Шаблон документа без выбора: по умолчанию (действующий), иначе действующая версия самого
    /// раннего шаблона (D2). Null — действующих нет. Тот же ответ даёт «действующий шаблон типа» (D7).
    /// Две действующие версии одного шаблона (гонка #1922, данные до #1921) — берётся новая, как у
    /// <see cref="TemplateCurrentVersion"/>.
    /// </summary>
    /// <param name="typeTemplates">Версии шаблонов одного типа; достаточно действующих, если дано
    /// <paramref name="firstCreated"/>.</param>
    /// <param name="firstCreated">Создание шаблона по названию — когда исторические версии не
    /// загружены (см. <see cref="Order"/>).</param>
    public static Template? Fallback(
        IEnumerable<Template> typeTemplates, IReadOnlyDictionary<string, DateTimeOffset>? firstCreated = null)
    {
        var active = Order(typeTemplates, firstCreated).Where(t => t.IsActive).ToList();
        return active.FirstOrDefault(t => t.IsDefault) ?? active.FirstOrDefault();
    }

    /// <summary>
    /// Шаблоны в порядке запасного выбора (D2, D6). Шаблон — все версии одного названия в типе; шаблоны
    /// идут по созданию первой из оставшихся версий, при равенстве — по названию, внутри шаблона — от
    /// новой версии к старой (затем <c>Id</c>). Тогда первая действующая в списке — та, что возьмёт
    /// документ без выбора, и клиенту хватает порядка.
    /// <para>По созданию шаблона, а не версии: новая версия моложе всех, и порядок по версиям
    /// перекидывал бы выбор на соседний шаблон при каждом сохранении раннего. Копия переносит
    /// <c>CreatedAt</c> каждой версии, поэтому после восстановления порядок тот же.</para>
    /// </summary>
    /// <param name="firstCreated">Наименьший <c>CreatedAt</c> версий по названию шаблона (один тип) —
    /// из запроса без текстов (<c>ITemplateNames.FirstCreatedAsync</c>). Без него — по загруженным
    /// версиям, которых тогда должно хватать: загружены все.</param>
    public static IReadOnlyList<Template> Order(
        IEnumerable<Template> templates, IReadOnlyDictionary<string, DateTimeOffset>? firstCreated = null)
        => templates
            .GroupBy(t => (t.DocumentTypeId, t.Name))
            .OrderBy(g => firstCreated is not null && firstCreated.TryGetValue(g.Key.Name, out var at)
                ? at
                : g.Min(t => t.CreatedAt))
            .ThenBy(g => g.Key.Name, StringComparer.Ordinal)
            .ThenBy(g => g.Key.DocumentTypeId)
            .SelectMany(g => g.OrderByDescending(t => t.Version).ThenByDescending(t => t.Id))
            .ToList();
}
