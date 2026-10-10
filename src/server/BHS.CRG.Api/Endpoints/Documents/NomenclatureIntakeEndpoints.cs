using BHS.CRG.Api.Auth;
using BHS.CRG.Application.Documents;
using BHS.CRG.Modules;

namespace BHS.CRG.Api.Endpoints.Documents;

/// <summary>
/// Новая позиция номенклатуры коротким окном (задача C3, issue #1079, ТЗ COST-7.1, TYPE-8).
///
/// <para><b>Дверь ядра под <c>core.nomenclature.edit</c></b> — и это первая дверь, на которой право
/// стоит. Открывают её из строки счёта, но адрес не модульный: справочник ведёт ядро, а модуль счетов
/// номенклатуру не создаёт ничем (сторож — «сопоставление не создаёт номенклатуру»).</para>
///
/// <para>Не <c>POST /api/common-data</c> под <c>core.catalog.edit</c>: то право открывает ВСЕ
/// справочники и все уровни. Здесь — только «Номенклатура» и её подтипы, только уровень всей системы,
/// только поля, названные описанием; остальное дополняет человек в справочнике.</para>
///
/// <para>Описание и записи на выбор (единицы измерения) отдаёт сама дверь: права читать схему типа и
/// справочники у того, кто вводит счета, может не быть, и окно без них не собралось бы.</para>
/// </summary>
public static class NomenclatureIntakeEndpoints
{
    public const string Permission = "core.nomenclature.edit";

    public static void MapNomenclatureIntakeEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/nomenclature").RequireAuthorization(AppPolicies.Permission(Permission));

        g.MapGet("/intake", async (INomenclatureIntake intake, CancellationToken ct) =>
            Results.Ok(new { kinds = (await intake.DescribeAsync(ct)).Select(KindDto.From) }));

        // POST, хотя ничего не пишет: набранное едет телом, а не строкой адреса — наименования
        // длинные, и в журналах прокси им не место.
        g.MapPost("/similar", async (IntakeBody body, INomenclatureIntake intake, CancellationToken ct) =>
        {
            var found = await intake.SimilarAsync(body.TypeId, body.Values ?? Empty, ct);
            return Results.Ok(new
            {
                exact = found.Exact is { } exact ? PositionDto.From(exact) : null,
                similar = found.Similar.Select(h => new { position = PositionDto.From(h.Record), why = h.Why }),
                more = found.More,
                unreadable = found.Unreadable,
            });
        });

        g.MapPost("/", async (IntakeBody body, INomenclatureIntake intake, CancellationToken ct) =>
        {
            var outcome = await intake.CreateAsync(
                new(body.TypeId, body.Values ?? Empty, body.Refs ?? new Dictionary<string, Guid>()), ct);

            // «Такая уже есть» — полями, а не словами: окно предлагает ВЫБРАТЬ лежащую кнопкой и
            // разбирать для этого фразу не должно.
            if (outcome.Existing is { } twin)
                return Results.Conflict(new
                {
                    error = twin.Archived
                        ? "Такая позиция уже есть — в архиве. Заводить вторую не нужно: вернуть её из архива может тот, кто ведёт справочник."
                        : "Такая позиция уже есть. Вторую такую же завести нельзя — выберите лежащую.",
                    code = "exists",
                    existing = PositionDto.From(twin),
                });

            var created = outcome.Created!;
            return Results.Ok(new PositionDto(created.Id, created.DisplayName, outcome.CreatedType ?? "", false));
        });
    }

    private static readonly IReadOnlyDictionary<string, string?> Empty = new Dictionary<string, string?>();

    /// <param name="Values">Тексты по ключам полей описания.</param>
    /// <param name="Refs">Выбранные записи по ключам полей-ссылок; сверке похожих не нужны.</param>
    public sealed record IntakeBody(
        Guid TypeId, Dictionary<string, string?>? Values, Dictionary<string, Guid>? Refs);

    public sealed record PositionDto(Guid Id, string? Name, string Type, bool Archived)
    {
        public static PositionDto From(SimilarRecord record) =>
            new(record.Id, record.Name, record.Type, record.Archived);
    }

    public sealed record FieldDto(
        string Key, string Title, bool Required, bool Identity, IReadOnlyList<IntakeOption>? Options);

    public sealed record KindDto(
        Guid TypeId, string Code, string Name, IReadOnlyList<FieldDto> Fields, IReadOnlyList<string> Refusals)
    {
        public static KindDto From(IntakeKind kind) => new(
            kind.TypeId, kind.Code, kind.Name,
            // «options: null» — поле текстовое; список (хоть и пустой) — поле выбора.
            [.. kind.Fields.Select(f => new FieldDto(
                f.Key, f.Title, f.Required, f.Identity, f.TargetTypeId is null ? null : f.Options))],
            kind.Refusals);
    }
}
