using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>
/// Условия поставщиков — отсрочка платежа (ТЗ COST-9.2; задача C4, issue #1080): прочитать, записать,
/// снять. Окно «Условия поставщиков» на странице счетов — по образцу окна соответствий (решение
/// владельца 14.10.2026).
///
/// <para>Правом правки счёта, как соответствия: условия ведёт тот, кто вводит счета, — он и видит, что
/// отсрочку подставлять нечем.</para>
///
/// <para>Правка и снятие называют версию записи заголовком <c>If-Match</c>, как правка соответствия.
/// <b>Запись без заголовка — это заведение новых условий</b>: у поставщика, у которого условия уже
/// есть, она получает 409 — иначе два человека, заводящие условия одному поставщику, затёрли бы друг
/// друга молча.</para>
/// </summary>
public static class SupplierTermsEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/costs/supplier-terms", ListAsync)
            .RequireAuthorization(AppPolicies.Module("costs"))
            .RequireAuthorization(AppPolicies.Permission(CostsModule.InvoiceEdit))
            .WithTags("Costs");

        endpoints.MapPut("/api/costs/supplier-terms/{supplierId:guid}", SetAsync)
            .RequireAuthorization(AppPolicies.Module("costs"))
            .RequireAuthorization(AppPolicies.Permission(CostsModule.InvoiceEdit))
            .WithTags("Costs");

        endpoints.MapDelete("/api/costs/supplier-terms/{supplierId:guid}", RemoveAsync)
            .RequireAuthorization(AppPolicies.Module("costs"))
            .RequireAuthorization(AppPolicies.Permission(CostsModule.InvoiceEdit))
            .WithTags("Costs");
    }

    /// <summary>
    /// Все условия — по алфавиту поставщиков. Без страниц: условия заводят руками, по строке на
    /// поставщика, и их десятки, а не тысячи, как соответствий.
    /// </summary>
    private static async Task<Ok<IReadOnlyList<SupplierTermsView>>> ListAsync(
        CostsDbContext db, IModuleCatalog catalog, CancellationToken ct)
    {
        var all = await db.SupplierTerms.ToListAsync(ct);
        var names = await RefsAsync(catalog, [.. all.Select(t => t.SupplierId)], ct);

        return TypedResults.Ok<IReadOnlyList<SupplierTermsView>>(
        [
            .. all.Select(t => View(db, t, names.GetValueOrDefault(t.SupplierId)))
                // Удалённый поставщик — в конце: имени у него нет, и среди имён ему не место.
                .OrderBy(v => v.SupplierLost)
                .ThenBy(v => v.SupplierName, StringComparer.CurrentCultureIgnoreCase),
        ]);
    }

    private static async Task<Ok<SupplierTermsView>> SetAsync(
        Guid supplierId, SupplierTermsRequest body, HttpRequest request, CostsDbContext db, IModuleCatalog catalog,
        IModuleUser user, IModuleActivityLog log, CancellationToken ct)
    {
        if (body.DeferralDays is not { } days || days is < 0 or > 3650)
            throw new InvalidRequestException(
                "Отсрочка («deferralDays») — целое число дней от 0 до 3650, как у поля счёта. Ноль — платим в день отгрузки.");

        var seen = Seen(request);
        var terms = await db.SupplierTerms.FirstOrDefaultAsync(t => t.SupplierId == supplierId, ct);
        var supplier = (await RefsAsync(catalog, [supplierId], ct)).GetValueOrDefault(supplierId);
        int? before = terms?.DeferralDays;

        if (seen is null)
        {
            if (terms is not null)
                throw new ConflictException(
                    "У этого поставщика условия уже заведены — пока окно было открыто, их записал кто-то " +
                    "другой. Перечитайте условия и правьте их строку.");

            // Новые условия — НОВАЯ ссылка на поставщика, и правило у неё то же, что у выбора в счёте
            // (ТЗ CORE-34.4): удалённая и архивная запись не выбираются.
            if (supplier is null)
                throw new InvalidRequestException(
                    "Такой организации нет — её могли удалить, пока окно было открыто. Выберите другую.");
            if (supplier.Archived)
                throw new InvalidRequestException(
                    $"Организация «{supplier.DisplayName}» в архиве — новых счетов у неё не будет, и условия ей ни к чему.");

            terms = Data.SupplierTerms.Create(supplierId, days, user.Id, user.Name);
            db.SupplierTerms.Add(terms);
        }
        else
        {
            if (terms is null)
                throw new NotFoundException(
                    "Условий этого поставщика нет — их сняли, пока окно было открыто. Перечитайте условия.");
            EnsureSeen(db, terms, seen);
            if (terms.DeferralDays == days) return TypedResults.Ok(View(db, terms, supplier));
            terms.Set(days, user.Id, user.Name);
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException lost) when (seen is null && lost is not DbUpdateConcurrencyException)
        {
            // Два заведения условий одному поставщику разом: второе упирается в ключ таблицы. Тот же
            // ответ, что у проверки выше, — она просто не успела увидеть первое.
            throw new ConflictException(
                "У этого поставщика условия уже заведены — их записали одновременно с вами. Перечитайте " +
                "условия и правьте их строку.", lost);
        }

        await log.RecordAsync(InvoiceActions.TermsSet, supplierId.ToString(), Label(supplier),
            before: before is { } was ? Days(was) : null, after: Days(days), ct: ct);

        return TypedResults.Ok(View(db, terms, supplier));
    }

    /// <summary>
    /// Снять условия. Заведённые счета не меняются: отсрочка в них — значение, подставленное в свой
    /// день. Новые счета поставщика отсрочку получать перестают.
    /// </summary>
    private static async Task<NoContent> RemoveAsync(
        Guid supplierId, HttpRequest request, CostsDbContext db, IModuleCatalog catalog, IModuleActivityLog log,
        CancellationToken ct)
    {
        var seen = Seen(request)
            ?? throw new InvalidRequestException(
                $"Версия условий не названа (заголовок {SeenVersion.Header}): снятие обязано сказать, какие " +
                "условия человек видел. Значение — поле «version» из ответа чтения.");

        var terms = await db.SupplierTerms.FirstOrDefaultAsync(t => t.SupplierId == supplierId, ct)
            ?? throw new NotFoundException(
                "Условий этого поставщика нет — их уже сняли, пока окно было открыто. Перечитайте условия.");
        EnsureSeen(db, terms, seen);
        var supplier = (await RefsAsync(catalog, [supplierId], ct)).GetValueOrDefault(supplierId);

        db.SupplierTerms.Remove(terms);
        await db.SaveChangesAsync(ct);
        await log.RecordAsync(InvoiceActions.TermsRemoved, supplierId.ToString(), Label(supplier),
            before: Days(terms.DeferralDays), ct: ct);

        return TypedResults.NoContent();
    }

    private static string? Seen(HttpRequest request) =>
        SeenVersion.TryRead(request, out var seen) ? seen : throw new InvalidRequestException(SeenVersion.Malformed);

    private static void EnsureSeen(CostsDbContext db, SupplierTerms terms, string seen)
    {
        if (db.VersionOf(terms) != seen)
            throw new ConflictException(
                "Условия поставщика тем временем изменили, и это действие не выполнено. Перечитайте условия: " +
                "выполненное поверх, оно затёрло бы чужую правку.");
    }

    /// <summary>
    /// Записи поставщиков словарём. Вида «Организация» нет — отказ, а не пустой ответ: пустой словарь
    /// объявил бы удалённым каждого поставщика, и человек снял бы рабочие условия («ещё не знаем» — не
    /// «пусто», как в списке соответствий).
    /// </summary>
    private static async Task<Dictionary<Guid, ModuleCatalogRef>> RefsAsync(
        IModuleCatalog catalog, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];

        var refs = await catalog.RefsAsync(CostsDirectories.Organization, ids, ct);
        return refs?.ToDictionary(r => r.Id)
            ?? throw new ConflictException(
                $"Вид «{CostsDirectories.Organization}» в системе не заведён, а условия на него ссылаются. Сказать, " +
                "какие поставщики на месте, нечем — и это не «поставщики удалены». Обратитесь к администратору.");
    }

    private static string Label(ModuleCatalogRef? supplier) => supplier?.DisplayName ?? "поставщик удалён";

    /// <summary>Дни словами журнала — без склонений: «30 дн.» читается при любом числе.</summary>
    private static string Days(int days) => $"{days} дн.";

    private static SupplierTermsView View(CostsDbContext db, SupplierTerms terms, ModuleCatalogRef? supplier) =>
        new(terms.SupplierId, db.VersionOf(terms), supplier?.DisplayName, supplier?.Archived ?? false, supplier is null,
            terms.DeferralDays, terms.UpdatedAt, terms.UpdatedByName);
}

/// <summary>Что записать в условия поставщика.</summary>
public sealed record SupplierTermsRequest(int? DeferralDays);

/// <summary>Условия поставщика — строка окна.</summary>
/// <param name="Version">Версия записи — её называют правка и снятие заголовком <c>If-Match</c>.</param>
/// <param name="SupplierLost">Записи поставщика больше нет: имени взять неоткуда, и это не «без имени».</param>
public sealed record SupplierTermsView(
    Guid SupplierId, string Version, string? SupplierName, bool SupplierArchived, bool SupplierLost,
    int DeferralDays, DateTimeOffset UpdatedAt, string? UpdatedBy);
