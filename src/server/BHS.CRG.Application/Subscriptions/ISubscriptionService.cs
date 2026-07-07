using BHS.CRG.Domain.Catalog;

namespace BHS.CRG.Application.Subscriptions;

/// <summary>Прямой подписчик уровня (для управления списком).</summary>
public record SubscriberDto(Guid Id, Guid UserId, string DisplayName, string? Email, bool ValidEmail);

/// <summary>Эффективный получатель (прямой или унаследованный по иерархии) — для отправки.</summary>
public record RecipientDto(Guid UserId, string DisplayName, string? Email, bool ValidEmail);

/// <summary>
/// Подписки пользователей на уровни стройка/раздел/комплект + резолв получателей с наследованием
/// (подписчик стройки получает события её разделов/комплектов), считаемым на лету подъёмом по иерархии.
/// </summary>
public interface ISubscriptionService
{
    /// <summary>Прямые подписчики уровня (scope, scopeId).</summary>
    Task<IReadOnlyList<SubscriberDto>> ListAsync(CatalogScope scope, Guid scopeId, CancellationToken ct = default);

    /// <summary>Добавляет подписку пользователя; при повторе возвращает существующую (идемпотентно).</summary>
    Task<SubscriberDto?> AddAsync(Guid userId, CatalogScope scope, Guid scopeId, CancellationToken ct = default);

    Task<bool> RemoveAsync(Guid id, CancellationToken ct = default);

    /// <summary>Эффективные получатели уровня: прямые подписчики + унаследованные с вышестоящих уровней.</summary>
    Task<IReadOnlyList<RecipientDto>> ResolveRecipientsAsync(CatalogScope scope, Guid scopeId, CancellationToken ct = default);
}
