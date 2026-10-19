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
/// раз позицию выбирали в счетах и накладных. Ядро о счетах не знает.</para>
/// </summary>
public static class NomenclatureCardEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/costs/nomenclature/{id:guid}/card", CardAsync)
            .RequireAuthorization(AppPolicies.Module("costs"))
            .AddEndpointFilter(CostsLookups.RequireDocumentReader)
            .WithTags("Счета на оплату");

    private static async Task<Ok<NomenclatureCard>> CardAsync(
        Guid id, CostsDbContext db, IModuleCatalog catalog, CancellationToken ct)
    {
        var card = await catalog.CardAsync(CostsDirectories.Nomenclature, id,
                [CostsDirectories.Article, CostsDirectories.Unit], ct)
            // Удалена, другого вида или справочник не назначен — в справочнике номенклатуры её нет.
            ?? throw new NotFoundException("Этой позиции в справочнике номенклатуры нет: её могли удалить.");

        // Строки, где позиция стоит, — обеих таблиц: «выбирали» — это и счёт, и накладная.
        var stamps = db.InvoiceLines.AsNoTracking().Where(l => l.NomenclatureId == id).Select(l => l.UpdatedAt)
            .Concat(db.WaybillLines.AsNoTracking().Where(l => l.NomenclatureId == id).Select(l => l.UpdatedAt));
        var times = await stamps.CountAsync(ct);
        var last = times == 0 ? null : await stamps.MaxAsync(s => (DateTimeOffset?)s, ct);

        return TypedResults.Ok(new NomenclatureCard(
            card.Ref.Id, card.Ref.DisplayName, card.Ref.EntityType, card.TypeName, card.Ref.Archived, card.Lines,
            card.Aliases, card.NotShown, card.EmptyCount,
            card.Tagged.GetValueOrDefault(CostsDirectories.Article), card.Tagged.GetValueOrDefault(CostsDirectories.Unit),
            card.Scope, new NomenclatureChosen(times, last)));
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

/// <summary>Сколько раз позицию ставили в строки счетов и накладных.</summary>
/// <param name="LastAt">Когда строку с ней правили последней; <c>null</c> — не выбирали ни разу.</param>
public sealed record NomenclatureChosen(int Times, DateTimeOffset? LastAt);
