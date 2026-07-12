using BHS.CRG.Domain.Objects;

namespace BHS.CRG.Application.Common;

/// <summary>
/// Специализированный репозиторий <see cref="DomainObject"/> (issue #84): загрузка документов
/// комплекта с документной фасетой (у общих данных фасеты нет). Обычные CRUD — из <see cref="IRepository{T}"/>;
/// <see cref="IRepository{T}.GetByIdAsync"/> здесь грузит фасету и её файлы.
/// </summary>
public interface IDomainObjectRepository : IRepository<DomainObject>
{
    /// <summary>Документы комплекта — объекты на оси (Set, setId), у которых есть фасета.
    /// <paramref name="tracked"/>=true — с трекингом и фасетой (для массовых изменений порядка/т.п.).</summary>
    Task<IReadOnlyList<DomainObject>> GetSetDocumentsAsync(Guid setId, bool tracked, CancellationToken ct = default);

    /// <summary>Документы нескольких комплектов (untracked, для списков/пикеров).</summary>
    Task<IReadOnlyList<DomainObject>> GetDocumentsInSetsAsync(IReadOnlyCollection<Guid> setIds, CancellationToken ct = default);
}
