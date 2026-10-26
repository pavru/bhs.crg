using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Schema;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace BHS.CRG.Modules.Plan.Endpoints;

/// <summary>
/// Поиск по номенклатуре для окна материала сметы (change <c>plan-09-estimate-ui</c>, ТЗ PLAN-9.1) — тот же поиск
/// ядра (<see cref="IModuleCatalog.SearchAsync" />), что у строк счёта (<c>/api/costs/nomenclature</c>), и тот же
/// ответ: окно выбора позиции у них общее.
///
/// <para><b>Свой адрес, а не адрес счетов</b> (решение владельца 26.10.2026): тот закрыт модулем и правами
/// счетов, которых у сметчика нет, — и окно материала без поиска показывало бы только пять похожих.</para>
///
/// <para>Правом правки смет: поиск нужен, чтобы выбрать позицию, а выбор — правка. ⚠️ Ответ неполон, и это
/// сказано полем <c>more</c>: отсечение иначе читалось бы как «такой позиции нет», и человек завёл бы дубль.</para>
/// </summary>
public static class NomenclatureSearchEndpoints
{
    /// <summary>Позиций на один запрос — как у счетов: список, который читают глазами, не прокручивая.</summary>
    public const int Limit = 25;

    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/plan/nomenclature", SearchAsync)
            .WithTags("Сметы").RequireAuthorization(AppPolicies.Permission(PlanModule.EstimateEdit));

    private static async Task<Ok<PlanNomenclatureSearchDto>> SearchAsync(
        IModuleCatalog catalog, CancellationToken ct, string? query = null)
    {
        var found = await catalog.SearchAsync(FunctionalTag.RoleNomenclature, query, Limit + 1, ct)
            // Пустой список здесь означал бы «позиций ещё не завели», а справочника нет вовсе.
            ?? throw new ConflictException(EstimateRowReferences.NoNomenclature);

        // На одну больше, чем отдаём: «есть ещё» — факт, а не догадка по заполненному до предела списку.
        return TypedResults.Ok(new PlanNomenclatureSearchDto(
            [.. found.Items.Take(Limit).Select(r => new PlanNomenclatureItemDto(r.Id, r.DisplayName, r.EntityType, r.MatchedAlias))],
            found.Items.Count > Limit, found.InArchive));
    }
}

/// <param name="Name">Название позиции; <c>null</c> — у записи его нет вовсе.</param>
/// <param name="Type">Код типа записи — у подтипа свой: различить похожие позиции.</param>
/// <param name="MatchedAlias">Альтернативное имя, по которому позиция найдена, когда набранного нет в названии.</param>
public sealed record PlanNomenclatureItemDto(Guid Id, string? Name, string Type, string? MatchedAlias);

/// <param name="More">Подходящих больше, чем в ответе.</param>
/// <param name="InArchive">Сколько подходящих позиций в архиве — в выборе их нет.</param>
public sealed record PlanNomenclatureSearchDto(IReadOnlyList<PlanNomenclatureItemDto> Items, bool More, int InArchive);
