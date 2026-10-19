using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>
/// Кандидаты для строки бумаги в окне «Позиция номенклатуры» (issue #1515, эпик #1511): что подходит
/// строке счёта, накладной или соответствия — с причиной одной фразой.
///
/// <para><b>Два источника.</b> Ядро (порт <see cref="IModuleCatalog.SimilarAsync" />) сверяет строку со
/// справочником: артикул, то же наименование, название позиции в строке, большинство слов. Модуль добавляет
/// своё — <b>соответствия ДРУГИХ поставщиков</b>: тот же артикул или то же наименование у другого поставщика
/// уже выбирали, и выбор назван с поставщиком. Порядок источников — артикул ядра, затем чужой выбор, затем
/// остальное ядро: артикул называет товар, чужой выбор — догадка человека, слова — догадка правила.</para>
///
/// <para>⚠️ <b>Чужой выбор никогда не «лучшее совпадение»</b> (решение владельца 4 от 19.10.2026): «00-0029»
/// бывает внутренним кодом 1С продавца, и у другого тот же код — другой товар; правилом это не различить. И
/// «лучшее» ядра он снимает, если указал на другую позицию: у строки тогда два ответа, и Enter по одному из
/// них — догадка, которая запомнится для поставщика.</para>
///
/// <para><b>POST, но только читает</b> — как <see cref="SupplierMatchEndpoints" />: наименования из бумаги
/// длинные. Открыт тому, кто читает счета ИЛИ накладные (<see cref="CostsLookups" />), без прав справочника:
/// окно выбора есть в обоих документах.</para>
/// </summary>
public static class NomenclatureCandidateEndpoints
{
    /// <summary>
    /// Сколько кандидатов на строку. Пять — блок над списком, который читают, не прокручивая: больше —
    /// уже поиск, и окно говорит «есть ещё — уточните».
    /// </summary>
    public const int Limit = 5;

    /// <summary>
    /// Сколько строк в одном вопросе. Окно спрашивает об одной; предел — для предвыборки на счёт, и он мал
    /// нарочно: подбор сравнивает слова строки с каждой позицией, и 50 строк на справочник в 2000 позиций —
    /// уже секунда (замер PR #1536).
    /// </summary>
    public const int MaxLines = 50;

    /// <summary>Сколько поставщиков назвать в причине по имени; остальные — числом.</summary>
    private const int NamedSuppliers = 3;

    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/api/costs/nomenclature/candidates", CandidatesAsync)
            .RequireAuthorization(AppPolicies.Module("costs"))
            .AddEndpointFilter(CostsLookups.RequireDocumentReader)
            .WithTags("Счета на оплату");

    private static async Task<Ok<NomenclatureCandidatesView>> CandidatesAsync(
        NomenclatureCandidatesRequest body, CostsDbContext db, IModuleCatalog catalog, CancellationToken ct)
    {
        if (body.Lines is not { } lines)
            throw new InvalidRequestException("Строки не присланы («lines»): подбирать не для чего.");

        if (lines.Count > MaxLines)
            throw new InvalidRequestException(
                $"Строк в вопросе — {lines.Count}, больше {MaxLines}. Спросите частями.");

        // Все кандидаты ядра, без предела: предел режет уже слитый список, иначе позиция, которую назвал и
        // чужой выбор, и слова, за пределом ядра потеряла бы причину «по словам».
        var found = await catalog.SimilarAsync(CostsDirectories.Nomenclature,
                [.. lines.Select(l => new ModuleLineWords(l.SupplierText, l.SupplierCode))], int.MaxValue, ct)
            // Пустой ответ здесь означал бы «подходящих нет», а справочника нет вовсе.
            ?? throw new ConflictException(CostsDirectories.NoNomenclature("подбирать позицию не из чего"));

        var sought = lines.Select(l => SupplierMatchKey.Sought(l.SupplierCode, l.SupplierText).ToList()).ToList();
        var foreign = await ForeignAsync(db, body.SupplierId, sought, ct);
        var own = body.SupplierId is { } supplier
            ? await SupplierMatching.FindAsync(db, supplier, [.. lines.Select(l => (l.SupplierCode, l.SupplierText))], ct)
            : null;

        var positions = foreign.Select(m => m.NomenclatureId)
            .Concat(own?.OfType<SupplierMatch>().Select(m => m.NomenclatureId) ?? [])
            .Distinct().ToList();
        var known = positions.Count == 0 ? []
            : (await catalog.RefsAsync(CostsDirectories.Nomenclature, positions, ct))?.ToDictionary(r => r.Id)
                ?? throw new ConflictException(CostsDirectories.NoNomenclature("подбирать позицию не из чего"));
        var suppliers = await SupplierNamesAsync(catalog, [.. foreign.Select(m => m.SupplierId).Distinct()], ct);

        return TypedResults.Ok(new NomenclatureCandidatesView([.. lines.Select((line, index) => Merge(
            found[index], Chosen(line, sought[index], foreign, known, suppliers),
            own?[index] is { } match ? Unusable(match, known) : null))]));
    }

    /// <summary>
    /// Соответствия других поставщиков с ключами строк. Без поставщика в вопросе — всех: строка, набранная
    /// до выбора поставщика, всё равно чья-то.
    /// </summary>
    private static async Task<List<ForeignMatch>> ForeignAsync(
        CostsDbContext db, Guid? supplier, IReadOnlyList<List<SupplierMatchKey>> sought, CancellationToken ct)
    {
        var keys = sought.SelectMany(k => k).ToList();
        var codes = keys.Where(k => k.Kind == SupplierMatchKind.Code).Select(k => k.Hash).Distinct().ToList();
        var names = keys.Where(k => k.Kind == SupplierMatchKind.Name).Select(k => k.Hash).Distinct().ToList();
        if (codes.Count + names.Count == 0) return [];

        // По виду и отпечатку — так вопрос ложится на индекс (kind, key_hash): единственный индекс
        // начинается с поставщика.
        var query = db.SupplierMatches.AsNoTracking()
            .Where(m => (m.Kind == SupplierMatchKind.Code && codes.Contains(m.KeyHash))
                || (m.Kind == SupplierMatchKind.Name && names.Contains(m.KeyHash)));
        if (supplier is { } own) query = query.Where(m => m.SupplierId != own);

        return await query.Select(m => new ForeignMatch(m.SupplierId, m.Kind, m.KeyHash, m.NomenclatureId)).ToListAsync(ct);
    }

    /// <summary>
    /// Названия поставщиков — для причины. Справочник организаций не назначен или запись удалена — поставщик
    /// назван «другим»: это подпись, а не данные, и «другой поставщик» о нём правда.
    /// </summary>
    private static async Task<Dictionary<Guid, string>> SupplierNamesAsync(
        IModuleCatalog catalog, IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        ids.Count == 0 ? [] : (await catalog.RefsAsync(CostsDirectories.Organization, ids, ct) ?? [])
            .Where(r => !string.IsNullOrWhiteSpace(r.DisplayName))
            .ToDictionary(r => r.Id, r => r.DisplayName!);

    /// <summary>
    /// Чужой выбор для одной строки: позиция — и причины, по ключу на причину, артикул первым.
    ///
    /// <para>Позиция только действующая: соответствие могло вести на позицию, с тех пор ушедшую в архив или
    /// удалённую, а кандидат — это то, что можно выбрать. Архивные похожие считает ядро.</para>
    /// </summary>
    private static List<(ModuleCatalogRef Position, List<string> Why)> Chosen(
        MatchSuggestionLine line, List<SupplierMatchKey> sought, List<ForeignMatch> foreign,
        Dictionary<Guid, ModuleCatalogRef> known, Dictionary<Guid, string> suppliers)
    {
        var chosen = new List<(ModuleCatalogRef Position, List<string> Why)>();
        foreach (var key in sought)
            foreach (var position in foreign.Where(m => m.Kind == key.Kind && m.KeyHash == key.Hash)
                         .GroupBy(m => m.NomenclatureId)
                         .OrderByDescending(g => g.Select(m => m.SupplierId).Distinct().Count()))
            {
                if (known.GetValueOrDefault(position.Key) is not { Archived: false } live) continue;

                var who = Who([.. position.Select(m => m.SupplierId).Distinct()], suppliers);
                var why = key.Kind == SupplierMatchKind.Code
                    ? $"так выбрали у {who} по артикулу «{line.SupplierCode!.Trim()}»"
                    : $"так выбрали у {who} по тому же наименованию";

                var at = chosen.FindIndex(c => c.Position.Id == live.Id);
                if (at < 0) chosen.Add((live, [why]));
                else chosen[at].Why.Add(why);
            }
        return chosen;
    }

    /// <summary>«у «ЭТМ», «Элком» и ещё у 2» — по имени не больше трёх, остальные числом.</summary>
    private static string Who(IReadOnlyList<Guid> ids, Dictionary<Guid, string> suppliers)
    {
        var named = ids.Where(suppliers.ContainsKey).Select(id => suppliers[id])
            .Order(StringComparer.CurrentCultureIgnoreCase).Take(NamedSuppliers).ToList();
        var rest = ids.Count - named.Count;

        if (named.Count == 0) return rest == 1 ? "другого поставщика" : $"других поставщиков ({rest})";
        var listed = string.Join(", ", named.Select(n => $"«{n}»"));
        return rest > 0 ? $"{listed} и ещё у {rest}" : listed;
    }

    /// <summary>
    /// Слить ядро и чужой выбор: одна позиция — одна строка со всеми причинами.
    ///
    /// <para>Порядок: найденные артикулом (у них ядро назвало поле — <see cref="ModuleLineMatched.Field" />),
    /// затем названные чужим выбором, затем остальное ядро; внутри — порядок ядра. Причина чужого выбора
    /// встаёт после артикула и перед словами — тем же старшинством.</para>
    /// </summary>
    private static NomenclatureCandidates Merge(
        ModuleLineCandidates core, List<(ModuleCatalogRef Position, List<string> Why)> chosen,
        NomenclatureRecalledUnusable? unusable)
    {
        var strong = core.Items.FirstOrDefault(h => h.Strong)?.Ref.Id;
        // «Лучшее» ядра — одно среди ЕГО кандидатов. Чужой выбор, указавший на другую позицию, делает
        // ответ спорным, и Enter по «лучшему» стал бы догадкой.
        var sure = chosen.All(c => c.Position.Id == strong);
        var extra = chosen.ToDictionary(c => c.Position.Id, c => c.Why);

        var merged = core.Items.Select(h =>
            {
                var why = h.Why.ToList();
                var byCode = h.Matched.Field is not null;
                if (extra.Remove(h.Ref.Id, out var also)) why.InsertRange(byCode ? 1 : 0, also);
                return (Rank: byCode ? 0 : also is not null ? 1 : 2,
                    Item: new NomenclatureCandidate(h.Ref.Id, h.Ref.DisplayName, h.Ref.EntityType, h.Ref.MatchedAlias,
                        why, h.Strong && sure, h.Matched));
            })
            .Concat(chosen.Where(c => extra.ContainsKey(c.Position.Id)).Select(c => (Rank: 1,
                Item: new NomenclatureCandidate(c.Position.Id, c.Position.DisplayName, c.Position.EntityType, null,
                    c.Why, false, null))))
            .OrderBy(x => x.Rank)
            .Select(x => x.Item)
            .ToList();

        return new([.. merged.Take(Limit)], merged.Count > Limit, core.InArchive, core.Unreadable, unusable);
    }

    /// <summary>
    /// Запомненное для строки ЭТОГО поставщика, которое подставить нельзя. Без этого память молчала бы
    /// непонятно почему: строка узнана, а позиции нет — в архиве или удалена.
    /// </summary>
    private static NomenclatureRecalledUnusable? Unusable(SupplierMatch match, Dictionary<Guid, ModuleCatalogRef> known) =>
        known.GetValueOrDefault(match.NomenclatureId) switch
        {
            null => new(match.NomenclatureId, null, MatchSuggestion.Lost),
            { Archived: true } archived => new(match.NomenclatureId, archived.DisplayName, MatchSuggestion.Archived),
            _ => null,
        };

    /// <summary>Лёгкая строка чужого соответствия: ровно то, что нужно причине.</summary>
    private sealed record ForeignMatch(Guid SupplierId, SupplierMatchKind Kind, string KeyHash, Guid NomenclatureId);
}

/// <summary>Вопрос окна выбора: строки бумаги и чья она.</summary>
/// <param name="SupplierId">Поставщик документа; <c>null</c> — ещё не выбран. Его соответствия кандидатами не
/// идут — их подставляет сама форма, — а запомненное, которое подставить нельзя, названо.</param>
public sealed record NomenclatureCandidatesRequest(Guid? SupplierId, IReadOnlyList<MatchSuggestionLine>? Lines);

/// <summary>Ответ — по ответу на строку вопроса, в том же порядке.</summary>
public sealed record NomenclatureCandidatesView(IReadOnlyList<NomenclatureCandidates> Lines);

/// <summary>
/// Кандидаты для строки. Отдельная форма от поиска (<see cref="NomenclatureSearchResult" />) нарочно: у поиска
/// неполнота по алфавиту, у кандидатов — по правилам, и общая форма заставила бы окно путать оговорки.
/// </summary>
/// <param name="Items">Действующие кандидаты, старший первым, не больше <see cref="NomenclatureCandidateEndpoints.Limit" />.</param>
/// <param name="More">Подошло больше, чем показано.</param>
/// <param name="InArchive">Сколько подошло архивных позиций — заводить такую заново не нужно.</param>
/// <param name="Unreadable">Сколько позиций сверить не удалось — это не «не подошли».</param>
/// <param name="RecalledUnusable">Для строки этого поставщика запомнена позиция, которую выбрать нельзя.</param>
public sealed record NomenclatureCandidates(
    IReadOnlyList<NomenclatureCandidate> Items, bool More, int InArchive, int Unreadable,
    NomenclatureRecalledUnusable? RecalledUnusable);

/// <summary>Кандидат.</summary>
/// <param name="Why">Причины одной фразой каждая, старшая первой, — готовые к показу.</param>
/// <param name="Strong">«Лучшее совпадение», выбирается по Enter. Ставит только сервер: ручной выбор
/// запоминается для поставщика.</param>
/// <param name="Matched">Сравнение «строка ↔ позиция» словами ядра; <c>null</c> — кандидата назвал только
/// чужой выбор, слова строки с позицией не сверялись.</param>
public sealed record NomenclatureCandidate(
    Guid Id, string? Name, string Type, string? MatchedAlias, IReadOnlyList<string> Why, bool Strong,
    ModuleLineMatched? Matched);

/// <summary>Запомненная позиция, которую подставить нельзя.</summary>
/// <param name="Name">Название; <c>null</c> — позиции больше нет.</param>
/// <param name="Issue"><c>archived</c> — в архиве, <c>lost</c> — удалена (как у <see cref="MatchSuggestion" />).</param>
public sealed record NomenclatureRecalledUnusable(Guid Id, string? Name, string Issue);
