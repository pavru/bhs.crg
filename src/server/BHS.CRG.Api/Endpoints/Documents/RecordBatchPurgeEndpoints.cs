using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Objects;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Common;
using MediatR;

namespace BHS.CRG.Api.Endpoints.Documents;

/// <summary>
/// Пакетная очистка записей, занятых только данными выключенных или снятых модулей (issue #1245).
/// Адреса стоят в группе права принудительного удаления: без него — 403 на каждом, включая
/// предпросмотр (разбивка держателей — то, что без права не показывают и одиночному пути).
/// </summary>
public static class RecordBatchPurgeEndpoints
{
    public static void Map(RouteGroupBuilder purge)
    {
        // Есть ли что искать: экран по этому ответу решает, показывать ли действия пакета. Предел
        // пакета едет здесь же: он нужен панели выбора ДО предпросмотра, а владеет им сервер.
        purge.MapGet("/purge-batch/available", async (IRecordHolders holders, CancellationToken ct) =>
            Results.Ok(new { available = await holders.AnyDormantAsync(ct), limit = RecordBatchPurge.Limit }));

        // Предпросмотр: готовый отбор (тип на уровне) или названные записи. POST — список
        // идентификаторов в строку адреса не помещается.
        purge.MapPost("/purge-batch/preview", async (PreviewRequest req, IMediator m, CancellationToken ct) =>
        {
            if (req.Ids is { Length: > RecordBatchPurge.Limit })
                return TooMany(req.Ids.Length);
            if (req.Ids is not { Length: > 0 } && (req.TypeId is null || Scope(req.Scope) is null))
                return Results.BadRequest(new { error = "Назовите записи (ids) или тип и уровень (typeId, scope)." });
            try
            {
                return Results.Ok(PreviewDto.From(await m.Send(new PreviewRecordPurgeQuery(
                    req.TypeId is { } type && Scope(req.Scope) is { } scope ? new(type, scope, req.ScopeId) : null,
                    req.Ids))));
            }
            catch (ConflictException ex) { return Results.Conflict(new { error = ex.Message }); }
        });

        purge.MapPost("/purge-batch", async (BatchRequest req, IMediator m, CancellationToken ct) =>
        {
            if (req.Records is not { Length: > 0 })
                return Results.BadRequest(new { error = "Пакет пуст: назовите записи и число ссылок у каждой." });
            if (req.Records.Length > RecordBatchPurge.Limit) return TooMany(req.Records.Length);
            if (req.Records.Select(r => r.Id).Distinct().Count() != req.Records.Length)
                return Results.BadRequest(new { error = "Запись названа в пакете дважды." });
            try
            {
                return Results.Ok(await m.Send(new PurgeHeldRecordsCommand(
                    req.Records.ToDictionary(r => r.Id, r => r.References), req.References)));
            }
            // Пакет изменился — в отказе свежий предпросмотр: экран показывает его заново.
            catch (PurgeBatchChangedException ex)
            {
                return Results.Conflict(new { error = ex.Message, preview = PreviewDto.From(ex.Fresh) });
            }
            catch (ConflictException ex) { return Results.Conflict(new { error = ex.Message }); }
            // Внешний ключ посторонней схемы: сохранение одно, и отказ базы оставил на месте все.
            catch (Microsoft.EntityFrameworkCore.DbUpdateException ex)
                when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.ForeignKeyViolation } pg)
            {
                return RecordRefusal.HeldByConstraint(pg.ConstraintName);
            }
        });
    }

    private static IResult TooMany(int count) => Results.BadRequest(new
    {
        error = $"В пакете записей: {count}, а за один раз удаляется не больше {RecordBatchPurge.Limit}. " +
                "Разделите выбор на части.",
    });

    private static CatalogScope? Scope(string? scope) =>
        Enum.TryParse<CatalogScope>(scope, out var parsed) && Enum.IsDefined(parsed) ? parsed : null;

    private sealed record PreviewRequest(Guid? TypeId, string? Scope, Guid? ScopeId, Guid[]? Ids);
    private sealed record BatchRecord(Guid Id, int References);
    private sealed record BatchRequest(BatchRecord[]? Records, int References);

    private sealed record RecordDto(
        Guid Id, string Name, bool Archived, int References, int Untraceable, IReadOnlyList<PurgeHolder> Holders);

    private sealed record PreviewDto(
        IReadOnlyList<RecordDto> Records, IReadOnlyList<PurgeBatchExcluded> Excluded,
        int References, int Untraceable, int Checked, int Missing, int More, int Limit)
    {
        public static PreviewDto From(PurgeBatchPreview p) => new(
            [.. p.Records.Select(r => new RecordDto(r.Id, r.Name, r.Archived, r.Release.References, r.Release.Untraceable,
                [.. r.Release.Holders.Select(h => new PurgeHolder(h.Owner, h.What, h.Rows, h.Documents, h.Traceable))]))],
            p.Excluded, p.References, p.Untraceable, p.Checked, p.Missing, p.More, RecordBatchPurge.Limit);
    }
}
