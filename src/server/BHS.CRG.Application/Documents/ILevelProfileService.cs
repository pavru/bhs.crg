using BHS.CRG.Domain.Catalog;

namespace BHS.CRG.Application.Documents;

/// <summary>
/// Профиль уровня (issue #258): объект-синглтон составного типа, помеченного тэгом
/// profile-construction/section/set, на scope контейнера. Ленивое создание — при обращении к общим
/// данным уровня. Реализация в Infrastructure (нужен доступ к DomainObject + контейнерам + типам).
/// </summary>
public interface ILevelProfileService
{
    /// <summary>
    /// Гарантирует наличие объекта-профиля для контейнера уровня (Construction/Section/Set), если для
    /// уровня сконфигурирован профиль-тип (тип с соответствующим тэгом). Создаёт пустой объект и
    /// проставляет FK контейнера. Идемпотентно; no-op, если профиль-тип не задан или уровень не
    /// контейнерный. Возвращает id объекта-профиля (или null).
    /// </summary>
    Task<Guid?> EnsureProfileAsync(CatalogScope level, Guid containerId, CancellationToken ct = default);
}
