using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Plan.Matching;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Plan.Endpoints;

/// <summary>
/// Список запомненных сопоставлений шифров: прочитать, перенацелить, забыть (change <c>plan-08-matching</c>,
/// ТЗ PLAN-6.1, D8 зонтика) — по образцу <c>SupplierMatchListEndpoints</c> модуля <c>costs</c>.
///
/// <para><b>Зачем он помимо удобства.</b> Запомненное держит вид работы и единицу от удаления (ТЗ CORE-34.1), а
/// держатель, которого нечем снять, делает запись неудаляемой. И второе: один шифр в разных сметах может
/// значить разные работы — неверное запомненное перенацеливают здесь.</para>
///
/// <para><b>Ключ не правится и руками не заводится</b>: шифр — слово файла, набранное по памяти не совпало бы
/// с ним и не сработало бы никогда. Забытое не трогает строк, которые оно уже сопоставило: их пометка сама
/// скажет «с тех пор забыли».</para>
///
/// <para>Чтение — правом чтения смет, правка — правом правки, с версией записи заголовком <c>If-Match</c>:
/// список открыт долго, и «перенацелить» поверх чужой правки затёрло бы её молча.</para>
/// </summary>
public static class RateMatchListEndpoints
{
    /// <summary>Почему запомненное не подставится: вида работы или единицы нет либо они в архиве.</summary>
    public const string IssueLost = "lost";
    public const string IssueArchived = "archived";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var matches = endpoints.MapGroup("/api/plan/rate-matches").WithTags("Сметы");
        matches.MapGet("/", ListAsync).RequireAuthorization(AppPolicies.Permission(PlanModule.EstimateRead));
        matches.MapPut("/{id:guid}", PointAsync).RequireAuthorization(AppPolicies.Permission(PlanModule.EstimateEdit));
        matches.MapDelete("/{id:guid}", ForgetAsync).RequireAuthorization(AppPolicies.Permission(PlanModule.EstimateEdit));
    }

    private static async Task<IResult> ListAsync(
        PlanDbContext db, IModuleWorkTypes workTypes, IModuleUnits units, CancellationToken ct,
        string? query = null, int skip = 0, int? take = null)
    {
        var portion = MatchLists.Take(skip, take);

        var selected = db.RateMatches.AsQueryable();
        if (!string.IsNullOrWhiteSpace(query))
        {
            // По словам файла — шифру и единице расценки. Название вида работы живёт в справочнике ядра.
            var pattern = MatchLists.Contains(query);
            selected = selected.Where(m => EF.Functions.ILike(m.RateCode, pattern, "\\"));
        }

        var total = await selected.CountAsync(ct);
        // Отслеживаемыми — ради версии записи: она теневое свойство и вне отслеживания не читается.
        var page = await selected.OrderBy(m => m.RateCode).ThenBy(m => m.NormativeBase).ThenBy(m => m.Id)
            .Skip(skip).Take(portion).ToListAsync(ct);
        var names = await NamesAsync(workTypes, units, page, ct);
        return Results.Ok(new RateMatchListDto([.. page.Select(m => View(db, m, names))], total));
    }

    /// <summary>Перенацелить: другой вид работы, единица или коэффициент. Единица расценки остаётся — она слово файла.</summary>
    private static async Task<IResult> PointAsync(
        Guid id, RateMatchPointRequest body, HttpRequest request, PlanDbContext db, RowMatching matching,
        IModuleWorkTypes workTypes, IModuleUnits units, IModuleUser user, IModuleActivityLog log, CancellationToken ct)
    {
        if (body.WorkTypeId is not { } workTypeId)
            throw new InvalidRequestException("Вид работы не назван («workTypeId»): перенацелить запомненное некуда.");
        var match = await SeenAsync(db, id, request, ct);
        var before = await NamesAsync(workTypes, units, [match], ct);

        // Новый вид работы и единица — новые ссылки (ТЗ CORE-34.4): архивные отвергаются, как при сопоставлении.
        var (workType, unit) = await matching.ResolveAsync(new RateChoice(workTypeId, body.UnitId, body.Factor), ct);
        var factor = body.Factor is null && unit.Id == match.UnitId
            ? match.Factor
            : RateMatching.FactorOf(body.Factor, match.SourceUnit, unit);
        var value = new RateMatchValue(workType.Id, unit.Id, factor, match.SourceUnit);

        if (!RateMatching.Same(match.Value, value))
        {
            var was = Label(match, before);
            match.Point(match.RateCode, value, user.Id, user.Name);
            await db.SaveChangesAsync(ct);
            await log.RecordAsync(EstimateActions.RateMatchPointed, match.Id.ToString(), $"Шифр «{match.RateCode}»",
                before: was, after: $"{workType.DisplayName}, {unit.Name} × {factor}", ct: ct);
        }

        return Results.Ok(View(db, match, await NamesAsync(workTypes, units, [match], ct)));
    }

    private static async Task<IResult> ForgetAsync(
        Guid id, HttpRequest request, PlanDbContext db, IModuleWorkTypes workTypes, IModuleUnits units,
        IModuleActivityLog log, CancellationToken ct)
    {
        var match = await SeenAsync(db, id, request, ct);
        var names = await NamesAsync(workTypes, units, [match], ct);

        // Строки, сопоставленные этим запомненным, не трогаем: сопоставление в них остаётся, а пометка сама
        // скажет «с тех пор забыли» (RateMarks).
        db.RateMatches.Remove(match);
        await db.SaveChangesAsync(ct);
        await log.RecordAsync(EstimateActions.RateMatchForgotten, match.Id.ToString(), $"Шифр «{match.RateCode}»",
            before: Label(match, names), ct: ct);
        return Results.NoContent();
    }

    /// <summary>Запомненное — той версии, которую человек видел. Без версии — 400, с устаревшей — 409.</summary>
    private static async Task<RateMatch> SeenAsync(PlanDbContext db, Guid id, HttpRequest request, CancellationToken ct)
    {
        var match = await db.RateMatches.FirstOrDefaultAsync(m => m.Id == id, ct)
            ?? throw new NotFoundException("Такого запомненного сопоставления нет — его могли забыть, пока список был открыт.");
        EstimateEndpoints.EnsureSeen(request, db.VersionOf(match), "Запомненное сопоставление", "список запомненного");
        return match;
    }

    /// <summary>Виды работ и единицы записей — словарями; справочник не заведён — отказ, а не «всё удалено».</summary>
    private static async Task<Names> NamesAsync(
        IModuleWorkTypes workTypes, IModuleUnits units, IReadOnlyList<RateMatch> matches, CancellationToken ct)
    {
        if (matches.Count == 0) return new([], []);
        var types = await workTypes.GetAsync([.. matches.Select(m => m.WorkTypeId).Distinct()], ct)
            ?? throw new ConflictException(IModuleWorkTypes.NotSetUp);
        var found = await units.GetAsync([.. matches.Select(m => m.UnitId).Distinct()], ct)
            ?? throw new ConflictException(IModuleUnits.NotSetUp);
        return new(types.ToDictionary(t => t.Id), found.ToDictionary(u => u.Id));
    }

    private static string Label(RateMatch match, Names names) =>
        $"{names.WorkTypes.GetValueOrDefault(match.WorkTypeId)?.DisplayName ?? "вид работы удалён"}, " +
        $"{names.Units.GetValueOrDefault(match.UnitId)?.Name ?? "единица удалена"} × {match.Factor}";

    private static RateMatchDto View(PlanDbContext db, RateMatch match, Names names)
    {
        var workType = names.WorkTypes.GetValueOrDefault(match.WorkTypeId);
        var unit = names.Units.GetValueOrDefault(match.UnitId);
        var issue = workType is null || unit is null ? IssueLost
            : workType.Archived || unit.Archived ? IssueArchived
            : null;
        return new(match.Id, db.VersionOf(match), match.RateCode, match.NormativeBase, match.WorkTypeId,
            workType?.DisplayName, match.UnitId, unit?.Name, match.Factor, match.SourceUnit, issue, match.UpdatedAt,
            match.UpdatedByName);
    }

    private sealed record Names(Dictionary<Guid, ModuleWorkType> WorkTypes, Dictionary<Guid, ModuleUnit> Units);
}

/// <summary>Куда перенацелить: вид работы, единица (не названа — единица вида работы) и коэффициент (не назван —
/// прежний при той же единице, иначе предложенный единицами).</summary>
public sealed record RateMatchPointRequest(Guid? WorkTypeId, Guid? UnitId, decimal? Factor);

/// <param name="Version">Версия записи — её называет правка заголовком <c>If-Match</c>.</param>
/// <param name="Issue">Почему запомненное НЕ подставится: <c>lost</c> — вида работы или единицы нет,
/// <c>archived</c> — в архиве. <c>null</c> — подставится.</param>
public sealed record RateMatchDto(
    Guid Id, string Version, string RateCode, string? NormativeBase, Guid WorkTypeId, string? WorkTypeName,
    Guid UnitId, string? UnitName, decimal Factor, string? SourceUnit, string? Issue, DateTimeOffset UpdatedAt,
    string? UpdatedBy);

/// <param name="Total">Сколько запомненного под отбором всего: без числа порция читалась бы как весь список.</param>
public sealed record RateMatchListDto(IReadOnlyList<RateMatchDto> Items, int Total);
