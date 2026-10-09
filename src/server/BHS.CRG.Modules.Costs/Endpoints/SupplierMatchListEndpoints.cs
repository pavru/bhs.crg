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

        endpoints.MapPut("/api/costs/supplier-matches/{id:guid}", PointAsync)
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

        // Поставщики для отбора — по ВСЕМ соответствиям, а не по отобранным: выбрав одного, человек
        // обязан видеть остальных, чтобы перейти к ним.
        var perSupplier = await db.SupplierMatches.AsNoTracking()
            .GroupBy(m => m.SupplierId)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var supplierRefs = await catalog.RefsAsync(CostsRecordTypes.OrganizationCode, [.. perSupplier.Select(s => s.Id)], ct);
        var organizations = (supplierRefs ?? []).ToDictionary(r => r.Id);

        var selected = db.SupplierMatches.AsQueryable();
        if (supplierId is { } supplier) selected = selected.Where(m => m.SupplierId == supplier);
        if (!string.IsNullOrWhiteSpace(query))
        {
            // Ищем по словам бумаги — артикулу и наименованию у поставщика. Название позиции живёт в
            // справочнике ядра, и искать по нему здесь значило бы читать справочник на каждый запрос.
            var pattern = "%" + query.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            selected = selected.Where(m => EF.Functions.ILike(m.SourceText, pattern, "\\"));
        }

        // Состояние позиций — по отобранному ДО отбора по состоянию: числа у чипов «удалена» и «в
        // архиве» обязаны стоять рядом, какой бы из них ни был нажат.
        var positionIds = await selected.Select(m => m.NomenclatureId).Distinct().ToListAsync(ct);
        var positionRefs = await catalog.RefsAsync(CostsRecordTypes.NomenclatureCode, positionIds, ct);
        var positions = (positionRefs ?? []).ToDictionary(r => r.Id);
        var lost = positionIds.Where(id => !positions.ContainsKey(id)).ToList();
        var archived = positions.Values.Where(p => p.Archived).Select(p => p.Id).ToList();

        var counts = new SupplierMatchIssueCounts(
            lost.Count == 0 ? 0 : await selected.CountAsync(m => lost.Contains(m.NomenclatureId), ct),
            archived.Count == 0 ? 0 : await selected.CountAsync(m => archived.Contains(m.NomenclatureId), ct));

        if (issue == IssueLost) selected = selected.Where(m => lost.Contains(m.NomenclatureId));
        if (issue == IssueArchived) selected = selected.Where(m => archived.Contains(m.NomenclatureId));

        var total = await selected.CountAsync(ct);
        // Отслеживаемыми — ради версии строки: она теневое свойство и вне отслеживания не читается.
        // Порция мала, и контекст живёт один запрос.
        var page = await selected
            .OrderBy(m => m.SupplierId).ThenBy(m => m.SourceText).ThenBy(m => m.Id)
            .Skip(skip).Take(take ?? DefaultTake)
            .ToListAsync(ct);

        return TypedResults.Ok(new SupplierMatchListView(
            [.. page.Select(m => View(db, m, organizations.GetValueOrDefault(m.SupplierId), positions.GetValueOrDefault(m.NomenclatureId)))],
            total, counts,
            [.. perSupplier
                .Select(s => (s, Ref: organizations.GetValueOrDefault(s.Id)))
                .Select(x => new SupplierMatchSupplier(x.s.Id, x.Ref?.DisplayName, x.Ref?.Archived ?? false, x.Ref is null, x.s.Count))
                .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)]));
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
            // Смена позиции — НОВАЯ ссылка, и правило у неё то же, что у выбора в строке (ТЗ CORE-34.4).
            var verdict = (await NewReferences.JudgeAsync(catalog, CostsRecordTypes.NomenclatureCode, [position], ct))
                ?.GetValueOrDefault(position) ?? NewReference.Missing;
            if (verdict == NewReference.Archived)
                throw new InvalidRequestException(
                    $"Позиция «{names.After?.DisplayName}» в архиве — соответствие на неё не направить: архивная " +
                    "позиция не подставляется. Выберите действующую.");
            if (verdict != NewReference.Fine)
                throw new InvalidRequestException(
                    "Такой позиции номенклатуры нет — её могли удалить, пока список был открыт. Выберите другую.");

            match.Point(match.SourceText, position, user.Id, user.Name);
            await db.SaveChangesAsync(ct);
            await log.RecordAsync(InvoiceActions.MatchPointed, match.Id.ToString(), names.Label,
                before: names.Before?.DisplayName ?? "позиция удалена", after: names.After?.DisplayName, ct: ct);
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
        Guid[] asked = [.. new[] { match.NomenclatureId, position }.Distinct()];
        var supplierRefs = await catalog.RefsAsync(CostsRecordTypes.OrganizationCode, [match.SupplierId], ct);
        var positionRefs = await catalog.RefsAsync(CostsRecordTypes.NomenclatureCode, asked, ct);
        var supplier = supplierRefs?.FirstOrDefault();
        var positions = (positionRefs ?? []).ToDictionary(r => r.Id);

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
/// <param name="Suppliers">Все поставщики с соответствиями — независимо от отбора.</param>
public sealed record SupplierMatchListView(
    IReadOnlyList<SupplierMatchView> Items, int Total, SupplierMatchIssueCounts Counts,
    IReadOnlyList<SupplierMatchSupplier> Suppliers);
