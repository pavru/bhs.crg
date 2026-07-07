using BHS.CRG.Application.Email;
using BHS.CRG.Application.Subscriptions;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Subscriptions;

/// <summary>
/// Подписки на уровни стройка/раздел/комплект + резолв получателей с наследованием (считается на лету
/// подъёмом по иерархии, без денормализации). Только зарегистрированные пользователи.
/// <para>Видимость: список строек в системе сейчас общий (row-level scoping отсутствует), поэтому
/// подписка не даёт доступа сверх имеющегося — отдельного фильтра видимости не требуется (симметрично
/// поиску документов). При появлении row-level видимости резолв нужно будет ограничить так же.</para>
/// </summary>
public class SubscriptionService(AppDbContext db) : ISubscriptionService
{
    public async Task<IReadOnlyList<SubscriberDto>> ListAsync(CatalogScope scope, Guid scopeId, CancellationToken ct = default)
    {
        var subs = await db.Subscriptions.AsNoTracking()
            .Where(s => s.Scope == scope && s.ScopeId == scopeId).ToListAsync(ct);
        return await JoinUsersAsync(subs, ct);
    }

    public async Task<SubscriberDto?> AddAsync(Guid userId, CatalogScope scope, Guid scopeId, CancellationToken ct = default)
    {
        var userExists = await db.Set<ApplicationUser>().AnyAsync(u => u.Id == userId, ct);
        if (!userExists) return null;

        var existing = await db.Subscriptions
            .FirstOrDefaultAsync(s => s.UserId == userId && s.Scope == scope && s.ScopeId == scopeId, ct);
        if (existing is null)
        {
            existing = Subscription.Create(userId, scope, scopeId);
            db.Subscriptions.Add(existing);
            await db.SaveChangesAsync(ct);
        }
        return (await JoinUsersAsync([existing], ct)).FirstOrDefault();
    }

    public async Task<bool> RemoveAsync(Guid id, CancellationToken ct = default)
    {
        var sub = await db.Subscriptions.FindAsync([id], ct);
        if (sub is null) return false;
        db.Subscriptions.Remove(sub);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<RecipientDto>> ResolveRecipientsAsync(CatalogScope scope, Guid scopeId, CancellationToken ct = default)
    {
        // Идентификаторы уровней, покрывающих цель (Guid глобально уникален → фильтра по самому уровню
        // достаточно): комплект наследует свой раздел и стройку, раздел — свою стройку.
        var scopeIds = await BuildScopeChainAsync(scope, scopeId, ct);
        if (scopeIds.Count == 0) return [];

        var userIds = await db.Subscriptions.AsNoTracking()
            .Where(s => scopeIds.Contains(s.ScopeId))
            .Select(s => s.UserId).Distinct().ToListAsync(ct);

        var users = await db.Set<ApplicationUser>().AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.DisplayName, u.Email })
            .ToListAsync(ct);

        return users
            .Select(u => new RecipientDto(u.Id, u.DisplayName, u.Email, EmailValidation.IsValid(u.Email)))
            .OrderBy(r => r.DisplayName)
            .ToList();
    }

    // Цепочка scopeId от целевого уровня вверх до стройки.
    private async Task<List<Guid>> BuildScopeChainAsync(CatalogScope scope, Guid scopeId, CancellationToken ct)
    {
        var chain = new List<Guid>();
        Guid? sectionId = null, constructionId = null;

        if (scope == CatalogScope.Set)
        {
            chain.Add(scopeId);
            var set = await db.DocumentSets.AsNoTracking().FirstOrDefaultAsync(s => s.Id == scopeId, ct);
            sectionId = set?.SectionId;
        }
        else if (scope == CatalogScope.Section) sectionId = scopeId;
        else if (scope == CatalogScope.Construction) constructionId = scopeId;

        if (sectionId is Guid sid)
        {
            chain.Add(sid);
            var section = await db.Set<Section>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == sid, ct);
            constructionId = section?.ConstructionId;
        }
        if (constructionId is Guid cid) chain.Add(cid);

        return chain;
    }

    private async Task<IReadOnlyList<SubscriberDto>> JoinUsersAsync(List<Subscription> subs, CancellationToken ct)
    {
        if (subs.Count == 0) return [];
        var userIds = subs.Select(s => s.UserId).Distinct().ToList();
        var users = await db.Set<ApplicationUser>().AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => new { u.DisplayName, u.Email }, ct);

        return subs
            .Select(s =>
            {
                var u = users.GetValueOrDefault(s.UserId);
                return new SubscriberDto(s.Id, s.UserId, u?.DisplayName ?? "?", u?.Email, EmailValidation.IsValid(u?.Email));
            })
            .OrderBy(s => s.DisplayName)
            .ToList();
    }
}
