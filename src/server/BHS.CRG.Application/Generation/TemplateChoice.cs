using BHS.CRG.Application.Common;
using BHS.CRG.Application.Templates;
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
    /// <param name="firstCreated">Создание шаблона по названию (см. <see cref="Order"/>). Обязателен,
    /// когда загружены не все версии и дело дойдёт до запасного выбора; null — загружены все.</param>
    /// <exception cref="ConflictException">Набор выбран, но ни один из выбранных не действует.</exception>
    public static IReadOnlyList<Template> For(DomainObject document, IEnumerable<Template> candidates,
        IReadOnlyDictionary<string, DateTimeOffset>? firstCreated)
        => For(document.CompositeTypeId, document.TemplateId, document.TemplateIds, candidates, firstCreated);

    /// <inheritdoc cref="For(DomainObject, IEnumerable{Template}, IReadOnlyDictionary{string, DateTimeOffset}?)"/>
    public static IReadOnlyList<Template> For(
        Guid documentTypeId, Guid? templateId, string? templateIds, IEnumerable<Template> candidates,
        IReadOnlyDictionary<string, DateTimeOffset>? firstCreated)
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
        var single = pinned ?? Fallback(own, firstCreated);
        return single is null ? [] : [single];
    }

    /// <summary>
    /// То же, что <see cref="For(DomainObject, IEnumerable{Template}, IReadOnlyDictionary{string, DateTimeOffset}?)"/>,
    /// но пустой ответ — отказ:
    /// выпускать нечем. Один текст у генерации и отладочного пакета.
    /// </summary>
    /// <exception cref="ConflictException">Набор выбран, но не действует, или действующих шаблонов нет.</exception>
    public static IReadOnlyList<Template> Require(DomainObject document, IEnumerable<Template> candidates,
        IReadOnlyDictionary<string, DateTimeOffset>? firstCreated)
    {
        var chosen = For(document, candidates, firstCreated);
        return chosen.Count > 0
            ? chosen
            : throw new ConflictException($"No active template for DocumentType {document.CompositeTypeId}");
    }

    /// <summary>
    /// Шаблоны выпуска документа, прочитанные из базы ровно в нужном объёме (issue #1969): действующие
    /// версии типа и та, за которой документ закреплён (<c>TemplateId</c> бывает исторической
    /// версией). Возраст шаблонов спрашивается, только когда дело дошло до запасного выбора, и без
    /// текстов. Прежде генерация, предпросмотр и отладочный пакет тянули Typst каждой сохранённой
    /// версии, а сборка комплекта — на каждый документ.
    /// </summary>
    /// <exception cref="ConflictException">Набор выбран, но ни один из выбранных не действует.</exception>
    public static async Task<IReadOnlyList<Template>> ForAsync(DomainObject document,
        IRepository<Template> templates, ITemplateNames names, CancellationToken ct)
    {
        var (candidates, firstCreated) = await CandidatesAsync(document, templates, names, ct);
        return For(document, candidates, firstCreated);
    }

    /// <summary>То же, что <see cref="ForAsync"/>, но пустой ответ — отказ (см. <see cref="Require"/>).</summary>
    /// <exception cref="ConflictException">Набор выбран, но не действует, или действующих шаблонов нет.</exception>
    public static async Task<IReadOnlyList<Template>> RequireAsync(DomainObject document,
        IRepository<Template> templates, ITemplateNames names, CancellationToken ct)
    {
        var (candidates, firstCreated) = await CandidatesAsync(document, templates, names, ct);
        return Require(document, candidates, firstCreated);
    }

    /// <summary>
    /// Шаблон документов типа без выбора (<see cref="Fallback"/>), прочитанный так же, как у генерации:
    /// действующие версии и возраст шаблонов без текстов. Одно место у «действующего шаблона типа»
    /// (D7) и сброса документов без выбора — иначе правило «что читать» разошлось бы между ними.
    /// </summary>
    public static async Task<Template?> FallbackAsync(Guid documentTypeId,
        IRepository<Template> templates, ITemplateNames names, CancellationToken ct)
        => Fallback(
            await templates.FindAsync(t => t.DocumentTypeId == documentTypeId && t.IsActive, ct),
            await names.FirstCreatedAsync(documentTypeId, ct));

    /// <summary>
    /// Кандидаты выбора: выбранный набор берёт только действующие версии, запасной выбор — тоже,
    /// историческая нужна одна — закреплённая. Остальные версии влияют на ответ лишь возрастом
    /// шаблона, а он нужен одному запасному выбору: при выборе или найденном закреплении его не
    /// спрашиваем — предпросмотр зовут на каждую правку формы.
    /// </summary>
    private static async Task<(IReadOnlyList<Template> Candidates, IReadOnlyDictionary<string, DateTimeOffset>? FirstCreated)>
        CandidatesAsync(DomainObject document, IRepository<Template> templates, ITemplateNames names, CancellationToken ct)
    {
        var type = document.CompositeTypeId;
        var pin = document.TemplateId;
        var candidates = await templates.FindAsync(
            t => t.DocumentTypeId == type && (t.IsActive || t.Id == pin), ct);
        var fallback = TemplateRefJson.SelectionIds(document.TemplateIds).Count == 0
            && !candidates.Any(t => t.Id == pin);
        return (candidates, fallback ? await names.FirstCreatedAsync(type, ct) : null);
    }

    /// <summary>
    /// Шаблон документа без выбора: по умолчанию (действующий), иначе действующая версия самого
    /// раннего шаблона (D2). Null — действующих нет. Тот же ответ даёт «действующий шаблон типа» (D7).
    /// Две действующие версии одного шаблона (гонка #1922, данные до #1921) — берётся новая, как у
    /// <see cref="TemplateCurrentVersion"/>.
    /// </summary>
    /// <param name="typeTemplates">Версии шаблонов одного типа; достаточно действующих, если дано
    /// <paramref name="firstCreated"/>.</param>
    /// <param name="firstCreated">Создание шаблона по названию — обязателен, когда исторические
    /// версии не загружены (см. <see cref="Order"/>); null — загружены все.</param>
    public static Template? Fallback(
        IEnumerable<Template> typeTemplates, IReadOnlyDictionary<string, DateTimeOffset>? firstCreated)
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
        IEnumerable<Template> templates, IReadOnlyDictionary<string, DateTimeOffset>? firstCreated)
        => Order(templates, t => new(t.Id, t.DocumentTypeId, t.Name, t.Version, t.CreatedAt), firstCreated);

    /// <summary>Тот же порядок для версий без текстов (список страницы шаблонов, issue #1972) — все
    /// версии типа, поэтому возраст шаблона берётся из них самих.</summary>
    public static IReadOnlyList<TemplateSummary> Order(IEnumerable<TemplateSummary> templates)
        => Order(templates, t => new(t.Id, t.DocumentTypeId, t.Name, t.Version, t.CreatedAt), firstCreated: null);

    private readonly record struct OrderKey(Guid Id, Guid DocumentTypeId, string Name, int Version, DateTimeOffset CreatedAt);

    private static IReadOnlyList<T> Order<T>(
        IEnumerable<T> templates, Func<T, OrderKey> keyOf, IReadOnlyDictionary<string, DateTimeOffset>? firstCreated)
        => templates
            .Select(t => (Row: t, Key: keyOf(t)))
            .GroupBy(x => (x.Key.DocumentTypeId, x.Key.Name))
            .OrderBy(g => firstCreated is not null && firstCreated.TryGetValue(g.Key.Name, out var at)
                ? at
                : g.Min(x => x.Key.CreatedAt))
            .ThenBy(g => g.Key.Name, StringComparer.Ordinal)
            .ThenBy(g => g.Key.DocumentTypeId)
            .SelectMany(g => g.OrderByDescending(x => x.Key.Version).ThenByDescending(x => x.Key.Id))
            .Select(x => x.Row)
            .ToList();
}
