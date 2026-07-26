using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Reconciliation;
using MediatR;

namespace BHS.CRG.Application.Reconciliation;

public record ListAliasesQuery(AliasStatus? Status = null) : IRequest<IReadOnlyList<ReconciliationAlias>>;

/// <param name="AliasKey">Ключ варианта — тот, что сводим. Именно он обязан быть уникален.</param>
/// <param name="Confirm">Заводит человек — сразу подтверждаем; предложение агента ждёт разбора.</param>
public record ProposeAliasCommand(
    string AliasKey, string AliasLabel, string CanonicalKey, string CanonicalLabel,
    string? Note, string? By, bool Confirm) : IRequest<ReconciliationAlias>;

public record ReviewAliasCommand(Guid Id, AliasStatus Status, string? Note, string? By)
    : IRequest<ReconciliationAlias>;

public record DeleteAliasCommand(Guid Id) : IRequest;

public class AliasHandlers(IRepository<ReconciliationAlias> repo) :
    IRequestHandler<ListAliasesQuery, IReadOnlyList<ReconciliationAlias>>,
    IRequestHandler<ProposeAliasCommand, ReconciliationAlias>,
    IRequestHandler<ReviewAliasCommand, ReconciliationAlias>,
    IRequestHandler<DeleteAliasCommand>
{
    public async Task<IReadOnlyList<ReconciliationAlias>> Handle(ListAliasesQuery q, CancellationToken ct)
    {
        var items = await repo.FindAsync(a => !q.Status.HasValue || a.Status == q.Status.Value, ct);
        // Неразобранные выше: они ждут человека, остальные — уже история.
        return [.. items
            .OrderBy(a => a.Status == AliasStatus.Proposed ? 0 : 1)
            .ThenBy(a => a.AliasLabel, StringComparer.CurrentCulture)];
    }

    public async Task<ReconciliationAlias> Handle(ProposeAliasCommand cmd, CancellationToken ct)
    {
        if (string.Equals(cmd.AliasKey, cmd.CanonicalKey, StringComparison.Ordinal))
            throw new InvalidOperationException("Позицию нельзя связать саму с собой.");

        // Повторное предложение по тому же варианту — правка существующего: два разных канона у одного
        // ключа сделали бы результат прогона зависящим от порядка выборки.
        var existing = await FindByAliasKeyAsync(cmd.AliasKey, ct);

        ReconciliationAlias alias;
        if (existing is null)
        {
            alias = ReconciliationAlias.Propose(cmd.AliasKey, cmd.AliasLabel,
                cmd.CanonicalKey, cmd.CanonicalLabel, cmd.Note, cmd.By);
            if (cmd.Confirm) alias.Review(AliasStatus.Confirmed, cmd.Note, cmd.By);
            await repo.AddAsync(alias, ct);
        }
        else
        {
            alias = existing;
            alias.Review(cmd.Confirm ? AliasStatus.Confirmed : alias.Status, cmd.Note, cmd.By);
            repo.Update(alias);
        }

        await repo.SaveChangesAsync(ct);
        return alias;
    }

    public async Task<ReconciliationAlias> Handle(ReviewAliasCommand cmd, CancellationToken ct)
    {
        var alias = await repo.GetByIdAsync(cmd.Id, ct) ?? throw new KeyNotFoundException();
        alias.Review(cmd.Status, cmd.Note, cmd.By);
        repo.Update(alias);
        await repo.SaveChangesAsync(ct);
        return alias;
    }

    public async Task Handle(DeleteAliasCommand cmd, CancellationToken ct)
    {
        var alias = await repo.GetByIdAsync(cmd.Id, ct) ?? throw new KeyNotFoundException();
        repo.Remove(alias);
        await repo.SaveChangesAsync(ct);
    }

    /// <summary>Два шага: <c>FindAsync</c> не отслеживает сущности, и правка копии столкнулась бы с
    /// уже отслеживаемым экземпляром.</summary>
    private async Task<ReconciliationAlias?> FindByAliasKeyAsync(string aliasKey, CancellationToken ct)
    {
        var id = (await repo.FindAsync(a => a.AliasKey == aliasKey, ct))
            .Select(a => (Guid?)a.Id).FirstOrDefault();
        return id is null ? null : await repo.GetByIdAsync(id.Value, ct);
    }
}
