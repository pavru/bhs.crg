using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Modules.Ports;
using MediatR;
using CoreRecordsFor = BHS.CRG.Application.Documents.RecordsFor;
using RecordsFor = BHS.CRG.Modules.Ports.RecordsFor;

namespace BHS.CRG.Api.Modules.Ports;

/// <summary>
/// Справочники ядра для модуля — через те же запросы, что и раздел общих данных (ТЗ CORE-34).
///
/// ⚠️ Через MediatR, а не прямым чтением набора: у справочника есть обработчики, и завтра в них
/// появится то, что модулю обязательно тоже (скажем, разрешение ссылок или отбор по правам, если
/// учётные записи выйдут за пределы компании). Прямое чтение прошло бы мимо, и разошлось бы это
/// молча — данные-то вернулись бы.
///
/// <para>⚠️ <b>Читаются ОБЩИЕ ДАННЫЕ</b> (<c>DomainObject</c>), а не прежняя таблица
/// <c>catalog_entities</c>. Разница не в имени набора: в <c>catalog_entities</c> не пишет ни один
/// экран и её не читает ни один адрес клиента, а ссылка <c>{"$ref":"catalog","entryId":…}</c>
/// разрешается по общим данным — и ядром при генерации (<c>EntityResolver</c>), и проверкой привязок.
/// Порт, читавший прежнюю таблицу, отвечал на вопрос «как называется эта организация» пустотой для
/// КАЖДОГО настоящего поставщика, а в реестре счетов это выглядело бы как «ссылка есть, названия
/// нет» — то есть как потеря данных. Поймано на форме счёта (issue #1076), прожило один PR.</para>
///
/// <para><b>Справочник находится по РОЛИ</b> (ТЗ CORE-20.4, issue #1101), и находит его
/// <see cref="RoleResolution" /> — тот же расчёт, которым покрытие считает экран сопоставления.
/// Своего поиска типа здесь быть не должно: два счёта однажды разойдутся, и экран скажет
/// «сопоставлено» про справочник, который порт не видит.</para>
/// </summary>
public sealed class ModuleCatalogPort(
    IMediator mediator, IRepository<DocumentType> types, ModuleNeeds needs, TagCatalog tags,
    ISimilarityRecordsLoader similarity) : IModuleCatalog
{
    public async Task<IReadOnlyList<ModuleCatalogEntry>?> ListAsync(
        string role, RecordsFor purpose, CancellationToken ct = default)
    {
        var records = Core(purpose);

        // Роль не обеспечена — ноль, а не пустой список: «справочник не назначен» и «записей в нём
        // ещё нет» — разные ответы, и выбирает между ними модуль. Отказом отсюда отвечать
        // нельзя и по букве: слой API отвечает кодами, доменные отказы бросает тот, кто знает, что
        // они означают на экране (`DomainExceptionPolicyTests`).
        // Справочник — это носитель роли И его подтипы: подтип организации остаётся организацией, и
        // поставщиком заказчика может быть любой из них. Считаем так же, как ядро в `isSubtypeOf`.
        if (await FamilyAsync(role, ct) is not { } codes) return null;
        var entries = new List<DomainObject>();
        foreach (var id in codes.Keys)
            entries.AddRange(await mediator.Send(new ListCommonDataEntriesQuery(records, null, null, id), ct));

        // ⚠️ Записи приезжают ЦЕЛИКОМ, с данными: так объявлен контракт порта (`DataJson`). Цена
        // названа: у вида с картинкой в поле это мегабайты на список (issue #1015). Кому нужен один
        // реквизит (сопоставление поставщика по ИНН), спрашивает `FieldValuesAsync`: там данные не
        // едут, а наследование реквизитов разрешено — здесь его нет, это СОБСТВЕННЫЕ данные записи.

        // Порядок задаёт ПОРТ. Запрос ядра не сортирует вовсе — порядок приходит от Postgres и
        // меняется после правок и уборки, — а список каталога на экране сортирует клиент. Обещание
        // «по названию» в контракте без сортировки здесь было бы ложным: оно сбывалось бы, пока
        // записи не правили (ревью PR #1106). Сравнение культурное, как у клиента: список читает
        // человек, и «Ёлка» в нём стоит между «Дубом» и «Жасмином», а не после латиницы.
        return [.. entries
            .Select(e => Map(e, codes))
            .OrderBy(e => e.DisplayName, StringComparer.CurrentCulture)];
    }

    public async Task<ModuleCatalogChoice?> SearchAsync(
        string role, string? query, int limit, CancellationToken ct = default)
    {
        if (await FamilyAsync(role, ct) is not { } codes) return null;

        var found = await mediator.Send(new SearchCommonDataForChoiceQuery([.. codes.Keys], query, limit), ct);
        return new ModuleCatalogChoice(Refs(found.Items, codes), found.InArchive);
    }

    public async Task<IReadOnlyList<ModuleCatalogRef>?> RefsAsync(
        string role, IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        // Спрашивать нечего — и справочник выяснять незачем: пустой перечень не повод отвечать
        // «роль не обеспечена». Но объявлена ли роль, проверяем и тут: дефект кода не должен
        // зависеть от того, пуст ли список.
        needs.EnsureDeclared(role);
        if (ids.Count == 0) return [];
        if (await FamilyAsync(role, ct) is not { } codes) return null;

        return Refs(await mediator.Send(new CommonDataRefsByIdsQuery([.. codes.Keys], ids), ct), codes);
    }

    public async Task<ModuleCatalogFieldValues?> FieldValuesAsync(
        string role, string fieldTag, RecordsFor purpose, CancellationToken ct = default)
    {
        var records = Core(purpose);
        needs.EnsureDeclared(role, fieldTag);

        var all = await types.GetAllAsync(ct);
        var resolved = RoleResolution.Role(role, all);
        if (resolved.Root is null) return null;

        // Поле сверяем со СХЕМОЙ, а не с данными: схему ведёт человек, и снятый тэг в данных
        // выглядел бы как «ни у одной организации ИНН нет» — то есть как «не совпало ни с кем».
        // Простое — сверх того, что допускает сам тэг: значение одного поля читается скаляром.
        // Тэг в реестре есть наверняка: потребность с неизвестным тэгом не пережила бы старт.
        var tag = tags.Find(fieldTag)!;
        var field = RoleResolution.Field(resolved, tag, all);
        if (field.Key is not { } fieldKey) return new ModuleCatalogFieldValues(false, [], field.Describe(tag));
        if (!SchemaFieldKinds.IsScalar(field.Field!.Type))
            return new ModuleCatalogFieldValues(false, [],
                $"поле «{tag.Label}» назначено полю «{fieldKey}», а оно составное — одним значением не читается");

        var codes = resolved.Family;
        var values = await mediator.Send(new CommonDataFieldValuesQuery([.. codes.Keys], fieldKey, records), ct);
        return new ModuleCatalogFieldValues(true,
            [.. values
                .Select(v => new ModuleCatalogFieldValue(
                    new ModuleCatalogRef(
                        v.Record.Id, codes.TryGetValue(v.Record.CompositeTypeId, out var code) ? code : string.Empty,
                        v.Record.DisplayName, v.Record.Archived),
                    v.Value, v.InheritedFrom, v.Unreadable))
                .OrderBy(v => v.Record.DisplayName, StringComparer.CurrentCulture)]);
    }

    public async Task<IReadOnlyList<ModuleLineCandidates>?> SimilarAsync(
        string role, IReadOnlyList<ModuleLineWords> lines, int limit, CancellationToken ct = default)
    {
        // Предел ноль — дефект вызывающего: ответ «кандидатов больше» без единого кандидата ничего не
        // значит, и модуль показал бы «есть ещё» над пустым списком.
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        // Как у названий: спрашивать нечего — и справочник выяснять незачем, но объявлена ли роль,
        // проверяем и тут.
        needs.EnsureDeclared(role);
        if (lines.Count == 0) return [];

        var all = (await types.GetAllAsync(ct)).ToDictionary(t => t.Id);
        var resolved = RoleResolution.Role(role, [.. all.Values]);
        if (resolved.Root is null) return null;
        var codes = resolved.Family;

        // Записи — один раз на все строки; ключ у каждой — её собственного вида
        // (`identity: null`).
        // ⚠️ Поля ключа читаются по тэгу «Идентификатор», а не по тэгу поля роли из Needs: ключ — понятие
        // ядра (его же сверяет шаг «Новая позиция», им же называется запись), и роль им не владеет. Снятый
        // с поля тэг ключа делает подбор беднее, но не ложным: запись сверяется названием.
        var lying = await similarity.LoadAsync(all, [.. all.Values.Where(t => codes.ContainsKey(t.Id))], null, ct);
        return [.. lines.Select(line =>
        {
            // Всех: действующих отсекает предел порта, архивные идут числом.
            var hits = NomenclatureSimilarity.ForLine(
                new(line.Name, line.Code), lying.Records, lying.Titles, limit: int.MaxValue).Hits;
            var live = hits.Where(h => !h.Record.Archived).ToList();
            return new ModuleLineCandidates(
                [.. live.Take(limit).Select(h => new ModuleLineCandidate(
                    new ModuleCatalogRef(
                        h.Record.Id, codes.TryGetValue(h.Record.TypeId, out var code) ? code : string.Empty,
                        h.Record.Name, Archived: false, MatchedAlias: h.Matched.Alias),
                    h.Why, h.Strong,
                    new ModuleLineMatched(
                        [.. h.Matched.Words.Select(w => new ModuleLineWord(w.Text, w.Start, w.Found))], h.Matched.Field)))],
                live.Count > limit, hits.Count - live.Count, lying.Unreadable);
        })];
    }

    public async Task<ModuleCatalogCard?> CardAsync(
        string role, Guid id, IReadOnlyList<string> fieldTags, CancellationToken ct = default)
    {
        foreach (var tag in fieldTags) needs.EnsureDeclared(role, tag);
        needs.EnsureDeclared(role);

        var all = await types.GetAllAsync(ct);
        var resolved = RoleResolution.Role(role, all);
        if (resolved.Root is null) return null;
        var codes = resolved.Family;

        // Семейство роли называет ПОРТ: запрос ядра по идентификатору открыл бы любую запись.
        if (await mediator.Send(new CommonDataCardQuery(id, [.. codes.Keys], all), ct) is not { } card) return null;

        var lines = card.Lines.ToDictionary(l => l.Key, Line, StringComparer.Ordinal);
        // Поле тэга — тем же расчётом, что у значений поля и окна «Новая позиция»: карточка, узнавшая
        // артикул иначе, сравнивала бы строку не с тем полем, по которому подбирали.
        var tagged = new Dictionary<string, ModuleCardLine>(StringComparer.Ordinal);
        foreach (var code in fieldTags)
            if (tags.Find(code) is { } tag && RoleResolution.Field(resolved, tag, all).Key is { } key
                && lines.TryGetValue(key, out var line))
                tagged[code] = line;

        return new ModuleCatalogCard(
            new ModuleCatalogRef(card.Record.Id, codes.GetValueOrDefault(card.Record.CompositeTypeId, string.Empty),
                card.Record.DisplayName, card.Record.Archived),
            card.TypeName, [.. card.Lines.Select(l => lines[l.Key])], card.Aliases, card.NotShown, card.EmptyCount,
            tagged, Scope(card.Scope));
    }

    private static ModuleCardLine Line(CommonDataCardLine line) => new(
        line.Title, line.Text, line.Kind switch
        {
            CardLineKind.Number => "number",
            CardLineKind.Date => "date",
            CardLineKind.Boolean => "boolean",
            CardLineKind.Choice => "choice",
            CardLineKind.Reference => "reference",
            CardLineKind.Hidden => "hidden",
            _ => "text",
        },
        line.Identity, line.InheritedFrom, line.Unreadable, line.Archived);

    private static string? Scope(BHS.CRG.Domain.Catalog.CatalogScope scope) => scope switch
    {
        BHS.CRG.Domain.Catalog.CatalogScope.Construction => "construction",
        BHS.CRG.Domain.Catalog.CatalogScope.Section => "section",
        BHS.CRG.Domain.Catalog.CatalogScope.Set => "set",
        _ => null,
    };

    /// <summary>
    /// Назначение модуля — в назначение ядра ПЕРЕБОРОМ, а не приведением числа: у двух перечислений
    /// порядок значений ничем не связан, и приведение пережило бы перестановку молча.
    /// </summary>
    private static CoreRecordsFor Core(RecordsFor purpose) => purpose switch
    {
        RecordsFor.Choice => CoreRecordsFor.Choice,
        RecordsFor.Display => CoreRecordsFor.Display,
        _ => throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "Неизвестное назначение чтения."),
    };

    /// <summary>
    /// Ссылки ядра — в ссылки порта: код вида вместо идентификатора типа, порядок для человека.
    ///
    /// <para>Сами ссылки приходят узкими запросами ядра, БЕЗ данных записи (issue #1078), а не
    /// отбором из <see cref="ListAsync" />: тот тянет записи целиком, и у номенклатуры это мегабайты
    /// картинок на список (issue #1015).</para>
    /// </summary>
    private static IReadOnlyList<ModuleCatalogRef> Refs(
        IReadOnlyList<CommonDataRef> refs, IReadOnlyDictionary<Guid, string> codes) =>
        // Порядок задаёт ПОРТ, как и в списке: в базе сортировка своя (ею отсекается limit), а список
        // читает человек — «Ёлка» в нём стоит между «Дубом» и «Жасмином», а не после латиницы.
        [.. refs
            .Select(r => new ModuleCatalogRef(
                r.Id, codes.TryGetValue(r.CompositeTypeId, out var code) ? code : string.Empty, r.DisplayName,
                r.Archived, r.MatchedAlias))
            .OrderBy(r => r.DisplayName, StringComparer.CurrentCulture)];

    /// <summary>
    /// Носитель роли и его подтипы; <c>null</c> — роль не обеспечена. Необъявленная роль — отказ:
    /// это дефект вызывающего кода, а не состояние установки.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, string>?> FamilyAsync(string role, CancellationToken ct)
    {
        needs.EnsureDeclared(role);
        var resolved = RoleResolution.Role(role, await types.GetAllAsync(ct));
        return resolved.Root is null ? null : resolved.Family;
    }

    public async Task<ModuleCatalogEntry?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var entry = await mediator.Send(new GetCommonDataEntryQuery(id), ct);

        // ⚠️ Документ записью справочника НЕ считается. Чтение по идентификатору фасету не отбирает
        // (в отличие от списка), а идентификатор у документа и у записи общих данных живёт в одной
        // таблице: подставь модуль идентификатор документа — и получил бы его реквизиты под видом
        // справочника, без единого отказа.
        if (entry is null || entry.IsDocument) return null;

        var all = await types.GetAllAsync(ct);
        return Map(entry, all.ToDictionary(t => t.Id, t => t.Code));
    }

    /// <summary>
    /// Данные уходят СТРОКОЙ. Не потому, что так удобнее: <c>JsonDocument</c> — объект,
    /// принадлежащий чтению, и отдать его модулю значит отдать владение временем жизни. Строка
    /// ничьей жизни не переживает и разбирается модулем тем, чем он разбирает свои данные.
    /// </summary>
    private static ModuleCatalogEntry Map(DomainObject e, IReadOnlyDictionary<Guid, string> codes) =>
        new(e.Id,
            codes.TryGetValue(e.CompositeTypeId, out var code) ? code : string.Empty,
            e.DisplayName,
            e.Data.RootElement.GetRawText(),
            e.IsArchived);
}
