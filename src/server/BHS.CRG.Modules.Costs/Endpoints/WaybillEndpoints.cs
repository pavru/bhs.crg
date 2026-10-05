using System.Text.Json;
using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>
/// Адреса расходной накладной (задача D1 этапа 2, issue #1083; ТЗ COST-5, COST-9, COST-17).
///
/// <para><b>Две оси правки.</b> Черновик правят целиком — шапку и набор строк. Проведённую накладную
/// не правят: её строки уже лежат в перечне отпущенного на стройку, и тихая правка количества меняла
/// бы перечень без следа. Исправить — вернуть в черновик (действие записано в журнал), поправить и
/// провести снова.</para>
///
/// <para>⚠️ <b>Исключение одно — сопоставление строки.</b> Накладная из 1С приходит сразу проведённой
/// и с несопоставленными строками (COST-8): свести их со справочником надо, не распроводя документ,
/// иначе «не сопоставлено: 4 строки» было бы числом, которое нечем уменьшить.</para>
///
/// <para>⚠️ <b>Проведение несопоставленных строк НЕ запрещает</b> — в отличие от «разобран» у счёта.
/// Накладная — свидетельство того, что материал выдан; отказать в проведении из-за справочника значило
/// бы держать выданное невыданным. Строка без позиции в перечень не попадает и названа числом.</para>
///
/// <para>⚠️ <b>Правка шапки и строк называет версию, которую видел человек</b> (<c>ifMatch</c>, ревью
/// PR #1206). Версия строки базы сама защищает только два запроса, идущих одновременно: форма,
/// открытая час назад, читала бы свежую версию и записывалась бы поверх — а замена набора строк при
/// этом молча удаляла бы строку, добавленную другим человеком.</para>
/// </summary>
public static class WaybillEndpoints
{
    private const string Read = "costs.waybill.read";
    private const string Edit = "costs.waybill.edit";

    /// <summary>
    /// Сколько накладных отдаёт список. Больше — ответ говорит «есть ещё», а не молчит: поиск на экране
    /// идёт по загруженному, и обрезанный молча список читался бы как «такой накладной нет».
    /// </summary>
    internal const int ListLimit = 300;

    internal static string Label(Waybill waybill) =>
        $"Накладная № {waybill.Number ?? "без номера"}" +
        (waybill.IssuedOn is { } date ? $" от {date:dd.MM.yyyy}" : string.Empty);

    public static void MapWaybills(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/costs/waybills").WithTags("Расходные накладные");

        group.MapGet("/", ListAsync).RequireAuthorization(AppPolicies.Permission(Read));
        group.MapGet("/{id:guid}", GetAsync).RequireAuthorization(AppPolicies.Permission(Read));

        group.MapPost("/", CreateAsync).RequireAuthorization(AppPolicies.Permission(Edit));
        group.MapPut("/{id:guid}", UpdateAsync).RequireAuthorization(AppPolicies.Permission(Edit));
        group.MapPut("/{id:guid}/lines", ReplaceLinesAsync).RequireAuthorization(AppPolicies.Permission(Edit));
        group.MapPut("/{id:guid}/lines/{lineId:guid}/nomenclature", MatchAsync)
            .RequireAuthorization(AppPolicies.Permission(Edit));
        group.MapPost("/{id:guid}/posted", PostAsync).RequireAuthorization(AppPolicies.Permission(Edit));
        group.MapPost("/{id:guid}/draft", DraftAsync).RequireAuthorization(AppPolicies.Permission(Edit));

        // «Материалы на объекте» — чтением накладных: это их свод, и денег в нём нет.
        endpoints.MapGet("/api/costs/materials", MaterialsAsync)
            .WithTags("Расходные накладные")
            .RequireAuthorization(AppPolicies.Permission(Read));
    }

    /// <summary>
    /// Список накладных, свежие сверху. <paramref name="unmatched" /> — отбор «свести со справочником»:
    /// накладные, где есть строка без позиции.
    /// </summary>
    private static async Task<Ok<WaybillListView>> ListAsync(
        CostsDbContext db, IModuleConstructions sites, CancellationToken ct,
        Guid? constructionId = null, bool unmatched = false)
    {
        var selected = db.Waybills.AsNoTracking();
        if (constructionId is { } site) selected = selected.Where(w => w.ConstructionId == site);
        if (unmatched)
            selected = selected.Where(w => db.WaybillLines.Any(l => l.WaybillId == w.Id && l.NomenclatureId == null));

        // Счётчики — подзапросами того же SELECT: отдельный запрос с перечнем идентификаторов рос бы
        // вместе со списком.
        var page = await selected
            .OrderByDescending(w => w.IssuedOn)
            .ThenByDescending(w => w.CreatedAt)
            .Take(ListLimit + 1)
            .Select(w => new
            {
                Waybill = w,
                Lines = db.WaybillLines.Count(l => l.WaybillId == w.Id),
                Unmatched = db.WaybillLines.Count(l => l.WaybillId == w.Id && l.NomenclatureId == null),
            })
            .ToListAsync(ct);

        var known = await sites.ListAsync(ct);

        return TypedResults.Ok(new WaybillListView(
            [.. page.Take(ListLimit).Select(row => WaybillViews.Item(
                row.Waybill, known.FirstOrDefault(s => s.Id == row.Waybill.ConstructionId), row.Lines, row.Unmatched))],
            More: page.Count > ListLimit));
    }

    private static async Task<Ok<WaybillView>> GetAsync(
        Guid id, CostsDbContext db, IModuleConstructions sites, IModuleCatalog catalog, CancellationToken ct) =>
        TypedResults.Ok(await ViewAsync(await FindAsync(db, id, ct), db, await sites.ListAsync(ct), catalog, ct));

    private static async Task<Created<WaybillView>> CreateAsync(
        JsonElement body, CostsDbContext db, IModuleConstructions sites, IModuleCatalog catalog, IModuleUser user,
        IModuleActivityLog log, CancellationToken ct)
    {
        var header = WaybillRequests.Header(body);
        var known = await sites.ListAsync(ct);
        EnsureSiteExists(known, header.ConstructionId);

        var waybill = Waybill.Create(user.Id);
        waybill.Apply(header);
        db.Waybills.Add(waybill);
        await db.SaveChangesAsync(ct);

        await log.RecordAsync(WaybillActions.Created, waybill.Id.ToString(), Label(waybill), ct: ct);

        return TypedResults.Created($"/api/costs/waybills/{waybill.Id}",
            await ViewAsync(waybill, db, known, catalog, ct));
    }

    /// <summary>
    /// Правка черновика: шапка и — если прислан «lines» — набор строк, ОДНИМ сохранением. Форма шлёт
    /// их вместе: двумя запросами шапка записалась бы и тогда, когда строки отказали, и человек
    /// остался бы с половиной сохранённого.
    /// </summary>
    private static async Task<Ok<WaybillView>> UpdateAsync(
        Guid id, JsonElement body, CostsDbContext db, IModuleConstructions sites, IModuleCatalog catalog,
        IModuleActivityLog log, CancellationToken ct)
    {
        var header = WaybillRequests.Header(body);
        var lines = WaybillRequests.Lines(body);
        if (lines is not null) await EnsureNomenclatureExistsAsync(catalog, Numbered(lines), ct);

        var waybill = await FindAsync(db, id, ct);
        EnsureSeen(db, waybill, WaybillRequests.IfMatch(body));
        EnsureDraft(waybill, "шапку");

        var known = await sites.ListAsync(ct);
        EnsureSiteExists(known, header.ConstructionId);

        var was = Label(waybill);
        // Форма шлёт шапку и тогда, когда человек её не менял. Запись в журнал без изменения учила бы
        // не верить журналу.
        var headerChanged = waybill.Header() != header;
        if (headerChanged) waybill.Apply(header);
        var linesChanged = lines is not null && await PlaceLinesAsync(db, waybill, lines, ct);

        if (headerChanged || linesChanged)
        {
            await db.SaveChangesAsync(ct);
            if (headerChanged)
                await log.RecordAsync(WaybillActions.Changed, waybill.Id.ToString(), Label(waybill),
                    before: was == Label(waybill) ? null : was, ct: ct);
            if (linesChanged)
                await log.RecordAsync(WaybillActions.LinesChanged, waybill.Id.ToString(), Label(waybill),
                    after: $"строк: {lines!.Count}", ct: ct);
        }

        return TypedResults.Ok(await ViewAsync(waybill, db, known, catalog, ct));
    }

    private static async Task<Ok<WaybillView>> ReplaceLinesAsync(
        Guid id, JsonElement body, CostsDbContext db, IModuleConstructions sites, IModuleCatalog catalog,
        IModuleActivityLog log, CancellationToken ct)
    {
        var lines = WaybillRequests.Lines(body)
            ?? throw new InvalidRequestException(
                "Набор строк не прислан. Пустой набор — это «lines»: [], и он означает «строк нет». " +
                "Отсутствие поля прочитать как «строки не менять» нельзя: адрес заменяет набор целиком.");

        await EnsureNomenclatureExistsAsync(catalog, Numbered(lines), ct);

        var waybill = await FindAsync(db, id, ct);
        EnsureSeen(db, waybill, WaybillRequests.IfMatch(body));
        EnsureDraft(waybill, "строки");

        if (await PlaceLinesAsync(db, waybill, lines, ct))
        {
            await db.SaveChangesAsync(ct);
            await log.RecordAsync(WaybillActions.LinesChanged, waybill.Id.ToString(), Label(waybill),
                after: $"строк: {lines.Count}", ct: ct);
        }

        return TypedResults.Ok(await ViewAsync(waybill, db, await sites.ListAsync(ct), catalog, ct));
    }

    /// <summary>
    /// Разложить присланный набор по строкам накладной. Не сохраняет — сохраняет звавший, одним разом
    /// со своей правкой. Возвращает, изменилось ли что-нибудь.
    /// </summary>
    private static async Task<bool> PlaceLinesAsync(
        CostsDbContext db, Waybill waybill, IReadOnlyList<(Guid? Id, WaybillLineValues Values)> parsed,
        CancellationToken ct)
    {
        if (parsed.Where(p => p.Id is not null).GroupBy(p => p.Id).FirstOrDefault(g => g.Count() > 1) is { } twice)
            throw new InvalidRequestException(
                $"Строка {twice.Key} прислана дважды. Набор заменяет состояние целиком, и одна из двух " +
                "исчезла бы без следа. Новые строки присылайте без «id».");

        var existing = await db.WaybillLines.Where(l => l.WaybillId == waybill.Id).ToListAsync(ct);
        var was = existing.OrderBy(l => l.Ordinal).Select(l => (l.Id, l.Snapshot())).ToList();
        var now = new List<(Guid, WaybillLineValues)>(parsed.Count);

        for (var index = 0; index < parsed.Count; index++)
        {
            var (lineId, values) = parsed[index];
            var line = lineId is { } known
                ? existing.FirstOrDefault(l => l.Id == known)
                    ?? throw new InvalidRequestException(
                        $"Строка {index + 1}: строки {known} у этой накладной нет. Так бывает, когда форму " +
                        "оставили открытой, а строку тем временем удалили. Перечитайте накладную и " +
                        "повторите правку — иначе удалённая строка вернулась бы молча.")
                : Added(db, waybill.Id);

            if (line.Ordinal != index + 1 || line.Snapshot() != values) line.Apply(index + 1, values);
            now.Add((line.Id, values));
        }

        var kept = now.Select(l => l.Item1).ToHashSet();
        db.WaybillLines.RemoveRange(existing.Where(l => !kept.Contains(l.Id)));

        if (was.SequenceEqual(now)) return false;

        // Версия строки накладной защищает и набор строк: две одновременные замены иначе сложились бы
        // в набор, которого не присылал никто.
        waybill.ContentChanged();
        return true;
    }

    /// <summary>Сопоставить строку (или снять сопоставление) — в любом состоянии накладной.</summary>
    private static async Task<Ok<WaybillView>> MatchAsync(
        Guid id, Guid lineId, JsonElement body, CostsDbContext db, IModuleConstructions sites,
        IModuleCatalog catalog, IModuleActivityLog log, CancellationToken ct)
    {
        CostsValues.EnsureObject(body, "Сопоставление", "сопоставления");
        var position = WaybillRequests.Nomenclature(body, "Позиция номенклатуры");

        var waybill = await FindAsync(db, id, ct);
        var line = await db.WaybillLines.FirstOrDefaultAsync(l => l.Id == lineId && l.WaybillId == waybill.Id, ct)
            ?? throw new NotFoundException("Строка накладной не найдена.");

        // Отказ называет ТУ строку, которую сопоставляют, а не первую в присланном.
        await EnsureNomenclatureExistsAsync(catalog, [(line.Ordinal, position)], ct);

        if (line.NomenclatureId != position)
        {
            line.Match(position);
            waybill.ContentChanged();
            await db.SaveChangesAsync(ct);
            await log.RecordAsync(WaybillActions.Matched, waybill.Id.ToString(), Label(waybill),
                after: position is null ? $"строка {line.Ordinal}: сопоставление снято" : $"строка {line.Ordinal}",
                ct: ct);
        }

        return TypedResults.Ok(await ViewAsync(waybill, db, await sites.ListAsync(ct), catalog, ct));
    }

    private static async Task<Ok<WaybillView>> PostAsync(
        Guid id, CostsDbContext db, IModuleConstructions sites, IModuleCatalog catalog, IModuleUser user,
        IModuleActivityLog log, CancellationToken ct)
    {
        var waybill = await FindAsync(db, id, ct);
        var known = await sites.ListAsync(ct);
        if (waybill.State == WaybillState.Posted)
            return TypedResults.Ok(await ViewAsync(waybill, db, known, catalog, ct));

        var lines = await db.WaybillLines.AsNoTracking().Where(l => l.WaybillId == waybill.Id)
            .OrderBy(l => l.Ordinal).ToListAsync(ct);

        var problems = new List<string>();
        if (waybill.Number is null) problems.Add("не назван номер");
        if (waybill.IssuedOn is null) problems.Add("не названа дата отпуска");
        if (waybill.ConstructionId is null) problems.Add("не названа стройка-получатель");
        else if (known.All(s => s.Id != waybill.ConstructionId))
            problems.Add("стройки-получателя больше нет — выберите её заново");
        if (lines.Count == 0) problems.Add("нет ни одной строки");

        var empty = lines.Where(l => l.Quantity is null or 0).Select(l => l.Ordinal).ToList();
        if (empty.Count > 0)
            problems.Add($"не названо количество: {(empty.Count == 1 ? "строка" : "строки")} {string.Join(", ", empty)}");

        if (problems.Count > 0)
            throw new InvalidRequestException(
                $"{Label(waybill)}: {string.Join("; ", problems)}. Проведённая накладная означает, что " +
                "материалы выданы на стройку: без даты, стройки и количества выданное нечем посчитать. " +
                "Черновиком накладная живёт сколько нужно.");

        var unmatched = lines.Count(l => l.NomenclatureId is null);
        waybill.Post(user.Id);
        await db.SaveChangesAsync(ct);

        await log.RecordAsync(WaybillActions.Posted, waybill.Id.ToString(), Label(waybill),
            after: unmatched == 0
                ? $"строк: {lines.Count}"
                : $"строк: {lines.Count}, не сопоставлено: {unmatched}",
            ct: ct);

        return TypedResults.Ok(await ViewAsync(waybill, db, known, catalog, ct));
    }

    private static async Task<Ok<WaybillView>> DraftAsync(
        Guid id, CostsDbContext db, IModuleConstructions sites, IModuleCatalog catalog,
        IModuleActivityLog log, CancellationToken ct)
    {
        var waybill = await FindAsync(db, id, ct);
        if (waybill.State != WaybillState.Draft)
        {
            waybill.ReturnToDraft();
            await db.SaveChangesAsync(ct);
            await log.RecordAsync(WaybillActions.Draft, waybill.Id.ToString(), Label(waybill), ct: ct);
        }

        return TypedResults.Ok(await ViewAsync(waybill, db, await sites.ListAsync(ct), catalog, ct));
    }

    private static async Task<Ok<IssuedMaterialsView>> MaterialsAsync(
        CostsDbContext db, IModuleCatalog catalog, CancellationToken ct, Guid? constructionId = null)
    {
        if (constructionId is not { } site)
            throw new InvalidRequestException(
                "Не названа стройка (constructionId): перечень отпущенного считается по стройке.");

        var report = await IssuedMaterials.ReadAsync(db, site, ct);
        var names = await NamesAsync(catalog, [.. report.Items.Select(i => (Guid?)i.NomenclatureId)], ct);

        return TypedResults.Ok(new IssuedMaterialsView(
            site,
            [.. report.Items
                .Select(i => new IssuedMaterialView(i.NomenclatureId, names?.GetValueOrDefault(i.NomenclatureId),
                    i.Unit, i.Quantity, i.First, i.Last, i.Waybills))
                .OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(i => i.Unit)],
            report.UnmatchedLines,
            report.UnmatchedWaybills));
    }

    /// <param name="known">Стройки, прочитанные звавшим: список читается один раз на запрос, а не
    /// заново ради каждого вопроса к нему.</param>
    private static async Task<WaybillView> ViewAsync(
        Waybill waybill, CostsDbContext db, IReadOnlyList<ModuleConstruction> known, IModuleCatalog catalog,
        CancellationToken ct)
    {
        var lines = await db.WaybillLines.AsNoTracking().Where(l => l.WaybillId == waybill.Id).ToListAsync(ct);

        return WaybillViews.Full(waybill, db.VersionOf(waybill), lines,
            known.FirstOrDefault(s => s.Id == waybill.ConstructionId),
            await NamesAsync(catalog, [.. lines.Select(l => l.NomenclatureId)], ct));
    }

    private static async Task<Waybill> FindAsync(CostsDbContext db, Guid id, CancellationToken ct) =>
        await db.Waybills.FirstOrDefaultAsync(w => w.Id == id, ct)
        ?? throw new NotFoundException("Накладная не найдена.");

    private static WaybillLine Added(CostsDbContext db, Guid waybillId)
    {
        var line = WaybillLine.Create(waybillId);
        db.WaybillLines.Add(line);
        return line;
    }

    private static IReadOnlyList<(int Number, Guid? Position)> Numbered(
        IReadOnlyList<(Guid? Id, WaybillLineValues Values)> lines) =>
        [.. lines.Select((line, index) => (index + 1, line.Values.NomenclatureId))];

    /// <summary>
    /// Правка собрана по той версии накладной, что лежит сейчас. Без названной версии — отказ, а не
    /// «значит, свежая»: умолчание записывало бы устаревшую форму поверх чужой правки.
    /// </summary>
    private static void EnsureSeen(CostsDbContext db, Waybill waybill, string? seen)
    {
        if (seen is null)
            throw new InvalidRequestException(
                "Не названа версия накладной, по которой собрана правка (ifMatch) — она приходит в ответе " +
                "чтения полем «version». Без неё правка записалась бы поверх чужой.");

        if (seen != db.VersionOf(waybill))
            throw new ConflictException(
                $"{Label(waybill)} тем временем изменили, и правка не записана. Перечитайте накладную и " +
                "повторите: записанная поверх, эта правка затёрла бы чужую — в том числе удалила бы " +
                "строки, которых не было на вашем экране.");
    }

    private static void EnsureDraft(Waybill waybill, string what)
    {
        if (waybill.State == WaybillState.Posted)
            throw new ConflictException(
                $"{Label(waybill)} проведена, и править {what} у неё нельзя: её строки уже лежат в перечне " +
                "материалов, отпущенных на стройку. Верните накладную в черновик, поправьте и проведите " +
                "снова. Сопоставить строку с номенклатурой можно и у проведённой.");
    }

    private static void EnsureSiteExists(IReadOnlyList<ModuleConstruction> known, Guid? id)
    {
        if (id is { } site && known.All(s => s.Id != site))
            throw new InvalidRequestException(
                "Стройки, названной получателем, нет. Так бывает, когда её удалили, пока форма была " +
                "открыта: выберите стройку заново.");
    }

    /// <summary>Названия позиций; <c>null</c> — типа «Номенклатура» в системе нет.</summary>
    private static async Task<IReadOnlyDictionary<Guid, string?>?> NamesAsync(
        IModuleCatalog catalog, IReadOnlyList<Guid?> positions, CancellationToken ct)
    {
        var ids = positions.OfType<Guid>().Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, string?>();

        return (await catalog.RefsAsync(CostsRecordTypes.NomenclatureCode, ids, ct))
            ?.ToDictionary(r => r.Id, r => r.DisplayName);
    }

    /// <param name="positions">Позиция и НОМЕР строки, которым её назовёт отказ: номер приходит от
    /// звавшего, потому что у сопоставления одной строки он не «первая в присланном».</param>
    private static async Task EnsureNomenclatureExistsAsync(
        IModuleCatalog catalog, IReadOnlyList<(int Number, Guid? Position)> positions, CancellationToken ct)
    {
        if (positions.All(p => p.Position is null)) return;

        var names = await NamesAsync(catalog, [.. positions.Select(p => p.Position)], ct)
            ?? throw new ConflictException(
                $"Тип «{CostsRecordTypes.NomenclatureCode}» в системе не заведён, поэтому ссылаться строкам " +
                "не на что. Строки без позиции при этом сохраняются — они считаются несопоставленными.");

        var lost = positions
            .Where(p => p.Position is { } value && !names.ContainsKey(value))
            .Select(p => p.Number)
            .ToList();
        if (lost.Count > 0)
            throw new InvalidRequestException(
                $"Позиции номенклатуры нет в справочнике: {(lost.Count == 1 ? "строка" : "строки")} " +
                $"{string.Join(", ", lost)}. Так бывает, когда позицию удалили или перенесли в другой вид. " +
                "Выберите позицию заново — ссылка в пустоту в перечень отпущенного не попала бы.");
    }
}
