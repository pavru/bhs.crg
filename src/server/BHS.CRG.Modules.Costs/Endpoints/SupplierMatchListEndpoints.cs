using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>
/// Список соответствий наименований поставщика: прочитать, направить на другую позицию, забыть (задача
/// C3, issue #1079, ТЗ COST-7.1).
///
/// <para><b>Зачем он нужен помимо удобства.</b> Соответствие ДЕРЖИТ поставщика и позицию от удаления
/// (решение владельца продукта от 09.10.2026), а держатель, которого нечем снять, делает запись
/// неудаляемой. Список — то, чем его снимают: «Забыть» либо «Сменить позицию».</para>
///
/// <para><b>Ключ не правится.</b> Артикул и наименование — слова бумаги; неверный ключ забывают, а
/// верный запомнится сам первым же выбором. По той же причине соответствие нельзя завести руками:
/// набранное по памяти не совпадёт с бумагой и не сработает никогда.</para>
///
/// <para>Правка называет версию записи заголовком <c>If-Match</c> — как правка счёта: список открыт
/// долго, и «сменить позицию» поверх чужой смены затёрло бы её молча.</para>
///
/// <para>Правом правки счёта, как и подстановка: соответствия ведёт тот, кто вводит счета.</para>
/// </summary>
public static class SupplierMatchListEndpoints
{
    /// <summary>Порция списка по умолчанию и её предел.</summary>
    public const int DefaultTake = 50;
    public const int MaxTake = 200;

    public const string IssueLost = "lost";
    public const string IssueArchived = "archived";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/costs/supplier-matches", ListAsync)
            .RequireAuthorization(AppPolicies.Module("costs"))
            .RequireAuthorization(AppPolicies.Permission(CostsModule.InvoiceEdit))
            .WithTags("Счета на оплату");

        endpoints.MapGet("/api/costs/supplier-matches/suppliers", SuppliersAsync)
            .RequireAuthorization(AppPolicies.Module("costs"))
            .RequireAuthorization(AppPolicies.Permission(CostsModule.InvoiceEdit))
            .WithTags("Счета на оплату");

        endpoints.MapPut("/api/costs/supplier-matches/{id:guid}", PointAsync)
            .RequireAuthorization(AppPolicies.Module("costs"))
            .RequireAuthorization(AppPolicies.Permission(CostsModule.InvoiceEdit))
            .WithTags("Счета на оплату");

        endpoints.MapDelete("/api/costs/supplier-matches", ForgetAllAsync)
            .RequireAuthorization(AppPolicies.Module("costs"))
            .RequireAuthorization(AppPolicies.Permission(CostsModule.InvoiceEdit))
            .WithTags("Счета на оплату");

        endpoints.MapDelete("/api/costs/supplier-matches/{id:guid}", ForgetAsync)
            .RequireAuthorization(AppPolicies.Module("costs"))
            .RequireAuthorization(AppPolicies.Permission(CostsModule.InvoiceEdit))
            .WithTags("Счета на оплату");
    }

    private static async Task<Ok<SupplierMatchListView>> ListAsync(
        CostsDbContext db, IModuleCatalog catalog, CancellationToken ct,
        Guid? supplierId = null, string? query = null, string? issue = null, int skip = 0, int? take = null)
    {
        if (issue is not (null or IssueLost or IssueArchived))
            throw new InvalidRequestException(
                $"Отбор «issue» принимает «{IssueLost}» (позиция удалена) или «{IssueArchived}» (позиция в архиве), " +
                $"а прислано «{issue}».");
        if (skip < 0 || take is <= 0 or > MaxTake)
            throw new InvalidRequestException(
                $"Порция списка задана неверно: «skip» не меньше нуля, «take» от 1 до {MaxTake}.");

        var selected = db.SupplierMatches.AsNoTracking();
        if (supplierId is { } supplier) selected = selected.Where(m => m.SupplierId == supplier);
        if (!string.IsNullOrWhiteSpace(query))
        {
            // Ищем по словам бумаги — артикулу и наименованию у поставщика. Название позиции живёт в
            // справочнике ядра: искать по нему значило бы читать справочник целиком на каждый запрос.
            var pattern = "%" + query.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            selected = selected.Where(m => EF.Functions.ILike(m.SourceText, pattern, "\\"));
        }

        // Отобранное читается ЛЁГКИМИ строками целиком, а порядок и порция считаются здесь: порядок —
        // по НАЗВАНИЮ поставщика, а оно живёт в справочнике ядра, и база упорядочить им не может. По
        // идентификатору блоки поставщиков шли бы в случайном для человека порядке, и «Показать ещё»
        // листалось бы вслепую (ревью PR #1272). Соответствий — тысячи, а не миллионы: это выбор людей.
        var keys = await selected
            .Select(m => new Key(m.Id, m.SupplierId, m.NomenclatureId, m.SourceText))
            .ToListAsync(ct);

        var positions = await RefsAsync(catalog, CostsRecordTypes.NomenclatureCode, [.. keys.Select(k => k.NomenclatureId).Distinct()], ct);
        var organizations = await RefsAsync(catalog, CostsRecordTypes.OrganizationCode, [.. keys.Select(k => k.SupplierId).Distinct()], ct);

        string? IssueOf(Key key) => !positions.TryGetValue(key.NomenclatureId, out var position) ? IssueLost
            : position.Archived ? IssueArchived
            : null;

        // Числа чипов — по отобранному ДО отбора по состоянию: «удалена» и «в архиве» обязаны стоять
        // рядом, какой бы из них ни был нажат.
        var counts = new SupplierMatchIssueCounts(
            keys.Count(k => IssueOf(k) == IssueLost), keys.Count(k => IssueOf(k) == IssueArchived));
        if (issue is not null) keys = [.. keys.Where(k => IssueOf(k) == issue)];

        var words = StringComparer.CurrentCultureIgnoreCase;
        var pageIds = keys
            .OrderBy(k => organizations.GetValueOrDefault(k.SupplierId)?.DisplayName ?? string.Empty, words)
            .ThenBy(k => k.SupplierId)
            .ThenBy(k => k.SourceText, words)
            .ThenBy(k => k.Id)
            .Skip(skip).Take(take ?? DefaultTake)
            .Select(k => k.Id)
            .ToList();

        // Отслеживаемыми — ради версии строки: она теневое свойство и вне отслеживания не читается.
        var page = await db.SupplierMatches.Where(m => pageIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, ct);

        return TypedResults.Ok(new SupplierMatchListView(
            // Соответствие, забытое между двумя чтениями, из порции просто выпадает.
            [.. pageIds.Where(page.ContainsKey).Select(id => page[id]).Select(m =>
                View(db, m, organizations.GetValueOrDefault(m.SupplierId), positions.GetValueOrDefault(m.NomenclatureId)))],
            keys.Count, counts));
    }

    /// <summary>
    /// Поставщики, у которых есть соответствия, — пункты отбора. Отдельным адресом: от отбора и поиска
    /// список не зависит, и считать его на каждое нажатие клавиши в поиске незачем.
    /// </summary>
    private static async Task<Ok<IReadOnlyList<SupplierMatchSupplier>>> SuppliersAsync(
        CostsDbContext db, IModuleCatalog catalog, CancellationToken ct)
    {
        var perSupplier = await db.SupplierMatches.AsNoTracking()
            .GroupBy(m => m.SupplierId)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var organizations = await RefsAsync(catalog, CostsRecordTypes.OrganizationCode, [.. perSupplier.Select(s => s.Id)], ct);

        return TypedResults.Ok<IReadOnlyList<SupplierMatchSupplier>>(
        [
            .. perSupplier
                .Select(s => (s, Ref: organizations.GetValueOrDefault(s.Id)))
                .Select(x => new SupplierMatchSupplier(x.s.Id, x.Ref?.DisplayName, x.Ref?.Archived ?? false, x.Ref is null, x.s.Count))
                .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase),
        ]);
    }

    /// <summary>Лёгкая строка соответствия: то, по чему список упорядочен и отобран.</summary>
    private sealed record Key(Guid Id, Guid SupplierId, Guid NomenclatureId, string SourceText);

    /// <summary>
    /// Записи справочника по идентификаторам — словарём. Вида в системе НЕТ — отказ, а не пустой ответ.
    ///
    /// <para>⚠️ Пустой словарь на месте «справочник не ответил» объявил бы каждую позицию удалённой, а
    /// каждого поставщика — удалённым: человек увидел бы красное «не подставляется» у рабочих
    /// соответствий и забыл бы их (ревью PR #1272). «Ещё не знаем» — не «пусто».</para>
    /// </summary>
    private static async Task<Dictionary<Guid, ModuleCatalogRef>> RefsAsync(
        IModuleCatalog catalog, string typeCode, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];

        var refs = await catalog.RefsAsync(typeCode, ids, ct);
        return refs?.ToDictionary(r => r.Id)
            ?? throw new ConflictException(
                $"Вид «{typeCode}» в системе не заведён, а соответствия на него ссылаются. Сказать, какие записи " +
                "на месте, нечем — и это не «записи удалены». Обратитесь к администратору.");
    }

    private static async Task<Ok<SupplierMatchView>> PointAsync(
        Guid id, SupplierMatchPointRequest body, HttpRequest request, CostsDbContext db, IModuleCatalog catalog,
        IModuleUser user, IModuleActivityLog log, CancellationToken ct)
    {
        if (body.NomenclatureId is not { } position)
            throw new InvalidRequestException("Позиция не названа («nomenclatureId»): направить соответствие некуда.");

        var match = await SeenAsync(db, id, request, ct);
        var names = await NamesAsync(catalog, match, position, ct);

        if (match.NomenclatureId != position)
        {
            // Смена позиции — НОВАЯ ссылка, и правило у неё то же, что у выбора в строке (ТЗ CORE-34.4):
            // архивная и удалённая отвергаются. Судим по той же записи, что прочитана ради названия, —
            // два ответа справочника, взятые в разные мгновения, могли бы разойтись.
            if (names.After is null)
                throw new InvalidRequestException(
                    "Такой позиции номенклатуры нет — её могли удалить, пока список был открыт. Выберите другую.");
            if (names.After.Archived)
                throw new InvalidRequestException(
                    $"Позиция «{names.After.DisplayName}» в архиве — соответствие на неё не направить: архивная " +
                    "позиция не подставляется. Выберите действующую.");

            match.Point(match.SourceText, position, user.Id, user.Name);
            await db.SaveChangesAsync(ct);
            await log.RecordAsync(InvoiceActions.MatchPointed, match.Id.ToString(), names.Label,
                before: names.Before?.DisplayName ?? "позиция удалена", after: names.After.DisplayName, ct: ct);
        }

        return TypedResults.Ok(View(db, match, names.Supplier, names.After));
    }

    private static async Task<NoContent> ForgetAsync(
        Guid id, HttpRequest request, CostsDbContext db, IModuleCatalog catalog, IModuleActivityLog log,
        CancellationToken ct)
    {
        var match = await SeenAsync(db, id, request, ct);
        var names = await NamesAsync(catalog, match, match.NomenclatureId, ct);

        // Строки счетов, подставленные по этому соответствию, не трогаем: позиция в них остаётся, а
        // пометка сама скажет «соответствие с тех пор забыли» (InvoiceLineMatchView.Gone).
        db.SupplierMatches.Remove(match);
        await db.SaveChangesAsync(ct);
        await log.RecordAsync(InvoiceActions.MatchForgotten, match.Id.ToString(), names.Label,
            before: names.Before?.DisplayName ?? "позиция удалена", ct: ct);

        return TypedResults.NoContent();
    }

    /// <summary>
    /// Забыть ВСЕ соответствия поставщика разом (решение владельца продукта от 10.10.2026).
    ///
    /// <para>Каждое соответствие держит поставщика от удаления, а забывать по одному — это сотни
    /// подтверждений: держатель был бы снимаем формально, а на деле нет (ревью PR #1272). Нужно это,
    /// когда запись поставщика — дубль, который убирают.</para>
    ///
    /// <para>Версии здесь нет: забывается не то, что человек видел в списке, а всё, что у поставщика
    /// есть к этой минуте, — и число забытого возвращается ответом и пишется в журнал. Поставщик
    /// обязателен: адрес без него стёр бы память системы целиком.</para>
    /// </summary>
    private static async Task<Ok<SupplierMatchesForgotten>> ForgetAllAsync(
        CostsDbContext db, IModuleCatalog catalog, IModuleActivityLog log, CancellationToken ct, Guid? supplierId = null)
    {
        if (supplierId is not { } supplier)
            throw new InvalidRequestException(
                "Поставщик не назван («supplierId»). Забыть разом можно только соответствия одного поставщика.");

        var matches = await db.SupplierMatches.Where(m => m.SupplierId == supplier).ToListAsync(ct);
        if (matches.Count == 0) return TypedResults.Ok(new SupplierMatchesForgotten(0));

        var name = (await RefsAsync(catalog, CostsRecordTypes.OrganizationCode, [supplier], ct))
            .GetValueOrDefault(supplier)?.DisplayName ?? "поставщик удалён";

        db.SupplierMatches.RemoveRange(matches);
        await db.SaveChangesAsync(ct);
        await log.RecordAsync(InvoiceActions.MatchesForgotten, supplier.ToString(), name,
            before: $"соответствий: {matches.Count}", ct: ct);

        return TypedResults.Ok(new SupplierMatchesForgotten(matches.Count));
    }

    /// <summary>Соответствие — той версии, которую человек видел. Без версии — 400, с устаревшей — 409.</summary>
    private static async Task<SupplierMatch> SeenAsync(CostsDbContext db, Guid id, HttpRequest request, CancellationToken ct)
    {
        if (!SeenVersion.TryRead(request, out var seen)) throw new InvalidRequestException(SeenVersion.Malformed);
        if (seen is null)
            throw new InvalidRequestException(
                $"Версия соответствия не названа (заголовок {SeenVersion.Header}): правка обязана сказать, какое " +
                "состояние списка человек видел. Значение — поле «version» из ответа чтения.");

        var match = await db.SupplierMatches.FirstOrDefaultAsync(m => m.Id == id, ct)
            ?? throw new NotFoundException(
                "Такого соответствия нет — его могли забыть, пока список был открыт. Перечитайте список.");

        if (db.VersionOf(match) != seen)
            throw new ConflictException(
                "Соответствие тем временем изменили, и это действие не выполнено. Перечитайте список: " +
                "выполненное поверх, оно затёрло бы чужую правку.");
        return match;
    }

    /// <summary>Чем назвать соответствие в журнале и в ответе: поставщик, прежняя и новая позиция.</summary>
    private static async Task<(string Label, ModuleCatalogRef? Supplier, ModuleCatalogRef? Before, ModuleCatalogRef? After)>
        NamesAsync(IModuleCatalog catalog, SupplierMatch match, Guid position, CancellationToken ct)
    {
        var supplier = (await RefsAsync(catalog, CostsRecordTypes.OrganizationCode, [match.SupplierId], ct))
            .GetValueOrDefault(match.SupplierId);
        var positions = await RefsAsync(
            catalog, CostsRecordTypes.NomenclatureCode, [.. new[] { match.NomenclatureId, position }.Distinct()], ct);

        var key = match.Kind == SupplierMatchKind.Code ? "артикул" : "наименование";
        return ($"{supplier?.DisplayName ?? "поставщик удалён"}: {key} «{match.SourceText}»",
            supplier, positions.GetValueOrDefault(match.NomenclatureId), positions.GetValueOrDefault(position));
    }

    private static SupplierMatchView View(
        CostsDbContext db, SupplierMatch match, ModuleCatalogRef? supplier, ModuleCatalogRef? position) =>
        new(match.Id, db.VersionOf(match), match.SupplierId, supplier?.DisplayName, supplier?.Archived ?? false,
            supplier is null, InvoiceLineMatchView.Name(match.Kind), match.SourceText, match.NomenclatureId,
            position?.DisplayName, position?.EntityType,
            position is null ? IssueLost : position.Archived ? IssueArchived : null,
            match.UpdatedAt, match.UpdatedByName);
}

/// <summary>Сколько соответствий поставщика забыто разом.</summary>
public sealed record SupplierMatchesForgotten(int Forgotten);

/// <summary>Куда направить соответствие.</summary>
public sealed record SupplierMatchPointRequest(Guid? NomenclatureId);

/// <summary>Строка списка соответствий.</summary>
/// <param name="Version">Версия записи — её называет правка заголовком <c>If-Match</c>.</param>
/// <param name="SupplierLost">Записи поставщика больше нет: имя взять неоткуда, и это не «без имени».</param>
/// <param name="By"><c>code</c> — ключ есть артикул, <c>name</c> — наименование.</param>
/// <param name="Issue">Почему соответствие НЕ ПОДСТАВЛЯЕТСЯ: <c>archived</c> — позиция в архиве,
/// <c>lost</c> — её больше нет. <c>null</c> — подставляется.</param>
public sealed record SupplierMatchView(
    Guid Id, string Version, Guid SupplierId, string? SupplierName, bool SupplierArchived, bool SupplierLost,
    string By, string Source, Guid NomenclatureId, string? NomenclatureName, string? NomenclatureType,
    string? Issue, DateTimeOffset UpdatedAt, string? UpdatedBy);

/// <summary>Сколько соответствий под отбором не подставляется — числа у чипов отбора.</summary>
public sealed record SupplierMatchIssueCounts(int Lost, int Archived);

/// <summary>Поставщик, у которого есть соответствия, — пункт отбора.</summary>
public sealed record SupplierMatchSupplier(Guid Id, string? Name, bool Archived, bool Lost, int Count);

/// <summary>Порция списка соответствий.</summary>
/// <param name="Total">Сколько соответствий под отбором ВСЕГО: без числа порция читалась бы как весь список.</param>
public sealed record SupplierMatchListView(
    IReadOnlyList<SupplierMatchView> Items, int Total, SupplierMatchIssueCounts Counts);
