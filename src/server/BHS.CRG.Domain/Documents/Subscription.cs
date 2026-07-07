using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Common;

namespace BHS.CRG.Domain.Documents;

/// <summary>
/// Подписка пользователя на уведомления/документы уровня стройки/раздела/комплекта. При отправке
/// получатели резолвятся подъёмом по иерархии (подписчик стройки получает события её разделов и
/// комплектов) — наследование считается на лету, без денормализации. Только зарегистрированные
/// пользователи (внешние email — отдельный этап).
/// </summary>
public class Subscription : Entity
{
    public Guid UserId { get; private set; }
    /// <summary>Уровень подписки — Construction / Section / Set (переиспользуем <see cref="CatalogScope"/>).</summary>
    public CatalogScope Scope { get; private set; }
    public Guid ScopeId { get; private set; }

    private Subscription() { }

    public static Subscription Create(Guid userId, CatalogScope scope, Guid scopeId)
        => new() { UserId = userId, Scope = scope, ScopeId = scopeId };
}
