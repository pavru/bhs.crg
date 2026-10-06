using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>Статья в запросе — одно название.</summary>
public sealed record ArticleRequest(string? Name);

/// <summary>
/// Справочник статей вне строек — «Склад», «Общие расходы» (задача F3, issue #1087, ТЗ COST-10.1).
///
/// <para><b>Своя дверь под своим правом.</b> Статьи лежат в общей таблице ядра (класс A), и раздел «Общие
/// данные» их тоже правит — но под <c>core.catalog.edit</c>, которого бухгалтеру ради двух статей не дают.
/// Право <c>costs.articles.edit</c> открывает ровно этот справочник: запись идёт портом, который сверяет тип
/// каждой записи, так что узкое право не превращается в широкое.</para>
///
/// <para>Читать статьи может всякий, кто видит счета: без названий цели разноски не прочитать.</para>
///
/// <para><b>Архив — здесь же и под тем же правом</b> (issue #1185): общим адресом ядра запись
/// справочника модуля в архив не уходит. Занятую статью удалить нельзя, а убрать из выбора нужно —
/// «Склад на Лесной» закрыли, деньги на нём остались.</para>
/// </summary>
public static class ArticleEndpoints
{
    private const string Edit = "costs.articles.edit";

    /// <summary>Длиннее название не бывает осмысленным: это подпись колонки в матрице и строки в затратах.</summary>
    private const int NameLimit = 200;

    public static void MapArticles(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/costs/articles", ListAsync)
            .RequireAuthorization(AppPolicies.Permission("costs.invoice.read"))
            .WithTags("Счета на оплату");

        endpoints.MapPost("/api/costs/articles", CreateAsync)
            .RequireAuthorization(AppPolicies.Permission(Edit))
            .WithTags("Счета на оплату");

        endpoints.MapPut("/api/costs/articles/{id:guid}", RenameAsync)
            .RequireAuthorization(AppPolicies.Permission(Edit))
            .WithTags("Счета на оплату");

        endpoints.MapDelete("/api/costs/articles/{id:guid}", DeleteAsync)
            .RequireAuthorization(AppPolicies.Permission(Edit))
            .WithTags("Счета на оплату");

        // Отдельными адресами, а не полем правки — как у ядра: форма, не знающая признака, сняла бы
        // его обычным переименованием.
        endpoints.MapPost("/api/costs/articles/{id:guid}/archive",
                (Guid id, IModuleOwnCatalog own, IModuleActivityLog log, CancellationToken ct) =>
                    SetArchivedAsync(id, archived: true, own, log, ct))
            .RequireAuthorization(AppPolicies.Permission(Edit))
            .WithTags("Счета на оплату");

        endpoints.MapPost("/api/costs/articles/{id:guid}/unarchive",
                (Guid id, IModuleOwnCatalog own, IModuleActivityLog log, CancellationToken ct) =>
                    SetArchivedAsync(id, archived: false, own, log, ct))
            .RequireAuthorization(AppPolicies.Permission(Edit))
            .WithTags("Счета на оплату");
    }

    private static async Task<Ok<IReadOnlyList<CostsArticle>>> ListAsync(IModuleCatalog catalog, CancellationToken ct) =>
        TypedResults.Ok(await AllocationPlacesSource.ArticlesAsync(catalog, ct) ?? throw NoType());

    private static async Task<Ok<CostsArticle>> CreateAsync(
        ArticleRequest body, IModuleCatalog catalog, IModuleOwnCatalog own, IModuleActivityLog log, CancellationToken ct)
    {
        var name = Name(body);
        await EnsureUniqueAsync(catalog, name, except: null, ct);

        var created = await own.CreateAsync(CostsRecordTypes.ArticleCode, name, ct) ?? throw NoType();
        await log.RecordAsync(InvoiceActions.ArticleCreated, created.Id.ToString(), name, after: name, ct: ct);
        return TypedResults.Ok(new CostsArticle(created.Id, name, Archived: false));
    }

    private static async Task<Ok<CostsArticle>> RenameAsync(
        Guid id, ArticleRequest body, IModuleCatalog catalog, IModuleOwnCatalog own, IModuleActivityLog log,
        CancellationToken ct)
    {
        var name = Name(body);
        var articles = await AllocationPlacesSource.ArticlesAsync(catalog, ct) ?? throw NoType();
        var was = articles.FirstOrDefault(a => a.Id == id) ?? throw Missing();
        await EnsureUniqueAsync(catalog, name, except: id, ct);

        _ = await own.RenameAsync(CostsRecordTypes.ArticleCode, id, name, ct) ?? throw Missing();
        if (was.Name != name)
            await log.RecordAsync(InvoiceActions.ArticleRenamed, id.ToString(), name, before: was.Name, after: name, ct: ct);
        // Переименовать можно и архивную: её название стоит в разноске старых счетов, и опечатку
        // в нём исправляют так же.
        return TypedResults.Ok(new CostsArticle(id, name, was.Archived));
    }

    /// <summary>
    /// В архив или обратно. Повтор — двойное нажатие, вторая вкладка — отвечает тем же и в журнал не
    /// идёт: иначе журнал свидетельствовал бы о действии, которого не было.
    /// </summary>
    private static async Task<Ok<CostsArticle>> SetArchivedAsync(
        Guid id, bool archived, IModuleOwnCatalog own, IModuleActivityLog log, CancellationToken ct)
    {
        var set = await own.SetArchivedAsync(CostsRecordTypes.ArticleCode, id, archived, ct) ?? throw Missing();
        var name = set.Record.DisplayName ?? "";

        // Журнал — без токена отмены запроса: признак уже записан, и оборванный запрос не должен
        // оставить архив без следа — повтор его не допишет, он вернёт «без изменений».
        if (set.Changed)
            await log.RecordAsync(archived ? InvoiceActions.ArticleArchived : InvoiceActions.ArticleUnarchived,
                id.ToString(), name, ct: CancellationToken.None);
        return TypedResults.Ok(new CostsArticle(id, name, archived));
    }

    /// <summary>
    /// Убрать статью. ⚠️ <b>Занятую — нельзя</b>: на неё разнесены деньги, и без статьи эти части не относились
    /// бы ни к чему — счёт перестал бы быть разнесённым задним числом. Отказ называет, сколько частей и в
    /// скольких счетах, и выход: перенести разноску — либо отправить статью в архив.
    /// </summary>
    private static async Task<NoContent> DeleteAsync(
        Guid id, CostsDbContext db, IModuleCatalog catalog, IModuleOwnCatalog own, IModuleActivityLog log,
        CancellationToken ct)
    {
        var articles = await AllocationPlacesSource.ArticlesAsync(catalog, ct) ?? throw NoType();
        var article = articles.FirstOrDefault(a => a.Id == id) ?? throw Missing();

        var used = await db.InvoiceAllocations.AsNoTracking()
            .Where(a => a.ArticleId == id)
            .GroupBy(a => a.InvoiceId)
            .Select(g => g.Count())
            .ToListAsync(ct);
        if (used.Count > 0)
            throw new ConflictException(
                $"На статью «{article.Name}» разнесено частей: {used.Sum()}, в счетах: {used.Count}. Убрать её нельзя — " +
                "эти деньги перестали бы относиться к чему-либо, и счета стали бы неразнесёнными задним числом. " +
                (article.Archived
                    ? "Она уже в архиве: в выборе цели её нет, а в этих счетах она остаётся."
                    : "Чтобы статья больше не предлагалась, отправьте её в архив; чтобы убрать совсем — " +
                      "сначала перенесите разноску на другую цель."));

        if (!await own.DeleteAsync(CostsRecordTypes.ArticleCode, id, ct)) throw Missing();
        await log.RecordAsync(InvoiceActions.ArticleDeleted, id.ToString(), article.Name, before: article.Name, ct: ct);
        return TypedResults.NoContent();
    }

    private static string Name(ArticleRequest body) => body.Name?.Trim() switch
    {
        null or "" => throw new InvalidRequestException("Название статьи не заполнено."),
        { Length: > NameLimit } => throw new InvalidRequestException(
            $"Название статьи длиннее {NameLimit} знаков — это подпись колонки в матрице разноски, а не описание."),
        var name => name,
    };

    /// <summary>
    /// Две статьи с одним названием — отказ: в выборе цели они неразличимы, и затраты расходились бы по двум
    /// «Складам» случайно. Сравнение без учёта регистра — «склад» и «Склад» человек читает одинаково.
    /// </summary>
    private static async Task EnsureUniqueAsync(IModuleCatalog catalog, string name, Guid? except, CancellationToken ct)
    {
        var articles = await AllocationPlacesSource.ArticlesAsync(catalog, ct) ?? throw NoType();
        if (articles.FirstOrDefault(a => a.Id != except
                && string.Equals(a.Name.Trim(), name, StringComparison.CurrentCultureIgnoreCase)) is not { } same)
            return;

        // Одноимённая в архиве — не «заведите другую», а «верните эту» (ТЗ CORE-34.4): иначе под
        // одним названием лежали бы две статьи, и затраты одного склада разошлись бы по обеим.
        throw new ConflictException(same.Archived
            ? $"Статья «{same.Name}» есть в архиве. Верните её из архива — вторая статья с тем же названием " +
              "разделила бы затраты одного места на две."
            : $"Статья «{name}» уже есть. Две статьи с одним названием в выборе неразличимы.");
    }

    private static NotFoundException Missing() => new(
        "Такой статьи вне строек нет. Так бывает, когда её убрали, пока справочник был открыт: перечитайте его.");

    private static ConflictException NoType() => new(
        $"Справочника статей вне строек («{CostsRecordTypes.ArticleCode}») в системе нет. Его заводит модуль при " +
        "запуске приложения; раз его нет, запуск его пропустил — причина названа в журнале запуска (обычно код " +
        "или название типа уже заняты другим типом).");
}
