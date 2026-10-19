using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>
/// Карточка позиции номенклатуры в окне выбора (issue #1516, эпик #1511): поля записи, чтобы убедиться до
/// выбора, что позиция та.
///
/// <para>⚠️ <b>Расширение доступа — решение владельца 3 от 19.10.2026.</b> Карточку видит тот, кто читает
/// счета или накладные (<see cref="CostsLookups" />), без <c>core.catalog.read</c>: все простые поля позиций
/// номенклатуры. Кто может выбрать позицию, тот может её проверить; допустимо по инварианту #675 (данные не
/// разграничены по пользователям). Картинки, файлы и составные поля не едут — только их заголовки.</para>
///
/// <para>Собирает карточку ядро (порт <see cref="IModuleCatalog.CardAsync" />); модуль добавляет своё — сколько
/// раз позицию выбирали в счетах и накладных (тех, что читающему открыты). Ядро о счетах не знает.</para>
/// </summary>
public static class NomenclatureCardEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/costs/nomenclature/{id:guid}/card", CardAsync)
            .RequireAuthorization(AppPolicies.Module("costs"))
            .AddEndpointFilter(CostsLookups.RequireDocumentReader)
            .WithTags("Счета на оплату");

    private static async Task<Ok<NomenclatureCard>> CardAsync(
        Guid id, CostsDbContext db, IModuleCatalog catalog, IModuleUser user, CancellationToken ct)
    {
        if (await catalog.CardAsync(CostsDirectories.Nomenclature, id,
                [CostsDirectories.Article, CostsDirectories.Unit], ct) is not { } card)
            // Порт отвечает одним «нет» и на удалённую позицию, и на неназначенный справочник; второе — не
            // «позицию удалили», и спросить порт, есть ли справочник, дешевле, чем объяснять человеку не то.
            throw await catalog.RefsAsync(CostsDirectories.Nomenclature, [id], ct) is null
                ? new ConflictException(CostsDirectories.NoNomenclature("показать позицию нечем"))
                : new NotFoundException("Этой позиции в справочнике номенклатуры нет: её могли удалить.");

        return TypedResults.Ok(new NomenclatureCard(
            card.Ref.Id, card.Ref.DisplayName, card.Ref.EntityType, card.TypeName, card.Ref.Archived, card.Lines,
            card.Aliases, card.NotShown, card.EmptyCount,
            card.Tagged.GetValueOrDefault(CostsDirectories.Article), card.Tagged.GetValueOrDefault(CostsDirectories.Unit),
            card.Scope, await ChosenAsync(db, user, id, ct)));
    }

    /// <summary>
    /// Сколько строк документов держат позицию и дата последнего такого документа — только по документам,
    /// которые спрашивающему открыты: право на накладные счетов не открывает (COST-29), и число покупок с
    /// датой — уже сведения о счетах.
    ///
    /// <para>Дата — документа (<c>IssuedOn</c>), а не правки строки: строку правят и переставляют долго после
    /// выбора, и «последний раз» по ней показывал бы день правки количества.</para>
    /// </summary>
    private static async Task<NomenclatureChosen> ChosenAsync(
        CostsDbContext db, IModuleUser user, Guid id, CancellationToken ct)
    {
        IQueryable<DateOnly?>? dates = null;
        if (await user.HasAsync("costs.invoice.read", ct))
            dates = db.InvoiceLines.AsNoTracking().Where(l => l.NomenclatureId == id)
                .Join(db.Invoices, l => l.InvoiceId, i => i.Id, (_, i) => i.IssuedOn);
        if (await user.HasAsync("costs.waybill.read", ct))
        {
            var waybills = db.WaybillLines.AsNoTracking().Where(l => l.NomenclatureId == id)
                .Join(db.Waybills, l => l.WaybillId, w => w.Id, (_, w) => w.IssuedOn);
            dates = dates is null ? waybills : dates.Concat(waybills);
        }
        if (dates is null) return new(0, null);

        var times = await dates.CountAsync(ct);
        return new(times, times == 0 ? null : await dates.MaxAsync(ct));
    }
}

/// <summary>Карточка позиции — для человека: сравнивать по этим строкам код не должен.</summary>
/// <param name="Type">Код вида записи; <paramref name="TypeName" /> — его название.</param>
/// <param name="Lines">Поля: ключ первым и всегда, остальные простые — только заполненные.</param>
/// <param name="NotShown">Заполненные картинки, файлы, составные — заголовками, без содержимого.</param>
/// <param name="EmptyCount">Сколько простых полей не заполнено — их в строках нет.</param>
/// <param name="Article">Строка поля с тэгом артикула; <c>null</c> — справочник не размечен.</param>
/// <param name="Unit">Строка поля с тэгом единицы; <c>null</c> — справочник не размечен.</param>
/// <param name="Scope">Уровень области, если не вся система.</param>
public sealed record NomenclatureCard(
    Guid Id, string? Name, string Type, string TypeName, bool Archived, IReadOnlyList<ModuleCardLine> Lines,
    IReadOnlyList<string> Aliases, IReadOnlyList<string> NotShown, int EmptyCount,
    ModuleCardLine? Article, ModuleCardLine? Unit, string? Scope, NomenclatureChosen Chosen);

/// <summary>Сколько строк счетов и накладных держат позицию — по документам, открытым спрашивающему.</summary>
/// <param name="LastOn">Дата последнего такого документа; <c>null</c> — не выбирали ни разу либо у
/// документов даты нет.</param>
public sealed record NomenclatureChosen(int Times, DateOnly? LastOn);
